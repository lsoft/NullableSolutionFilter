using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Internal.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace NullableSolutionFilter
{
    [SolutionTreeFilterProvider(PackageGuids.NullableSolutionFilterString, (uint)(PackageIds.FilterCommandId))]
    public class NullableFilterProvider : HierarchyTreeFilterProvider
    {
        private readonly IVsHierarchyItemCollectionProvider _hierarchyCollectionProvider;

        [ImportingConstructor]
        public NullableFilterProvider(IVsHierarchyItemCollectionProvider hierarchyCollectionProvider)
        {
            _hierarchyCollectionProvider = hierarchyCollectionProvider;
        }

        protected override HierarchyTreeFilter CreateFilter()
        {
            return new Filter(_hierarchyCollectionProvider);
        }

        private sealed class Filter : HierarchyTreeFilter
        {
            private static readonly Regex _nullableEnableRegex = new Regex(@"^\s*#\s*nullable\s*enable\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
            private static readonly Regex _nullableDisableRegex = new Regex(@"^\s*#\s*nullable\s*disable\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

            private readonly IVsHierarchyItemCollectionProvider _hierarchyCollectionProvider;
            private readonly ProjectNullableStatusContainer _projectNullableStatus = new ProjectNullableStatusContainer();

            public Filter(IVsHierarchyItemCollectionProvider hierarchyCollectionProvider)
            {
                _hierarchyCollectionProvider = hierarchyCollectionProvider;
            }

            protected override async Task<IReadOnlyObservableSet> GetIncludedItemsAsync(IEnumerable<IVsHierarchyItem> rootItems)
            {
                _projectNullableStatus.Reset();

                IVsHierarchyItem root = HierarchyUtilities.FindCommonAncestor(rootItems);
                IReadOnlyObservableSet<IVsHierarchyItem> sourceItems;

                sourceItems = await _hierarchyCollectionProvider.GetDescendantsAsync(root.HierarchyIdentity.NestedHierarchy, CancellationToken);

                return await _hierarchyCollectionProvider.GetFilteredHierarchyItemsAsync(sourceItems, MeetsFilter, CancellationToken);
            }

            private bool MeetsFilter(IVsHierarchyItem item)
            {
                if (item == null)
                {
                    return false;
                }

                var canonicalName = item.CanonicalName;
                if (string.IsNullOrEmpty(canonicalName))
                {
                    return false;
                }

                if (!canonicalName.EndsWith(".cs"))
                {
                    return false;
                }

                if (!File.Exists(canonicalName))
                {
                    return false;
                }

                var fileText = File.ReadAllText(canonicalName);

                if (string.IsNullOrEmpty(fileText))
                {
                    return false;
                }

                var fileNullableStatus = GetFileNullableStatus(fileText);
                switch (fileNullableStatus)
                {
                    case FileNullableStatusEnum.NotDefined:
                        {
                            //file does not specify nullable status, check for project status
                            var projectNullableStatus = GetProjectNullableStatus(item);
                            if (projectNullableStatus.HasValue)
                            {
                                return !projectNullableStatus.Value;
                            }

                            //project does not specify nullable status, assume it disabled
                            //so show the item
                            return true;
                        }
                    case FileNullableStatusEnum.BothDefined:
                        //strange file, show it anyway
                        return true;
                    case FileNullableStatusEnum.Enable:
                        //nullable enable, so do not show item
                        return false;
                    case FileNullableStatusEnum.Disable:
                        //nullable disable, item should be visible
                        return true;
                    default:
                        throw new NotSupportedException(fileNullableStatus.ToString());
                }
            }

            private bool? GetProjectNullableStatus(IVsHierarchyItem item)
            {
                bool? projectNullableStatus = null;
                EnvDTE.Project parentCsproj = item.TryFindParentCsprojProject();
                if (parentCsproj != null)
                {
                    projectNullableStatus = _projectNullableStatus.GetNullableStatus(parentCsproj);
                }

                return projectNullableStatus;
            }

            private FileNullableStatusEnum GetFileNullableStatus(string fileText)
            {
                var fileNullableEnable = _nullableEnableRegex.IsMatch(fileText);
                var fileNullableDisable = _nullableDisableRegex.IsMatch(fileText);
                if (fileNullableEnable && fileNullableDisable)
                {
                    //strange file, both directives found
                    return FileNullableStatusEnum.BothDefined;
                }

                var fileNullableStatus = FileNullableStatusEnum.NotDefined;
                if (fileNullableEnable && !fileNullableDisable)
                {
                    fileNullableStatus = FileNullableStatusEnum.Enable;
                }
                else if (!fileNullableEnable && fileNullableDisable)
                {
                    fileNullableStatus = FileNullableStatusEnum.Disable;
                }

                return fileNullableStatus;
            }

            private enum FileNullableStatusEnum
            {
                NotDefined,
                BothDefined,
                Enable,
                Disable
            }
        }
    }

    public class ProjectNullableStatusContainer
    {
        private readonly Dictionary<string, bool?> _projectNullableStatus = new Dictionary<string, bool?>();

        public bool? GetNullableStatus(EnvDTE.Project project)
        {
            var projectFilePath = project.FullName;

            if (!_projectNullableStatus.TryGetValue(projectFilePath, out var projectNullableStatus))
            {
                var csprojBody = File.ReadAllText(projectFilePath);
                if (csprojBody.Contains("Nullable"))
                {
                    var xml = new XmlDocument();
                    xml.LoadXml(csprojBody);

                    var nullableNodes = xml.GetElementsByTagName("Nullable");
                    if (nullableNodes.Count == 1)
                    {
                        var nullableNode = nullableNodes.Item(0);
                        if (StringComparer.InvariantCultureIgnoreCase.Compare(nullableNode.InnerText, "enable") == 0)
                        {
                            projectNullableStatus = true;
                        }
                        else if (StringComparer.InvariantCultureIgnoreCase.Compare(nullableNode.InnerText, "disable") == 0)
                        {
                            projectNullableStatus = false;
                        }
                        else
                        {
                            projectNullableStatus = null;
                        }
                    }
                    else
                    {
                        //none or many Nullable nodes found
                        //anyway, let's think that project have DISABLED nullable status
                        //to show up this project
                        projectNullableStatus = false;
                    }
                }
                else
                {
                    projectNullableStatus = null;
                }

                _projectNullableStatus[projectFilePath] = projectNullableStatus;
            }

            return projectNullableStatus;
        }

        public void Reset()
        {
            _projectNullableStatus.Clear();
        }
    }


    internal static class VsSolutionItemHelper
    {
        public static EnvDTE.Project TryFindParentCsprojProject(this IVsHierarchyItem item)
        {
            while (item != null)
            {
                var result = item.IsCsprojProject();
                if (result != null)
                {
                    return result;
                }

                item = item.Parent;
            }

            return null;
        }

        public static EnvDTE.Project IsCsprojProject(this IVsHierarchyItem item)
        {
            var isProject = HierarchyUtilities.IsProject(item.HierarchyIdentity);
            if (!isProject)
            {
                return null;
            }

            var project = HierarchyUtilities.GetProject(item);
            if(project?.Kind == Community.VisualStudio.Toolkit.ProjectTypes.CSHARP)
            {
                return project;
            }

            return null;
        }
    }
}

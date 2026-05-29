using System.Collections.ObjectModel;

namespace GitPuller_WinUI.ViewModels;

public enum RepositoryTreeNodeKind
{
    Folder,
    Repository
}

public sealed class RepositoryTreeNodeViewModel
{
    public RepositoryTreeNodeViewModel(
        RepositoryTreeNodeKind kind,
        string name,
        string fullCategoryName,
        string fullPath,
        int repositoryCount,
        int attentionCount,
        bool isAllRepositories = false,
        RepositoryResultViewModel? repositoryResult = null,
        IEnumerable<RepositoryTreeNodeViewModel>? children = null)
    {
        Kind = kind;
        Name = string.IsNullOrWhiteSpace(name)
            ? kind == RepositoryTreeNodeKind.Repository ? "(unnamed repository)" : "Unnamed folder"
            : name;
        FullCategoryName = fullCategoryName;
        FullPath = fullPath;
        RepositoryCount = Math.Max(0, repositoryCount);
        AttentionCount = Math.Max(0, attentionCount);
        IsAllRepositories = isAllRepositories;
        RepositoryResult = repositoryResult;
        Children = new ObservableCollection<RepositoryTreeNodeViewModel>(children ?? []);
    }

    public RepositoryTreeNodeKind Kind { get; }
    public bool IsFolder => Kind == RepositoryTreeNodeKind.Folder;
    public bool IsRepository => Kind == RepositoryTreeNodeKind.Repository;
    public bool IsAllRepositories { get; }
    public string Name { get; }
    public string FullCategoryName { get; }
    public string FullPath { get; }
    public int RepositoryCount { get; }
    public int AttentionCount { get; }
    public RepositoryResultViewModel? RepositoryResult { get; }
    public ObservableCollection<RepositoryTreeNodeViewModel> Children { get; }

    public string DisplayName => RepositoryCount == 1
        ? $"{Name} (1 repo)"
        : $"{Name} ({RepositoryCount} repos)";

    public string AttentionText => AttentionCount == 0
        ? "No failed or warning repositories"
        : AttentionCount == 1
            ? "1 repository needs review"
            : $"{AttentionCount} repositories need review";

    public string StatusText => RepositoryResult?.StatusText ?? string.Empty;
    public string StatusIcon => RepositoryResult?.StatusIcon ?? string.Empty;
    public string StatusResourceKey => RepositoryResult?.StatusResourceKey ?? "GitPullerCleanBrush";
    public bool HasStatus => RepositoryResult is not null;
    public string TreeIconGlyph => IsRepository ? "\uE8A5" : "\uE8B7";
    public string TreeMetaText => IsRepository
        ? RepositoryResult?.Summary ?? FullCategoryName
        : $"{RepositoryCount} repositories  /  {AttentionCount} need review";
}

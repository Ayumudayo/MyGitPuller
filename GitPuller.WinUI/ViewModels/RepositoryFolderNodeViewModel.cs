using System.Collections.ObjectModel;

namespace GitPuller_WinUI.ViewModels;

public sealed class RepositoryFolderNodeViewModel
{
    public RepositoryFolderNodeViewModel(
        string name,
        string fullCategoryName,
        string fullPath,
        int repositoryCount,
        int attentionCount,
        bool isAllRepositories = false,
        IEnumerable<RepositoryFolderNodeViewModel>? children = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Unnamed folder" : name;
        FullCategoryName = fullCategoryName;
        FullPath = fullPath;
        RepositoryCount = Math.Max(0, repositoryCount);
        AttentionCount = Math.Max(0, attentionCount);
        IsAllRepositories = isAllRepositories;
        Children = new ObservableCollection<RepositoryFolderNodeViewModel>(children ?? []);
    }

    public string Name { get; }
    public string FullCategoryName { get; }
    public string FullPath { get; }
    public int RepositoryCount { get; }
    public int AttentionCount { get; }
    public bool IsAllRepositories { get; }
    public ObservableCollection<RepositoryFolderNodeViewModel> Children { get; }

    public string DisplayName => RepositoryCount == 1
        ? $"{Name} (1 repo)"
        : $"{Name} ({RepositoryCount} repos)";

    public string AttentionText => AttentionCount == 0
        ? "No failed or warning repositories"
        : AttentionCount == 1
            ? "1 repository needs review"
            : $"{AttentionCount} repositories need review";
}

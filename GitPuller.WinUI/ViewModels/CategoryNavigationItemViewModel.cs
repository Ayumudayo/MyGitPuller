namespace GitPuller_WinUI.ViewModels;

public sealed record CategoryNavigationItemViewModel(
    string Name,
    string FullPath,
    int RepositoryCount,
    int AttentionCount,
    bool IsAllRepositories = false)
{
    public string DisplayName => RepositoryCount == 1
        ? $"{Name} (1 repo)"
        : $"{Name} ({RepositoryCount} repos)";

    public string AttentionText => AttentionCount == 0
        ? "No failed or warning repositories"
        : AttentionCount == 1
            ? "1 repository needs review"
            : $"{AttentionCount} repositories need review";
}

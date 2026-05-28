using System.Collections.ObjectModel;
using System.Windows.Input;
using GitPuller;

namespace GitPuller_WinUI.ViewModels;

public sealed class MainShellViewModel : ObservableObject
{
    private bool showCleanRepositories;
    private RepositoryResultViewModel? selectedResult;
    private CategoryNavigationItemViewModel? selectedCategory;
    private string repositoryUrlToAdd = string.Empty;

    public MainShellViewModel(
        string libraryRoot,
        IEnumerable<CategoryNavigationItemViewModel> categories,
        IEnumerable<RepositoryResultViewModel> repositoryResults,
        IEnumerable<RemovedRepositoryViewModel> removedRepositories)
    {
        LibraryRoot = libraryRoot;
        Categories = new ObservableCollection<CategoryNavigationItemViewModel>(categories);
        RepositoryResults = new ObservableCollection<RepositoryResultViewModel>(repositoryResults);
        RemovedRepositories = new ObservableCollection<RemovedRepositoryViewModel>(removedRepositories);
        selectedResult = VisibleResults.FirstOrDefault();

        AddRepositoryCommand = new RelayCommand(
            execute: () => { },
            canExecute: () => CanAddRepositoryFromUrl);
        RefreshCommand = new RelayCommand(execute: () => { }, canExecute: () => true);
        RetrySelectedCommand = new RelayCommand(
            execute: () => { },
            canExecute: () => SelectedResult?.CanRetry == true);
    }

    public string LibraryRoot { get; }
    public ObservableCollection<CategoryNavigationItemViewModel> Categories { get; }
    public ObservableCollection<RepositoryResultViewModel> RepositoryResults { get; }
    public ObservableCollection<RemovedRepositoryViewModel> RemovedRepositories { get; }
    public ICommand AddRepositoryCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetrySelectedCommand { get; }

    public bool ShowCleanRepositories
    {
        get => showCleanRepositories;
        set
        {
            if (SetProperty(ref showCleanRepositories, value))
            {
                OnPropertyChanged(nameof(VisibleResults));
                OnPropertyChanged(nameof(VisibleResultCount));
                OnPropertyChanged(nameof(ResultSummary));
                EnsureSelectedResultIsVisible();
            }
        }
    }

    public RepositoryResultViewModel? SelectedResult
    {
        get => selectedResult;
        set
        {
            if (SetProperty(ref selectedResult, value))
            {
                RaiseSelectedResultPropertiesChanged();
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public CategoryNavigationItemViewModel? SelectedCategory
    {
        get => selectedCategory;
        set
        {
            if (SetProperty(ref selectedCategory, value))
            {
                OnPropertyChanged(nameof(CanAddRepositoryFromUrl));
                OnPropertyChanged(nameof(SelectedCategoryName));
                OnPropertyChanged(nameof(VisibleResults));
                OnPropertyChanged(nameof(VisibleResultCount));
                OnPropertyChanged(nameof(ResultSummary));
                RaiseCommandCanExecuteChanged();
                EnsureSelectedResultIsVisible();
            }
        }
    }

    public string RepositoryUrlToAdd
    {
        get => repositoryUrlToAdd;
        set
        {
            if (SetProperty(ref repositoryUrlToAdd, value))
            {
                OnPropertyChanged(nameof(CanAddRepositoryFromUrl));
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<RepositoryResultViewModel> VisibleResults => RepositoryResults
        .Where(result => SelectedCategory is null
            || string.Equals(result.Category, SelectedCategory.Name, StringComparison.OrdinalIgnoreCase))
        .Where(result => ShowCleanRepositories || result.Status != RepositoryResultStatus.Clean)
        .OrderBy(result => result.Status)
        .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string SelectedCategoryName => SelectedCategory?.Name ?? "All repositories";
    public bool CanAddRepositoryFromUrl =>
        SelectedCategory is not null
        && !string.IsNullOrWhiteSpace(RepositoryUrlToAdd);

    public int FailedCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Failed);
    public int WarningCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Warning);
    public int UpdatedCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Updated);
    public int CleanCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Clean);
    public int VisibleResultCount => VisibleResults.Count;
    public int TotalResultCount => RepositoryResults.Count;
    public double RunProgress => TotalResultCount == 0 ? 0 : 100;
    public bool HasAttentionItems => FailedCount > 0 || WarningCount > 0;
    public string AttentionSummary => $"{FailedCount} failed, {WarningCount} warning, {UpdatedCount} updated, {CleanCount} clean.";
    public string ResultSummary => $"{VisibleResultCount} of {TotalResultCount} repositories shown";
    public string SelectedResultName => SelectedResult?.Name ?? "No repository selected";
    public string SelectedResultStatus => SelectedResult?.StatusText ?? string.Empty;
    public string SelectedResultCategory => SelectedResult?.Category ?? string.Empty;
    public string SelectedResultPath => SelectedResult?.Path ?? string.Empty;
    public string SelectedResultRemoteUrl => SelectedResult?.RemoteUrl ?? string.Empty;
    public string SelectedResultSummary => SelectedResult?.Summary ?? "Select a repository to review its result.";
    public string SelectedResultDiagnosticExplanation => SelectedResult?.DiagnosticExplanation ?? string.Empty;
    public string SelectedResultSuggestedAction => SelectedResult?.SuggestedAction ?? string.Empty;
    public string SelectedResultEvidence => SelectedResult?.Evidence ?? string.Empty;
    public string SelectedResultRelatedCommand => SelectedResult?.RelatedCommand ?? string.Empty;
    public IReadOnlyList<string> SelectedResultLogLines => SelectedResult?.LogLines ?? Array.Empty<string>();
    public bool HasSelectedResult => SelectedResult is not null;

    public static MainShellViewModel CreateSample()
    {
        var libraryRoot = @"E:\FF14\Repos\MyRepos";
        var categories = new[]
        {
            new CategoryNavigationItemViewModel("Dalamud plugins", Path.Combine(libraryRoot, "Dalamud plugins"), 2, 2),
            new CategoryNavigationItemViewModel("Tooling", Path.Combine(libraryRoot, "Tooling"), 1, 0),
            new CategoryNavigationItemViewModel("Archived experiments", Path.Combine(libraryRoot, "Archived experiments"), 1, 0)
        };

        var failedDiagnostic = new FailureDiagnostic(
            FailureCategory.NetworkTimeout,
            RetryPolicy.Recommended,
            DiagnosticSeverity.Error,
            "Network timeout while fetching origin",
            "The remote did not respond before the Git timeout. The repository can be retried from the GUI without changing configuration.",
            "Retry this repository. If the timeout repeats, check the remote host and network route.",
            "fatal: unable to access 'https://github.com/example/extremely-long-plugin-name-with-many-folders.git/': Operation timed out after 60000 milliseconds with 0 bytes received.",
            RelatedPath: Path.Combine(libraryRoot, "Dalamud plugins", "ExtremelyLongPluginName.With.Multiple.Namespace.Parts.And.LocalExperimentalBranches"),
            RelatedCommand: "git fetch --all --prune --recurse-submodules");

        var warningDiagnostic = new FailureDiagnostic(
            FailureCategory.StaleLockRemoved,
            RetryPolicy.NotApplicable,
            DiagnosticSeverity.Warning,
            "Stale Git lock file was removed",
            "A stale lock was found and removed before the repository completed. The run continued, but the event is kept visible for review.",
            "No retry is needed unless later operations fail again.",
            @"Removed stale Git lock file (18.0 min old): E:\FF14\Repos\MyRepos\Dalamud plugins\PluginWithAVeryLongFolderName\.git\index.lock",
            RelatedPath: Path.Combine(libraryRoot, "Dalamud plugins", "PluginWithAVeryLongFolderName"),
            RelatedCommand: "git status --porcelain=v1");

        var results = new[]
        {
            new RepositoryResultViewModel(
                "ExtremelyLongPluginName.With.Multiple.Namespace.Parts.And.LocalExperimentalBranches",
                "Dalamud plugins",
                Path.Combine(libraryRoot, "Dalamud plugins", "ExtremelyLongPluginName.With.Multiple.Namespace.Parts.And.LocalExperimentalBranches"),
                "https://github.com/example/extremely-long-plugin-name-with-many-folders-and-a-long-url-to-test-wrapping.git",
                RepositoryResultStatus.Failed,
                newCommitsCount: 0,
                elapsed: TimeSpan.FromSeconds(61.4),
                failedDiagnostic,
                [
                    "git fetch timed out after 60000 ms while contacting the remote.",
                    "Normal fetch output is available but kept inside the expandable log section."
                ]),
            new RepositoryResultViewModel(
                "PluginWithAVeryLongFolderName.ThatTriggersAStaleLockWarningButStillCompletes",
                "Dalamud plugins",
                Path.Combine(libraryRoot, "Dalamud plugins", "PluginWithAVeryLongFolderName.ThatTriggersAStaleLockWarningButStillCompletes"),
                "github-bf:sample/plugin-with-warning-and-a-very-long-scp-like-url.git",
                RepositoryResultStatus.Warning,
                newCommitsCount: 0,
                elapsed: TimeSpan.FromSeconds(8.8),
                warningDiagnostic,
                [
                    "Removed stale Git lock file before running status.",
                    "Repository completed after lock cleanup."
                ]),
            new RepositoryResultViewModel(
                "UpdatedRepo-FeatureBranchCollector",
                "Tooling",
                Path.Combine(libraryRoot, "Tooling", "UpdatedRepo-FeatureBranchCollector"),
                "https://github.com/example/updated-repo-feature-branch-collector.git",
                RepositoryResultStatus.Updated,
                newCommitsCount: 7,
                elapsed: TimeSpan.FromSeconds(12.2),
                diagnostic: null,
                [
                    "Fast-forwarded main by 7 commits.",
                    "Submodules already up to date."
                ]),
            new RepositoryResultViewModel(
                "CleanRepoWithLongButBoringName.HiddenUntilShowCleanIsEnabled",
                "Archived experiments",
                Path.Combine(libraryRoot, "Archived experiments", "CleanRepoWithLongButBoringName.HiddenUntilShowCleanIsEnabled"),
                "https://github.com/example/clean-repo-with-long-but-boring-name.git",
                RepositoryResultStatus.Clean,
                newCommitsCount: 0,
                elapsed: TimeSpan.FromSeconds(3.1),
                diagnostic: null,
                [
                    "Already up to date."
                ])
        };

        var removed = new[]
        {
            RemovedRepositoryViewModel.FromRecord(
                new RemovedRepositoryRecord
                {
                    Name = "RemovedPluginReadyToRestore",
                    Category = "Archived experiments",
                    OriginalPath = Path.Combine(libraryRoot, "Archived experiments", "RemovedPluginReadyToRestore"),
                    RemovedPath = Path.Combine(libraryRoot, ".mygitpuller", "removed", "Archived experiments", "RemovedPluginReadyToRestore"),
                    RemoteUrl = "https://github.com/example/removed-plugin-ready-to-restore.git",
                    RemovedAt = DateTimeOffset.UtcNow.AddDays(-2)
                },
                directoryExists: _ => true,
                pathExists: _ => false),
            RemovedRepositoryViewModel.FromRecord(
                new RemovedRepositoryRecord
                {
                    Name = "RemovedPluginBlockedByExistingFolder",
                    Category = "Dalamud plugins",
                    OriginalPath = Path.Combine(libraryRoot, "Dalamud plugins", "RemovedPluginBlockedByExistingFolder"),
                    RemovedPath = Path.Combine(libraryRoot, ".mygitpuller", "removed", "Dalamud plugins", "RemovedPluginBlockedByExistingFolder"),
                    RemoteUrl = "https://github.com/example/removed-plugin-blocked-by-existing-folder.git",
                    RemovedAt = DateTimeOffset.UtcNow.AddDays(-7)
                },
                directoryExists: _ => true,
                pathExists: _ => true)
        };

        return new MainShellViewModel(libraryRoot, categories, results, removed);
    }

    private void EnsureSelectedResultIsVisible()
    {
        if (SelectedResult is not null && VisibleResults.Contains(SelectedResult))
        {
            return;
        }

        SelectedResult = VisibleResults.FirstOrDefault();
    }

    private void RaiseSelectedResultPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedResultName));
        OnPropertyChanged(nameof(SelectedResultStatus));
        OnPropertyChanged(nameof(SelectedResultCategory));
        OnPropertyChanged(nameof(SelectedResultPath));
        OnPropertyChanged(nameof(SelectedResultRemoteUrl));
        OnPropertyChanged(nameof(SelectedResultSummary));
        OnPropertyChanged(nameof(SelectedResultDiagnosticExplanation));
        OnPropertyChanged(nameof(SelectedResultSuggestedAction));
        OnPropertyChanged(nameof(SelectedResultEvidence));
        OnPropertyChanged(nameof(SelectedResultRelatedCommand));
        OnPropertyChanged(nameof(SelectedResultLogLines));
        OnPropertyChanged(nameof(HasSelectedResult));
    }

    private void RaiseCommandCanExecuteChanged()
    {
        if (AddRepositoryCommand is RelayCommand addCommand)
        {
            addCommand.RaiseCanExecuteChanged();
        }

        if (RetrySelectedCommand is RelayCommand retryCommand)
        {
            retryCommand.RaiseCanExecuteChanged();
        }
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action execute;
    private readonly Func<bool> canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return canExecute();
    }

    public void Execute(object? parameter)
    {
        execute();
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

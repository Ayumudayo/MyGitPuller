using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using GitPuller;
using GitPuller_WinUI.Services;

namespace GitPuller_WinUI.ViewModels;

public sealed class MainShellViewModel : ObservableObject
{
    private readonly IGitPullerSyncService? syncService;
    private readonly IViewModelDispatcher dispatcher;
    private bool showCleanRepositories;
    private bool isRunning;
    private bool hasInitialized;
    private RepositoryResultViewModel? selectedResult;
    private CategoryNavigationItemViewModel? selectedCategory;
    private CategoryNavigationItemViewModel allRepositoriesNavigationItem;
    private CategoryNavigationItemViewModel? selectedNavigationItem;
    private string repositoryUrlToAdd = string.Empty;
    private string libraryRoot;
    private int runProgressCompleted;
    private int runProgressTotal;
    private string currentProgressMessage = "Ready";
    private string runErrorMessage = string.Empty;
    private GitPullerLibraryLoadResult? currentLibraryLoad;
    private GitPullerRunRequest? currentRunRequest;

    public MainShellViewModel(
        string libraryRoot,
        IGitPullerSyncService syncService,
        IViewModelDispatcher? dispatcher = null)
        : this(
            libraryRoot,
            categories: [],
            repositoryResults: [],
            removedRepositories: [],
            syncService,
            dispatcher)
    {
    }

    public MainShellViewModel(
        string libraryRoot,
        IEnumerable<CategoryNavigationItemViewModel> categories,
        IEnumerable<RepositoryResultViewModel> repositoryResults,
        IEnumerable<RemovedRepositoryViewModel> removedRepositories,
        IGitPullerSyncService? syncService = null,
        IViewModelDispatcher? dispatcher = null)
    {
        this.libraryRoot = string.IsNullOrWhiteSpace(libraryRoot) ? string.Empty : libraryRoot;
        this.syncService = syncService;
        this.dispatcher = dispatcher ?? ImmediateViewModelDispatcher.Instance;

        Categories = new ObservableCollection<CategoryNavigationItemViewModel>(categories);
        RepositoryResults = new ObservableCollection<RepositoryResultViewModel>(repositoryResults);
        RemovedRepositories = new ObservableCollection<RemovedRepositoryViewModel>(removedRepositories);
        allRepositoriesNavigationItem = CreateAllRepositoriesNavigationItem();
        selectedNavigationItem = allRepositoriesNavigationItem;
        selectedResult = VisibleResults.FirstOrDefault();

        Categories.CollectionChanged += Categories_CollectionChanged;
        RepositoryResults.CollectionChanged += RepositoryResults_CollectionChanged;
        RemovedRepositories.CollectionChanged += RemovedRepositories_CollectionChanged;

        AddRepositoryCommand = new RelayCommand(
            execute: () => { },
            canExecute: () => CanAddRepositoryFromUrl);
        RunSyncCommand = new AsyncRelayCommand(
            execute: () => RunSyncAsync(),
            canExecute: () => CanRunSync);
        RefreshCommand = RunSyncCommand;
        RetrySelectedCommand = new AsyncRelayCommand(
            execute: () => RetrySelectedAsync(),
            canExecute: () => CanRetrySelected);
    }

    public string LibraryRoot
    {
        get => libraryRoot;
        private set => SetProperty(ref libraryRoot, value);
    }

    public ObservableCollection<CategoryNavigationItemViewModel> Categories { get; }
    public ObservableCollection<RepositoryResultViewModel> RepositoryResults { get; }
    public ObservableCollection<RemovedRepositoryViewModel> RemovedRepositories { get; }
    public ICommand AddRepositoryCommand { get; }
    public ICommand RunSyncCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetrySelectedCommand { get; }
    public CategoryNavigationItemViewModel AllRepositoriesNavigationItem => allRepositoriesNavigationItem;
    public IReadOnlyList<CategoryNavigationItemViewModel> CategoryNavigationItems =>
        [AllRepositoriesNavigationItem, .. Categories];

    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (SetProperty(ref isRunning, value))
            {
                OnPropertyChanged(nameof(CanRunSync));
                OnPropertyChanged(nameof(RunSyncButtonText));
                OnPropertyChanged(nameof(RunStatusTitle));
                OnPropertyChanged(nameof(IsRunProgressIndeterminate));
                OnPropertyChanged(nameof(CanAddRepositoryFromUrl));
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public bool CanRunSync =>
        !IsRunning
        && syncService is not null
        && !string.IsNullOrWhiteSpace(LibraryRoot);

    public string RunSyncButtonText => IsRunning ? "Running" : "Run sync";

    public int RunProgressCompleted => runProgressCompleted;
    public int RunProgressTotal => runProgressTotal;
    public string CurrentProgressMessage => currentProgressMessage;
    public string RunErrorMessage => runErrorMessage;
    public bool HasRunError => !string.IsNullOrWhiteSpace(RunErrorMessage);
    public bool HasRunStatus => !string.IsNullOrWhiteSpace(RunStatusMessage);
    public bool HasRunInfoStatus => HasRunStatus && !HasRunError;
    public string RunStatusTitle => HasRunError
        ? "Sync failed"
        : IsRunning
            ? "Sync running"
            : "Sync status";
    public string RunStatusMessage => HasRunError ? RunErrorMessage : CurrentProgressMessage;
    public string RunProgressText => RunProgressTotal > 0
        ? $"{RunProgressCompleted} of {RunProgressTotal} repositories completed"
        : IsRunning
            ? "Scanning library"
            : "No repositories loaded";
    public bool IsRunProgressIndeterminate => IsRunning && RunProgressTotal == 0;

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
        set => SetSelectedCategory(value, updateNavigation: true);
    }

    public CategoryNavigationItemViewModel? SelectedNavigationItem
    {
        get => selectedNavigationItem;
        set => SetSelectedNavigationItem(value, updateCategory: true);
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
        !IsRunning
        && SelectedCategory is not null
        && !string.IsNullOrWhiteSpace(RepositoryUrlToAdd);

    public int FailedCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Failed);
    public int WarningCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Warning);
    public int UpdatedCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Updated);
    public int CleanCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Clean);
    public int VisibleResultCount => VisibleResults.Count;
    public int TotalResultCount => RepositoryResults.Count;
    public int RemovedRepositoryCount => RemovedRepositories.Count;
    public double RunProgress => RunProgressTotal == 0
        ? 0
        : Math.Clamp((double)RunProgressCompleted / RunProgressTotal * 100, 0, 100);
    public bool HasAttentionItems => FailedCount > 0 || WarningCount > 0;
    public string AttentionSummary => $"{FailedCount} failed, {WarningCount} warning, {UpdatedCount} updated, {CleanCount} clean.";
    public string ResultSummary => $"{VisibleResultCount} of {TotalResultCount} repositories shown";
    public string SelectedResultName => SelectedResult?.Name ?? "No repository selected";
    public string SelectedResultStatus => SelectedResult?.StatusText ?? string.Empty;
    public string SelectedResultCategory => SelectedResult?.Category ?? string.Empty;
    public string SelectedResultPath => SelectedResult?.Path ?? string.Empty;
    public string SelectedResultRemoteUrl => SelectedResult?.RemoteUrl ?? string.Empty;
    public string SelectedResultSummary => SelectedResult?.Summary ?? "Select a repository to review its result.";
    public string SelectedResultDiagnosticTitle => SelectedResult?.DiagnosticTitle ?? "No diagnostic selected";
    public string SelectedResultDiagnosticExplanation => SelectedResult?.DiagnosticExplanation ?? string.Empty;
    public string SelectedResultSuggestedAction => SelectedResult?.SuggestedAction ?? string.Empty;
    public string SelectedResultRetryPolicyText => SelectedResult?.RetryPolicyText ?? "No retry information";
    public string SelectedResultRetryPolicyDescription => SelectedResult?.RetryPolicyDescription ?? string.Empty;
    public string SelectedResultRetryButtonText => SelectedResult?.RetryButtonText ?? "Retry";
    public bool SelectedResultCanRetry => CanRetrySelected;
    public bool IsSelectedResultRetryPrimary => SelectedResult?.IsRetryPrimary == true;
    public bool IsSelectedResultRetrySecondary => SelectedResult?.IsRetrySecondary == true;
    public string SelectedResultEvidence => SelectedResult?.Evidence ?? string.Empty;
    public string SelectedResultRelatedCommand => SelectedResult?.RelatedCommand ?? string.Empty;
    public IReadOnlyList<string> SelectedResultLogLines => SelectedResult?.LogLines ?? Array.Empty<string>();
    public bool HasSelectedResult => SelectedResult is not null;

    private bool CanRetrySelected =>
        !IsRunning
        && currentRunRequest is not null
        && SelectedResult?.CanRetry == true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (hasInitialized || syncService is null)
        {
            return;
        }

        hasInitialized = true;
        ClearRunError();
        SetRunProgress(0, 0, "Scanning library...");

        try
        {
            var loadResult = await LoadLibraryForCurrentRootAsync(resetResults: false, cancellationToken);
            SetRunProgress(
                0,
                loadResult.Inventory.Repositories.Count,
                loadResult.Inventory.Repositories.Count == 0
                    ? "No repositories found in the selected library root."
                    : $"Ready to run {loadResult.Inventory.Repositories.Count} repositories.");
        }
        catch (OperationCanceledException)
        {
            SetRunError("Library scan was canceled.");
        }
        catch (Exception ex)
        {
            SetRunError(ex.Message);
        }
    }

    public async Task RunSyncAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunSync || syncService is null)
        {
            return;
        }

        IsRunning = true;
        ClearRunError();
        SetRunProgress(0, 0, "Scanning library...");

        try
        {
            var loadResult = await LoadLibraryForCurrentRootAsync(resetResults: true, cancellationToken);
            var request = loadResult.CreateRunRequest();
            currentRunRequest = request;
            SetRunProgress(0, request.Inventory.Repositories.Count, "Starting sync...");

            var runResult = await syncService.RunAllAsync(
                request,
                CreateProgress(),
                cancellationToken);
            ApplyRunCompleted(runResult);
        }
        catch (OperationCanceledException)
        {
            SetRunError("Sync was canceled.");
        }
        catch (Exception ex)
        {
            SetRunError(ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public async Task RetrySelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRetrySelected || syncService is null || currentRunRequest is null || SelectedResult is null)
        {
            return;
        }

        var resultToRetry = SelectedResult;
        IsRunning = true;
        ClearRunError();
        SetRunProgress(0, 1, $"Retrying {resultToRetry.Name}...");

        try
        {
            var retryResult = await syncService.RetryRepositoryAsync(
                currentRunRequest,
                resultToRetry.Path,
                CreateProgress(),
                cancellationToken);
            UpsertRepositoryResult(retryResult, FindRepositoryDescriptor(retryResult.Path));
            SetRunProgress(1, 1, $"Retry completed: {retryResult.Name}");
        }
        catch (OperationCanceledException)
        {
            SetRunError("Retry was canceled.");
        }
        catch (Exception ex)
        {
            SetRunError(ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public static MainShellViewModel CreateDefault(IViewModelDispatcher? dispatcher = null)
    {
        var service = new CoreGitPullerSyncService();
        return new MainShellViewModel(service.GetDefaultLibraryRoot(), service, dispatcher);
    }

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

    private async Task<GitPullerLibraryLoadResult> LoadLibraryForCurrentRootAsync(
        bool resetResults,
        CancellationToken cancellationToken)
    {
        if (syncService is null)
        {
            throw new InvalidOperationException("Sync service is not configured.");
        }

        var loadResult = await syncService.LoadLibraryAsync(LibraryRoot, cancellationToken);
        ApplyLibraryLoadResult(loadResult, resetResults);
        return loadResult;
    }

    private IProgress<GitPullerProgressEvent> CreateProgress()
    {
        return new DispatchingProgress(dispatcher, ApplyProgressEvent);
    }

    private void ApplyProgressEvent(GitPullerProgressEvent progressEvent)
    {
        switch (progressEvent.Kind)
        {
            case GitPullerProgressEventKind.RunStarted:
                SetRunProgress(
                    progressEvent.CompletedRepositories,
                    progressEvent.TotalRepositories,
                    progressEvent.Message ?? "Sync started.");
                break;

            case GitPullerProgressEventKind.RepositoryStarted:
                SetRunProgress(
                    progressEvent.CompletedRepositories,
                    progressEvent.TotalRepositories,
                    progressEvent.Repository is null
                        ? "Running repository..."
                        : $"Running {progressEvent.Repository.Name}...");
                break;

            case GitPullerProgressEventKind.RepositoryCompleted:
                if (progressEvent.RepositoryResult is not null)
                {
                    UpsertRepositoryResult(progressEvent.RepositoryResult, progressEvent.Repository);
                }

                SetRunProgress(
                    progressEvent.CompletedRepositories,
                    progressEvent.TotalRepositories,
                    progressEvent.Repository is null
                        ? "Repository completed."
                        : $"Completed {progressEvent.Repository.Name}.");
                break;

            case GitPullerProgressEventKind.RunCompleted:
                if (progressEvent.RunResult is not null)
                {
                    ApplyRunCompleted(progressEvent.RunResult);
                }
                break;
        }
    }

    private void ApplyRunCompleted(GitPullerRunResult runResult)
    {
        foreach (var result in runResult.RepositoryResults)
        {
            UpsertRepositoryResult(result, FindRepositoryDescriptor(result.Path));
        }

        if (!string.IsNullOrWhiteSpace(runResult.ErrorMessage))
        {
            SetRunError(runResult.ErrorMessage);
        }

        SetRunProgress(
            runResult.TotalRepositories,
            runResult.TotalRepositories,
            runResult.HasFailures ? "Sync completed with items to review." : "Sync completed.");
    }

    private void ApplyLibraryLoadResult(GitPullerLibraryLoadResult loadResult, bool resetResults)
    {
        currentLibraryLoad = loadResult;
        currentRunRequest = loadResult.CreateRunRequest();
        LibraryRoot = loadResult.LibraryRoot;
        RefreshCategoryNavigationItems();
        ReplaceRemovedRepositories(loadResult.RemovedRepositories);

        if (resetResults)
        {
            RepositoryResults.Clear();
            SelectedResult = null;
        }
    }

    private void ReplaceRemovedRepositories(IEnumerable<RemovedRepositoryRecord> removedRepositories)
    {
        RemovedRepositories.Clear();
        foreach (var record in removedRepositories)
        {
            RemovedRepositories.Add(RemovedRepositoryViewModel.FromRecord(record));
        }
    }

    private void UpsertRepositoryResult(RepoResult result, RepositoryDescriptor? repository)
    {
        var viewModel = RepositoryResultViewModel.FromResult(result, repository);
        var existingIndex = IndexOfResult(viewModel.Path);
        var shouldSelect = SelectedResult is null
            || PathsEqual(SelectedResult.Path, viewModel.Path);

        if (existingIndex >= 0)
        {
            RepositoryResults[existingIndex] = viewModel;
        }
        else
        {
            RepositoryResults.Add(viewModel);
        }

        if (shouldSelect)
        {
            SelectedResult = viewModel;
        }
    }

    private int IndexOfResult(string path)
    {
        for (var index = 0; index < RepositoryResults.Count; index++)
        {
            if (PathsEqual(RepositoryResults[index].Path, path))
            {
                return index;
            }
        }

        return -1;
    }

    private RepositoryDescriptor? FindRepositoryDescriptor(string path)
    {
        return currentRunRequest?.Inventory.Repositories.FirstOrDefault(repository =>
            PathsEqual(repository.Path, path));
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizePathForComparison(left),
            NormalizePathForComparison(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathForComparison(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private void SetRunProgress(int completedRepositories, int totalRepositories, string message)
    {
        runProgressCompleted = Math.Max(0, completedRepositories);
        runProgressTotal = Math.Max(0, totalRepositories);
        currentProgressMessage = message;
        OnPropertyChanged(nameof(RunProgressCompleted));
        OnPropertyChanged(nameof(RunProgressTotal));
        OnPropertyChanged(nameof(CurrentProgressMessage));
        OnPropertyChanged(nameof(RunProgress));
        OnPropertyChanged(nameof(RunProgressText));
        OnPropertyChanged(nameof(IsRunProgressIndeterminate));
        OnPropertyChanged(nameof(HasRunStatus));
        OnPropertyChanged(nameof(HasRunInfoStatus));
        OnPropertyChanged(nameof(RunStatusMessage));
        OnPropertyChanged(nameof(RunStatusTitle));
    }

    private void SetRunError(string message)
    {
        runErrorMessage = string.IsNullOrWhiteSpace(message)
            ? "The sync failed without an error message."
            : message;
        OnPropertyChanged(nameof(RunErrorMessage));
        OnPropertyChanged(nameof(HasRunError));
        OnPropertyChanged(nameof(HasRunStatus));
        OnPropertyChanged(nameof(HasRunInfoStatus));
        OnPropertyChanged(nameof(RunStatusMessage));
        OnPropertyChanged(nameof(RunStatusTitle));
    }

    private void ClearRunError()
    {
        if (string.IsNullOrEmpty(runErrorMessage))
        {
            return;
        }

        runErrorMessage = string.Empty;
        OnPropertyChanged(nameof(RunErrorMessage));
        OnPropertyChanged(nameof(HasRunError));
        OnPropertyChanged(nameof(HasRunStatus));
        OnPropertyChanged(nameof(HasRunInfoStatus));
        OnPropertyChanged(nameof(RunStatusMessage));
        OnPropertyChanged(nameof(RunStatusTitle));
    }

    private void SetSelectedCategory(CategoryNavigationItemViewModel? value, bool updateNavigation)
    {
        var normalizedValue = value is null
            ? null
            : Categories.FirstOrDefault(category =>
                ReferenceEquals(category, value)
                || string.Equals(category.Name, value.Name, StringComparison.OrdinalIgnoreCase));

        if (SetProperty(ref selectedCategory, normalizedValue, nameof(SelectedCategory)))
        {
            RaiseCategorySelectionDerivedPropertiesChanged();

            if (updateNavigation)
            {
                SetSelectedNavigationItem(normalizedValue ?? AllRepositoriesNavigationItem, updateCategory: false);
            }
        }
    }

    private void SetSelectedNavigationItem(CategoryNavigationItemViewModel? value, bool updateCategory)
    {
        var normalizedValue = value?.IsAllRepositories == true
            ? AllRepositoriesNavigationItem
            : value is null
                ? AllRepositoriesNavigationItem
                : Categories.FirstOrDefault(category =>
                    ReferenceEquals(category, value)
                    || string.Equals(category.Name, value.Name, StringComparison.OrdinalIgnoreCase))
                    ?? AllRepositoriesNavigationItem;

        if (SetProperty(ref selectedNavigationItem, normalizedValue, nameof(SelectedNavigationItem)) && updateCategory)
        {
            SetSelectedCategory(normalizedValue.IsAllRepositories ? null : normalizedValue, updateNavigation: false);
        }
    }

    private void EnsureSelectedResultIsVisible()
    {
        if (SelectedResult is not null && VisibleResults.Contains(SelectedResult))
        {
            return;
        }

        SelectedResult = VisibleResults.FirstOrDefault();
    }

    private void Categories_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CategoryNavigationItems));

        if (SelectedCategory is not null && !Categories.Contains(SelectedCategory))
        {
            SetSelectedCategory(null, updateNavigation: true);
        }
    }

    private void RepositoryResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshCategoryNavigationItems();
        RefreshAllRepositoriesNavigationItem();
        RaiseResultDerivedPropertiesChanged();
        EnsureSelectedResultIsVisible();
    }

    private void RemovedRepositories_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RemovedRepositoryCount));
    }

    private CategoryNavigationItemViewModel CreateAllRepositoriesNavigationItem()
    {
        return new CategoryNavigationItemViewModel(
            "All repositories",
            LibraryRoot,
            currentRunRequest?.Inventory.Repositories.Count ?? TotalResultCount,
            FailedCount + WarningCount,
            IsAllRepositories: true);
    }

    private void RefreshAllRepositoriesNavigationItem()
    {
        var wasAllSelected = selectedNavigationItem?.IsAllRepositories == true;
        allRepositoriesNavigationItem = CreateAllRepositoriesNavigationItem();
        OnPropertyChanged(nameof(AllRepositoriesNavigationItem));
        OnPropertyChanged(nameof(CategoryNavigationItems));

        if (wasAllSelected)
        {
            selectedNavigationItem = allRepositoriesNavigationItem;
            OnPropertyChanged(nameof(SelectedNavigationItem));
        }
    }

    private void RefreshCategoryNavigationItems()
    {
        if (currentLibraryLoad is null)
        {
            return;
        }

        var selectedName = SelectedCategory?.Name;
        var categoryNames = currentLibraryLoad.ConfiguredCategories
            .Concat(currentLibraryLoad.Inventory.Repositories.Select(repository => NormalizeCategoryName(repository.Category)))
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Categories.CollectionChanged -= Categories_CollectionChanged;
        try
        {
            Categories.Clear();
            foreach (var categoryName in categoryNames)
            {
                var repositoryCount = currentLibraryLoad.Inventory.Repositories.Count(repository =>
                    string.Equals(NormalizeCategoryName(repository.Category), categoryName, StringComparison.OrdinalIgnoreCase));
                var attentionCount = RepositoryResults.Count(result =>
                    string.Equals(result.Category, categoryName, StringComparison.OrdinalIgnoreCase)
                    && (result.Status == RepositoryResultStatus.Failed || result.Status == RepositoryResultStatus.Warning));
                Categories.Add(new CategoryNavigationItemViewModel(
                    categoryName,
                    GetCategoryFullPath(categoryName),
                    repositoryCount,
                    attentionCount));
            }
        }
        finally
        {
            Categories.CollectionChanged += Categories_CollectionChanged;
        }

        OnPropertyChanged(nameof(CategoryNavigationItems));

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            SetSelectedCategory(
                Categories.FirstOrDefault(category => string.Equals(category.Name, selectedName, StringComparison.OrdinalIgnoreCase)),
                updateNavigation: true);
        }
        else
        {
            RefreshAllRepositoriesNavigationItem();
        }
    }

    private static string NormalizeCategoryName(string? category)
    {
        return string.IsNullOrWhiteSpace(category) ? "(uncategorized)" : category.Trim();
    }

    private string GetCategoryFullPath(string categoryName)
    {
        if (string.Equals(categoryName, "(uncategorized)", StringComparison.OrdinalIgnoreCase))
        {
            return LibraryRoot;
        }

        var pathParts = categoryName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return pathParts.Length == 0
            ? LibraryRoot
            : Path.Combine(new[] { LibraryRoot }.Concat(pathParts).ToArray());
    }

    private void RaiseCategorySelectionDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(CanAddRepositoryFromUrl));
        OnPropertyChanged(nameof(SelectedCategoryName));
        OnPropertyChanged(nameof(VisibleResults));
        OnPropertyChanged(nameof(VisibleResultCount));
        OnPropertyChanged(nameof(ResultSummary));
        RaiseCommandCanExecuteChanged();
        EnsureSelectedResultIsVisible();
    }

    private void RaiseResultDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(VisibleResults));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(UpdatedCount));
        OnPropertyChanged(nameof(CleanCount));
        OnPropertyChanged(nameof(VisibleResultCount));
        OnPropertyChanged(nameof(TotalResultCount));
        OnPropertyChanged(nameof(HasAttentionItems));
        OnPropertyChanged(nameof(AttentionSummary));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void RaiseSelectedResultPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedResultName));
        OnPropertyChanged(nameof(SelectedResultStatus));
        OnPropertyChanged(nameof(SelectedResultCategory));
        OnPropertyChanged(nameof(SelectedResultPath));
        OnPropertyChanged(nameof(SelectedResultRemoteUrl));
        OnPropertyChanged(nameof(SelectedResultSummary));
        OnPropertyChanged(nameof(SelectedResultDiagnosticTitle));
        OnPropertyChanged(nameof(SelectedResultDiagnosticExplanation));
        OnPropertyChanged(nameof(SelectedResultSuggestedAction));
        OnPropertyChanged(nameof(SelectedResultRetryPolicyText));
        OnPropertyChanged(nameof(SelectedResultRetryPolicyDescription));
        OnPropertyChanged(nameof(SelectedResultRetryButtonText));
        OnPropertyChanged(nameof(SelectedResultCanRetry));
        OnPropertyChanged(nameof(IsSelectedResultRetryPrimary));
        OnPropertyChanged(nameof(IsSelectedResultRetrySecondary));
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

        if (RunSyncCommand is AsyncRelayCommand runCommand)
        {
            runCommand.RaiseCanExecuteChanged();
        }

        if (RetrySelectedCommand is AsyncRelayCommand retryCommand)
        {
            retryCommand.RaiseCanExecuteChanged();
        }

        OnPropertyChanged(nameof(SelectedResultCanRetry));
    }
}

public interface IViewModelDispatcher
{
    void Enqueue(Action action);
}

public sealed class ImmediateViewModelDispatcher : IViewModelDispatcher
{
    public static ImmediateViewModelDispatcher Instance { get; } = new();

    private ImmediateViewModelDispatcher()
    {
    }

    public void Enqueue(Action action)
    {
        action();
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
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;

    public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return canExecute();
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        await execute();
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class DispatchingProgress : IProgress<GitPullerProgressEvent>
{
    private readonly IViewModelDispatcher dispatcher;
    private readonly Action<GitPullerProgressEvent> report;

    public DispatchingProgress(
        IViewModelDispatcher dispatcher,
        Action<GitPullerProgressEvent> report)
    {
        this.dispatcher = dispatcher;
        this.report = report;
    }

    public void Report(GitPullerProgressEvent value)
    {
        dispatcher.Enqueue(() => report(value));
    }
}

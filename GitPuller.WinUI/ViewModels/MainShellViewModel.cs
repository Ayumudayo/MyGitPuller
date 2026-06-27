using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using GitPuller;
using GitPuller_WinUI.Services;

namespace GitPuller_WinUI.ViewModels;

public sealed class MainShellViewModel : ObservableObject
{
    private readonly IGitPullerSyncService? syncService;
    private readonly IRepositoryManagementService? repositoryManagementService;
    private readonly IFileSystemLauncher? launcher;
    private readonly IAppSettingsService? appSettingsService;
    private readonly IRunStateStore? runStateStore;
    private readonly IRemoteLinkBuilder remoteLinkBuilder;
    private readonly IViewModelDispatcher dispatcher;
    private bool showCleanRepositories;
    private bool isRunning;
    private bool isRepositoryManagementBusy;
    private bool hasInitialized;
    private bool deferRepositoryNavigationRefresh;
    private bool repositoryNavigationRefreshPending;
    private RepositoryResultViewModel? selectedResult;
    private IReadOnlyList<RepositoryResultViewModel>? visibleResultsCache;
    private string selectedResultPath = string.Empty;
    private CategoryNavigationItemViewModel? selectedCategory;
    private RepositoryTreeNodeViewModel? selectedFolderNode;
    private RepositoryTreeNodeViewModel? selectedTreeNode;
    private CategoryNavigationItemViewModel allRepositoriesNavigationItem;
    private CategoryNavigationItemViewModel? selectedNavigationItem;
    private RepositoryResultFilter selectedResultFilter;
    private readonly HashSet<string> selectedRetryIssuePaths = new(StringComparer.OrdinalIgnoreCase);
    private string repositorySearchText = string.Empty;
    private string repositoryUrlToAdd = string.Empty;
    private string libraryRoot;
    private string? latestReportPath;
    private bool latestReportPathResolved;
    private int runProgressCompleted;
    private int runProgressTotal;
    private DateTimeOffset lastSyncCompletedAt;
    private DateTimeOffset currentRunStartedAt;
    private bool lastRunWasInterrupted;
    private bool lastRunWasCanceled;
    private string currentProgressMessage = "Ready";
    private string runErrorMessage = string.Empty;
    private GitPullerLibraryLoadResult? currentLibraryLoad;
    private GitPullerRunRequest? currentRunRequest;
    private bool runCompletionApplied;
    private RepositoryAddPreview? addRepositoryPreview;
    private string addRepositoryUrl = string.Empty;
    private string addRepositoryCategoryName = string.Empty;
    private string addRepositoryFolderName = string.Empty;
    private string addRepositoryTargetPathPreview = string.Empty;
    private string addRepositoryDiagnosticTitle = "Enter a repository URL to preview the target path.";
    private string addRepositoryDiagnosticExplanation = string.Empty;
    private string addRepositoryDiagnosticEvidence = string.Empty;
    private string addRepositoryStatusMessage = string.Empty;
    private string addRepositoryErrorMessage = string.Empty;
    private string advancedOptionsStatusMessage = string.Empty;
    private string advancedOptionsErrorMessage = string.Empty;
    private string removedRepositoryStatusMessage = string.Empty;
    private string removedRepositoryErrorMessage = string.Empty;
    private string launchStatusMessage = string.Empty;
    private string launchErrorMessage = string.Empty;
    private GitPullerOptions advancedOptionsBase = new();
    private int advancedWorkers = new GitPullerOptions().MaxDegreeOfParallelism;
    private int advancedTimeoutSeconds = new GitPullerOptions().GitTimeoutMilliseconds / 1000;
    private bool advancedSyncAllBranches = new GitPullerOptions().SyncAllBranches;
    private int advancedStaleLockMinutes = (int)new GitPullerOptions().StaleGitLockAge.TotalMinutes;
    private bool advancedNoStaleLockCleanup = !new GitPullerOptions().StaleGitLockCleanup;
    private bool advancedVerboseReport = new GitPullerOptions().VerboseReport;
    private bool advancedInitMissingSubmodules = new GitPullerOptions().InitMissingSubmodules;

    public MainShellViewModel(
        string libraryRoot,
        IGitPullerSyncService syncService,
        IViewModelDispatcher? dispatcher = null,
        IRepositoryManagementService? repositoryManagementService = null,
        IFileSystemLauncher? launcher = null,
        IAppSettingsService? appSettingsService = null,
        IRunStateStore? runStateStore = null,
        IRemoteLinkBuilder? remoteLinkBuilder = null)
        : this(
            libraryRoot,
            categories: [],
            repositoryResults: [],
            removedRepositories: [],
            syncService,
            dispatcher,
            repositoryManagementService,
            launcher,
            appSettingsService,
            runStateStore,
            remoteLinkBuilder)
    {
    }

    public MainShellViewModel(
        string libraryRoot,
        IEnumerable<CategoryNavigationItemViewModel> categories,
        IEnumerable<RepositoryResultViewModel> repositoryResults,
        IEnumerable<RemovedRepositoryViewModel> removedRepositories,
        IGitPullerSyncService? syncService = null,
        IViewModelDispatcher? dispatcher = null,
        IRepositoryManagementService? repositoryManagementService = null,
        IFileSystemLauncher? launcher = null,
        IAppSettingsService? appSettingsService = null,
        IRunStateStore? runStateStore = null,
        IRemoteLinkBuilder? remoteLinkBuilder = null)
    {
        this.libraryRoot = string.IsNullOrWhiteSpace(libraryRoot) ? string.Empty : libraryRoot;
        this.syncService = syncService;
        this.repositoryManagementService = repositoryManagementService;
        this.launcher = launcher;
        this.appSettingsService = appSettingsService;
        this.runStateStore = runStateStore;
        this.remoteLinkBuilder = remoteLinkBuilder ?? RemoteLinkBuilder.Instance;
        this.dispatcher = dispatcher ?? ImmediateViewModelDispatcher.Instance;

        Categories = new ObservableCollection<CategoryNavigationItemViewModel>(categories);
        RepositoryTreeNodes = new ObservableCollection<RepositoryTreeNodeViewModel>();
        RepositoryResults = new ObservableCollection<RepositoryResultViewModel>(repositoryResults);
        RemovedRepositories = new ObservableCollection<RemovedRepositoryViewModel>(removedRepositories);
        RecentLibraryRoots = new ObservableCollection<string>(CreateRecentLibraryRootList(this.libraryRoot));
        allRepositoriesNavigationItem = CreateAllRepositoriesNavigationItem();
        selectedNavigationItem = allRepositoriesNavigationItem;
        selectedResult = VisibleResults.FirstOrDefault();
        selectedResultPath = NormalizePathForComparison(selectedResult?.Path ?? string.Empty);

        Categories.CollectionChanged += Categories_CollectionChanged;
        RepositoryResults.CollectionChanged += RepositoryResults_CollectionChanged;
        RemovedRepositories.CollectionChanged += RemovedRepositories_CollectionChanged;
        RefreshRepositoryTreeNodes();

        AddRepositoryCommand = new RelayCommand(
            execute: () => { },
            canExecute: () => CanAddRepositoryFromUrl);
        CloneRepositoryCommand = new AsyncRelayCommand(
            execute: () => CloneRepositoryAsync(),
            canExecute: () => CanCloneRepository);
        SaveAdvancedOptionsCommand = new AsyncRelayCommand(
            execute: () => SaveAdvancedOptionsAsync(),
            canExecute: () => CanSaveAdvancedOptions);
        OpenSelectedRepositoryFolderCommand = new AsyncRelayCommand(
            execute: () => OpenSelectedRepositoryFolderAsync(),
            canExecute: () => CanOpenSelectedRepositoryFolder);
        OpenSelectedRemoteCommand = new AsyncRelayCommand(
            execute: () => OpenSelectedRemoteAsync(),
            canExecute: () => CanOpenSelectedRemote);
        OpenLibraryFolderCommand = new AsyncRelayCommand(
            execute: () => OpenLibraryFolderAsync(),
            canExecute: () => CanOpenLibraryFolder);
        OpenLatestReportCommand = new AsyncRelayCommand(
            execute: () => OpenLatestReportAsync(),
            canExecute: () => CanOpenLatestReport);
        RunSyncCommand = new AsyncRelayCommand(
            execute: () => RunSyncAsync(),
            canExecute: () => CanRunSync);
        RefreshCommand = RunSyncCommand;
        RetrySelectedCommand = new AsyncRelayCommand(
            execute: () => RetrySelectedAsync(),
            canExecute: () => CanRetrySelected);
        RetrySelectedIssuesCommand = new AsyncRelayCommand(
            execute: () => RetrySelectedIssuesAsync(),
            canExecute: () => CanRetrySelectedIssues);
    }

    public string LibraryRoot
    {
        get => libraryRoot;
        private set
        {
            if (SetProperty(ref libraryRoot, value))
            {
                OnPropertyChanged(nameof(CanOpenLibraryFolder));
                OnPropertyChanged(nameof(LatestReportPath));
                OnPropertyChanged(nameof(CanOpenLatestReport));
            }
        }
    }

    public ObservableCollection<CategoryNavigationItemViewModel> Categories { get; }
    public ObservableCollection<RepositoryTreeNodeViewModel> RepositoryTreeNodes { get; }
    public ObservableCollection<RepositoryResultViewModel> RepositoryResults { get; }
    public ObservableCollection<RemovedRepositoryViewModel> RemovedRepositories { get; }
    public ObservableCollection<string> RecentLibraryRoots { get; }
    public ICommand AddRepositoryCommand { get; }
    public ICommand CloneRepositoryCommand { get; }
    public ICommand SaveAdvancedOptionsCommand { get; }
    public ICommand OpenSelectedRepositoryFolderCommand { get; }
    public ICommand OpenSelectedRemoteCommand { get; }
    public ICommand OpenLibraryFolderCommand { get; }
    public ICommand OpenLatestReportCommand { get; }
    public ICommand RunSyncCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetrySelectedCommand { get; }
    public ICommand RetrySelectedIssuesCommand { get; }
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
                if (!value && selectedResult is null)
                {
                    selectedResultPath = string.Empty;
                }

                OnPropertyChanged(nameof(CanRunSync));
                OnPropertyChanged(nameof(RunSyncButtonText));
                OnPropertyChanged(nameof(RunStatusTitle));
                OnPropertyChanged(nameof(IsRunProgressIndeterminate));
                OnPropertyChanged(nameof(RunCompletionStatusText));
                OnPropertyChanged(nameof(FooterRunStateText));
                RaiseRunStatusIndicatorPropertiesChanged();
                OnPropertyChanged(nameof(CanAddRepositoryFromUrl));
                OnPropertyChanged(nameof(CanCloneRepository));
                OnPropertyChanged(nameof(CanSaveAdvancedOptions));
                OnPropertyChanged(nameof(CanChangeLibraryRoot));
                RaiseRetryIssueSelectionPropertiesChanged();
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public bool IsRepositoryManagementBusy
    {
        get => isRepositoryManagementBusy;
        private set
        {
            if (SetProperty(ref isRepositoryManagementBusy, value))
            {
                OnPropertyChanged(nameof(CanCloneRepository));
                OnPropertyChanged(nameof(CanSaveAdvancedOptions));
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
        ? lastRunWasCanceled
            ? "Sync canceled"
            : lastRunWasInterrupted
            ? "Sync interrupted"
            : "Sync failed"
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
    public RunStatusIndicatorKind CurrentRunStatusIndicatorKind
    {
        get
        {
            if (HasRunError)
            {
                if (lastRunWasCanceled)
                {
                    return RunStatusIndicatorKind.Canceled;
                }

                return lastRunWasInterrupted
                    ? RunStatusIndicatorKind.Interrupted
                    : RunStatusIndicatorKind.Failed;
            }

            if (IsRunning)
            {
                return RunStatusIndicatorKind.Running;
            }

            if (lastSyncCompletedAt == default)
            {
                return RunStatusIndicatorKind.Ready;
            }

            return HasAttentionItems
                ? RunStatusIndicatorKind.ReviewRequired
                : RunStatusIndicatorKind.Completed;
        }
    }

    public RunStatusIndicatorViewModel RunCompletionStatusIndicator =>
        new(RunCompletionStatusText, CurrentRunStatusIndicatorKind);
    public RunStatusIndicatorViewModel FooterRunStateIndicator =>
        new(FooterRunStateText, CurrentRunStatusIndicatorKind);

    public bool ShowCleanRepositories
    {
        get => showCleanRepositories;
        set
        {
            if (SetProperty(ref showCleanRepositories, value))
            {
                InvalidateVisibleResults();
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
                UpdateSelectedResultPath(value);
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

    public RepositoryTreeNodeViewModel? SelectedFolderNode
    {
        get => selectedFolderNode;
        set => SetSelectedFolderNode(value);
    }

    public RepositoryTreeNodeViewModel? SelectedTreeNode
    {
        get => selectedTreeNode;
        set => SetSelectedTreeNode(value);
    }

    public RepositoryResultFilter SelectedResultFilter
    {
        get => selectedResultFilter;
        set
        {
            if (SetProperty(ref selectedResultFilter, value))
            {
                if (value == RepositoryResultFilter.Retryable)
                {
                    EnsureRetryableIssueSelection();
                }

                RaiseResultFilterDerivedPropertiesChanged();
            }
        }
    }

    public string RepositorySearchText
    {
        get => repositorySearchText;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (SetProperty(ref repositorySearchText, normalizedValue))
            {
                RaiseResultFilterDerivedPropertiesChanged();
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

    public string AddRepositoryUrl
    {
        get => addRepositoryUrl;
        set
        {
            if (SetProperty(ref addRepositoryUrl, value))
            {
                UpdateAddRepositoryPreview();
            }
        }
    }

    public string AddRepositoryCategoryName
    {
        get => addRepositoryCategoryName;
        set
        {
            if (SetProperty(ref addRepositoryCategoryName, value))
            {
                UpdateAddRepositoryPreview();
            }
        }
    }

    public string AddRepositoryFolderName
    {
        get => addRepositoryFolderName;
        set
        {
            if (SetProperty(ref addRepositoryFolderName, value))
            {
                UpdateAddRepositoryPreview();
            }
        }
    }

    public string AddRepositoryTargetPathPreview => addRepositoryTargetPathPreview;
    public string AddRepositoryDiagnosticTitle => addRepositoryDiagnosticTitle;
    public string AddRepositoryDiagnosticExplanation => addRepositoryDiagnosticExplanation;
    public string AddRepositoryDiagnosticEvidence => addRepositoryDiagnosticEvidence;
    public string AddRepositoryStatusMessage => addRepositoryStatusMessage;
    public string AddRepositoryErrorMessage => addRepositoryErrorMessage;
    public bool HasAddRepositoryStatus => !string.IsNullOrWhiteSpace(AddRepositoryStatusMessage);
    public bool HasAddRepositoryError => !string.IsNullOrWhiteSpace(AddRepositoryErrorMessage);
    public bool HasAddRepositoryDiagnostic => !string.IsNullOrWhiteSpace(AddRepositoryDiagnosticTitle)
        || !string.IsNullOrWhiteSpace(AddRepositoryDiagnosticExplanation)
        || !string.IsNullOrWhiteSpace(AddRepositoryDiagnosticEvidence);
    public bool CanCloneRepository =>
        !IsRunning
        && !IsRepositoryManagementBusy
        && repositoryManagementService is not null
        && addRepositoryPreview?.IsValid == true;

    public int AdvancedWorkers
    {
        get => advancedWorkers;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetProperty(ref advancedWorkers, normalized))
            {
                OnAdvancedOptionChanged();
            }
        }
    }

    public int AdvancedTimeoutSeconds
    {
        get => advancedTimeoutSeconds;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetProperty(ref advancedTimeoutSeconds, normalized))
            {
                OnAdvancedOptionChanged();
            }
        }
    }

    public bool AdvancedSyncAllBranches
    {
        get => advancedSyncAllBranches;
        set
        {
            if (SetProperty(ref advancedSyncAllBranches, value))
            {
                OnAdvancedOptionChanged();
            }
        }
    }

    public int AdvancedStaleLockMinutes
    {
        get => advancedStaleLockMinutes;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetProperty(ref advancedStaleLockMinutes, normalized))
            {
                OnAdvancedOptionChanged();
            }
        }
    }

    public bool AdvancedNoStaleLockCleanup
    {
        get => advancedNoStaleLockCleanup;
        set
        {
            if (SetProperty(ref advancedNoStaleLockCleanup, value))
            {
                OnAdvancedOptionChanged();
            }
        }
    }

    public bool AdvancedVerboseReport
    {
        get => advancedVerboseReport;
        set
        {
            if (SetProperty(ref advancedVerboseReport, value))
            {
                OnAdvancedOptionChanged();
            }
        }
    }

    public bool AdvancedInitMissingSubmodules
    {
        get => advancedInitMissingSubmodules;
        set
        {
            if (SetProperty(ref advancedInitMissingSubmodules, value))
            {
                OnAdvancedOptionChanged();
            }
        }
    }

    public string AdvancedOptionsStatusMessage => advancedOptionsStatusMessage;
    public string AdvancedOptionsErrorMessage => advancedOptionsErrorMessage;
    public bool HasAdvancedOptionsStatus => !string.IsNullOrWhiteSpace(AdvancedOptionsStatusMessage);
    public bool HasAdvancedOptionsError => !string.IsNullOrWhiteSpace(AdvancedOptionsErrorMessage);
    public bool CanSaveAdvancedOptions =>
        !IsRunning
        && !IsRepositoryManagementBusy
        && repositoryManagementService is not null
        && !string.IsNullOrWhiteSpace(LibraryRoot);
    public bool CanChangeLibraryRoot =>
        !IsRunning
        && syncService is not null;

    public string RemovedRepositoryStatusMessage => removedRepositoryStatusMessage;
    public string RemovedRepositoryErrorMessage => removedRepositoryErrorMessage;
    public bool HasRemovedRepositoryStatus => !string.IsNullOrWhiteSpace(RemovedRepositoryStatusMessage);
    public bool HasRemovedRepositoryError => !string.IsNullOrWhiteSpace(RemovedRepositoryErrorMessage);

    public string LaunchStatusMessage => launchStatusMessage;
    public string LaunchErrorMessage => launchErrorMessage;
    public bool HasLaunchStatus => !string.IsNullOrWhiteSpace(LaunchStatusMessage);
    public bool HasLaunchError => !string.IsNullOrWhiteSpace(LaunchErrorMessage);
    public string LatestReportPath => !latestReportPathResolved
        ? Path.Combine(LibraryRoot, GitPullerReportWriter.LatestReportFileName)
        : string.IsNullOrWhiteSpace(latestReportPath)
            ? string.Empty
            : latestReportPath;

    private string? OpenableLatestReportPath => !latestReportPathResolved
        ? Path.Combine(LibraryRoot, GitPullerReportWriter.LatestReportFileName)
        : latestReportPath;
    public bool CanOpenSelectedRepositoryFolder =>
        launcher is not null
        && !string.IsNullOrWhiteSpace(SelectedResult?.Path);
    public bool CanOpenSelectedRemote =>
        launcher is not null
        && remoteLinkBuilder.TryBuildBrowserUrl(SelectedResult?.RemoteUrl, out _);
    public bool CanOpenLibraryFolder =>
        launcher is not null
        && !string.IsNullOrWhiteSpace(LibraryRoot);
    public bool CanOpenLatestReport =>
        launcher is not null
        && !string.IsNullOrWhiteSpace(OpenableLatestReportPath)
        && File.Exists(OpenableLatestReportPath);

    public IReadOnlyList<RepositoryResultViewModel> VisibleResults => visibleResultsCache ??= RepositoryResults
            .Where(ResultMatchesSelectedFolder)
            .Where(ResultMatchesSelectedStatusFilter)
            .Where(ResultMatchesRepositorySearchText)
            .OrderBy(result => GetStatusSortOrder(result.Status))
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string SelectedCategoryName => SelectedFolderNode?.IsAllRepositories == false
        ? SelectedFolderNode.FullCategoryName
        : SelectedFolderNode?.Name
            ?? SelectedCategory?.Name
            ?? "All repositories";
    public bool CanAddRepositoryFromUrl =>
        !IsRunning
        && SelectedCategory is not null
        && !string.IsNullOrWhiteSpace(RepositoryUrlToAdd);

    public int FailedCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Failed);
    public int WarningCount => RepositoryResults.Count(result => result.Status == RepositoryResultStatus.Warning);
    public int RetryableCount => RepositoryResults.Count(IsRetryableIssue);
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
    public string AllFilterText => $"All {TotalResultCount}";
    public string FailedFilterText => $"Failed {FailedCount}";
    public string WarningFilterText => $"Warning {WarningCount}";
    public string RetryableFilterText => $"Retryable {RetryableCount}";
    public string UpdatedFilterText => $"Updated {UpdatedCount}";
    public string CleanFilterText => $"Clean {CleanCount}";
    public bool IsAllFilterSelected => SelectedResultFilter == RepositoryResultFilter.All;
    public bool IsFailedFilterSelected => SelectedResultFilter == RepositoryResultFilter.Failed;
    public bool IsWarningFilterSelected => SelectedResultFilter == RepositoryResultFilter.Warning;
    public bool IsRetryableFilterSelected => SelectedResultFilter == RepositoryResultFilter.Retryable;
    public bool IsUpdatedFilterSelected => SelectedResultFilter == RepositoryResultFilter.Updated;
    public bool IsCleanFilterSelected => SelectedResultFilter == RepositoryResultFilter.Clean;
    public string LastSyncCompletedText => lastSyncCompletedAt == default
        ? "-"
        : lastSyncCompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string RunCompletionStatusText => HasRunError
        ? lastRunWasCanceled
            ? "Canceled"
            : lastRunWasInterrupted
            ? "Interrupted"
            : "Failed"
        : IsRunning
            ? "Running"
            : lastSyncCompletedAt == default
                ? "Ready"
                : "Completed";
    public string FooterSummaryText => TotalResultCount == 1
        ? "1 repository"
        : $"{TotalResultCount} repositories";
    public string UpdatedFooterText => $"{UpdatedCount} updated";
    public string CleanFooterText => $"{CleanCount} clean";
    public string WarningFooterText => $"{WarningCount} warning";
    public string FailedFooterText => $"{FailedCount} failed";
    public string FooterRunStateText => HasRunError
        ? lastRunWasCanceled
            ? "Canceled"
            : lastRunWasInterrupted
            ? "Interrupted"
            : "Needs review"
        : IsRunning
            ? RunProgressText
            : HasAttentionItems
                ? "Review required"
                : "All up to date";
    public string SelectedResultName => SelectedResult?.Name ?? "No repository selected";
    public string SelectedResultStatus => SelectedResult?.StatusText ?? string.Empty;
    public string SelectedResultCategory => SelectedResult?.Category ?? string.Empty;
    public string SelectedResultPath => SelectedResult?.Path ?? string.Empty;
    public string SelectedResultRemoteUrl => SelectedResult?.RemoteUrl ?? string.Empty;
    public string SelectedResultCurrentText => SelectedResult?.CurrentText ?? "-";
    public string SelectedResultLastUpdatedText => SelectedResult?.LastUpdatedText ?? "-";
    public string SelectedResultTrackingText => "origin/main";
    public string SelectedResultSummary => SelectedResult?.Summary ?? "Select a repository to review its result.";
    public string SelectedResultDiagnosticTitle => SelectedResult?.DiagnosticTitle ?? "No diagnostic selected";
    public string SelectedResultDiagnosticExplanation => SelectedResult?.DiagnosticExplanation ?? string.Empty;
    public string SelectedResultSuggestedAction => SelectedResult?.SuggestedAction ?? string.Empty;
    public string SelectedResultRetryPolicyText => SelectedResult?.RetryPolicyText ?? "No retry information";
    public string SelectedResultRetryPolicyDescription => SelectedResult?.RetryPolicyDescription ?? string.Empty;
    public string SelectedResultRetryButtonText => SelectedResult?.RetryButtonText ?? "Retry";
    public string SelectedResultRetryToolTipText => GetSelectedResultRetryToolTipText();
    public bool SelectedResultCanRetry => CanRetrySelected;
    public int SelectedRetryIssueCount => RetryableResults.Count(result =>
        selectedRetryIssuePaths.Contains(NormalizePathForComparison(result.Path)));
    public bool HasSelectedRetryIssues => SelectedRetryIssueCount > 0;
    public string RetrySelectedIssuesButtonText => $"Retry selected ({SelectedRetryIssueCount})";
    public string RetrySelectedIssuesToolTipText => GetRetrySelectedIssuesToolTipText();
    public bool CanRetrySelectedIssues =>
        !IsRunning
        && currentRunRequest is not null
        && SelectedRetryIssueCount > 0;
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

    public bool IsRetryIssueSelected(string path)
    {
        var normalizedPath = NormalizePathForComparison(path);
        return selectedRetryIssuePaths.Contains(normalizedPath)
            && IsRetryableIssuePath(normalizedPath);
    }

    public void SetRetryIssueSelected(string path, bool selected)
    {
        var normalizedPath = NormalizePathForComparison(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        var changed = selected
            ? IsRetryableIssuePath(normalizedPath) && selectedRetryIssuePaths.Add(normalizedPath)
            : selectedRetryIssuePaths.Remove(normalizedPath);
        if (changed)
        {
            RaiseRetryIssueSelectionPropertiesChanged();
        }
    }

    public void SelectAllRetryableIssues()
    {
        var changed = false;
        foreach (var result in RetryableResults)
        {
            changed |= selectedRetryIssuePaths.Add(NormalizePathForComparison(result.Path));
        }

        if (changed)
        {
            RaiseRetryIssueSelectionPropertiesChanged();
        }
    }

    public void ClearSelectedRetryableIssues()
    {
        if (selectedRetryIssuePaths.Count == 0)
        {
            return;
        }

        selectedRetryIssuePaths.Clear();
        RaiseRetryIssueSelectionPropertiesChanged();
    }

    private string GetSelectedResultRetryToolTipText()
    {
        if (SelectedResult is null)
        {
            return "Select a repository result to review retry options.";
        }

        if (IsRunning)
        {
            return "Retry is disabled while a sync is running.";
        }

        if (currentRunRequest is null)
        {
            return "Run sync once before retrying a repository.";
        }

        return SelectedResult.Diagnostic?.RetryPolicy switch
        {
            RetryPolicy.Recommended => "Retry this repository only.",
            RetryPolicy.PossibleAfterCheck => "Review the evidence, then retry this repository if the condition looks resolved.",
            RetryPolicy.BlockedUntilAction => "Fix the blocking repository or remote condition before retrying.",
            RetryPolicy.Unknown => "Review the evidence before retrying this repository.",
            RetryPolicy.NotApplicable => "This result does not need a retry action.",
            _ => "No retry guidance was recorded."
        };
    }

    private string GetRetrySelectedIssuesToolTipText()
    {
        if (IsRunning)
        {
            return "Retry selected is disabled while a sync is running.";
        }

        if (currentRunRequest is null)
        {
            return "Run sync once before retrying failed or warning repositories.";
        }

        return SelectedRetryIssueCount == 0
            ? "Select one or more retryable failed or warning repositories."
            : $"Retry {SelectedRetryIssueCount} selected repositories.";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (hasInitialized || IsRunning || syncService is null)
        {
            return;
        }

        IsRunning = true;
        ClearRunError();
        SetRunProgress(0, 0, "Scanning library...");

        try
        {
            await LoadAppSettingsAsync(cancellationToken);
            var loadResult = await LoadLibraryForCurrentRootAsync(resetResults: false, cancellationToken);
            if (!ApplyPersistedRunState(loadResult))
            {
                SetRunProgress(
                    0,
                    loadResult.Inventory.Repositories.Count,
                    loadResult.Inventory.Repositories.Count == 0
                        ? "No repositories found in the selected library root."
                        : $"Ready to run {loadResult.Inventory.Repositories.Count} repositories.");
            }
            hasInitialized = true;
        }
        catch (OperationCanceledException)
        {
            hasInitialized = false;
            SetRunError("Library scan was canceled.");
        }
        catch (Exception ex)
        {
            hasInitialized = false;
            SetRunError(ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public async Task ChangeLibraryRootAsync(string newRoot, CancellationToken cancellationToken = default)
    {
        if (!CanChangeLibraryRoot || syncService is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(newRoot))
        {
            SetRunError("Library root is required.");
            return;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(newRoot.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetRunError(ex.Message);
            return;
        }

        IsRunning = true;
        ClearRunError();
        SetRunProgress(0, 0, "Scanning library...");

        try
        {
            var loadResult = await LoadLibraryForRootAsync(normalizedRoot, cancellationToken);
            await SaveAppSettingsAsync(loadResult.LibraryRoot, cancellationToken);
            ClearLoadedRunState();
            ClearAddRepositoryMessages();
            ApplyLibraryLoadResult(loadResult, resetResults: true);
            if (!ApplyPersistedRunState(loadResult))
            {
                SetRunProgress(
                    0,
                    loadResult.Inventory.Repositories.Count,
                    loadResult.Inventory.Repositories.Count == 0
                        ? "No repositories found in the selected library root."
                        : $"Ready to run {loadResult.Inventory.Repositories.Count} repositories.");
            }
        }
        catch (OperationCanceledException)
        {
            SetRunError("Library root change was canceled.");
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

    public async Task RunSyncAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRunSync || syncService is null)
        {
            return;
        }

        IsRunning = true;
        ClearRunError();
        ClearLoadedRunState();
        SetRunProgress(0, 0, "Scanning library...");

        try
        {
            var loadResult = await LoadLibraryForCurrentRootAsync(resetResults: true, cancellationToken);
            var request = loadResult.CreateRunRequest();
            currentRunRequest = request;
            runCompletionApplied = false;
            currentRunStartedAt = DateTimeOffset.Now;
            SetRunProgress(0, request.Inventory.Repositories.Count, "Starting sync...");
            PersistCurrentRunState(PersistedRunStatus.Running);

            deferRepositoryNavigationRefresh = true;
            var runResult = await syncService.RunAllAsync(
                request,
                CreateProgress(),
                cancellationToken);
            await dispatcher.EnqueueAsync(() => ApplyRunCompleted(runResult));
        }
        catch (OperationCanceledException)
        {
            SetRunError("Sync was canceled.", canceled: true);
            PersistCurrentRunState(PersistedRunStatus.Canceled, completedAt: DateTimeOffset.Now, errorMessage: RunErrorMessage);
        }
        catch (Exception ex)
        {
            SetRunError(ex.Message);
            PersistCurrentRunState(PersistedRunStatus.Failed, completedAt: DateTimeOffset.Now, errorMessage: RunErrorMessage);
        }
        finally
        {
            deferRepositoryNavigationRefresh = false;
            FlushDeferredRepositoryNavigationRefresh();
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
        currentRunStartedAt = DateTimeOffset.Now;
        SetRunProgress(0, 1, $"Retrying {resultToRetry.Name}...");
        PersistCurrentRunState(PersistedRunStatus.Running);

        try
        {
            var retryResult = await syncService.RetryRepositoryAsync(
                currentRunRequest,
                resultToRetry.Path,
                CreateProgress(),
                cancellationToken);
            await dispatcher.EnqueueAsync(() => ApplyRetryCompleted(retryResult));
        }
        catch (OperationCanceledException)
        {
            SetRunError("Retry was canceled.", canceled: true);
            PersistCurrentRunState(PersistedRunStatus.Canceled, completedAt: DateTimeOffset.Now, errorMessage: RunErrorMessage);
        }
        catch (Exception ex)
        {
            SetRunError(ex.Message);
            PersistCurrentRunState(PersistedRunStatus.Failed, completedAt: DateTimeOffset.Now, errorMessage: RunErrorMessage);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public async Task RetrySelectedIssuesAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRetrySelectedIssues || syncService is null || currentRunRequest is null)
        {
            return;
        }

        var request = currentRunRequest;
        var targets = RetryableResults
            .Where(result => IsRetryIssueSelected(result.Path))
            .OrderBy(result => GetStatusSortOrder(result.Status))
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length == 0)
        {
            RaiseRetryIssueSelectionPropertiesChanged();
            return;
        }

        IsRunning = true;
        ClearRunError();
        currentRunStartedAt = DateTimeOffset.Now;
        SetRunProgress(0, targets.Length, $"Retrying {targets.Length} repositories...");
        PersistCurrentRunState(PersistedRunStatus.Running);

        var completed = 0;
        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetRunProgress(completed, targets.Length, $"Retrying {target.Name}...");
                PersistCurrentRunState(PersistedRunStatus.Running);

                var retryResult = await syncService.RetryRepositoryAsync(
                    request,
                    target.Path,
                    progress: null,
                    cancellationToken);
                await dispatcher.EnqueueAsync(() =>
                {
                    UpsertRepositoryResult(retryResult, FindRepositoryDescriptor(retryResult.Path));
                    completed++;
                    SetRunProgress(completed, targets.Length, $"Retried {completed} of {targets.Length} repositories.");
                    PersistCurrentRunState(PersistedRunStatus.Running);
                });
            }

            await dispatcher.EnqueueAsync(() => ApplyRetryIssuesCompleted(targets.Length));
        }
        catch (OperationCanceledException)
        {
            SetRunError("Retry selected was canceled.", canceled: true);
            PersistCurrentRunState(PersistedRunStatus.Canceled, completedAt: DateTimeOffset.Now, errorMessage: RunErrorMessage);
        }
        catch (Exception ex)
        {
            SetRunError(ex.Message);
            PersistCurrentRunState(PersistedRunStatus.Failed, completedAt: DateTimeOffset.Now, errorMessage: RunErrorMessage);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void BeginAddRepository(string? remoteUrl = null, string? categoryName = null)
    {
        AddRepositoryUrl = remoteUrl ?? RepositoryUrlToAdd;
        AddRepositoryCategoryName = categoryName
            ?? SelectedCategory?.Name
            ?? string.Empty;
        AddRepositoryFolderName = string.Empty;
        ClearAddRepositoryMessages();
        UpdateAddRepositoryPreview();
    }

    public void UpdateAddRepositoryPreview()
    {
        if (repositoryManagementService is null)
        {
            SetAddRepositoryPreview(
                preview: null,
                targetPath: string.Empty,
                title: "Repository management is unavailable",
                explanation: "The WinUI repository-management service is not configured.",
                evidence: string.Empty);
            return;
        }

        try
        {
            var request = CreateAddRepositoryRequest();
            var preview = repositoryManagementService.PreviewAddRepository(request);
            var diagnostic = preview.Diagnostic;
            SetAddRepositoryPreview(
                preview,
                preview.TargetPath,
                diagnostic?.Title ?? "Clone target is valid",
                diagnostic?.Explanation ?? "The repository can be cloned to the previewed target path.",
                diagnostic?.Evidence ?? preview.TargetPath);
        }
        catch (Exception ex)
        {
            SetAddRepositoryPreview(
                preview: null,
                targetPath: string.Empty,
                title: "Clone preview failed",
                explanation: ex.Message,
                evidence: string.Empty);
        }
    }

    public async Task CloneRepositoryAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCloneRepository || repositoryManagementService is null)
        {
            return;
        }

        IsRepositoryManagementBusy = true;
        ClearAddRepositoryMessages();

        try
        {
            var result = await repositoryManagementService.CloneRepositoryAsync(
                CreateAddRepositoryRequest(),
                BuildAdvancedOptions(),
                cancellationToken);

            if (!result.Succeeded)
            {
                var diagnostic = result.CloneResult.Diagnostic ?? result.CloneResult.Preview.Diagnostic;
                SetAddRepositoryError(diagnostic is null
                    ? "Clone failed without a diagnostic."
                    : $"{diagnostic.Title}: {diagnostic.Explanation} {diagnostic.Evidence}".Trim());
                SetAddRepositoryPreview(
                    result.CloneResult.Preview,
                    result.CloneResult.Preview.TargetPath,
                    diagnostic?.Title ?? "Clone failed",
                    diagnostic?.Explanation ?? "The clone command did not complete successfully.",
                    diagnostic?.Evidence ?? string.Empty);
                return;
            }

            if (result.LibraryLoadResult is not null)
            {
                ApplyLibraryLoadResult(result.LibraryLoadResult, resetResults: false);
            }

            if (result.CloneResult.Repository is not null)
            {
                UpsertRepositoryResult(
                    new RepoResult
                    {
                        Path = result.CloneResult.Repository.Path,
                        Name = result.CloneResult.Repository.Name,
                        Elapsed = result.CloneResult.GitResult?.Elapsed ?? TimeSpan.Zero
                    },
                    result.CloneResult.Repository);
            }

            SetAddRepositoryStatus($"Cloned {result.CloneResult.Repository?.Name ?? "repository"}.");
            RepositoryUrlToAdd = string.Empty;
            addRepositoryUrl = string.Empty;
            addRepositoryFolderName = string.Empty;
            addRepositoryTargetPathPreview = string.Empty;
            addRepositoryPreview = null;
            OnPropertyChanged(nameof(AddRepositoryUrl));
            OnPropertyChanged(nameof(AddRepositoryFolderName));
            RaiseAddRepositoryPreviewPropertiesChanged();
        }
        catch (OperationCanceledException)
        {
            SetAddRepositoryError("Clone was canceled.");
        }
        catch (Exception ex)
        {
            SetAddRepositoryError(ex.Message);
        }
        finally
        {
            IsRepositoryManagementBusy = false;
        }
    }

    public async Task SaveAdvancedOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSaveAdvancedOptions || repositoryManagementService is null)
        {
            return;
        }

        IsRepositoryManagementBusy = true;
        ClearAdvancedOptionsMessages();

        try
        {
            var options = BuildAdvancedOptions();
            var loadResult = await repositoryManagementService.SaveDefaultOptionsAsync(
                LibraryRoot,
                options,
                cancellationToken);
            ApplyLibraryLoadResult(loadResult, resetResults: false);
            SetAdvancedOptionsStatus("Advanced options saved.");
        }
        catch (OperationCanceledException)
        {
            SetAdvancedOptionsError("Saving advanced options was canceled.");
        }
        catch (Exception ex)
        {
            SetAdvancedOptionsError(ex.Message);
        }
        finally
        {
            IsRepositoryManagementBusy = false;
        }
    }

    public async Task RestoreRemovedRepositoryAsync(
        RemovedRepositoryViewModel? removedRepository,
        CancellationToken cancellationToken = default)
    {
        if (removedRepository is null || repositoryManagementService is null || IsRepositoryManagementBusy)
        {
            return;
        }

        await RunRemovedRepositoryOperationAsync(
            () => repositoryManagementService.RestoreRepositoryAsync(
                LibraryRoot,
                removedRepository.Record,
                cancellationToken),
            $"Restored {removedRepository.Name}.",
            cancellationToken);
    }

    public async Task RestoreRemovedRepositoryAsAsync(
        RemovedRepositoryViewModel? removedRepository,
        string category,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        if (removedRepository is null || repositoryManagementService is null || IsRepositoryManagementBusy)
        {
            return;
        }

        await RunRemovedRepositoryOperationAsync(
            () => repositoryManagementService.RestoreRepositoryAsAsync(
                LibraryRoot,
                removedRepository.Record,
                category,
                folderName,
                cancellationToken),
            $"Restored {removedRepository.Name}.",
            cancellationToken);
    }

    public async Task PermanentlyDeleteRemovedRepositoryAsync(
        RemovedRepositoryViewModel? removedRepository,
        CancellationToken cancellationToken = default)
    {
        if (removedRepository is null || repositoryManagementService is null || IsRepositoryManagementBusy)
        {
            return;
        }

        await RunRemovedRepositoryOperationAsync(
            () => repositoryManagementService.PermanentlyDeleteRepositoryAsync(
                LibraryRoot,
                removedRepository.Record,
                cancellationToken),
            $"Permanently deleted {removedRepository.Name}.",
            cancellationToken);
    }

    public Task OpenSelectedRepositoryFolderAsync()
    {
        return LaunchPathAsync(SelectedResult?.Path, "repository folder");
    }

    public Task OpenSelectedRemoteAsync()
    {
        return LaunchRemoteAsync(SelectedResult?.RemoteUrl, "repository remote");
    }

    public Task OpenLibraryFolderAsync()
    {
        return LaunchPathAsync(LibraryRoot, "library folder");
    }

    public Task OpenLatestReportAsync()
    {
        return LaunchPathAsync(OpenableLatestReportPath ?? string.Empty, "latest report");
    }

    public Task OpenRemovedFolderAsync(RemovedRepositoryViewModel? removedRepository)
    {
        return LaunchPathAsync(removedRepository?.RemovedPath, "removed folder");
    }

    public Task OpenRemovedOriginalFolderAsync(RemovedRepositoryViewModel? removedRepository)
    {
        return LaunchPathAsync(removedRepository?.OriginalPath, "original folder");
    }

    public Task OpenRemovedRemoteAsync(RemovedRepositoryViewModel? removedRepository)
    {
        return LaunchRemoteAsync(removedRepository?.RemoteUrl, "removed repository remote");
    }

    public static MainShellViewModel CreateDefault(IViewModelDispatcher? dispatcher = null)
    {
        var appSettingsService = new JsonAppSettingsService();
        var service = new CoreGitPullerSyncService();
        return new MainShellViewModel(
            service.GetDefaultLibraryRoot(),
            service,
            dispatcher,
            new CoreRepositoryManagementService(),
            new WinUiFileSystemLauncher(),
            appSettingsService,
            new JsonRunStateStore());
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

    private RepositoryAddRequest CreateAddRepositoryRequest()
    {
        return new RepositoryAddRequest(
            LibraryRoot,
            AddRepositoryCategoryName,
            AddRepositoryUrl,
            string.IsNullOrWhiteSpace(AddRepositoryFolderName) ? null : AddRepositoryFolderName);
    }

    private async Task LoadAppSettingsAsync(CancellationToken cancellationToken)
    {
        if (appSettingsService is null)
        {
            ReplaceRecentLibraryRoots(CreateRecentLibraryRootList(LibraryRoot));
            return;
        }

        var settings = await appSettingsService.LoadAsync(cancellationToken);
        var normalized = JsonAppSettingsService.Normalize(settings);
        var selectedRoot = string.IsNullOrWhiteSpace(normalized.SelectedLibraryRoot)
            ? LibraryRoot
            : normalized.SelectedLibraryRoot;

        LibraryRoot = selectedRoot;
        ReplaceRecentLibraryRoots(CreateRecentLibraryRootList(selectedRoot, normalized.RecentLibraryRoots));
    }

    private async Task SaveAppSettingsAsync(string selectedRoot, CancellationToken cancellationToken)
    {
        var recentRoots = CreateRecentLibraryRootList(selectedRoot, RecentLibraryRoots);

        if (appSettingsService is not null)
        {
            await appSettingsService.SaveAsync(
                new AppSettings(selectedRoot, recentRoots),
                cancellationToken);
        }

        ReplaceRecentLibraryRoots(recentRoots);
    }

    private void ReplaceRecentLibraryRoots(IEnumerable<string> roots)
    {
        RecentLibraryRoots.Clear();
        foreach (var root in CreateRecentLibraryRootList(roots.ToArray()))
        {
            RecentLibraryRoots.Add(root);
        }

        OnPropertyChanged(nameof(RecentLibraryRoots));
    }

    private static IReadOnlyList<string> CreateRecentLibraryRootList(params string?[] roots)
    {
        return CreateRecentLibraryRootList(roots.AsEnumerable());
    }

    private static IReadOnlyList<string> CreateRecentLibraryRootList(
        string? selectedRoot,
        IEnumerable<string> roots)
    {
        return CreateRecentLibraryRootList([selectedRoot, .. roots]);
    }

    private static IReadOnlyList<string> CreateRecentLibraryRootList(IEnumerable<string?> roots)
    {
        var recentRoots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(root.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(normalizedRoot))
            {
                recentRoots.Add(normalizedRoot);
            }
        }

        return recentRoots;
    }

    private void SetAddRepositoryPreview(
        RepositoryAddPreview? preview,
        string targetPath,
        string title,
        string explanation,
        string evidence)
    {
        addRepositoryPreview = preview;
        addRepositoryTargetPathPreview = targetPath;
        addRepositoryDiagnosticTitle = title;
        addRepositoryDiagnosticExplanation = explanation;
        addRepositoryDiagnosticEvidence = evidence;
        RaiseAddRepositoryPreviewPropertiesChanged();
    }

    private void RaiseAddRepositoryPreviewPropertiesChanged()
    {
        OnPropertyChanged(nameof(AddRepositoryTargetPathPreview));
        OnPropertyChanged(nameof(AddRepositoryDiagnosticTitle));
        OnPropertyChanged(nameof(AddRepositoryDiagnosticExplanation));
        OnPropertyChanged(nameof(AddRepositoryDiagnosticEvidence));
        OnPropertyChanged(nameof(HasAddRepositoryDiagnostic));
        OnPropertyChanged(nameof(CanCloneRepository));
        RaiseCommandCanExecuteChanged();
    }

    private void ClearAddRepositoryMessages()
    {
        if (!string.IsNullOrEmpty(addRepositoryStatusMessage))
        {
            addRepositoryStatusMessage = string.Empty;
            OnPropertyChanged(nameof(AddRepositoryStatusMessage));
            OnPropertyChanged(nameof(HasAddRepositoryStatus));
        }

        if (!string.IsNullOrEmpty(addRepositoryErrorMessage))
        {
            addRepositoryErrorMessage = string.Empty;
            OnPropertyChanged(nameof(AddRepositoryErrorMessage));
            OnPropertyChanged(nameof(HasAddRepositoryError));
        }
    }

    private void SetAddRepositoryStatus(string message)
    {
        addRepositoryStatusMessage = message;
        addRepositoryErrorMessage = string.Empty;
        OnPropertyChanged(nameof(AddRepositoryStatusMessage));
        OnPropertyChanged(nameof(AddRepositoryErrorMessage));
        OnPropertyChanged(nameof(HasAddRepositoryStatus));
        OnPropertyChanged(nameof(HasAddRepositoryError));
    }

    private void SetAddRepositoryError(string message)
    {
        addRepositoryErrorMessage = string.IsNullOrWhiteSpace(message)
            ? "Repository add failed."
            : message;
        addRepositoryStatusMessage = string.Empty;
        OnPropertyChanged(nameof(AddRepositoryErrorMessage));
        OnPropertyChanged(nameof(AddRepositoryStatusMessage));
        OnPropertyChanged(nameof(HasAddRepositoryError));
        OnPropertyChanged(nameof(HasAddRepositoryStatus));
    }

    private GitPullerOptions BuildAdvancedOptions()
    {
        return advancedOptionsBase with
        {
            MaxDegreeOfParallelism = Math.Max(1, AdvancedWorkers),
            GitTimeoutMilliseconds = checked(Math.Max(1, AdvancedTimeoutSeconds) * 1000),
            SyncAllBranches = AdvancedSyncAllBranches,
            StaleGitLockCleanup = !AdvancedNoStaleLockCleanup,
            StaleGitLockAge = TimeSpan.FromMinutes(Math.Max(1, AdvancedStaleLockMinutes)),
            VerboseReport = AdvancedVerboseReport,
            InitMissingSubmodules = AdvancedInitMissingSubmodules
        };
    }

    private void ApplyAdvancedOptions(GitPullerOptions? options)
    {
        advancedOptionsBase = options ?? new GitPullerOptions();
        advancedWorkers = Math.Max(1, advancedOptionsBase.MaxDegreeOfParallelism);
        advancedTimeoutSeconds = Math.Max(1, advancedOptionsBase.GitTimeoutMilliseconds / 1000);
        advancedSyncAllBranches = advancedOptionsBase.SyncAllBranches;
        advancedStaleLockMinutes = Math.Max(1, (int)Math.Round(advancedOptionsBase.StaleGitLockAge.TotalMinutes));
        advancedNoStaleLockCleanup = !advancedOptionsBase.StaleGitLockCleanup;
        advancedVerboseReport = advancedOptionsBase.VerboseReport;
        advancedInitMissingSubmodules = advancedOptionsBase.InitMissingSubmodules;
        OnPropertyChanged(nameof(AdvancedWorkers));
        OnPropertyChanged(nameof(AdvancedTimeoutSeconds));
        OnPropertyChanged(nameof(AdvancedSyncAllBranches));
        OnPropertyChanged(nameof(AdvancedStaleLockMinutes));
        OnPropertyChanged(nameof(AdvancedNoStaleLockCleanup));
        OnPropertyChanged(nameof(AdvancedVerboseReport));
        OnPropertyChanged(nameof(AdvancedInitMissingSubmodules));
    }

    private void OnAdvancedOptionChanged()
    {
        ClearAdvancedOptionsMessages();
    }

    private void ClearAdvancedOptionsMessages()
    {
        var hadStatus = !string.IsNullOrEmpty(advancedOptionsStatusMessage);
        var hadError = !string.IsNullOrEmpty(advancedOptionsErrorMessage);
        advancedOptionsStatusMessage = string.Empty;
        advancedOptionsErrorMessage = string.Empty;

        if (hadStatus)
        {
            OnPropertyChanged(nameof(AdvancedOptionsStatusMessage));
            OnPropertyChanged(nameof(HasAdvancedOptionsStatus));
        }

        if (hadError)
        {
            OnPropertyChanged(nameof(AdvancedOptionsErrorMessage));
            OnPropertyChanged(nameof(HasAdvancedOptionsError));
        }
    }

    private void SetAdvancedOptionsStatus(string message)
    {
        advancedOptionsStatusMessage = message;
        advancedOptionsErrorMessage = string.Empty;
        OnPropertyChanged(nameof(AdvancedOptionsStatusMessage));
        OnPropertyChanged(nameof(AdvancedOptionsErrorMessage));
        OnPropertyChanged(nameof(HasAdvancedOptionsStatus));
        OnPropertyChanged(nameof(HasAdvancedOptionsError));
    }

    private void SetAdvancedOptionsError(string message)
    {
        advancedOptionsErrorMessage = string.IsNullOrWhiteSpace(message)
            ? "Advanced options could not be saved."
            : message;
        advancedOptionsStatusMessage = string.Empty;
        OnPropertyChanged(nameof(AdvancedOptionsErrorMessage));
        OnPropertyChanged(nameof(AdvancedOptionsStatusMessage));
        OnPropertyChanged(nameof(HasAdvancedOptionsError));
        OnPropertyChanged(nameof(HasAdvancedOptionsStatus));
    }

    private async Task RunRemovedRepositoryOperationAsync(
        Func<Task<GitPullerLibraryLoadResult>> operation,
        string successMessage,
        CancellationToken cancellationToken)
    {
        IsRepositoryManagementBusy = true;
        ClearRemovedRepositoryMessages();

        try
        {
            var loadResult = await operation();
            ApplyLibraryLoadResult(loadResult, resetResults: false);
            SetRemovedRepositoryStatus(successMessage);
        }
        catch (OperationCanceledException)
        {
            SetRemovedRepositoryError("Removed repository operation was canceled.");
        }
        catch (Exception ex)
        {
            SetRemovedRepositoryError(ex.Message);
        }
        finally
        {
            IsRepositoryManagementBusy = false;
        }
    }

    private void ClearRemovedRepositoryMessages()
    {
        var hadStatus = !string.IsNullOrEmpty(removedRepositoryStatusMessage);
        var hadError = !string.IsNullOrEmpty(removedRepositoryErrorMessage);
        removedRepositoryStatusMessage = string.Empty;
        removedRepositoryErrorMessage = string.Empty;

        if (hadStatus)
        {
            OnPropertyChanged(nameof(RemovedRepositoryStatusMessage));
            OnPropertyChanged(nameof(HasRemovedRepositoryStatus));
        }

        if (hadError)
        {
            OnPropertyChanged(nameof(RemovedRepositoryErrorMessage));
            OnPropertyChanged(nameof(HasRemovedRepositoryError));
        }
    }

    private void SetRemovedRepositoryStatus(string message)
    {
        removedRepositoryStatusMessage = message;
        removedRepositoryErrorMessage = string.Empty;
        OnPropertyChanged(nameof(RemovedRepositoryStatusMessage));
        OnPropertyChanged(nameof(RemovedRepositoryErrorMessage));
        OnPropertyChanged(nameof(HasRemovedRepositoryStatus));
        OnPropertyChanged(nameof(HasRemovedRepositoryError));
    }

    private void SetRemovedRepositoryError(string message)
    {
        removedRepositoryErrorMessage = string.IsNullOrWhiteSpace(message)
            ? "Removed repository operation failed."
            : message;
        removedRepositoryStatusMessage = string.Empty;
        OnPropertyChanged(nameof(RemovedRepositoryErrorMessage));
        OnPropertyChanged(nameof(RemovedRepositoryStatusMessage));
        OnPropertyChanged(nameof(HasRemovedRepositoryError));
        OnPropertyChanged(nameof(HasRemovedRepositoryStatus));
    }

    private async Task LaunchPathAsync(string? path, string description)
    {
        if (launcher is null || string.IsNullOrWhiteSpace(path))
        {
            SetLaunchError($"Cannot open {description}.");
            return;
        }

        ClearLaunchMessages();
        try
        {
            var launched = await launcher.LaunchPathAsync(path);
            if (launched)
            {
                SetLaunchStatus($"Opened {description}.");
            }
            else
            {
                SetLaunchError($"Could not open {description}: {path}");
            }
        }
        catch (Exception ex)
        {
            SetLaunchError(ex.Message);
        }
    }

    private async Task LaunchRemoteAsync(string? remoteUrl, string description)
    {
        if (launcher is null || !remoteLinkBuilder.TryBuildBrowserUrl(remoteUrl, out var browserUrl))
        {
            SetLaunchError($"Cannot open {description}.");
            return;
        }

        ClearLaunchMessages();
        try
        {
            var launched = await launcher.LaunchUriAsync(browserUrl);
            if (launched)
            {
                SetLaunchStatus($"Opened {description}.");
            }
            else
            {
                SetLaunchError($"Could not open {description}: {browserUrl}");
            }
        }
        catch (Exception ex)
        {
            SetLaunchError(ex.Message);
        }
    }

    private void ClearLaunchMessages()
    {
        var hadStatus = !string.IsNullOrEmpty(launchStatusMessage);
        var hadError = !string.IsNullOrEmpty(launchErrorMessage);
        launchStatusMessage = string.Empty;
        launchErrorMessage = string.Empty;

        if (hadStatus)
        {
            OnPropertyChanged(nameof(LaunchStatusMessage));
            OnPropertyChanged(nameof(HasLaunchStatus));
        }

        if (hadError)
        {
            OnPropertyChanged(nameof(LaunchErrorMessage));
            OnPropertyChanged(nameof(HasLaunchError));
        }
    }

    private void SetLaunchStatus(string message)
    {
        launchStatusMessage = message;
        launchErrorMessage = string.Empty;
        OnPropertyChanged(nameof(LaunchStatusMessage));
        OnPropertyChanged(nameof(LaunchErrorMessage));
        OnPropertyChanged(nameof(HasLaunchStatus));
        OnPropertyChanged(nameof(HasLaunchError));
    }

    private void SetLaunchError(string message)
    {
        launchErrorMessage = string.IsNullOrWhiteSpace(message) ? "Launch failed." : message;
        launchStatusMessage = string.Empty;
        OnPropertyChanged(nameof(LaunchErrorMessage));
        OnPropertyChanged(nameof(LaunchStatusMessage));
        OnPropertyChanged(nameof(HasLaunchError));
        OnPropertyChanged(nameof(HasLaunchStatus));
    }

    private async Task<GitPullerLibraryLoadResult> LoadLibraryForCurrentRootAsync(
        bool resetResults,
        CancellationToken cancellationToken)
    {
        var loadResult = await LoadLibraryForRootAsync(LibraryRoot, cancellationToken);
        ApplyLibraryLoadResult(loadResult, resetResults);
        return loadResult;
    }

    private async Task<GitPullerLibraryLoadResult> LoadLibraryForRootAsync(
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        if (syncService is null)
        {
            throw new InvalidOperationException("Sync service is not configured.");
        }

        return await syncService.LoadLibraryAsync(libraryRoot, cancellationToken);
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
                PersistCurrentRunState(PersistedRunStatus.Running);
                break;

            case GitPullerProgressEventKind.RepositoryStarted:
                SetRunProgress(
                    progressEvent.CompletedRepositories,
                    progressEvent.TotalRepositories,
                    progressEvent.Repository is null
                        ? "Running repository..."
                        : $"Running {progressEvent.Repository.Name}...");
                PersistCurrentRunState(PersistedRunStatus.Running);
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
                PersistCurrentRunState(PersistedRunStatus.Running);
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
        if (runCompletionApplied)
        {
            return;
        }

        runCompletionApplied = true;

        foreach (var result in runResult.RepositoryResults)
        {
            UpsertRepositoryResult(result, FindRepositoryDescriptor(result.Path));
        }

        SetLatestReportPath(runResult.LatestReportPath);
        lastSyncCompletedAt = runResult.CompletedAt == default ? DateTimeOffset.Now : runResult.CompletedAt;
        OnPropertyChanged(nameof(LastSyncCompletedText));
        OnPropertyChanged(nameof(RunCompletionStatusText));

        if (!string.IsNullOrWhiteSpace(runResult.ErrorMessage))
        {
            SetRunError(AppendReportPath(runResult.ErrorMessage, runResult.LatestReportPath));
        }

        SetRunProgress(
            runResult.TotalRepositories,
            runResult.TotalRepositories,
            AppendReportPath(
                GetRunCompletedMessage(runResult),
                runResult.LatestReportPath));
        PersistCurrentRunState(
            string.IsNullOrWhiteSpace(runResult.ErrorMessage)
                ? PersistedRunStatus.Completed
                : PersistedRunStatus.Failed,
            runResult.CompletedAt == default ? DateTimeOffset.Now : runResult.CompletedAt,
            runResult.ErrorMessage,
            runResult.WarningMessage);
        FlushDeferredRepositoryNavigationRefresh();
    }

    private bool ApplyPersistedRunState(GitPullerLibraryLoadResult loadResult)
    {
        if (runStateStore?.Load(loadResult.LibraryRoot) is not { } state
            || !PathsEqual(state.LibraryRoot, loadResult.LibraryRoot))
        {
            return false;
        }

        RepositoryResults.Clear();
        foreach (var result in state.RepositoryResults)
        {
            RepositoryResults.Add(result.ToViewModel());
        }

        SelectedResult = VisibleResults.FirstOrDefault();
        SetLatestReportPath(state.LatestReportPath);
        currentRunStartedAt = state.StartedAt;

        if (state.CompletedAt is { } completedAt)
        {
            lastSyncCompletedAt = completedAt;
            OnPropertyChanged(nameof(LastSyncCompletedText));
        }

        var totalRepositories = Math.Max(
            Math.Max(0, state.TotalRepositories),
            Math.Max(state.RepositoryResults.Count, state.CompletedRepositories));
        var completedRepositories = Math.Clamp(
            state.CompletedRepositories,
            0,
            totalRepositories == 0 ? int.MaxValue : totalRepositories);
        var message = string.IsNullOrWhiteSpace(state.Message)
            ? GetDefaultPersistedRunMessage(state.Status)
            : state.Message;

        switch (state.Status)
        {
            case PersistedRunStatus.Running:
                SetRunProgress(
                    completedRepositories,
                    totalRepositories,
                    $"Previous sync was interrupted. Last saved progress: {message}");
                SetRunError(
                    $"Previous sync was interrupted before completion. Last saved progress: {message}",
                    interrupted: true);
                PersistCurrentRunState(
                    PersistedRunStatus.Interrupted,
                    DateTimeOffset.Now,
                    RunErrorMessage);
                break;

            case PersistedRunStatus.Failed:
            case PersistedRunStatus.Canceled:
            case PersistedRunStatus.Interrupted:
                SetRunProgress(completedRepositories, totalRepositories, message);
                SetRunError(
                    string.IsNullOrWhiteSpace(state.ErrorMessage)
                        ? message
                        : state.ErrorMessage,
                    interrupted: state.Status == PersistedRunStatus.Interrupted,
                    canceled: state.Status == PersistedRunStatus.Canceled);
                break;

            default:
                SetRunProgress(completedRepositories, totalRepositories, message);
                break;
        }

        return true;
    }

    private static string GetDefaultPersistedRunMessage(PersistedRunStatus status)
    {
        return status switch
        {
            PersistedRunStatus.Running => "Sync was running.",
            PersistedRunStatus.Failed => "Sync failed.",
            PersistedRunStatus.Canceled => "Sync was canceled.",
            PersistedRunStatus.Interrupted => "Sync was interrupted.",
            _ => "Sync completed."
        };
    }

    private void PersistCurrentRunState(
        PersistedRunStatus status,
        DateTimeOffset? completedAt = null,
        string? errorMessage = null,
        string? warningMessage = null)
    {
        if (runStateStore is null || string.IsNullOrWhiteSpace(LibraryRoot))
        {
            return;
        }

        var startedAt = currentRunStartedAt == default
            ? DateTimeOffset.Now
            : currentRunStartedAt;
        runStateStore.Save(new PersistedRunState
        {
            LibraryRoot = LibraryRoot,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            CompletedRepositories = RunProgressCompleted,
            TotalRepositories = RunProgressTotal,
            Message = CurrentProgressMessage,
            ErrorMessage = errorMessage,
            WarningMessage = warningMessage,
            LatestReportPath = string.IsNullOrWhiteSpace(OpenableLatestReportPath) ? null : OpenableLatestReportPath,
            RepositoryResults = RepositoryResults
                .Select(PersistedRepositoryResult.FromViewModel)
                .ToList()
        });
    }

    private static string GetRunCompletedMessage(GitPullerRunResult runResult)
    {
        if (runResult.HasFailures)
        {
            return string.IsNullOrWhiteSpace(runResult.WarningMessage)
                ? "Sync completed with items to review."
                : $"Sync completed with items to review. {runResult.WarningMessage}";
        }

        return string.IsNullOrWhiteSpace(runResult.WarningMessage)
            ? "Sync completed."
            : runResult.WarningMessage;
    }

    private void ApplyRetryCompleted(RepoResult retryResult)
    {
        UpsertRepositoryResult(retryResult, FindRepositoryDescriptor(retryResult.Path));
        SetRunProgress(1, 1, $"Retry completed: {retryResult.Name}");
        PersistCurrentRunState(PersistedRunStatus.Completed, completedAt: retryResult.CompletedAt);
    }

    private void ApplyRetryIssuesCompleted(int attemptedCount)
    {
        var remainingRetryableCount = RetryableCount;
        var message = remainingRetryableCount == 0
            ? $"Retried {attemptedCount} repositories. No retryable issues remain."
            : $"Retried {attemptedCount} repositories. {remainingRetryableCount} retryable issue(s) remain.";
        SetRunProgress(attemptedCount, attemptedCount, message);
        PersistCurrentRunState(PersistedRunStatus.Completed, completedAt: DateTimeOffset.Now);
    }

    private void ClearLoadedRunState()
    {
        currentLibraryLoad = null;
        currentRunRequest = null;
        runCompletionApplied = false;
        currentRunStartedAt = default;
        SetLatestReportPath(null);

        Categories.Clear();
        RemovedRepositories.Clear();
        RepositoryResults.Clear();
        SelectedResult = null;
        selectedRetryIssuePaths.Clear();
        RaiseRetryIssueSelectionPropertiesChanged();
        RefreshAllRepositoriesNavigationItem();
        RaiseCommandCanExecuteChanged();
    }

    private void ApplyLibraryLoadResult(GitPullerLibraryLoadResult loadResult, bool resetResults)
    {
        currentLibraryLoad = loadResult;
        currentRunRequest = loadResult.CreateRunRequest();
        LibraryRoot = loadResult.LibraryRoot;
        ApplyAdvancedOptions(loadResult.Options);
        RefreshCategoryNavigationItems();
        ReplaceRemovedRepositories(loadResult.RemovedRepositories);

        if (resetResults)
        {
            RepositoryResults.Clear();
            SelectedResult = null;
        }
    }

    private void SetLatestReportPath(string? path)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
        if (latestReportPathResolved
            && string.Equals(latestReportPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        latestReportPathResolved = true;
        latestReportPath = normalizedPath;
        OnPropertyChanged(nameof(LatestReportPath));
        OnPropertyChanged(nameof(CanOpenLatestReport));
        RaiseCommandCanExecuteChanged();
    }

    private static string AppendReportPath(string message, string? reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return message;
        }

        return $"{message} Report: {Path.GetFileName(reportPath)} ({reportPath})";
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
        var shouldSelect = IsTrackedSelectedResultPath(viewModel.Path)
            || (SelectedResult is null && string.IsNullOrWhiteSpace(selectedResultPath));

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

    private void UpdateSelectedResultPath(RepositoryResultViewModel? value)
    {
        if (value is not null)
        {
            selectedResultPath = NormalizePathForComparison(value.Path);
            return;
        }

        if (!IsRunning)
        {
            selectedResultPath = string.Empty;
        }
    }

    private bool IsTrackedSelectedResultPath(string path)
    {
        return !string.IsNullOrWhiteSpace(selectedResultPath)
            && PathsEqual(selectedResultPath, path);
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
        OnPropertyChanged(nameof(RunCompletionStatusText));
        OnPropertyChanged(nameof(FooterRunStateText));
        RaiseRunStatusIndicatorPropertiesChanged();
    }

    private void SetRunError(string message, bool interrupted = false, bool canceled = false)
    {
        lastRunWasInterrupted = interrupted;
        lastRunWasCanceled = canceled;
        runErrorMessage = string.IsNullOrWhiteSpace(message)
            ? "The sync failed without an error message."
            : message;
        OnPropertyChanged(nameof(RunErrorMessage));
        OnPropertyChanged(nameof(HasRunError));
        OnPropertyChanged(nameof(HasRunStatus));
        OnPropertyChanged(nameof(HasRunInfoStatus));
        OnPropertyChanged(nameof(RunStatusMessage));
        OnPropertyChanged(nameof(RunStatusTitle));
        OnPropertyChanged(nameof(RunCompletionStatusText));
        OnPropertyChanged(nameof(FooterRunStateText));
        RaiseRunStatusIndicatorPropertiesChanged();
    }

    private void ClearRunError()
    {
        var wasInterrupted = lastRunWasInterrupted;
        var wasCanceled = lastRunWasCanceled;
        lastRunWasInterrupted = false;
        lastRunWasCanceled = false;
        if (string.IsNullOrEmpty(runErrorMessage))
        {
            if (wasInterrupted || wasCanceled)
            {
                OnPropertyChanged(nameof(RunStatusTitle));
                OnPropertyChanged(nameof(RunCompletionStatusText));
                OnPropertyChanged(nameof(FooterRunStateText));
                RaiseRunStatusIndicatorPropertiesChanged();
            }

            return;
        }

        runErrorMessage = string.Empty;
        OnPropertyChanged(nameof(RunErrorMessage));
        OnPropertyChanged(nameof(HasRunError));
        OnPropertyChanged(nameof(HasRunStatus));
        OnPropertyChanged(nameof(HasRunInfoStatus));
        OnPropertyChanged(nameof(RunStatusMessage));
        OnPropertyChanged(nameof(RunStatusTitle));
        OnPropertyChanged(nameof(RunCompletionStatusText));
        OnPropertyChanged(nameof(FooterRunStateText));
        RaiseRunStatusIndicatorPropertiesChanged();
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
            SetSelectedFolderNode(normalizedValue.IsAllRepositories
                ? RepositoryTreeNodes.FirstOrDefault()
                : FindFolderNodeByFullCategoryName(normalizedValue.Name));
        }
    }

    private void SetSelectedTreeNode(RepositoryTreeNodeViewModel? value)
    {
        var normalizedValue = NormalizeSelectedTreeNode(value);
        if (SetProperty(ref selectedTreeNode, normalizedValue, nameof(SelectedTreeNode)))
        {
            if (normalizedValue?.IsRepository == true)
            {
                SelectRepositoryTreeNode(normalizedValue);
            }
            else
            {
                SetSelectedFolderNode(normalizedValue);
            }
        }
    }

    public void SelectRepositoryTreeNode(RepositoryTreeNodeViewModel? node)
    {
        if (node?.IsRepository != true)
        {
            return;
        }

        if (!ReferenceEquals(selectedTreeNode, node))
        {
            selectedTreeNode = node;
            OnPropertyChanged(nameof(SelectedTreeNode));
        }

        var result = node.RepositoryResult
            ?? RepositoryResults.FirstOrDefault(candidate => PathsEqual(candidate.Path, node.FullPath));
        if (result is not null)
        {
            SelectedResult = result;
        }
    }

    private void SetSelectedFolderNode(RepositoryTreeNodeViewModel? value)
    {
        var normalizedValue = NormalizeSelectedFolderNode(value);
        if (SetProperty(ref selectedFolderNode, normalizedValue, nameof(SelectedFolderNode)))
        {
            if (normalizedValue is not null && !ReferenceEquals(selectedTreeNode, normalizedValue))
            {
                selectedTreeNode = normalizedValue;
                OnPropertyChanged(nameof(SelectedTreeNode));
            }

            UpdateSelectedCategoryFromFolderNode(normalizedValue);
            RaiseFolderSelectionDerivedPropertiesChanged();
        }
    }

    private void UpdateSelectedCategoryFromFolderNode(RepositoryTreeNodeViewModel? folderNode)
    {
        if (folderNode is null || folderNode.IsAllRepositories)
        {
            SetSelectedCategory(null, updateNavigation: false);
            return;
        }

        SetSelectedCategory(
            Categories.FirstOrDefault(category =>
                string.Equals(category.Name, folderNode.FullCategoryName, StringComparison.OrdinalIgnoreCase)),
            updateNavigation: false);
    }

    private void EnsureSelectedResultIsVisible()
    {
        var visibleResults = VisibleResults;
        if (SelectedResult is not null && visibleResults.Contains(SelectedResult))
        {
            return;
        }

        var matchingResult = visibleResults.FirstOrDefault(result => IsTrackedSelectedResultPath(result.Path));
        SelectedResult = matchingResult ?? visibleResults.FirstOrDefault();
    }

    private void Categories_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CategoryNavigationItems));
        RefreshRepositoryTreeNodes();

        if (SelectedCategory is not null && !Categories.Contains(SelectedCategory))
        {
            SetSelectedCategory(null, updateNavigation: true);
        }
    }

    private void RepositoryResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshRepositoryNavigationForResults();
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
            currentLibraryLoad?.Inventory.Repositories.Count
                ?? currentRunRequest?.Inventory.Repositories.Count
                ?? TotalResultCount,
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
        var selectedName = SelectedCategory?.Name;
        var categoryNames = GetAvailableCategoryNames();

        Categories.CollectionChanged -= Categories_CollectionChanged;
        try
        {
            Categories.Clear();
            foreach (var categoryName in categoryNames)
            {
                var repositoryCount = CountRepositoriesInExactCategory(categoryName);
                var attentionCount = RepositoryResults.Count(result =>
                    string.Equals(NormalizeCategoryName(result.Category), categoryName, StringComparison.OrdinalIgnoreCase)
                    && RequiresAttention(result));
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
        RefreshAllRepositoriesNavigationItem();
        RefreshRepositoryTreeNodes();

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            SetSelectedCategory(
                Categories.FirstOrDefault(category => string.Equals(category.Name, selectedName, StringComparison.OrdinalIgnoreCase)),
                updateNavigation: true);
        }
    }

    private void RefreshRepositoryNavigationForResults()
    {
        if (deferRepositoryNavigationRefresh)
        {
            repositoryNavigationRefreshPending = true;
            return;
        }

        RefreshCategoryNavigationItems();
    }

    private void FlushDeferredRepositoryNavigationRefresh()
    {
        if (!repositoryNavigationRefreshPending)
        {
            return;
        }

        repositoryNavigationRefreshPending = false;
        var wasDeferred = deferRepositoryNavigationRefresh;
        deferRepositoryNavigationRefresh = false;
        try
        {
            RefreshCategoryNavigationItems();
        }
        finally
        {
            deferRepositoryNavigationRefresh = wasDeferred;
        }
    }

    private static string NormalizeCategoryName(string? category)
    {
        return string.IsNullOrWhiteSpace(category) ? "(uncategorized)" : category.Trim();
    }

    private string[] GetAvailableCategoryNames()
    {
        return Categories.Select(category => NormalizeCategoryName(category.Name))
            .Concat(currentLibraryLoad?.ConfiguredCategories ?? [])
            .Concat((currentLibraryLoad?.Inventory.Repositories ?? []).Select(repository => NormalizeCategoryName(repository.Category)))
            .Concat(RepositoryResults.Select(result => NormalizeCategoryName(result.Category)))
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int CountRepositoriesInExactCategory(string categoryName)
    {
        if (currentLibraryLoad is not null)
        {
            return currentLibraryLoad.Inventory.Repositories.Count(repository =>
                string.Equals(NormalizeCategoryName(repository.Category), categoryName, StringComparison.OrdinalIgnoreCase));
        }

        return RepositoryResults.Count(result =>
            string.Equals(NormalizeCategoryName(result.Category), categoryName, StringComparison.OrdinalIgnoreCase));
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

    private void RefreshRepositoryTreeNodes()
    {
        var selectedFolderCategoryName = SelectedFolderNode?.FullCategoryName;
        var selectedTreeNodePath = SelectedTreeNode?.IsRepository == true ? SelectedTreeNode.FullPath : null;
        var treeNodes = BuildRepositoryTreeNodes();

        RepositoryTreeNodes.Clear();
        foreach (var node in treeNodes)
        {
            RepositoryTreeNodes.Add(node);
        }

        OnPropertyChanged(nameof(RepositoryTreeNodes));
        SetSelectedFolderNode(FindFolderNodeByFullCategoryName(selectedFolderCategoryName) ?? RepositoryTreeNodes.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(selectedTreeNodePath))
        {
            selectedTreeNode = FindRepositoryNodeByPath(selectedTreeNodePath) ?? SelectedFolderNode;
            OnPropertyChanged(nameof(SelectedTreeNode));
        }
    }

    private IReadOnlyList<RepositoryTreeLeafSource> GetRepositoryTreeLeaves()
    {
        var leaves = new List<RepositoryTreeLeafSource>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (currentLibraryLoad is not null)
        {
            foreach (var repository in currentLibraryLoad.Inventory.Repositories)
            {
                var result = RepositoryResults.FirstOrDefault(candidate => PathsEqual(candidate.Path, repository.Path));
                leaves.Add(new RepositoryTreeLeafSource(
                    repository.Name,
                    NormalizeCategoryName(repository.Category),
                    repository.Path,
                    result));
                seenPaths.Add(NormalizePathForComparison(repository.Path));
            }
        }

        foreach (var result in RepositoryResults)
        {
            var normalizedPath = NormalizePathForComparison(result.Path);
            if (seenPaths.Add(normalizedPath))
            {
                leaves.Add(new RepositoryTreeLeafSource(
                    result.Name,
                    NormalizeCategoryName(result.Category),
                    result.Path,
                    result));
            }
        }

        return leaves
            .OrderBy(leaf => leaf.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(leaf => leaf.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private RepositoryTreeNodeViewModel[] BuildRepositoryTreeNodes()
    {
        var availableCategoryNames = GetAvailableCategoryNames();
        var repositoryLeaves = GetRepositoryTreeLeaves();
        var repositoryCategoryNames = repositoryLeaves
            .Select(repository => NormalizeCategoryName(repository.Category))
            .ToArray();

        var attentionCategoryNames = repositoryLeaves
            .Select(repository => repository.Result)
            .OfType<RepositoryResultViewModel>()
            .Where(RequiresAttention)
            .Select(result => NormalizeCategoryName(result.Category))
            .ToArray();

        var lookup = new Dictionary<string, RepositoryFolderNodeBuilder>(StringComparer.OrdinalIgnoreCase);
        var rootNodes = new List<RepositoryFolderNodeBuilder>();

        foreach (var categoryName in availableCategoryNames)
        {
            AddCategoryPath(categoryName, lookup, rootNodes);
        }

        foreach (var categoryName in repositoryCategoryNames)
        {
            AddCategoryPath(categoryName, lookup, rootNodes);
            foreach (var path in EnumerateCategoryPath(categoryName))
            {
                if (lookup.TryGetValue(path, out var node))
                {
                    node.RepositoryCount++;
                }
            }
        }

        foreach (var categoryName in attentionCategoryNames)
        {
            foreach (var path in EnumerateCategoryPath(categoryName))
            {
                if (lookup.TryGetValue(path, out var node))
                {
                    node.AttentionCount++;
                }
            }
        }

        foreach (var repository in repositoryLeaves)
        {
            var categoryName = NormalizeCategoryName(repository.Category);
            if (lookup.TryGetValue(categoryName, out var node))
            {
                node.Repositories.Add(repository);
            }
        }

        var allRepositoriesNode = new RepositoryTreeNodeViewModel(
            RepositoryTreeNodeKind.Folder,
            "All repositories",
            string.Empty,
            LibraryRoot,
            repositoryLeaves.Count,
            attentionCategoryNames.Length,
            isAllRepositories: true);

        return [allRepositoriesNode, .. rootNodes.Select(BuildRepositoryTreeNode)];
    }

    private void AddCategoryPath(
        string categoryName,
        IDictionary<string, RepositoryFolderNodeBuilder> lookup,
        ICollection<RepositoryFolderNodeBuilder> rootNodes)
    {
        var segments = categoryName
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return;
        }

        RepositoryFolderNodeBuilder? parent = null;
        for (var index = 0; index < segments.Length; index++)
        {
            var fullCategoryName = string.Join('/', segments.Take(index + 1));
            if (!lookup.TryGetValue(fullCategoryName, out var current))
            {
                current = new RepositoryFolderNodeBuilder(
                    segments[index],
                    fullCategoryName,
                    GetCategoryFullPath(fullCategoryName));
                lookup.Add(fullCategoryName, current);
                if (parent is null)
                {
                    rootNodes.Add(current);
                }
                else
                {
                    parent.Children.Add(current);
                }
            }

            parent = current;
        }
    }

    private RepositoryTreeNodeViewModel BuildRepositoryTreeNode(RepositoryFolderNodeBuilder builder)
    {
        var childFolders = builder.Children
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildRepositoryTreeNode);
        var repositoryLeaves = builder.Repositories
            .OrderBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
            .Select(repository => new RepositoryTreeNodeViewModel(
                RepositoryTreeNodeKind.Repository,
                repository.Name,
                repository.Category,
                repository.Path,
                repositoryCount: 1,
                attentionCount: repository.Result is not null && RequiresAttention(repository.Result) ? 1 : 0,
                repositoryResult: repository.Result));

        return new RepositoryTreeNodeViewModel(
            RepositoryTreeNodeKind.Folder,
            builder.Name,
            builder.FullCategoryName,
            builder.FullPath,
            builder.RepositoryCount,
            builder.AttentionCount,
            children: childFolders.Concat(repositoryLeaves));
    }

    private RepositoryTreeNodeViewModel? NormalizeSelectedTreeNode(RepositoryTreeNodeViewModel? value)
    {
        if (value?.IsRepository == true)
        {
            return FindRepositoryNodeByPath(value.FullPath) ?? value;
        }

        return NormalizeSelectedFolderNode(value);
    }

    private RepositoryTreeNodeViewModel? NormalizeSelectedFolderNode(RepositoryTreeNodeViewModel? value)
    {
        if (RepositoryTreeNodes.Count == 0)
        {
            return value;
        }

        if (value is null || value.IsAllRepositories)
        {
            return RepositoryTreeNodes.FirstOrDefault();
        }

        return FindFolderNodeByFullCategoryName(value.FullCategoryName) ?? RepositoryTreeNodes.FirstOrDefault();
    }

    private RepositoryTreeNodeViewModel? FindFolderNodeByFullCategoryName(string? fullCategoryName)
    {
        foreach (var rootNode in RepositoryTreeNodes)
        {
            var match = FindFolderNodeRecursive(rootNode, fullCategoryName);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static RepositoryTreeNodeViewModel? FindFolderNodeRecursive(
        RepositoryTreeNodeViewModel node,
        string? fullCategoryName)
    {
        if (node.IsFolder
            && (string.Equals(node.FullCategoryName, fullCategoryName, StringComparison.OrdinalIgnoreCase)
            || (node.IsAllRepositories && string.IsNullOrWhiteSpace(fullCategoryName)))
           )
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindFolderNodeRecursive(child, fullCategoryName);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private RepositoryTreeNodeViewModel? FindRepositoryNodeByPath(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return null;
        }

        foreach (var rootNode in RepositoryTreeNodes)
        {
            var match = FindRepositoryNodeByPathRecursive(rootNode, repositoryPath);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static RepositoryTreeNodeViewModel? FindRepositoryNodeByPathRecursive(
        RepositoryTreeNodeViewModel node,
        string repositoryPath)
    {
        if (node.IsRepository && PathsEqual(node.FullPath, repositoryPath))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindRepositoryNodeByPathRecursive(child, repositoryPath);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private bool ResultMatchesSelectedFolder(RepositoryResultViewModel result)
    {
        if (SelectedFolderNode is null || SelectedFolderNode.IsAllRepositories)
        {
            return true;
        }

        var categoryName = NormalizeCategoryName(result.Category);
        return string.Equals(categoryName, SelectedFolderNode.FullCategoryName, StringComparison.OrdinalIgnoreCase)
            || categoryName.StartsWith($"{SelectedFolderNode.FullCategoryName}/", StringComparison.OrdinalIgnoreCase);
    }

    private bool ResultMatchesSelectedStatusFilter(RepositoryResultViewModel result)
    {
        return SelectedResultFilter switch
        {
            RepositoryResultFilter.Failed => result.Status == RepositoryResultStatus.Failed,
            RepositoryResultFilter.Warning => result.Status == RepositoryResultStatus.Warning,
            RepositoryResultFilter.Retryable => IsRetryableIssue(result),
            RepositoryResultFilter.Updated => result.Status == RepositoryResultStatus.Updated,
            RepositoryResultFilter.Clean => result.Status == RepositoryResultStatus.Clean,
            _ => true
        };
    }

    private bool ResultMatchesRepositorySearchText(RepositoryResultViewModel result)
    {
        return string.IsNullOrWhiteSpace(RepositorySearchText)
            || result.SearchText.Contains(RepositorySearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int GetStatusSortOrder(RepositoryResultStatus status)
    {
        return status switch
        {
            RepositoryResultStatus.Failed => 0,
            RepositoryResultStatus.Warning => 1,
            RepositoryResultStatus.Updated => 2,
            _ => 3
        };
    }

    private static bool RequiresAttention(RepositoryResultViewModel result)
    {
        return result.Status is RepositoryResultStatus.Failed or RepositoryResultStatus.Warning;
    }

    private static bool IsRetryableIssue(RepositoryResultViewModel result)
    {
        return RequiresAttention(result) && result.CanRetry;
    }

    private IReadOnlyList<RepositoryResultViewModel> RetryableResults =>
        RepositoryResults
            .Where(IsRetryableIssue)
            .ToArray();

    private bool IsRetryableIssuePath(string normalizedPath)
    {
        return RetryableResults.Any(result =>
            string.Equals(
                NormalizePathForComparison(result.Path),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureRetryableIssueSelection()
    {
        var changed = PruneSelectedRetryIssuePaths();
        if (SelectedRetryIssueCount == 0)
        {
            foreach (var result in RetryableResults)
            {
                changed |= selectedRetryIssuePaths.Add(NormalizePathForComparison(result.Path));
            }
        }

        if (changed)
        {
            RaiseRetryIssueSelectionPropertiesChanged();
        }
    }

    private bool PruneSelectedRetryIssuePaths()
    {
        if (selectedRetryIssuePaths.Count == 0)
        {
            return false;
        }

        var retryablePaths = RetryableResults
            .Select(result => NormalizePathForComparison(result.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stalePaths = selectedRetryIssuePaths
            .Where(path => !retryablePaths.Contains(path))
            .ToArray();
        foreach (var path in stalePaths)
        {
            selectedRetryIssuePaths.Remove(path);
        }

        return stalePaths.Length > 0;
    }

    private static IEnumerable<string> EnumerateCategoryPath(string categoryName)
    {
        var segments = categoryName
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            yield return string.Join('/', segments.Take(index + 1));
        }
    }

    private void RaiseCategorySelectionDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(CanAddRepositoryFromUrl));
        OnPropertyChanged(nameof(SelectedCategoryName));
        InvalidateVisibleResults();
        RaiseCommandCanExecuteChanged();
        EnsureSelectedResultIsVisible();
    }

    private void RaiseFolderSelectionDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedCategoryName));
        InvalidateVisibleResults();
        EnsureSelectedResultIsVisible();
    }

    private void RaiseResultFilterDerivedPropertiesChanged()
    {
        InvalidateVisibleResults();
        OnPropertyChanged(nameof(IsAllFilterSelected));
        OnPropertyChanged(nameof(IsFailedFilterSelected));
        OnPropertyChanged(nameof(IsWarningFilterSelected));
        OnPropertyChanged(nameof(IsRetryableFilterSelected));
        OnPropertyChanged(nameof(IsUpdatedFilterSelected));
        OnPropertyChanged(nameof(IsCleanFilterSelected));
        EnsureSelectedResultIsVisible();
    }

    private void RaiseResultDerivedPropertiesChanged()
    {
        InvalidateVisibleResults();
        var selectionChanged = PruneSelectedRetryIssuePaths();
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(RetryableCount));
        OnPropertyChanged(nameof(UpdatedCount));
        OnPropertyChanged(nameof(CleanCount));
        OnPropertyChanged(nameof(VisibleResultCount));
        OnPropertyChanged(nameof(TotalResultCount));
        OnPropertyChanged(nameof(HasAttentionItems));
        OnPropertyChanged(nameof(AttentionSummary));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(AllFilterText));
        OnPropertyChanged(nameof(FailedFilterText));
        OnPropertyChanged(nameof(WarningFilterText));
        OnPropertyChanged(nameof(RetryableFilterText));
        OnPropertyChanged(nameof(UpdatedFilterText));
        OnPropertyChanged(nameof(CleanFilterText));
        OnPropertyChanged(nameof(FooterSummaryText));
        OnPropertyChanged(nameof(UpdatedFooterText));
        OnPropertyChanged(nameof(CleanFooterText));
        OnPropertyChanged(nameof(WarningFooterText));
        OnPropertyChanged(nameof(FailedFooterText));
        OnPropertyChanged(nameof(FooterRunStateText));
        RaiseRunStatusIndicatorPropertiesChanged();
        if (selectionChanged)
        {
            RaiseRetryIssueSelectionPropertiesChanged();
        }
    }

    private void RaiseRunStatusIndicatorPropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentRunStatusIndicatorKind));
        OnPropertyChanged(nameof(RunCompletionStatusIndicator));
        OnPropertyChanged(nameof(FooterRunStateIndicator));
    }

    private void InvalidateVisibleResults()
    {
        visibleResultsCache = null;
        OnPropertyChanged(nameof(VisibleResults));
        OnPropertyChanged(nameof(VisibleResultCount));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void RaiseSelectedResultPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedResultName));
        OnPropertyChanged(nameof(SelectedResultStatus));
        OnPropertyChanged(nameof(SelectedResultCategory));
        OnPropertyChanged(nameof(SelectedResultPath));
        OnPropertyChanged(nameof(SelectedResultRemoteUrl));
        OnPropertyChanged(nameof(SelectedResultCurrentText));
        OnPropertyChanged(nameof(SelectedResultLastUpdatedText));
        OnPropertyChanged(nameof(SelectedResultTrackingText));
        OnPropertyChanged(nameof(SelectedResultSummary));
        OnPropertyChanged(nameof(SelectedResultDiagnosticTitle));
        OnPropertyChanged(nameof(SelectedResultDiagnosticExplanation));
        OnPropertyChanged(nameof(SelectedResultSuggestedAction));
        OnPropertyChanged(nameof(SelectedResultRetryPolicyText));
        OnPropertyChanged(nameof(SelectedResultRetryPolicyDescription));
        OnPropertyChanged(nameof(SelectedResultRetryButtonText));
        OnPropertyChanged(nameof(SelectedResultRetryToolTipText));
        OnPropertyChanged(nameof(SelectedResultCanRetry));
        OnPropertyChanged(nameof(IsSelectedResultRetryPrimary));
        OnPropertyChanged(nameof(IsSelectedResultRetrySecondary));
        OnPropertyChanged(nameof(SelectedResultEvidence));
        OnPropertyChanged(nameof(SelectedResultRelatedCommand));
        OnPropertyChanged(nameof(SelectedResultLogLines));
        OnPropertyChanged(nameof(HasSelectedResult));
        OnPropertyChanged(nameof(CanOpenSelectedRepositoryFolder));
        OnPropertyChanged(nameof(CanOpenSelectedRemote));
    }

    private void RaiseRetryIssueSelectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedRetryIssueCount));
        OnPropertyChanged(nameof(HasSelectedRetryIssues));
        OnPropertyChanged(nameof(RetrySelectedIssuesButtonText));
        OnPropertyChanged(nameof(RetrySelectedIssuesToolTipText));
        OnPropertyChanged(nameof(CanRetrySelectedIssues));

        if (RetrySelectedIssuesCommand is AsyncRelayCommand retryIssuesCommand)
        {
            retryIssuesCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseCommandCanExecuteChanged()
    {
        if (AddRepositoryCommand is RelayCommand addCommand)
        {
            addCommand.RaiseCanExecuteChanged();
        }

        if (CloneRepositoryCommand is AsyncRelayCommand cloneCommand)
        {
            cloneCommand.RaiseCanExecuteChanged();
        }

        if (SaveAdvancedOptionsCommand is AsyncRelayCommand saveAdvancedCommand)
        {
            saveAdvancedCommand.RaiseCanExecuteChanged();
        }

        if (OpenSelectedRepositoryFolderCommand is AsyncRelayCommand openFolderCommand)
        {
            openFolderCommand.RaiseCanExecuteChanged();
        }

        if (OpenSelectedRemoteCommand is AsyncRelayCommand openRemoteCommand)
        {
            openRemoteCommand.RaiseCanExecuteChanged();
        }

        if (OpenLibraryFolderCommand is AsyncRelayCommand openLibraryCommand)
        {
            openLibraryCommand.RaiseCanExecuteChanged();
        }

        if (OpenLatestReportCommand is AsyncRelayCommand openReportCommand)
        {
            openReportCommand.RaiseCanExecuteChanged();
        }

        if (RunSyncCommand is AsyncRelayCommand runCommand)
        {
            runCommand.RaiseCanExecuteChanged();
        }

        if (RetrySelectedCommand is AsyncRelayCommand retryCommand)
        {
            retryCommand.RaiseCanExecuteChanged();
        }

        if (RetrySelectedIssuesCommand is AsyncRelayCommand retryIssuesCommand)
        {
            retryIssuesCommand.RaiseCanExecuteChanged();
        }

        OnPropertyChanged(nameof(SelectedResultCanRetry));
        OnPropertyChanged(nameof(SelectedResultRetryToolTipText));
        OnPropertyChanged(nameof(CanRetrySelectedIssues));
        OnPropertyChanged(nameof(RetrySelectedIssuesToolTipText));
    }
}

internal sealed class RepositoryFolderNodeBuilder
{
    public RepositoryFolderNodeBuilder(string name, string fullCategoryName, string fullPath)
    {
        Name = name;
        FullCategoryName = fullCategoryName;
        FullPath = fullPath;
    }

    public string Name { get; }
    public string FullCategoryName { get; }
    public string FullPath { get; }
    public int RepositoryCount { get; set; }
    public int AttentionCount { get; set; }
    public List<RepositoryFolderNodeBuilder> Children { get; } = [];
    public List<RepositoryTreeLeafSource> Repositories { get; } = [];
}

internal sealed record RepositoryTreeLeafSource(
    string Name,
    string Category,
    string Path,
    RepositoryResultViewModel? Result);

public interface IViewModelDispatcher
{
    void Enqueue(Action action);
    Task EnqueueAsync(Action action);
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

    public Task EnqueueAsync(Action action)
    {
        action();
        return Task.CompletedTask;
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

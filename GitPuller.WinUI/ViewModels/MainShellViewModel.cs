using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using GitPuller;
using GitPuller_WinUI.Services;

namespace GitPuller_WinUI.ViewModels;

public sealed class MainShellViewModel : ObservableObject
{
    private readonly IGitPullerSyncService? syncService;
    private readonly IRepositoryManagementService? repositoryManagementService;
    private readonly IFileSystemLauncher? launcher;
    private readonly IViewModelDispatcher dispatcher;
    private bool showCleanRepositories;
    private bool isRunning;
    private bool isRepositoryManagementBusy;
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
        IFileSystemLauncher? launcher = null)
        : this(
            libraryRoot,
            categories: [],
            repositoryResults: [],
            removedRepositories: [],
            syncService,
            dispatcher,
            repositoryManagementService,
            launcher)
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
        IFileSystemLauncher? launcher = null)
    {
        this.libraryRoot = string.IsNullOrWhiteSpace(libraryRoot) ? string.Empty : libraryRoot;
        this.syncService = syncService;
        this.repositoryManagementService = repositoryManagementService;
        this.launcher = launcher;
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
    public ObservableCollection<RepositoryResultViewModel> RepositoryResults { get; }
    public ObservableCollection<RemovedRepositoryViewModel> RemovedRepositories { get; }
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
                OnPropertyChanged(nameof(CanCloneRepository));
                OnPropertyChanged(nameof(CanSaveAdvancedOptions));
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

    public string RemovedRepositoryStatusMessage => removedRepositoryStatusMessage;
    public string RemovedRepositoryErrorMessage => removedRepositoryErrorMessage;
    public bool HasRemovedRepositoryStatus => !string.IsNullOrWhiteSpace(RemovedRepositoryStatusMessage);
    public bool HasRemovedRepositoryError => !string.IsNullOrWhiteSpace(RemovedRepositoryErrorMessage);

    public string LaunchStatusMessage => launchStatusMessage;
    public string LaunchErrorMessage => launchErrorMessage;
    public bool HasLaunchStatus => !string.IsNullOrWhiteSpace(LaunchStatusMessage);
    public bool HasLaunchError => !string.IsNullOrWhiteSpace(LaunchErrorMessage);
    public string LatestReportPath => Path.Combine(AppContext.BaseDirectory, "git_update_report.md");
    public bool CanOpenSelectedRepositoryFolder =>
        launcher is not null
        && !string.IsNullOrWhiteSpace(SelectedResult?.Path);
    public bool CanOpenSelectedRemote =>
        launcher is not null
        && IsLaunchableUri(SelectedResult?.RemoteUrl);
    public bool CanOpenLibraryFolder =>
        launcher is not null
        && !string.IsNullOrWhiteSpace(LibraryRoot);
    public bool CanOpenLatestReport =>
        launcher is not null
        && File.Exists(LatestReportPath);

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
        if (hasInitialized || IsRunning || syncService is null)
        {
            return;
        }

        hasInitialized = true;
        IsRunning = true;
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
        return LaunchUriAsync(SelectedResult?.RemoteUrl, "repository remote");
    }

    public Task OpenLibraryFolderAsync()
    {
        return LaunchPathAsync(LibraryRoot, "library folder");
    }

    public Task OpenLatestReportAsync()
    {
        return LaunchPathAsync(LatestReportPath, "latest report");
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
        return LaunchUriAsync(removedRepository?.RemoteUrl, "removed repository remote");
    }

    public static MainShellViewModel CreateDefault(IViewModelDispatcher? dispatcher = null)
    {
        var service = new CoreGitPullerSyncService();
        return new MainShellViewModel(
            service.GetDefaultLibraryRoot(),
            service,
            dispatcher,
            new CoreRepositoryManagementService(),
            new WinUiFileSystemLauncher());
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
            cancellationToken.ThrowIfCancellationRequested();
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

    private async Task LaunchUriAsync(string? uri, string description)
    {
        if (launcher is null || !IsLaunchableUri(uri))
        {
            SetLaunchError($"Cannot open {description}.");
            return;
        }

        ClearLaunchMessages();
        try
        {
            var launched = await launcher.LaunchUriAsync(uri!);
            if (launched)
            {
                SetLaunchStatus($"Opened {description}.");
            }
            else
            {
                SetLaunchError($"Could not open {description}: {uri}");
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

    private static bool IsLaunchableUri(string? uri)
    {
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri)
            && (parsedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
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
        if (runCompletionApplied)
        {
            return;
        }

        runCompletionApplied = true;

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

    private void ClearLoadedRunState()
    {
        currentLibraryLoad = null;
        currentRunRequest = null;
        runCompletionApplied = false;

        Categories.Clear();
        RemovedRepositories.Clear();
        RepositoryResults.Clear();
        SelectedResult = null;
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
        OnPropertyChanged(nameof(CanOpenSelectedRepositoryFolder));
        OnPropertyChanged(nameof(CanOpenSelectedRemote));
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

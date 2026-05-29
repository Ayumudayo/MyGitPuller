using GitPuller;

namespace GitPuller_WinUI.Services;

public interface IGitPullerSyncService
{
    string GetDefaultLibraryRoot();

    Task<GitPullerLibraryLoadResult> LoadLibraryAsync(
        string libraryRoot,
        CancellationToken cancellationToken);

    Task<GitPullerRunResult> RunAllAsync(
        GitPullerRunRequest request,
        IProgress<GitPullerProgressEvent>? progress,
        CancellationToken cancellationToken);

    Task<RepoResult> RetryRepositoryAsync(
        GitPullerRunRequest previousRunRequest,
        string repoPath,
        IProgress<GitPullerProgressEvent>? progress,
        CancellationToken cancellationToken);
}

public sealed record GitPullerLibraryLoadResult(
    string LibraryRoot,
    GitPullerOptions Options,
    RepositoryInventory Inventory,
    IReadOnlyList<RemovedRepositoryRecord> RemovedRepositories,
    IReadOnlyList<string> ConfiguredCategories)
{
    public GitPullerRunRequest CreateRunRequest()
    {
        return new GitPullerRunRequest(Options, Inventory);
    }
}

public interface IGitPullerReportService
{
    GitPullerReportWriteResult WriteReports(
        string libraryRoot,
        GitPullerRunResult runResult,
        GitPullerOptions options,
        CancellationToken cancellationToken);
}

public sealed class GitPullerReportService : IGitPullerReportService
{
    public GitPullerReportWriteResult WriteReports(
        string libraryRoot,
        GitPullerRunResult runResult,
        GitPullerOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GitPullerReportWriter.WriteReports(libraryRoot, runResult, options);
    }
}

public interface IGitPullerRunner
{
    Task<GitPullerRunResult> RunAllAsync(
        GitPullerRunRequest request,
        IProgress<GitPullerProgressEvent>? progress,
        CancellationToken cancellationToken);

    Task<RepoResult> RetryRepositoryAsync(
        GitPullerRunRequest previousRunRequest,
        string repoPath,
        IProgress<GitPullerProgressEvent>? progress,
        CancellationToken cancellationToken);
}

public sealed class GitPullerRunnerAdapter : IGitPullerRunner
{
    private readonly GitPullerRunner inner;

    public GitPullerRunnerAdapter(GitPullerRunner inner)
    {
        this.inner = inner;
    }

    public Task<GitPullerRunResult> RunAllAsync(
        GitPullerRunRequest request,
        IProgress<GitPullerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        return inner.RunAllAsync(request, progress, cancellationToken);
    }

    public Task<RepoResult> RetryRepositoryAsync(
        GitPullerRunRequest previousRunRequest,
        string repoPath,
        IProgress<GitPullerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        return inner.RetryRepositoryAsync(previousRunRequest, repoPath, progress, cancellationToken);
    }
}

public sealed class CoreGitPullerSyncService : IGitPullerSyncService
{
    private const string PreferredDefaultLibraryRoot = @"E:\FF14\Repos\Remotes";
    private const string FallbackLibraryRoot = @"E:\FF14\Repos\MyRepos";

    private readonly GitRepositoryScanner scanner;
    private readonly LibraryConfigStore configStore;
    private readonly IGitPullerRunner runner;
    private readonly IGitPullerReportService reportService;
    private readonly Func<string, bool> directoryExists;
    private readonly string? defaultLibraryRoot;

    public CoreGitPullerSyncService(
        GitRepositoryScanner? scanner = null,
        LibraryConfigStore? configStore = null,
        GitPullerRunner? runner = null,
        IGitPullerRunner? gitPullerRunner = null,
        IGitPullerReportService? reportService = null,
        Func<string, bool>? directoryExists = null,
        string? defaultLibraryRoot = null)
    {
        this.scanner = scanner ?? new GitRepositoryScanner();
        this.configStore = configStore ?? new LibraryConfigStore();
        this.runner = gitPullerRunner ?? new GitPullerRunnerAdapter(runner ?? new GitPullerRunner());
        this.reportService = reportService ?? new GitPullerReportService();
        this.directoryExists = directoryExists ?? Directory.Exists;
        this.defaultLibraryRoot = defaultLibraryRoot;
    }

    public string GetDefaultLibraryRoot()
    {
        if (!string.IsNullOrWhiteSpace(defaultLibraryRoot))
        {
            return Path.GetFullPath(defaultLibraryRoot);
        }

        if (directoryExists(PreferredDefaultLibraryRoot))
        {
            return PreferredDefaultLibraryRoot;
        }

        if (directoryExists(FallbackLibraryRoot))
        {
            return FallbackLibraryRoot;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? Environment.CurrentDirectory
            : userProfile;
    }

    public async Task<GitPullerLibraryLoadResult> LoadLibraryAsync(
        string libraryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);

        var config = await configStore.LoadAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var inventory = scanner.ScanLibraryRoot(config.LibraryRoot);
        return new GitPullerLibraryLoadResult(
            config.LibraryRoot,
            config.DefaultOptions,
            inventory,
            config.RemovedRepositories,
            config.Categories);
    }

    public async Task<GitPullerRunResult> RunAllAsync(
        GitPullerRunRequest request,
        IProgress<GitPullerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        var progressProxy = progress is null
            ? null
            : new RunCompletedDeferringProgress(progress);
        var runResult = await runner.RunAllAsync(request, progressProxy, cancellationToken).ConfigureAwait(false);
        GitPullerReportWriteResult? reportResult = null;
        string? warningMessage = null;
        try
        {
            reportResult = reportService.WriteReports(request.Inventory.LibraryRoot, runResult, request.Options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warningMessage = $"Report writing failed: {ex.Message}";
        }

        var completedResult = new GitPullerRunResult
        {
            RepositoryResults = runResult.RepositoryResults,
            StartedAt = runResult.StartedAt,
            CompletedAt = runResult.CompletedAt,
            Elapsed = runResult.Elapsed,
            ErrorMessage = runResult.ErrorMessage,
            WarningMessage = CombineWarningMessages(runResult.WarningMessage, warningMessage),
            LatestReportPath = reportResult?.LatestReportPath ?? runResult.LatestReportPath,
            RunReportPath = reportResult?.RunReportPath ?? runResult.RunReportPath
        };

        progress?.Report(GitPullerProgressEvent.RunCompleted(completedResult));
        return completedResult;
    }

    private static string? CombineWarningMessages(params string?[] warnings)
    {
        var activeWarnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Select(warning => warning!.Trim())
            .ToArray();
        return activeWarnings.Length == 0
            ? null
            : string.Join(" ", activeWarnings);
    }

    public Task<RepoResult> RetryRepositoryAsync(
        GitPullerRunRequest previousRunRequest,
        string repoPath,
        IProgress<GitPullerProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        return runner.RetryRepositoryAsync(previousRunRequest, repoPath, progress, cancellationToken);
    }
}

internal sealed class RunCompletedDeferringProgress : IProgress<GitPullerProgressEvent>
{
    private readonly IProgress<GitPullerProgressEvent> inner;

    public RunCompletedDeferringProgress(IProgress<GitPullerProgressEvent> inner)
    {
        this.inner = inner;
    }

    public void Report(GitPullerProgressEvent value)
    {
        if (value.Kind != GitPullerProgressEventKind.RunCompleted)
        {
            inner.Report(value);
        }
    }
}

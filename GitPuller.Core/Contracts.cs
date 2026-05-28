namespace GitPuller;

public sealed record GitPullerOptions
{
    public int MaxDegreeOfParallelism { get; init; } = 6;
    public bool InitMissingSubmodules { get; init; } = true;
    public bool ForceSync { get; init; } = true;
    public bool CleanUntracked { get; init; } = true;
    public bool PullFfOnly { get; init; } = true;
    public bool SyncAllBranches { get; init; } = true;
    public bool StaleGitLockCleanup { get; init; } = true;
    public bool VerboseReport { get; init; }
    public int GitTimeoutMilliseconds { get; init; } = 60000;
    public TimeSpan StaleGitLockAge { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record GitPullerRunRequest(GitPullerOptions Options, RepositoryInventory Inventory);

public sealed class GitPullerRunResult
{
    public IReadOnlyList<RepoResult> RepositoryResults { get; init; } = Array.Empty<RepoResult>();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? ErrorMessage { get; init; }

    public int TotalRepositories => RepositoryResults.Count;
    public int SuccessCount => RepositoryResults.Count(x => !x.Failed);
    public int FailCount => RepositoryResults.Count(x => x.Failed);
    public int TotalNewCommitsCount => RepositoryResults.Sum(x => x.NewCommitsCount);
    public bool HasFailures => !string.IsNullOrWhiteSpace(ErrorMessage) || FailCount > 0;
}

public enum GitPullerProgressEventKind
{
    RunStarted,
    RepositoryStarted,
    RepositoryCompleted,
    RunCompleted
}

public sealed class GitPullerProgressEvent
{
    public GitPullerProgressEventKind Kind { get; private init; }
    public int TotalRepositories { get; private init; }
    public int CompletedRepositories { get; private init; }
    public RepositoryDescriptor? Repository { get; private init; }
    public RepoResult? RepositoryResult { get; private init; }
    public GitPullerRunResult? RunResult { get; private init; }
    public string? Message { get; private init; }
    public bool IsWarning { get; private init; }
    public bool IsError { get; private init; }

    private GitPullerProgressEvent()
    {
    }

    public static GitPullerProgressEvent RunStarted(int totalRepositories, string? message = null, bool isWarning = false, bool isError = false)
    {
        return new GitPullerProgressEvent
        {
            Kind = GitPullerProgressEventKind.RunStarted,
            TotalRepositories = totalRepositories,
            Message = message,
            IsWarning = isWarning,
            IsError = isError
        };
    }

    public static GitPullerProgressEvent RepositoryStarted(RepositoryDescriptor repository, int totalRepositories, int completedRepositories)
    {
        return new GitPullerProgressEvent
        {
            Kind = GitPullerProgressEventKind.RepositoryStarted,
            Repository = repository,
            TotalRepositories = totalRepositories,
            CompletedRepositories = completedRepositories
        };
    }

    public static GitPullerProgressEvent RepositoryCompleted(RepositoryDescriptor repository, RepoResult repositoryResult, int totalRepositories, int completedRepositories)
    {
        return new GitPullerProgressEvent
        {
            Kind = GitPullerProgressEventKind.RepositoryCompleted,
            Repository = repository,
            RepositoryResult = repositoryResult,
            TotalRepositories = totalRepositories,
            CompletedRepositories = completedRepositories
        };
    }

    public static GitPullerProgressEvent RunCompleted(GitPullerRunResult runResult)
    {
        return new GitPullerProgressEvent
        {
            Kind = GitPullerProgressEventKind.RunCompleted,
            RunResult = runResult,
            TotalRepositories = runResult.TotalRepositories,
            CompletedRepositories = runResult.TotalRepositories,
            IsError = !string.IsNullOrWhiteSpace(runResult.ErrorMessage)
        };
    }
}

public sealed record RepositoryInventory(string LibraryRoot, IReadOnlyList<RepositoryDescriptor> Repositories);

public sealed record RepositoryDescriptor(string Path, string Name, string Category, string? RemoteUrl);

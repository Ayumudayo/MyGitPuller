using System.Threading;

namespace GitPuller;

public sealed class RepoResult
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int NewCommitsCount { get; set; }
    public bool Failed { get; set; }
    public int WorkerSlot { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<RepoOperation> Operations { get; } = new();
    public List<LogItem> Logs { get; } = new();
}

public sealed class RepoOperation
{
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
}

internal sealed class RemoteBranchRef
{
    public string RemoteName { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string LocalBranchName { get; set; } = string.Empty;
    public string RemoteShortName { get; set; } = string.Empty;
    public string RemoteRefName { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
}

internal sealed class RepoMutexLease : IDisposable
{
    private readonly Mutex mutex;
    private bool disposed;

    public RepoMutexLease(Mutex mutex)
    {
        this.mutex = mutex;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }
}

public sealed class LogItem
{
    public string Text { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public bool IsWarning { get; set; }
    public bool IsCommit { get; set; }
}

using GitPuller;

namespace GitPuller_WinUI.ViewModels;

public enum RepositoryResultStatus
{
    Failed,
    Warning,
    Updated,
    Clean
}

public sealed class RepositoryResultViewModel
{
    public RepositoryResultViewModel(
        string name,
        string category,
        string path,
        string remoteUrl,
        RepositoryResultStatus status,
        int newCommitsCount,
        TimeSpan elapsed,
        FailureDiagnostic? diagnostic,
        IEnumerable<string>? logLines = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "(unnamed repository)" : name;
        Category = string.IsNullOrWhiteSpace(category) ? "(uncategorized)" : category;
        Path = path;
        RemoteUrl = remoteUrl;
        Status = status;
        NewCommitsCount = newCommitsCount;
        Elapsed = elapsed;
        Diagnostic = diagnostic;
        LogLines = (logLines ?? Array.Empty<string>())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    public string Name { get; }
    public string Category { get; }
    public string Path { get; }
    public string RemoteUrl { get; }
    public RepositoryResultStatus Status { get; }
    public int NewCommitsCount { get; }
    public TimeSpan Elapsed { get; }
    public FailureDiagnostic? Diagnostic { get; }
    public IReadOnlyList<string> LogLines { get; }
    public RetryActionState RetryActionState => RetryPolicyPresentation.GetRetryActionState(Diagnostic);
    public bool CanRetry => RetryActionState != RetryActionState.Disabled;
    public bool IsRetryPrimary => RetryActionState == RetryActionState.EnabledPrimary;
    public string StatusText => Status.ToString();
    public string RetryButtonText => IsRetryPrimary ? "Retry now" : "Retry";
    public string ElapsedText => Elapsed.TotalSeconds < 1
        ? "under 1s"
        : $"{Elapsed.TotalSeconds:0.0}s";

    public string Summary => Status switch
    {
        RepositoryResultStatus.Failed => Diagnostic?.Title ?? "Repository failed",
        RepositoryResultStatus.Warning => Diagnostic?.Title ?? "Repository completed with warnings",
        RepositoryResultStatus.Updated => NewCommitsCount == 1
            ? "1 new commit"
            : $"{NewCommitsCount} new commits",
        _ => "Clean"
    };

    public string DiagnosticExplanation => Diagnostic?.Explanation ?? "No diagnostic details are available.";
    public string SuggestedAction => Diagnostic?.SuggestedAction ?? "No action is required.";
    public string Evidence => Diagnostic?.Evidence ?? "No diagnostic evidence was recorded.";
    public string RelatedCommand => Diagnostic?.RelatedCommand ?? "No related command was recorded.";

    public static RepositoryResultViewModel FromResult(RepoResult result, RepositoryDescriptor? repository = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var status = GetStatus(result);
        var category = repository?.Category ?? "(uncategorized)";
        var remoteUrl = repository?.RemoteUrl ?? string.Empty;
        var logLines = result.Logs.Select(log => log.Text);

        return new RepositoryResultViewModel(
            result.Name,
            category,
            result.Path,
            remoteUrl,
            status,
            result.NewCommitsCount,
            result.Elapsed,
            result.Diagnostic,
            logLines);
    }

    private static RepositoryResultStatus GetStatus(RepoResult result)
    {
        if (result.Failed || result.Diagnostic?.Severity == DiagnosticSeverity.Error)
        {
            return RepositoryResultStatus.Failed;
        }

        if (result.Diagnostic?.Severity == DiagnosticSeverity.Warning || result.Logs.Any(log => log.IsWarning))
        {
            return RepositoryResultStatus.Warning;
        }

        return result.NewCommitsCount > 0
            ? RepositoryResultStatus.Updated
            : RepositoryResultStatus.Clean;
    }
}

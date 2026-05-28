namespace GitPuller;

public enum FailureCategory
{
    None,
    LockExistsRecent,
    StaleLockRemoved,
    AuthenticationFailure,
    NetworkTimeout,
    RemoteNotFoundOrNoAccess,
    ClonePathConflict,
    SubmoduleFailure,
    UnknownGitFailure
}

public enum RetryPolicy
{
    NotApplicable,
    Recommended,
    PossibleAfterCheck,
    BlockedUntilAction,
    Unknown
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record FailureDiagnostic(
    FailureCategory Category,
    RetryPolicy RetryPolicy,
    DiagnosticSeverity Severity,
    string Title,
    string Explanation,
    string SuggestedAction,
    string Evidence,
    string? RelatedPath,
    string? RelatedCommand);

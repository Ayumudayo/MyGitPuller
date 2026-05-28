namespace GitPuller;

public enum RetryActionState
{
    Disabled,
    EnabledSecondary,
    EnabledPrimary
}

public static class RetryPolicyPresentation
{
    public static RetryActionState GetRetryActionState(FailureDiagnostic? diagnostic)
    {
        var policy = diagnostic?.RetryPolicy ?? RetryPolicy.NotApplicable;
        return policy switch
        {
            RetryPolicy.Recommended => RetryActionState.EnabledPrimary,
            RetryPolicy.PossibleAfterCheck => RetryActionState.EnabledSecondary,
            RetryPolicy.Unknown => RetryActionState.EnabledSecondary,
            _ => RetryActionState.Disabled
        };
    }
}

public static class GitFailureClassifier
{
    public static FailureDiagnostic Classify(RepoResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var logTexts = result.Logs
            .Select(log => log.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        var hasStaleLockRemoved = TryFindMatch(logTexts, IsStaleLockRemovedText, out var staleLockRemovedEvidence);
        var hasStaleLockCleanupFailure = TryFindMatch(logTexts, IsStaleLockCleanupFailureText, out var staleLockCleanupFailureEvidence);

        if (!result.Failed)
        {
            if (hasStaleLockRemoved)
            {
                return CreateDiagnostic(
                    category: FailureCategory.StaleLockRemoved,
                    retryPolicy: RetryPolicy.NotApplicable,
                    severity: DiagnosticSeverity.Warning,
                    title: "Stale Git lock file was removed",
                    explanation: "A stale Git lock file was removed and the repository completed without a blocking error.",
                    suggestedAction: "No retry is needed unless later operations fail again.",
                    evidence: staleLockRemovedEvidence,
                    result,
                    relatedCommandSelector: null);
            }

            if (hasStaleLockCleanupFailure)
            {
                return CreateRecentLockDiagnostic(staleLockCleanupFailureEvidence, result);
            }

            return CreateDiagnostic(
                category: FailureCategory.None,
                retryPolicy: RetryPolicy.NotApplicable,
                severity: DiagnosticSeverity.Info,
                title: "No retry needed",
                explanation: "No failure signals were detected for this repository.",
                suggestedAction: "No action required.",
                evidence: "No failure signals detected.",
                result,
                relatedCommandSelector: null);
        }

        if (TryFindMatch(logTexts, IsRecentLockFailureText, out var recentLockEvidence))
        {
            return CreateDiagnostic(
                category: FailureCategory.LockExistsRecent,
                retryPolicy: RetryPolicy.PossibleAfterCheck,
                severity: DiagnosticSeverity.Error,
                title: "Git lock is still active",
                explanation: "Git reported an active lock file or another Git process still using the repository.",
                suggestedAction: "Check whether another Git process is still running or whether the lock file is genuinely recent before retrying.",
                evidence: recentLockEvidence,
                result,
                relatedCommandSelector: operation => operation.ExitCode != 0 || operation.TimedOut);
        }

        if (TryFindMatch(logTexts, IsAuthenticationFailureText, out var authenticationEvidence))
        {
            return CreateDiagnostic(
                category: FailureCategory.AuthenticationFailure,
                retryPolicy: RetryPolicy.BlockedUntilAction,
                severity: DiagnosticSeverity.Error,
                title: "Authentication to the remote failed",
                explanation: "Git could not authenticate with the configured remote.",
                suggestedAction: "Fix the remote credentials, SSH key, or token access before retrying.",
                evidence: authenticationEvidence,
                result,
                relatedCommandSelector: operation => operation.ExitCode != 0 || operation.TimedOut);
        }

        if (TryGetNetworkTimeoutEvidence(result, logTexts, out var timeoutEvidence))
        {
            return CreateDiagnostic(
                category: FailureCategory.NetworkTimeout,
                retryPolicy: RetryPolicy.Recommended,
                severity: DiagnosticSeverity.Error,
                title: "Git operation timed out",
                explanation: "The repository failed because a Git network or transport operation timed out.",
                suggestedAction: "Retry this repository. If the timeout repeats, check network reachability and remote responsiveness.",
                evidence: timeoutEvidence,
                result,
                relatedCommandSelector: operation => operation.TimedOut && IsTransportCommand(operation.Command));
        }

        if (TryFindMatch(logTexts, IsClonePathConflictText, out var clonePathConflictEvidence))
        {
            return CreateDiagnostic(
                category: FailureCategory.ClonePathConflict,
                retryPolicy: RetryPolicy.BlockedUntilAction,
                severity: DiagnosticSeverity.Error,
                title: "Clone destination path is blocked",
                explanation: "The repository path already exists or contains a non-Git folder that blocks clone or initialization work.",
                suggestedAction: "Resolve the existing folder conflict before retrying.",
                evidence: clonePathConflictEvidence,
                result,
                relatedCommandSelector: operation => operation.ExitCode != 0 || operation.TimedOut);
        }

        if (TryFindMatch(logTexts, IsSubmoduleFailureText, out var submoduleFailureEvidence))
        {
            return CreateDiagnostic(
                category: FailureCategory.SubmoduleFailure,
                retryPolicy: RetryPolicy.PossibleAfterCheck,
                severity: DiagnosticSeverity.Error,
                title: "Submodule operation failed",
                explanation: "A submodule fetch or update reported a failure.",
                suggestedAction: "Inspect the failing submodule path or remote state, then retry after checking the submodule configuration.",
                evidence: submoduleFailureEvidence,
                result,
                relatedCommandSelector: operation => operation.ExitCode != 0 || operation.TimedOut);
        }

        if (TryFindMatch(logTexts, IsRemoteNotFoundOrNoAccessText, out var remoteNotFoundEvidence))
        {
            return CreateDiagnostic(
                category: FailureCategory.RemoteNotFoundOrNoAccess,
                retryPolicy: RetryPolicy.BlockedUntilAction,
                severity: DiagnosticSeverity.Error,
                title: "Remote repository was not found or access was denied",
                explanation: "Git reported that the remote repository could not be found or that access was denied.",
                suggestedAction: "Verify the remote URL, repository existence, and your access rights before retrying.",
                evidence: remoteNotFoundEvidence,
                result,
                relatedCommandSelector: operation => operation.ExitCode != 0 || operation.TimedOut);
        }

        if (hasStaleLockRemoved)
        {
            return CreateDiagnostic(
                category: FailureCategory.StaleLockRemoved,
                retryPolicy: RetryPolicy.Recommended,
                severity: DiagnosticSeverity.Warning,
                title: "Stale Git lock file was removed",
                explanation: "A stale Git lock file was removed before the repository still failed, and no later specific Git failure category matched.",
                suggestedAction: "Retry this repository once. If it fails again, inspect the latest Git error before continuing.",
                evidence: staleLockRemovedEvidence,
                result,
                relatedCommandSelector: operation => operation.TimedOut || operation.ExitCode != 0);
        }

        if (hasStaleLockCleanupFailure)
        {
            return CreateRecentLockDiagnostic(staleLockCleanupFailureEvidence, result);
        }

        var unknownEvidence = logTexts.LastOrDefault()
            ?? "Repository failed without a matching Git failure pattern.";

        return CreateDiagnostic(
            category: FailureCategory.UnknownGitFailure,
            retryPolicy: RetryPolicy.Unknown,
            severity: DiagnosticSeverity.Error,
            title: "Git failure could not be classified",
            explanation: "The repository failed, but the current classifier did not recognize the failure pattern.",
            suggestedAction: "Inspect the repository log details before retrying.",
            evidence: unknownEvidence,
            result,
            relatedCommandSelector: operation => operation.ExitCode != 0 || operation.TimedOut);
    }

    private static FailureDiagnostic CreateDiagnostic(
        FailureCategory category,
        RetryPolicy retryPolicy,
        DiagnosticSeverity severity,
        string title,
        string explanation,
        string suggestedAction,
        string evidence,
        RepoResult result,
        Func<RepoOperation, bool>? relatedCommandSelector)
    {
        return new FailureDiagnostic(
            category,
            retryPolicy,
            severity,
            title,
            explanation,
            suggestedAction,
            evidence,
            RelatedPath: string.IsNullOrWhiteSpace(result.Path) ? null : result.Path,
            RelatedCommand: GetRelatedCommand(result, relatedCommandSelector));
    }

    private static FailureDiagnostic CreateRecentLockDiagnostic(string evidence, RepoResult result)
    {
        return CreateDiagnostic(
            category: FailureCategory.LockExistsRecent,
            retryPolicy: RetryPolicy.PossibleAfterCheck,
            severity: result.Failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            title: "Git lock needs review",
            explanation: "Git reported a lock file that still needs a user check before retrying.",
            suggestedAction: "Check whether another Git process is still running or whether the lock file can be removed before retrying.",
            evidence: evidence,
            result,
            relatedCommandSelector: operation => operation.ExitCode != 0 || operation.TimedOut);
    }

    private static string? GetRelatedCommand(RepoResult result, Func<RepoOperation, bool>? relatedCommandSelector)
    {
        if (result.Operations.Count == 0)
        {
            return null;
        }

        if (relatedCommandSelector != null)
        {
            var matchedOperation = result.Operations.LastOrDefault(relatedCommandSelector);
            if (matchedOperation != null && !string.IsNullOrWhiteSpace(matchedOperation.Command))
            {
                return matchedOperation.Command;
            }
        }

        return result.Operations
            .LastOrDefault(operation => !string.IsNullOrWhiteSpace(operation.Command))
            ?.Command;
    }

    private static bool TryFindMatch(IEnumerable<string> texts, Func<string, bool> matcher, out string evidence)
    {
        foreach (var text in texts)
        {
            if (matcher(text))
            {
                evidence = text;
                return true;
            }
        }

        evidence = string.Empty;
        return false;
    }

    private static bool IsRecentLockFailureText(string text)
    {
        if (IsStaleLockRemovedText(text) || IsStaleLockCleanupFailureText(text))
        {
            return false;
        }

        return Contains(text, ".lock")
            || (Contains(text, "file exists") && Contains(text, "lock"))
            || Contains(text, "another git process seems to be running");
    }

    private static bool IsStaleLockRemovedText(string text)
    {
        return Contains(text, "removed stale git lock file");
    }

    private static bool IsStaleLockCleanupFailureText(string text)
    {
        return Contains(text, "could not remove stale git lock file");
    }

    private static bool IsAuthenticationFailureText(string text)
    {
        return Contains(text, "permission denied")
            || Contains(text, "authentication failed")
            || Contains(text, "could not read from remote repository");
    }

    private static bool IsNetworkTimeoutText(string text)
    {
        return Contains(text, "timeout (")
            || Contains(text, "operation timed out")
            || Contains(text, "connection timed out")
            || (Contains(text, "unable to access") && Contains(text, "timed out"));
    }

    private static bool TryGetNetworkTimeoutEvidence(RepoResult result, IEnumerable<string> texts, out string evidence)
    {
        foreach (var text in texts)
        {
            if (!IsNetworkTimeoutText(text))
            {
                continue;
            }

            if (Contains(text, "unable to access"))
            {
                evidence = text;
                return true;
            }

            var commandFromText = ExtractCommandFromTimeoutText(text);
            if (IsTransportCommand(commandFromText))
            {
                evidence = text;
                return true;
            }
        }

        var timedOutTransportOperation = result.Operations.LastOrDefault(operation =>
            operation.TimedOut && IsTransportCommand(operation.Command));
        if (timedOutTransportOperation != null)
        {
            evidence = "A transport Git command timed out without a matching log entry.";
            return true;
        }

        evidence = string.Empty;
        return false;
    }

    private static string? ExtractCommandFromTimeoutText(string text)
    {
        const string marker = "Command:";
        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var commandText = text[(markerIndex + marker.Length)..]
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(commandText) ? null : commandText.Trim();
    }

    private static bool IsTransportCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return command.IndexOf("git fetch", StringComparison.OrdinalIgnoreCase) >= 0
            || command.IndexOf("git pull", StringComparison.OrdinalIgnoreCase) >= 0
            || command.IndexOf("submodule fetch", StringComparison.OrdinalIgnoreCase) >= 0
            || command.IndexOf("submodule update", StringComparison.OrdinalIgnoreCase) >= 0
            || command.IndexOf("lfs fetch", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsRemoteNotFoundOrNoAccessText(string text)
    {
        return Contains(text, "repository not found")
            || Contains(text, "not found")
            || Contains(text, "access denied");
    }

    private static bool IsClonePathConflictText(string text)
    {
        return Contains(text, "destination path")
            || Contains(text, "already exists and is not an empty directory")
            || Contains(text, "existing non-git folder");
    }

    private static bool IsSubmoduleFailureText(string text)
    {
        return Contains(text, "submodule update failed")
            || Contains(text, "submodule fetch failed");
    }

    private static bool Contains(string text, string value)
    {
        return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

using GitPuller;

namespace GitPuller.Core.Tests;

public sealed class GitFailureClassifierTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "MyGitPuller.Core.Tests", Guid.NewGuid().ToString("N"));

    public GitFailureClassifierTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void Classify_ReturnsNone_ForSuccessfulCleanRepository()
    {
        var result = CreateResult(failed: false, "Already up to date.");

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.None, diagnostic.Category);
        Assert.Equal(RetryPolicy.NotApplicable, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal(result.Path, diagnostic.RelatedPath);
        Assert.Null(diagnostic.RelatedCommand);
    }

    [Fact]
    public void Classify_ReturnsWarningOnlyStaleLockRemoved_ForSuccessfulRepository()
    {
        var result = CreateResult(
            failed: false,
            "Removed stale Git lock file (18.0 min old): C:\\Repos\\RepoA\\.git\\index.lock");

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.StaleLockRemoved, diagnostic.Category);
        Assert.Equal(RetryPolicy.NotApplicable, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("stale", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".lock", diagnostic.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_PrefersStaleLockRemovedRetry_WhenRepositoryStillFails()
    {
        var result = CreateResult(
            failed: true,
            "Removed stale Git lock file (18.0 min old): C:\\Repos\\RepoA\\.git\\index.lock",
            "Fetch failed after retries:\nfatal: could not read from remote repository");

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.StaleLockRemoved, diagnostic.Category);
        Assert.Equal(RetryPolicy.Recommended, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Theory]
    [InlineData("fatal: Unable to create 'C:/Repos/RepoA/.git/index.lock': File exists.")]
    [InlineData("another git process seems to be running in this repository")]
    public void Classify_ClassifiesRecentLockFailures_CaseInsensitive(string evidence)
    {
        var result = CreateResult(failed: true, evidence);

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.LockExistsRecent, diagnostic.Category);
        Assert.Equal(RetryPolicy.PossibleAfterCheck, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(evidence.Split('\n')[0], diagnostic.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Permission denied (publickey).")]
    [InlineData("fatal: Authentication failed for 'https://example.invalid/repo.git/'")]
    [InlineData("fatal: Could not read from remote repository.")]
    public void Classify_ClassifiesAuthenticationFailures(string evidence)
    {
        var result = CreateResult(failed: true, evidence);

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.AuthenticationFailure, diagnostic.Category);
        Assert.Equal(RetryPolicy.BlockedUntilAction, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(result.Path, diagnostic.RelatedPath);
    }

    [Theory]
    [InlineData("Timeout (60s)\nCommand: git fetch --all --prune --prune-tags --tags --force")]
    [InlineData("fatal: unable to access 'https://example.invalid/repo.git/': Operation timed out after 30001 milliseconds with 0 bytes received")]
    public void Classify_ClassifiesNetworkTimeouts(string evidence)
    {
        var result = CreateResult(failed: true, evidence);
        result.Operations.Add(new RepoOperation
        {
            Command = "git fetch --all --prune --prune-tags --tags --force",
            WorkingDirectory = result.Path,
            ExitCode = -1,
            TimedOut = true
        });

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.NetworkTimeout, diagnostic.Category);
        Assert.Equal(RetryPolicy.Recommended, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("git fetch --all --prune --prune-tags --tags --force", diagnostic.RelatedCommand);
    }

    [Theory]
    [InlineData("fatal: Repository not found.")]
    [InlineData("remote: not found")]
    [InlineData("fatal: access denied")]
    public void Classify_ClassifiesRemoteNotFoundOrNoAccess(string evidence)
    {
        var result = CreateResult(failed: true, evidence);

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.RemoteNotFoundOrNoAccess, diagnostic.Category);
        Assert.Equal(RetryPolicy.BlockedUntilAction, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("fatal: destination path 'RepoA' already exists and is not an empty directory.")]
    [InlineData("existing non-Git folder blocks clone target")]
    public void Classify_ClassifiesClonePathConflict(string evidence)
    {
        var result = CreateResult(failed: true, evidence);

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.ClonePathConflict, diagnostic.Category);
        Assert.Equal(RetryPolicy.BlockedUntilAction, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData("Submodule update failed:\nfatal: transport shutdown")]
    [InlineData("Submodule fetch failed (deps/libA):\nfatal: no such remote")]
    public void Classify_ClassifiesSubmoduleFailures(string evidence)
    {
        var result = CreateResult(failed: true, evidence);

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.SubmoduleFailure, diagnostic.Category);
        Assert.Equal(RetryPolicy.PossibleAfterCheck, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Classify_FallsBackToUnknown_ForUnmatchedFailedRepository()
    {
        var result = CreateResult(failed: true, "fatal: an unexpected git failure happened");

        var diagnostic = GitFailureClassifier.Classify(result);

        Assert.Equal(FailureCategory.UnknownGitFailure, diagnostic.Category);
        Assert.Equal(RetryPolicy.Unknown, diagnostic.RetryPolicy);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("unexpected git failure", diagnostic.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RetryPolicy.Recommended, RetryActionState.EnabledPrimary)]
    [InlineData(RetryPolicy.PossibleAfterCheck, RetryActionState.EnabledSecondary)]
    [InlineData(RetryPolicy.BlockedUntilAction, RetryActionState.Disabled)]
    [InlineData(RetryPolicy.Unknown, RetryActionState.EnabledSecondary)]
    [InlineData(RetryPolicy.NotApplicable, RetryActionState.Disabled)]
    public void RetryPolicyPresentation_MapsPoliciesToRetryActionState(RetryPolicy policy, RetryActionState expected)
    {
        var diagnostic = new FailureDiagnostic(
            FailureCategory.UnknownGitFailure,
            policy,
            DiagnosticSeverity.Error,
            "title",
            "explanation",
            "action",
            "evidence",
            RelatedPath: null,
            RelatedCommand: null);

        var state = RetryPolicyPresentation.GetRetryActionState(diagnostic);

        Assert.Equal(expected, state);
    }

    [Fact]
    public async Task RunRepositoryAsync_AttachesUnknownDiagnostic_WhenRepositoryIsOutsideInventory()
    {
        var libraryRoot = Path.Combine(tempRoot, "inventory-root");
        Directory.CreateDirectory(libraryRoot);

        var inventoryRepository = Path.Combine(libraryRoot, "RepoInInventory");
        var outsideRepository = Path.Combine(tempRoot, "outside-root", "RepoOutsideInventory");

        var request = new GitPullerRunRequest(
            new GitPullerOptions(),
            new RepositoryInventory(
                libraryRoot,
                new[]
                {
                    new RepositoryDescriptor(inventoryRepository, "RepoInInventory", string.Empty, RemoteUrl: null)
                }));

        var runner = new GitPullerRunner();
        var result = await runner.RunRepositoryAsync(request, outsideRepository, progress: null, CancellationToken.None);

        Assert.True(result.Failed);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(FailureCategory.UnknownGitFailure, result.Diagnostic.Category);
        Assert.Equal(RetryPolicy.Unknown, result.Diagnostic.RetryPolicy);
        Assert.Equal(outsideRepository, result.Diagnostic.RelatedPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private RepoResult CreateResult(bool failed, params string[] logLines)
    {
        var result = new RepoResult
        {
            Path = Path.Combine(tempRoot, "RepoA"),
            Name = "RepoA",
            Failed = failed
        };

        foreach (var logLine in logLines)
        {
            result.Logs.Add(new LogItem
            {
                Text = logLine,
                IsError = failed,
                IsWarning = !failed
            });
        }

        return result;
    }
}

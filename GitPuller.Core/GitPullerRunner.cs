using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitPuller;

public sealed class GitPullerRunner
{
    private const int GitLockRetryCount = 3;
    private const int GitLockRetryDelayMs = 1000;

    private readonly ConcurrentDictionary<int, int> workerSlotsByThreadId = new();
    private readonly RemoteRefsSnapshotReader remoteRefsSnapshotReader;
    private int nextWorkerSlot;

    public GitPullerRunner()
        : this(ReadRemoteRefsSnapshot)
    {
    }

    internal GitPullerRunner(RemoteRefsSnapshotReader? remoteRefsSnapshotReader)
    {
        this.remoteRefsSnapshotReader = remoteRefsSnapshotReader ?? ReadRemoteRefsSnapshot;
    }

    internal delegate RemoteRefsSnapshot RemoteRefsSnapshotReader(
        string repoPath,
        GitPullerOptions options,
        RepoResult? result,
        CancellationToken cancellationToken);

    internal sealed record RemoteRefsSnapshot(
        bool Succeeded,
        Dictionary<string, string> Refs,
        string Output);

    public Task<GitPullerRunResult> RunAllAsync(GitPullerRunRequest request, IProgress<GitPullerProgressEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => RunAll(request, progress, cancellationToken), cancellationToken);
    }

    public Task<RepoResult> RunRepositoryAsync(GitPullerRunRequest request, string repoPath, IProgress<GitPullerProgressEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repository = FindInventoryRepository(request, repoPath);
            if (repository == null)
            {
                var rejectedRepository = GitRepositorySupport.CreateRepositoryDescriptor(request.Inventory.LibraryRoot, repoPath, remoteUrl: null);
                progress?.Report(GitPullerProgressEvent.RepositoryStarted(rejectedRepository, 1, 0));
                var rejectedResult = RejectRepository(
                    rejectedRepository,
                    $"Repository is not part of the selected repository inventory: {GitRepositorySupport.NormalizeRepoPath(repoPath)}",
                    cancellationToken);
                progress?.Report(GitPullerProgressEvent.RepositoryCompleted(rejectedRepository, rejectedResult, 1, 1));
                return rejectedResult;
            }

            progress?.Report(GitPullerProgressEvent.RepositoryStarted(repository, 1, 0));
            var result = RunRepository(repository, request.Options, cancellationToken);
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(repository, result, 1, 1));
            return result;
        }, cancellationToken);
    }

    public Task<RepoResult> RetryRepositoryAsync(GitPullerRunRequest previousRunRequest, string repoPath, IProgress<GitPullerProgressEvent>? progress, CancellationToken cancellationToken)
    {
        return RunRepositoryAsync(previousRunRequest, repoPath, progress, cancellationToken);
    }

    private static RepositoryDescriptor? FindInventoryRepository(GitPullerRunRequest request, string repoPath)
    {
        var normalizedRepoPath = GitRepositorySupport.NormalizeRepoPath(repoPath);
        return request.Inventory.Repositories.FirstOrDefault(repository =>
            string.Equals(GitRepositorySupport.NormalizeRepoPath(repository.Path), normalizedRepoPath, StringComparison.OrdinalIgnoreCase));
    }

    private GitPullerRunResult RunAll(GitPullerRunRequest request, IProgress<GitPullerProgressEvent>? progress, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var repositories = request.Inventory.Repositories;
        progress?.Report(GitPullerProgressEvent.RunStarted(repositories.Count));

        var results = new ConcurrentBag<RepoResult>();
        var completedRepositories = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = request.Options.MaxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        Parallel.ForEach(repositories, options, repository =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(GitPullerProgressEvent.RepositoryStarted(repository, repositories.Count, Volatile.Read(ref completedRepositories)));

            var result = RunRepository(repository, request.Options, cancellationToken);
            results.Add(result);
            var completed = Interlocked.Increment(ref completedRepositories);
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(repository, result, repositories.Count, completed));
        });

        stopwatch.Stop();
        var runResult = new GitPullerRunResult
        {
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.Now,
            Elapsed = stopwatch.Elapsed,
            RepositoryResults = results.ToList()
        };

        progress?.Report(GitPullerProgressEvent.RunCompleted(runResult));
        return runResult;
    }

    private int GetWorkerSlot()
    {
        var threadId = Environment.CurrentManagedThreadId;
        return workerSlotsByThreadId.GetOrAdd(threadId, _ => Interlocked.Increment(ref nextWorkerSlot));
    }

    private RepoResult RunRepository(RepositoryDescriptor repository, GitPullerOptions options, CancellationToken cancellationToken)
    {
        return FinalizeRepositoryResult(() => ProcessRepository(repository, options, remoteRefsSnapshotReader, cancellationToken));
    }

    private RepoResult RejectRepository(RepositoryDescriptor repository, string message, CancellationToken cancellationToken)
    {
        return FinalizeRepositoryResult(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new RepoResult
            {
                Path = repository.Path,
                Name = repository.Name,
                Failed = true
            };
            result.Logs.Add(new LogItem { Text = message, IsError = true });
            return result;
        });
    }

    private RepoResult FinalizeRepositoryResult(Func<RepoResult> createResult)
    {
        var repositoryStartedAt = DateTimeOffset.Now;
        var repositoryStopwatch = Stopwatch.StartNew();
        var result = createResult();
        repositoryStopwatch.Stop();

        result.WorkerSlot = GetWorkerSlot();
        result.StartedAt = repositoryStartedAt;
        result.CompletedAt = DateTimeOffset.Now;
        result.Elapsed = repositoryStopwatch.Elapsed;
        result.Diagnostic = GitFailureClassifier.Classify(result);
        return result;
    }

    private static RepoResult ProcessRepository(
        RepositoryDescriptor repository,
        GitPullerOptions options,
        RemoteRefsSnapshotReader remoteRefsSnapshotReader,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new RepoResult
        {
            Path = repository.Path,
            Name = repository.Name
        };

        if (!GitRepositorySupport.IsGitRepoRoot(repository.Path, out var isSubmoduleRepo) || isSubmoduleRepo)
        {
            result.Failed = true;
            result.Logs.Add(new LogItem { Text = "Not a supported git repository.", IsError = true });
            return result;
        }

        using var repoLease = TryAcquireRepoMutex(repository.Path, options, result);
        if (repoLease == null)
        {
            return result;
        }

        TryCleanupStaleGitLocks(repository.Path, options, result);

        var beforeRefs = remoteRefsSnapshotReader(repository.Path, options, result, cancellationToken);
        if (!beforeRefs.Succeeded)
        {
            result.Logs.Add(new LogItem
            {
                Text = $"Could not snapshot remote refs before fetch; commit delta calculation will be skipped.\n{beforeRefs.Output}",
                IsWarning = true
            });
        }
        var retries = 3;
        var rc = -1;
        var outputText = string.Empty;

        while (retries > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (rc, outputText) = RunGitWithSshToHttpsFallback(repository.Path, "fetch --all --prune --prune-tags --tags --force", options, result, cancellationToken);
            if (rc == 0)
            {
                break;
            }

            if (retries < 3)
            {
                RunGitWithSshToHttpsFallback(repository.Path, "remote prune origin", options, result, cancellationToken);
            }

            retries--;
            if (retries > 0)
            {
                WaitBeforeRetry(GitLockRetryDelayMs, cancellationToken);
            }
        }

        if (rc != 0)
        {
            result.Failed = true;
            result.Logs.Add(new LogItem { Text = $"Fetch failed after retries:\n{outputText}", IsError = true });
            return result;
        }

        TryFetchLfsObjects(repository.Path, options, result, cancellationToken);

        var afterRefs = remoteRefsSnapshotReader(repository.Path, options, result, cancellationToken);
        if (!afterRefs.Succeeded)
        {
            result.Logs.Add(new LogItem
            {
                Text = $"Could not snapshot remote refs after fetch; commit delta calculation and local branch sync will be skipped.\n{afterRefs.Output}",
                IsWarning = true
            });
        }

        var seenCommits = new HashSet<string>(StringComparer.Ordinal);

        if (beforeRefs.Succeeded && afterRefs.Succeeded)
        {
            foreach (var kvp in afterRefs.Refs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refName = kvp.Key;
                var newSha = kvp.Value;

                if (refName.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!beforeRefs.Refs.TryGetValue(refName, out var oldSha))
                {
                    var (rcLog, logOutput) = RunGit(repository.Path, $"log -1 --format=\"%h %s (%an)\" {newSha}", options, result, cancellationToken);
                    if (rcLog == 0 && !string.IsNullOrWhiteSpace(logOutput))
                    {
                        ParseAndAddCommits(result, logOutput, seenCommits);
                    }
                }
                else if (!string.Equals(oldSha, newSha, StringComparison.OrdinalIgnoreCase))
                {
                    var (rcLog, logOutput) = RunGit(repository.Path, $"log --format=\"%h %s (%an)\" {oldSha}..{newSha}", options, result, cancellationToken);
                    if (rcLog == 0 && !string.IsNullOrWhiteSpace(logOutput))
                    {
                        ParseAndAddCommits(result, logOutput, seenCommits);
                    }
                }
            }
        }

        if (options.PullFfOnly)
        {
            if (options.ForceSync)
            {
                TrySyncWorkingTree(repository.Path, options, result, cancellationToken);
                if (options.SyncAllBranches)
                {
                    TrySyncLocalBranchesIfRefsAvailable(repository.Path, afterRefs, options, result, cancellationToken);
                }
            }
            else
            {
                if (options.SyncAllBranches)
                {
                    TrySyncLocalBranchesIfRefsAvailable(repository.Path, afterRefs, options, result, cancellationToken);
                }

                TrySyncWorkingTree(repository.Path, options, result, cancellationToken);
            }
        }

        TryUpdateSubmodules(repository.Path, options, result, cancellationToken);

        var (rcMod, outMod) = RunGit(repository.Path, "submodule status --recursive", options, result, cancellationToken);
        if (rcMod == 0)
        {
            using var reader = new StringReader(outMod);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.TrimStart().StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line.Trim().Split(' ');
                if (parts.Length > 1)
                {
                    result.Logs.Add(new LogItem { Text = $"Uninitialized submodule: {parts[1]}", IsWarning = true });
                }
            }
        }

        return result;
    }

    private static RepoMutexLease? TryAcquireRepoMutex(string repoPath, GitPullerOptions options, RepoResult result)
    {
        string normalized;
        try
        {
            normalized = GitRepositorySupport.GetRepoMutexIdentityPath(repoPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            normalized = repoPath.ToUpperInvariant();
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var mutexName = $"MyGitPuller_{hash}";
        var mutex = new Mutex(false, mutexName);

        try
        {
            if (!mutex.WaitOne(options.GitTimeoutMilliseconds))
            {
                mutex.Dispose();
                result.Failed = true;
                result.Logs.Add(new LogItem
                {
                    Text = $"Timed out waiting for another GitPuller worker using this repository: {repoPath}",
                    IsError = true
                });
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            result.Logs.Add(new LogItem
            {
                Text = "Recovered an abandoned GitPuller repository mutex; continuing after previous process exit.",
                IsWarning = true
            });
        }
        catch (Exception ex)
        {
            mutex.Dispose();
            result.Failed = true;
            result.Logs.Add(new LogItem
            {
                Text = $"Could not acquire repository mutex: {ex.Message}",
                IsError = true
            });
            return null;
        }

        return new RepoMutexLease(mutex);
    }

    private static void TryCleanupStaleGitLocks(string repoPath, GitPullerOptions options, RepoResult? result)
    {
        if (!options.StaleGitLockCleanup)
        {
            return;
        }

        foreach (var lockFile in EnumerateGitLockFiles(repoPath))
        {
            try
            {
                var info = new FileInfo(lockFile);
                if (!info.Exists)
                {
                    continue;
                }

                var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                if (age < options.StaleGitLockAge)
                {
                    continue;
                }

                info.Delete();
                result?.Logs.Add(new LogItem
                {
                    Text = $"Removed stale Git lock file ({age.TotalMinutes:F1} min old): {lockFile}",
                    IsWarning = true
                });
            }
            catch (Exception ex)
            {
                result?.Logs.Add(new LogItem
                {
                    Text = $"Could not remove stale Git lock file '{lockFile}': {ex.Message}",
                    IsWarning = true
                });
            }
        }
    }

    private static IEnumerable<string> EnumerateGitLockFiles(string repoPath)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string gitDir;
        try
        {
            gitDir = GitRepositorySupport.ResolveGitDirPath(repoPath);
        }
        catch
        {
            yield break;
        }

        if (Directory.Exists(gitDir))
        {
            directories.Add(gitDir);
        }

        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            try
            {
                var commonDirRaw = File.ReadAllText(commonDirFile, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(commonDirRaw))
                {
                    var commonDir = Path.IsPathRooted(commonDirRaw)
                        ? commonDirRaw
                        : Path.GetFullPath(Path.Combine(gitDir, commonDirRaw));
                    if (Directory.Exists(commonDir))
                    {
                        directories.Add(commonDir);
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var directory in directories)
        {
            foreach (var file in SafeEnumerateFiles(directory, "*.lock", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }

            foreach (var refsDirectoryName in new[] { "refs", Path.Combine("logs", "refs") })
            {
                var refsDirectory = Path.Combine(directory, refsDirectoryName);
                foreach (var file in SafeEnumerateFiles(refsDirectory, "*.lock", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern, SearchOption searchOption)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(path, pattern, searchOption);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }

    private static void TrySyncLocalBranches(string repoPath, Dictionary<string, string> remoteRefs, GitPullerOptions options, RepoResult result, CancellationToken cancellationToken)
    {
        var currentBranch = GetCurrentBranch(repoPath, options, cancellationToken);
        var remoteLocalBranchNames = new HashSet<string>(StringComparer.Ordinal);
        var refCommands = new List<string>();
        var newlyCreatedBranches = new List<RemoteBranchRef>();

        foreach (var remoteBranch in GetRemoteBranches(remoteRefs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localRef = $"refs/heads/{remoteBranch.LocalBranchName}";

            if (!IsSafeRefName(remoteBranch.LocalBranchName))
            {
                result.Logs.Add(new LogItem
                {
                    Text = $"Skipped remote branch with unsafe local branch name: {remoteBranch.RemoteShortName}",
                    IsWarning = true
                });
                continue;
            }

            remoteLocalBranchNames.Add(remoteBranch.LocalBranchName);

            if (string.Equals(currentBranch, remoteBranch.LocalBranchName, StringComparison.Ordinal))
            {
                EnsureBranchTracksRemote(repoPath, remoteBranch.LocalBranchName, remoteBranch.RemoteShortName, options, result, cancellationToken);
                continue;
            }

            if (!TryGetRefSha(repoPath, localRef, options, cancellationToken, out var localSha))
            {
                refCommands.Add($"create {localRef} {remoteBranch.Sha}");
                newlyCreatedBranches.Add(remoteBranch);
                continue;
            }

            if (string.Equals(localSha, remoteBranch.Sha, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (options.ForceSync)
            {
                refCommands.Add($"update {localRef} {remoteBranch.Sha}");
                continue;
            }

            var (rcAncestor, _) = RunGit(repoPath, $"merge-base --is-ancestor {localSha} {remoteBranch.Sha}", options, result, cancellationToken);
            if (rcAncestor == 0)
            {
                FastForwardLocalBranch(repoPath, localRef, localSha, remoteBranch.Sha, options, result, cancellationToken);
            }
            else
            {
                result.Logs.Add(new LogItem
                {
                    Text = $"Local branch '{remoteBranch.LocalBranchName}' diverged from '{remoteBranch.RemoteShortName}'. Kept local branch; remote-tracking ref is still backed up.",
                    IsWarning = true
                });
            }
        }

        if (options.ForceSync)
        {
            AddLocalBranchDeleteCommands(repoPath, remoteLocalBranchNames, options, refCommands, result, cancellationToken);
        }

        ApplyRefUpdates(repoPath, options, refCommands, result, cancellationToken);

        foreach (var remoteBranch in newlyCreatedBranches)
        {
            EnsureBranchTracksRemote(repoPath, remoteBranch.LocalBranchName, remoteBranch.RemoteShortName, options, result, cancellationToken);
        }
    }

    private static List<RemoteBranchRef> GetRemoteBranches(Dictionary<string, string> remoteRefs)
    {
        var branches = new List<RemoteBranchRef>();
        const string prefix = "refs/remotes/";

        foreach (var kvp in remoteRefs.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var refName = kvp.Key;
            if (!refName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = refName[prefix.Length..];
            var slashIndex = rest.IndexOf('/');
            if (slashIndex <= 0 || slashIndex == rest.Length - 1)
            {
                continue;
            }

            var remoteName = rest[..slashIndex];
            var branchName = rest[(slashIndex + 1)..];
            if (branchName.Equals("HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            var localBranchName = remoteName.Equals("origin", StringComparison.OrdinalIgnoreCase)
                ? branchName
                : $"{remoteName}/{branchName}";

            branches.Add(new RemoteBranchRef
            {
                RemoteName = remoteName,
                BranchName = branchName,
                LocalBranchName = localBranchName,
                RemoteShortName = $"{remoteName}/{branchName}",
                RemoteRefName = refName,
                Sha = kvp.Value
            });
        }

        return branches;
    }

    private static string? GetCurrentBranch(string repoPath, GitPullerOptions options, CancellationToken cancellationToken)
    {
        var (rc, output) = RunGit(repoPath, "symbolic-ref --quiet --short HEAD", options, result: null, cancellationToken);
        if (rc != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return output.Trim();
    }

    private static bool TryGetRefSha(string repoPath, string refName, GitPullerOptions options, CancellationToken cancellationToken, out string sha)
    {
        sha = string.Empty;
        var (rc, output) = RunGit(repoPath, $"rev-parse --verify --quiet {refName}", options, result: null, cancellationToken);
        if (rc != 0 || string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        sha = output.Trim();
        return true;
    }

    private static void EnsureBranchTracksRemote(string repoPath, string localBranchName, string remoteShortName, GitPullerOptions options, RepoResult result, CancellationToken cancellationToken)
    {
        var (rcSet, outSet) = RunGit(repoPath, new[] { "branch", "--set-upstream-to", remoteShortName, localBranchName }, options, result, cancellationToken);
        if (rcSet != 0)
        {
            result.Logs.Add(new LogItem
            {
                Text = $"Could not set upstream for current branch '{localBranchName}' to '{remoteShortName}':\n{outSet}",
                IsWarning = true
            });
        }
    }

    private static void FastForwardLocalBranch(string repoPath, string localRef, string oldSha, string newSha, GitPullerOptions options, RepoResult result, CancellationToken cancellationToken)
    {
        var (rc, output) = RunGit(repoPath, $"update-ref {localRef} {newSha} {oldSha}", options, result, cancellationToken);
        if (rc != 0)
        {
            result.Failed = true;
            result.Logs.Add(new LogItem
            {
                Text = $"Could not fast-forward {localRef} to {newSha}:\n{output}",
                IsError = true
            });
        }
    }

    private static void ApplyRefUpdates(string repoPath, GitPullerOptions options, List<string> commands, RepoResult result, CancellationToken cancellationToken)
    {
        if (commands.Count == 0)
        {
            return;
        }

        var input = string.Join("\n", commands) + "\n";
        var (rc, output) = RunGitWithInput(repoPath, new[] { "update-ref", "--stdin" }, input, options, result, cancellationToken);
        if (rc != 0)
        {
            result.Failed = true;
            result.Logs.Add(new LogItem { Text = $"Batch update-ref failed:\n{output}", IsError = true });
        }
    }

    private static void AddLocalBranchDeleteCommands(string repoPath, HashSet<string> remoteLocalBranchNames, GitPullerOptions options, List<string> commands, RepoResult result, CancellationToken cancellationToken)
    {
        var currentBranch = GetCurrentBranch(repoPath, options, cancellationToken);
        var (rc, output) = RunGit(repoPath, "for-each-ref --format=\"%(refname:short)\" refs/heads", options, result, cancellationToken);
        if (rc != 0)
        {
            result.Logs.Add(new LogItem
            {
                Text = $"Could not enumerate local branches for pruning:\n{output}",
                IsWarning = true
            });
            return;
        }

        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var branchName = line.Trim();
            if (string.IsNullOrWhiteSpace(branchName)
                || remoteLocalBranchNames.Contains(branchName)
                || string.Equals(branchName, currentBranch, StringComparison.Ordinal)
                || !IsSafeRefName(branchName))
            {
                continue;
            }

            commands.Add($"delete refs/heads/{branchName}");
            result.Logs.Add(new LogItem
            {
                Text = $"Queued deletion for local-only branch '{branchName}' because no matching remote branch exists."
            });
        }
    }

    private static bool IsSafeRefName(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return false;
        }

        if (branchName.StartsWith("/", StringComparison.Ordinal)
            || branchName.EndsWith("/", StringComparison.Ordinal)
            || branchName.Contains("..", StringComparison.Ordinal)
            || branchName.Contains("@{", StringComparison.Ordinal)
            || branchName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !branchName.Any(ch => char.IsWhiteSpace(ch) || ch == '\\' || ch == '~' || ch == '^' || ch == ':' || ch == '?' || ch == '*' || ch == '[');
    }

    private static void TrySyncWorkingTree(string repoPath, GitPullerOptions options, RepoResult result, CancellationToken cancellationToken)
    {
        if (options.ForceSync)
        {
            var (rcHead, outHead) = RunGit(repoPath, "symbolic-ref -q --short refs/remotes/origin/HEAD", options, result, cancellationToken);
            if (rcHead != 0 || string.IsNullOrWhiteSpace(outHead))
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = "Could not determine origin/HEAD; force sync failed.", IsError = true });
                return;
            }

            var remoteRef = outHead.Trim();
            var branchName = remoteRef.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
                ? remoteRef["origin/".Length..]
                : remoteRef;

            if (options.CleanUntracked)
            {
                var (rcCleanPre, outCleanPre) = RunGit(repoPath, new[] { "clean", "-fdx" }, options, result, cancellationToken);
                if (rcCleanPre != 0)
                {
                    result.Logs.Add(new LogItem { Text = $"git clean failed:\n{outCleanPre}", IsWarning = true });
                }
            }

            var (rcCheckout, outCheckout) = RunGit(repoPath, new[] { "checkout", "-f", "-B", branchName, remoteRef }, options, result, cancellationToken);
            if (rcCheckout != 0)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = $"Force sync checkout failed:\n{outCheckout}", IsError = true });
                return;
            }

            var (rcReset, outReset) = RunGit(repoPath, new[] { "reset", "--hard", remoteRef }, options, result, cancellationToken);
            if (rcReset != 0)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = $"Force sync reset failed:\n{outReset}", IsError = true });
                return;
            }

            if (options.CleanUntracked)
            {
                var (rcClean, outClean) = RunGit(repoPath, new[] { "clean", "-fdx" }, options, result, cancellationToken);
                if (rcClean != 0)
                {
                    result.Logs.Add(new LogItem { Text = $"git clean failed:\n{outClean}", IsWarning = true });
                }
            }

            return;
        }

        var (rcPull, outPull) = RunGitWithSshToHttpsFallback(repoPath, "pull --ff-only --recurse-submodules=no", options, result, cancellationToken);
        if (rcPull != 0)
        {
            result.Failed = true;
            result.Logs.Add(new LogItem { Text = $"Pull (ff-only) failed:\n{outPull}", IsError = true });
        }
    }

    private static void TryFetchLfsObjects(string repoPath, GitPullerOptions options, RepoResult result, CancellationToken cancellationToken)
    {
        var (rcVersion, _) = RunGit(repoPath, new[] { "lfs", "version" }, options, result: null, cancellationToken);
        if (rcVersion != 0)
        {
            return;
        }

        var (rcLsFiles, lfsFilesOutput) = RunGit(repoPath, new[] { "lfs", "ls-files", "--all" }, options, result: null, cancellationToken);
        if (rcLsFiles != 0 || string.IsNullOrWhiteSpace(lfsFilesOutput))
        {
            return;
        }

        var (rcFetch, outFetch) = RunGit(repoPath, new[] { "lfs", "fetch", "--all", "--prune" }, options, result, cancellationToken);
        if (rcFetch != 0)
        {
            result.Logs.Add(new LogItem { Text = $"Git LFS fetch failed:\n{outFetch}", IsWarning = true });
        }
    }

    private static void TryUpdateSubmodules(string repoPath, GitPullerOptions options, RepoResult result, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(repoPath, ".gitmodules")))
        {
            return;
        }

        var (rcSync, outSync) = RunGit(repoPath, "submodule sync --recursive", options, result, cancellationToken);
        if (rcSync != 0)
        {
            result.Logs.Add(new LogItem { Text = $"Submodule sync failed:\n{outSync}", IsWarning = true });
        }

        var args = options.InitMissingSubmodules
            ? "submodule update --init --recursive"
            : "submodule update --recursive";

        if (options.ForceSync)
        {
            args += " --force";
        }

        var (rcSubmodule, outSubmodule) = RunGitWithSshToHttpsFallback(repoPath, args, options, result, cancellationToken);
        if (rcSubmodule != 0)
        {
            result.Failed = true;
            result.Logs.Add(new LogItem { Text = $"Submodule update failed:\n{outSubmodule}", IsError = true });
            return;
        }

        TryFetchSubmoduleRemotes(repoPath, options, result, cancellationToken);
    }

    private static void TryFetchSubmoduleRemotes(string repoPath, GitPullerOptions options, RepoResult result, CancellationToken cancellationToken)
    {
        var (rc, output) = RunGit(repoPath, "submodule status --recursive", options, result, cancellationToken);
        if (rc != 0 || string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = parts[1];
            if (!seen.Add(relativePath))
            {
                continue;
            }

            var subPath = Path.Combine(repoPath, relativePath);
            if (!Directory.Exists(subPath))
            {
                continue;
            }

            using var submoduleLease = TryAcquireRepoMutex(subPath, options, result);
            if (submoduleLease == null)
            {
                continue;
            }

            TryCleanupStaleGitLocks(subPath, options, result);

            var (rcFetch, outFetch) = RunGitWithSshToHttpsFallback(subPath, "fetch --all --prune --prune-tags --tags --force", options, result, cancellationToken);
            if (rcFetch != 0)
            {
                result.Logs.Add(new LogItem { Text = $"Submodule fetch failed ({relativePath}):\n{outFetch}", IsWarning = true });
            }

            if (options.ForceSync && options.CleanUntracked)
            {
                var (rcClean, outClean) = RunGit(subPath, new[] { "clean", "-fdx" }, options, result, cancellationToken);
                if (rcClean != 0)
                {
                    result.Logs.Add(new LogItem { Text = $"Submodule clean failed ({relativePath}):\n{outClean}", IsWarning = true });
                }
            }
        }
    }

    private static void ParseAndAddCommits(RepoResult result, string logOutput, HashSet<string> seenCommits)
    {
        foreach (var line in logOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { ' ' }, 2);
            if (parts.Length < 2)
            {
                continue;
            }

            var hash = parts[0];
            if (seenCommits.Add(hash))
            {
                result.NewCommitsCount++;
                result.Logs.Add(new LogItem { Text = line, IsCommit = true });
            }
        }
    }

    private static void TrySyncLocalBranchesIfRefsAvailable(
        string repoPath,
        RemoteRefsSnapshot remoteRefs,
        GitPullerOptions options,
        RepoResult result,
        CancellationToken cancellationToken)
    {
        if (!remoteRefs.Succeeded)
        {
            return;
        }

        TrySyncLocalBranches(repoPath, remoteRefs.Refs, options, result, cancellationToken);
    }

    private static RemoteRefsSnapshot ReadRemoteRefsSnapshot(
        string repoPath,
        GitPullerOptions options,
        RepoResult? result,
        CancellationToken cancellationToken)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var (rc, output) = RunGit(repoPath, "for-each-ref --format=\"%(refname) %(objectname)\" refs/remotes", options, result, cancellationToken);
        if (rc != 0)
        {
            return new RemoteRefsSnapshot(Succeeded: false, refs, output);
        }

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = line.Split(' ');
            if (parts.Length >= 2)
            {
                refs[parts[0]] = parts[1];
            }
        }

        return new RemoteRefsSnapshot(Succeeded: true, refs, output);
    }

    private static (int rc, string output) RunGit(string cwd, string args, GitPullerOptions options, RepoResult? result, CancellationToken cancellationToken)
    {
        (int rc, string output) lastResult = (-1, string.Empty);

        for (var attempt = 0; attempt < GitLockRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = RunGitOnce(cwd, args, options, result, cancellationToken);
            if (lastResult.rc == 0 || !LooksLikeGitLockFailure(lastResult.output) || attempt == GitLockRetryCount - 1)
            {
                return lastResult;
            }

            TryCleanupStaleGitLocks(cwd, options, result);
            WaitBeforeRetry(GitLockRetryDelayMs * (attempt + 1), cancellationToken);
        }

        return lastResult;
    }

    private static (int rc, string output) RunGit(string cwd, IReadOnlyList<string> args, GitPullerOptions options, RepoResult? result, CancellationToken cancellationToken)
    {
        return RunGitWithInput(cwd, args, stdin: null, options, result, cancellationToken);
    }

    private static (int rc, string output) RunGitWithInput(string cwd, IReadOnlyList<string> args, string? stdin, GitPullerOptions options, RepoResult? result, CancellationToken cancellationToken)
    {
        (int rc, string output) lastResult = (-1, string.Empty);

        for (var attempt = 0; attempt < GitLockRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = RunGitWithInputOnce(cwd, args, stdin, options, result, cancellationToken);
            if (lastResult.rc == 0 || !LooksLikeGitLockFailure(lastResult.output) || attempt == GitLockRetryCount - 1)
            {
                return lastResult;
            }

            TryCleanupStaleGitLocks(cwd, options, result);
            WaitBeforeRetry(GitLockRetryDelayMs * (attempt + 1), cancellationToken);
        }

        return lastResult;
    }

    private static (int rc, string output) RunGitWithInputOnce(string cwd, IReadOnlyList<string> args, string? stdin, GitPullerOptions options, RepoResult? result, CancellationToken cancellationToken)
    {
        var commandLabel = $"git {FormatArgsForLog(args)}";
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        Process? process = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                RedirectStandardInput = stdin != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";

            process = Process.Start(psi);
            if (process == null)
            {
                stopwatch.Stop();
                RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
                return (-1, $"Failed to start git process. Command: {commandLabel}\nRepository: {cwd}");
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (stdin != null)
                {
                    process.StandardInput.Write(stdin);
                    process.StandardInput.Close();
                }

                if (!WaitForExit(process, options.GitTimeoutMilliseconds, cancellationToken))
                {
                    var timeoutDetails = BuildTimeoutDetails(process, stdout, stderr, options.GitTimeoutMilliseconds, commandLabel, cwd);
                    stopwatch.Stop();
                    RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: true);
                    return (-1, timeoutDetails);
                }

                Task.WaitAll(stdout, stderr);
                stopwatch.Stop();
                RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, process.ExitCode, timedOut: false);
                return (process.ExitCode, (stdout.Result + "\n" + stderr.Result).Trim());
            }
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            stopwatch.Stop();
            RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
            throw;
        }
        catch (Exception ex)
        {
            TryTerminateProcess(process);
            stopwatch.Stop();
            RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
            return (-1, $"{ex.Message}\nCommand: {commandLabel}\nRepository: {cwd}");
        }
    }

    private static string FormatArgsForLog(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(arg =>
        {
            if (arg.Length == 0)
            {
                return "\"\"";
            }

            if (arg.Any(char.IsWhiteSpace) || arg.Contains('"'))
            {
                return "\"" + arg.Replace("\"", "\\\"") + "\"";
            }

            return arg;
        }));
    }

    private static (int rc, string output) RunGitOnce(string cwd, string args, GitPullerOptions options, RepoResult? result, CancellationToken cancellationToken)
    {
        var commandLabel = $"git {args}";
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        Process? process = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";

            process = Process.Start(psi);
            if (process == null)
            {
                stopwatch.Stop();
                RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
                return (-1, $"Failed to start git process. Command: {commandLabel}\nRepository: {cwd}");
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!WaitForExit(process, options.GitTimeoutMilliseconds, cancellationToken))
                {
                    var timeoutDetails = BuildTimeoutDetails(process, stdout, stderr, options.GitTimeoutMilliseconds, commandLabel, cwd);
                    stopwatch.Stop();
                    RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: true);
                    return (-1, timeoutDetails);
                }

                Task.WaitAll(stdout, stderr);
                stopwatch.Stop();
                RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, process.ExitCode, timedOut: false);
                return (process.ExitCode, (stdout.Result + "\n" + stderr.Result).Trim());
            }
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            stopwatch.Stop();
            RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
            throw;
        }
        catch (Exception ex)
        {
            TryTerminateProcess(process);
            stopwatch.Stop();
            RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
            return (-1, $"{ex.Message}\nCommand: {commandLabel}\nRepository: {cwd}");
        }
    }

    private static void RecordOperation(RepoResult? result, string command, string cwd, DateTimeOffset startedAt, TimeSpan elapsed, int exitCode, bool timedOut)
    {
        if (result == null)
        {
            return;
        }

        result.Operations.Add(new RepoOperation
        {
            Command = command,
            WorkingDirectory = cwd,
            StartedAt = startedAt,
            Elapsed = elapsed,
            ExitCode = exitCode,
            TimedOut = timedOut
        });
    }

    private static (int rc, string output) RunGitWithSshToHttpsFallback(string cwd, string args, GitPullerOptions options, RepoResult? result, CancellationToken cancellationToken)
    {
        var (rc, output) = RunGit(cwd, args, options, result, cancellationToken);
        if (rc == 0 || !LooksLikeSshAuthOrHostKeyFailure(output))
        {
            return (rc, output);
        }

        var hosts = ExtractHostsFromText(output);
        if (hosts.Count == 0)
        {
            var (rcRemotes, outRemotes) = RunGit(cwd, "remote -v", options, result, cancellationToken);
            if (rcRemotes == 0 && !string.IsNullOrWhiteSpace(outRemotes))
            {
                hosts = ExtractHostsFromText(outRemotes);
            }
        }

        if (hosts.Count == 0)
        {
            hosts = ExtractHostsFromGitmodules(cwd);
        }

        var rewritePrefix = BuildSshToHttpsRewritePrefix(hosts);
        if (string.IsNullOrWhiteSpace(rewritePrefix))
        {
            return (rc, output);
        }

        var (rcRetry, retryOutput) = RunGit(cwd, $"{rewritePrefix} {args}", options, result, cancellationToken);
        if (rcRetry == 0)
        {
            return (rcRetry, retryOutput);
        }

        var combined = new StringBuilder();
        combined.AppendLine(output);
        combined.AppendLine();
        combined.AppendLine("--- retry with ssh->https rewrite ---");
        combined.AppendLine(retryOutput);
        return (rcRetry, combined.ToString().Trim());
    }

    private static bool LooksLikeSshAuthOrHostKeyFailure(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        return output.IndexOf("Host key verification failed", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("Permission denied (publickey", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("Could not read from remote repository", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("fatal: Could not read from remote repository", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeGitLockFailure(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        return output.IndexOf(".lock", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("cannot lock", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("could not lock", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("Unable to create", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("another git process seems to be running", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static HashSet<string> ExtractHostsFromText(string text)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return hosts;
        }

        foreach (Match match in Regex.Matches(text, @"git@([A-Za-z0-9\.-]+):", RegexOptions.IgnoreCase))
        {
            var host = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(host))
            {
                hosts.Add(host);
            }
        }

        foreach (Match match in Regex.Matches(text, @"ssh://git@([A-Za-z0-9\.-]+)/", RegexOptions.IgnoreCase))
        {
            var host = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(host))
            {
                hosts.Add(host);
            }
        }

        foreach (Match match in Regex.Matches(text, @"https?://([A-Za-z0-9\.-]+)/", RegexOptions.IgnoreCase))
        {
            var host = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(host))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    private static HashSet<string> ExtractHostsFromGitmodules(string repoPath)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(repoPath, ".gitmodules");
            if (!File.Exists(path))
            {
                return hosts;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            return ExtractHostsFromText(text);
        }
        catch
        {
            return hosts;
        }
    }

    private static string BuildSshToHttpsRewritePrefix(IEnumerable<string> hosts)
    {
        var builder = new StringBuilder();
        foreach (var host in hosts)
        {
            if (string.IsNullOrWhiteSpace(host) || !Regex.IsMatch(host, @"^[A-Za-z0-9\.-]+$"))
            {
                continue;
            }

            builder.Append($"-c url.\"https://{host}/\".insteadOf=git@{host}: ");
            builder.Append($"-c url.\"https://{host}/\".insteadOf=ssh://git@{host}/ ");
        }

        return builder.ToString().Trim();
    }

    private static void WaitBeforeRetry(int delayMilliseconds, CancellationToken cancellationToken)
    {
        if (delayMilliseconds <= 0)
        {
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(delayMilliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool WaitForExit(Process process, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var waitedMilliseconds = 0;
        const int pollMilliseconds = 200;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.WaitForExit(pollMilliseconds))
            {
                return true;
            }

            waitedMilliseconds += pollMilliseconds;
            if (waitedMilliseconds >= timeoutMilliseconds)
            {
                TryTerminateProcess(process);
                return false;
            }
        }
    }

    private static string BuildTimeoutDetails(Process process, Task<string> stdout, Task<string> stderr, int timeoutMilliseconds, string commandLabel, string cwd)
    {
        var timeoutDetails = new StringBuilder();
        timeoutDetails.AppendLine($"Timeout ({timeoutMilliseconds / 1000}s)");
        timeoutDetails.AppendLine($"Command: {commandLabel}");
        timeoutDetails.AppendLine($"Repository: {cwd}");

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (!process.WaitForExit(5000))
            {
                timeoutDetails.AppendLine("Warning: Process did not exit within 5s after kill request.");
            }
        }
        catch (Exception killEx)
        {
            timeoutDetails.AppendLine($"Warning: Failed to terminate process tree: {killEx.Message}");
        }

        try
        {
            Task.WaitAll(new Task[] { stdout, stderr }, 2000);
        }
        catch
        {
        }

        var partialStdout = stdout.Status == TaskStatus.RanToCompletion ? stdout.Result : string.Empty;
        var partialStderr = stderr.Status == TaskStatus.RanToCompletion ? stderr.Result : string.Empty;
        var partialOutput = (partialStdout + "\n" + partialStderr).Trim();
        if (!string.IsNullOrWhiteSpace(partialOutput))
        {
            timeoutDetails.AppendLine();
            timeoutDetails.AppendLine("Partial output:");
            timeoutDetails.AppendLine(partialOutput);
        }

        return timeoutDetails.ToString().Trim();
    }

    private static void TryTerminateProcess(Process? process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
    }
}

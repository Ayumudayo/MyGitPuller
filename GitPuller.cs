using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitPuller
{
    class Program
    {
        static readonly object ConsoleLock = new object();
        static int MaxDegreeOfParallelism = 6;
        const int DefaultMaxDegreeOfParallelism = 6;
        static bool InitMissingSubmodules = true;
        static bool ForceSync = true;
        static bool CleanUntracked = true;
        static bool ForceRescan = false;
        static bool PullFfOnly = true;
        static bool SyncAllBranches = true;
        static bool StaleGitLockCleanup = true;
        static bool VerboseReport = false;
        static bool ShowHelp = false;
        static string RootDir = AppContext.BaseDirectory;
        const string CacheFileName = ".git_repo_cache.json";
        const string LatestReportFileName = "git_update_report.md";
        static int GitTimeout = 60000; // Default 60s
        const int DefaultGitTimeoutSeconds = 60;
        const int MinGitTimeoutSeconds = 1;
        const int GitLockRetryCount = 3;
        const int GitLockRetryDelayMs = 1000;
        static TimeSpan StaleGitLockAge = TimeSpan.FromMinutes(10);
        static readonly string RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

        // Some environments (CI/redirected output) don't support cursor operations.
        static bool SupportsCursorControl = true;

        // Stats
        static int TotalRepos = 0;
        static int ProcessedCount = 0;
        static int SuccessCount = 0;
        static int FailCount = 0;
        static int GlobalNewCommitsCount = 0;
        static readonly ConcurrentDictionary<int, int> WorkerSlotsByThreadId = new();
        static int NextWorkerSlot = 0;

        // Tree characters
        const string TreeVert = "│ ";
        const string TreeBranch = "├─";
        const string TreeLast = "└─";

        static int Main(string[] args)
        {
            // Force UTF-8
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            try
            {
                Console.CursorVisible = false;
            }
            catch
            {
                SupportsCursorControl = false;
            }

            try
            {
                ParseArgs(args);
                if (ShowHelp)
                {
                    PrintUsage();
                    return 0;
                }

                if (!ValidateAndNormalizeSettings())
                    return 1;

                using var rootLease = TryAcquireRootMutex();
                if (rootLease == null)
                    return 1;

                List<string> repos;
                if (!ForceRescan && TryLoadCache(out repos))
                {
                    Console.WriteLine($"Loaded {repos.Count} repositories from cache.");
                }
                else
                {
                    Console.WriteLine($"Scanning {RootDir} for git repositories...");
                    repos = NormalizeRepoList(FindGitRepos(RootDir));
                    SaveCache(repos);
                }

                TotalRepos = repos.Count;

                if (TotalRepos == 0)
                {
                    Console.WriteLine("No repositories found.");
                    return 0;
                }

                Console.WriteLine($"Found {TotalRepos} repositories. Processing with {MaxDegreeOfParallelism} workers...");
                Console.WriteLine(); // Spacer

                var results = new List<RepoResult>();
                var options = new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism };
                var sw = Stopwatch.StartNew();

                // Initial Progress Bar
                DrawProgress();

                Parallel.ForEach(repos, options, (repo) =>
                {
                    int workerSlot = GetWorkerSlot();
                    var repoStartedAt = DateTimeOffset.Now;
                    var repoStopwatch = Stopwatch.StartNew();
                    var res = ProcessRepo(repo);
                    repoStopwatch.Stop();

                    res.WorkerSlot = workerSlot;
                    res.StartedAt = repoStartedAt;
                    res.CompletedAt = DateTimeOffset.Now;
                    res.Elapsed = repoStopwatch.Elapsed;
                    
                    lock (ConsoleLock)
                    {
                        ProcessedCount++;
                        if (res.Failed) FailCount++;
                        else SuccessCount++;
                        
                        GlobalNewCommitsCount += res.NewCommitsCount;

                        results.Add(res);
                        
                        // Only print to main stream if there's something interesting (Error or New Commits)
                        if (res.Failed || res.NewCommitsCount > 0)
                        {
                            ClearCurrentLine();
                            PrintResult(res);
                        }
                        
                        DrawProgress();
                    }
                });

                sw.Stop();
                ClearCurrentLine(); // Clear final progress bar
                WriteSummary(results, sw.Elapsed);
                return FailCount > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                ClearCurrentLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Fatal error: {ex.GetType().Name}: {ex.Message}");
                Console.ResetColor();
                return 1;
            }
            finally
            {
                try
                {
                    Console.CursorVisible = true;
                }
                catch
                {
                    // ignore
                }
            }
        }

        static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-w")
                {
                    if (!TryReadOptionValue(args, ref i, "-w", out var workerCountRaw))
                        continue;

                    if (!int.TryParse(workerCountRaw, out int w) || w < 1)
                    {
                        Console.WriteLine($"Warning: Invalid worker count '{workerCountRaw}'. Keeping {MaxDegreeOfParallelism}.");
                        continue;
                    }

                    MaxDegreeOfParallelism = w;
                }
                else if (args[i] == "--init-missing-submodules")
                {
                    InitMissingSubmodules = true;
                }
                else if (args[i] == "--no-init-submodules")
                {
                    InitMissingSubmodules = false;
                }
                else if (args[i] == "--rescan")
                {
                    ForceRescan = true;
                }
                else if (args[i] == "--force-sync")
                {
                    // Default behavior; kept for compatibility with older scripts.
                    ForceSync = true;
                }
                else if (args[i] == "--clean")
                {
                    // Default behavior; kept for compatibility with older scripts.
                    CleanUntracked = true;
                }
                else if (args[i] == "--no-pull")
                {
                    PullFfOnly = false;
                }
                else if (args[i] == "--all-branches")
                {
                    SyncAllBranches = true;
                }
                else if (args[i] == "--current-branch-only")
                {
                    SyncAllBranches = false;
                }
                else if (args[i] == "--stale-lock-minutes")
                {
                    if (!TryReadOptionValue(args, ref i, "--stale-lock-minutes", out var staleMinutesRaw))
                        continue;

                    if (!double.TryParse(staleMinutesRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes < 0)
                    {
                        Console.WriteLine($"Warning: Invalid stale lock age '{staleMinutesRaw}'. Keeping {StaleGitLockAge.TotalMinutes:F0} minutes.");
                        continue;
                    }

                    StaleGitLockAge = TimeSpan.FromMinutes(minutes);
                }
                else if (args[i] == "--no-stale-lock-cleanup")
                {
                    StaleGitLockCleanup = false;
                }
                else if (args[i] == "--verbose-report")
                {
                    VerboseReport = true;
                }
                else if (args[i] == "--root")
                {
                    if (!TryReadOptionValue(args, ref i, "--root", out var rootRaw))
                        continue;

                    RootDir = rootRaw;
                }
                else if (args[i] == "-t" || args[i] == "--timeout")
                {
                    if (!TryReadOptionValue(args, ref i, args[i], out var timeoutRaw))
                        continue;

                    if (!int.TryParse(timeoutRaw, out int seconds) || seconds < MinGitTimeoutSeconds)
                    {
                        Console.WriteLine($"Warning: Invalid timeout '{timeoutRaw}'. Keeping {GitTimeout / 1000}s.");
                        continue;
                    }

                    if (seconds > int.MaxValue / 1000)
                    {
                        Console.WriteLine($"Warning: Timeout '{timeoutRaw}' is too large. Keeping {GitTimeout / 1000}s.");
                        continue;
                    }

                    GitTimeout = seconds * 1000;
                }
                else if (args[i] == "-h" || args[i] == "--help")
                {
                    ShowHelp = true;
                }
                else
                {
                    Console.WriteLine($"Warning: Unknown option '{args[i]}' ignored. Use --help to see valid options.");
                }
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("MyGitPuller - Update multiple git repositories in parallel");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  GitPuller.exe [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine($"  -w <number>                 Number of parallel workers (default: {DefaultMaxDegreeOfParallelism})");
            Console.WriteLine("  --rescan                    Ignore cache and rescan directories");
            Console.WriteLine("  --init-missing-submodules   Initialize missing submodules when updating");
            Console.WriteLine("  --no-init-submodules        Do not initialize new submodules");
            Console.WriteLine("  --no-pull                   Skip git pull (fetch/report only)");
            Console.WriteLine("  --all-branches              Mirror all remote branches into local tracking branches (default)");
            Console.WriteLine("  --current-branch-only       Only force-sync origin/HEAD worktree, not all local branches");
            Console.WriteLine("  --force-sync                Force sync local state to remotes (default)");
            Console.WriteLine("  --clean                     Remove untracked/ignored files during force sync (default)");
            Console.WriteLine("  --stale-lock-minutes <num>  Delete Git lock files older than this many minutes (default: 10)");
            Console.WriteLine("  --no-stale-lock-cleanup     Do not delete stale Git lock files");
            Console.WriteLine("  --verbose-report            Include per-command operation details in the report");
            Console.WriteLine("  --root <path>               Root directory to scan");
            Console.WriteLine($"  -t, --timeout <seconds>     Per-git-command timeout in seconds (default: {DefaultGitTimeoutSeconds})");
            Console.WriteLine("  -h, --help                  Show this help and exit");
        }

        static bool ValidateAndNormalizeSettings()
        {
            if (MaxDegreeOfParallelism < 1)
            {
                Console.WriteLine($"Warning: Worker count must be >= 1. Falling back to {DefaultMaxDegreeOfParallelism}.");
                MaxDegreeOfParallelism = DefaultMaxDegreeOfParallelism;
            }

            int minTimeoutMs = MinGitTimeoutSeconds * 1000;
            if (GitTimeout < minTimeoutMs)
            {
                Console.WriteLine($"Warning: Timeout must be >= {MinGitTimeoutSeconds}s. Falling back to {DefaultGitTimeoutSeconds}s.");
                GitTimeout = DefaultGitTimeoutSeconds * 1000;
            }

            try
            {
                RootDir = Path.GetFullPath(RootDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: Invalid root path '{RootDir}'. {ex.Message}");
                return false;
            }

            if (!Directory.Exists(RootDir))
            {
                Console.WriteLine($"Error: Root directory does not exist: {RootDir}");
                return false;
            }

            return true;
        }

        static bool TryReadOptionValue(string[] args, ref int index, string option, out string value)
        {
            value = string.Empty;

            int valueIndex = index + 1;
            if (valueIndex >= args.Length)
            {
                Console.WriteLine($"Warning: Missing value for {option}. Option ignored.");
                return false;
            }

            var candidate = args[valueIndex];
            if (candidate.StartsWith("-", StringComparison.Ordinal) && IsRecognizedOption(candidate))
            {
                Console.WriteLine($"Warning: Missing value for {option}. Option ignored.");
                return false;
            }

            value = candidate;
            index = valueIndex;
            return true;
        }

        static bool IsRecognizedOption(string arg)
        {
            return arg == "-w"
                || arg == "--init-missing-submodules"
                || arg == "--no-init-submodules"
                || arg == "--rescan"
                || arg == "--force-sync"
                || arg == "--clean"
                || arg == "--no-pull"
                || arg == "--all-branches"
                || arg == "--current-branch-only"
                || arg == "--stale-lock-minutes"
                || arg == "--no-stale-lock-cleanup"
                || arg == "--verbose-report"
                || arg == "--root"
                || arg == "-t"
                || arg == "--timeout"
                || arg == "-h"
                || arg == "--help";
        }

        static bool TryLoadCache(out List<string> repos)
        {
            repos = new List<string>();
            string cachePath = Path.Combine(RootDir, CacheFileName);
            if (!File.Exists(cachePath)) return false;

            try
            {
                string json = File.ReadAllText(cachePath, Encoding.UTF8);
                var cached = JsonSerializer.Deserialize<List<string>>(json);
                if (cached == null) return false;

                var valid = new List<string>();
                foreach (var path in cached)
                {
                    if (!IsGitRepoRoot(path, out bool isSubmodule) || isSubmodule)
                        return false; // Invalidate if any missing

                    valid.Add(path);
                }

                repos = NormalizeRepoList(valid);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static List<string> NormalizeRepoList(IEnumerable<string> repoPaths)
        {
            var repos = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var repoPath in repoPaths)
            {
                if (string.IsNullOrWhiteSpace(repoPath))
                    continue;

                string normalized;
                try
                {
                    normalized = Path.GetFullPath(repoPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch
                {
                    normalized = repoPath.Trim()
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                if (seen.Add(normalized))
                    repos.Add(normalized);
            }

            repos.Sort(StringComparer.OrdinalIgnoreCase);
            return repos;
        }

        static void SaveCache(List<string> repos)
        {
            try
            {
                string cachePath = Path.Combine(RootDir, CacheFileName);
                string json = JsonSerializer.Serialize(repos, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(cachePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to save cache: {ex.Message}");
            }
        }

        static void DrawProgress()
        {
            if (TotalRepos == 0) return;

            if (!SupportsCursorControl || Console.IsOutputRedirected)
                return;

            int width;
            try
            {
                width = Math.Min(50, Console.WindowWidth - 30);
            }
            catch
            {
                return;
            }
            if (width < 10) width = 10;
            
            double pct = (double)ProcessedCount / TotalRepos;
            int filled = (int)(width * pct);
            int empty = width - filled;

            string bar = new string('█', filled) + new string('░', empty);
            string status = $"\r[{bar}] {ProcessedCount}/{TotalRepos} ({pct:P0})";

            Console.Write(status);
        }

        static int GetWorkerSlot()
        {
            int threadId = Environment.CurrentManagedThreadId;
            return WorkerSlotsByThreadId.GetOrAdd(threadId, _ => Interlocked.Increment(ref NextWorkerSlot));
        }

        static void ClearCurrentLine()
        {
            if (!SupportsCursorControl || Console.IsOutputRedirected)
                return;

            try
            {
                int currentLineCursor = Console.CursorTop;
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, currentLineCursor);
            }
            catch
            {
                SupportsCursorControl = false;
            }
        }

        static List<string> FindGitRepos(string root)
        {
            // Walk the directory tree while:
            // - skipping known noisy build folders
            // - stopping recursion once we hit a repo root (don't scan inside repos)
            // - never scanning inside any `.git` directory
            var repos = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (IsIgnoredDirName(name))
                    continue;

                if (IsGitRepoRoot(dir, out bool isSubmoduleRepo) && !isSubmoduleRepo)
                {
                    repos.Add(dir);
                    continue; // Don't recurse into a repo
                }

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(dir))
                    {
                        var childName = Path.GetFileName(child);
                        if (childName.Equals(".git", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (IsIgnoredDirName(childName))
                            continue;
                        if (IsReparsePoint(child))
                            continue;
                        pending.Push(child);
                    }
                }
                catch
                {
                    // Ignore access/IO issues and continue scanning.
                }
            }

            repos.Sort(StringComparer.OrdinalIgnoreCase);
            return repos;
        }

        static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }

        static bool IsIgnoredDirName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            // Keep this list intentionally small to avoid surprising behavior.
            return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
                || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsGitRepoRoot(string path, out bool isSubmoduleWorkingTree)
        {
            isSubmoduleWorkingTree = false;
            if (string.IsNullOrWhiteSpace(path)) return false;

            var gitPath = Path.Combine(path, ".git");
            if (Directory.Exists(gitPath))
                return true;

            // Worktrees and submodules often use a `.git` *file* with a `gitdir:` pointer.
            if (!File.Exists(gitPath))
                return false;

            try
            {
                var text = File.ReadAllText(gitPath, Encoding.UTF8);
                var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstLine == null) return false;

                const string prefix = "gitdir:";
                if (!firstLine.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Heuristic: a submodule's gitdir points into the superproject's .git/modules/...
                // We treat those as non-target repos for scanning.
                var gitdir = firstLine.Substring(firstLine.IndexOf(':') + 1).Trim();
                var normalized = gitdir.Replace('/', Path.DirectorySeparatorChar);

                var marker = string.Join(Path.DirectorySeparatorChar.ToString(), new[] { ".git", "modules" });
                if (normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    isSubmoduleWorkingTree = true;

                return true;
            }
            catch
            {
                return false;
            }
        }

        static RepoMutexLease? TryAcquireRepoMutex(string repoPath, RepoResult result)
        {
            string normalized;
            try
            {
                normalized = GetRepoMutexIdentityPath(repoPath)
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
                if (!mutex.WaitOne(GitTimeout))
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

        static RepoMutexLease? TryAcquireRootMutex()
        {
            var normalized = RootDir
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            var mutexName = $"MyGitPuller_Root_{hash}";
            var mutex = new Mutex(false, mutexName);

            try
            {
                if (!mutex.WaitOne(GitTimeout))
                {
                    mutex.Dispose();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Another GitPuller instance is already processing this root: {RootDir}");
                    Console.ResetColor();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Recovered an abandoned GitPuller root mutex; continuing after previous process exit.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                mutex.Dispose();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Could not acquire root mutex: {ex.Message}");
                Console.ResetColor();
                return null;
            }

            return new RepoMutexLease(mutex);
        }

        static string GetRepoMutexIdentityPath(string repoPath)
        {
            var resolvedGitdir = ResolveGitDirPath(repoPath);
            var normalized = resolvedGitdir.Replace('/', Path.DirectorySeparatorChar);
            var worktreesMarker = string.Join(Path.DirectorySeparatorChar.ToString(), new[] { ".git", "worktrees" });
            var worktreesIndex = normalized.IndexOf(worktreesMarker, StringComparison.OrdinalIgnoreCase);
            if (worktreesIndex >= 0)
                return normalized.Substring(0, worktreesIndex + ".git".Length);

            return normalized;
        }

        static string ResolveGitDirPath(string repoPath)
        {
            var gitPath = Path.Combine(repoPath, ".git");
            if (Directory.Exists(gitPath))
                return Path.GetFullPath(gitPath);

            if (!File.Exists(gitPath))
                return Path.GetFullPath(repoPath);

            var text = File.ReadAllText(gitPath, Encoding.UTF8);
            var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            const string prefix = "gitdir:";
            if (firstLine == null || !firstLine.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(repoPath);

            var gitdir = firstLine.Substring(firstLine.IndexOf(':') + 1).Trim();
            return Path.IsPathRooted(gitdir)
                ? Path.GetFullPath(gitdir)
                : Path.GetFullPath(Path.Combine(repoPath, gitdir));
        }

        static void TryCleanupStaleGitLocks(string repoPath, RepoResult? result)
        {
            if (!StaleGitLockCleanup)
                return;

            foreach (var lockFile in EnumerateGitLockFiles(repoPath))
            {
                try
                {
                    var info = new FileInfo(lockFile);
                    if (!info.Exists)
                        continue;

                    var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                    if (age < StaleGitLockAge)
                        continue;

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

        static IEnumerable<string> EnumerateGitLockFiles(string repoPath)
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string gitDir;
            try
            {
                gitDir = ResolveGitDirPath(repoPath);
            }
            catch
            {
                yield break;
            }

            if (Directory.Exists(gitDir))
                dirs.Add(gitDir);

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
                            dirs.Add(commonDir);
                    }
                }
                catch
                {
                    // Continue with the per-worktree gitdir.
                }
            }

            foreach (var dir in dirs)
            {
                foreach (var file in SafeEnumerateFiles(dir, "*.lock", SearchOption.TopDirectoryOnly))
                    yield return file;

                foreach (var refsDirName in new[] { "refs", Path.Combine("logs", "refs") })
                {
                    var refsDir = Path.Combine(dir, refsDirName);
                    foreach (var file in SafeEnumerateFiles(refsDir, "*.lock", SearchOption.AllDirectories))
                        yield return file;
                }
            }
        }

        static IEnumerable<string> SafeEnumerateFiles(string path, string pattern, SearchOption searchOption)
        {
            if (!Directory.Exists(path))
                yield break;

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
                yield return file;
        }

        static RepoResult ProcessRepo(string repoPath)
        {
            var result = new RepoResult { Path = repoPath, Name = Path.GetFileName(repoPath) };

            if (!IsGitRepoRoot(repoPath, out bool isSubmoduleRepo) || isSubmoduleRepo)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = "Not a supported git repository.", IsError = true });
                return result;
            }

            using var repoLease = TryAcquireRepoMutex(repoPath, result);
            if (repoLease == null)
                return result;

            TryCleanupStaleGitLocks(repoPath, result);

            var beforeRefs = GetRemoteRefs(repoPath, result);
            
            // Retry logic for fetch
            int retries = 3;
            int rc = -1;
            string outText = "";
            
            while (retries > 0)
            {
                (rc, outText) = RunGitWithSshToHttpsFallback(repoPath, "fetch --all --prune --prune-tags --tags --force", result);
                if (rc == 0) break;
                
                // If failed, try to prune explicit remote first to clear bad refs
                if (retries < 3) // Don't do it strictly on first attempt if we want, but valid to do it if failed
                {
                     RunGitWithSshToHttpsFallback(repoPath, "remote prune origin", result);
                }

                retries--;
                if (retries > 0) Thread.Sleep(1000); // Backoff
            }
            
            if (rc != 0)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = $"Fetch failed after retries:\n{outText}", IsError = true });
                return result;
            }

            TryFetchLfsObjects(repoPath, result);

            var afterRefs = GetRemoteRefs(repoPath, result);
            var seenCommits = new HashSet<string>();

            foreach (var kvp in afterRefs)
            {
                var refName = kvp.Key;
                var newSha = kvp.Value;

                // 2. Ignore HEAD refs
                if (refName.EndsWith("/HEAD")) continue;
                
                if (!beforeRefs.TryGetValue(refName, out var oldSha))
                {
                    // New branch
                    var (rcLog, logOut) = RunGit(repoPath, $"log -1 --format=\"%h %s (%an)\" {newSha}", result);
                    if (rcLog == 0 && !string.IsNullOrWhiteSpace(logOut))
                    {
                        ParseAndAddCommits(result, logOut, seenCommits);
                    }
                }
                else if (oldSha != newSha)
                {
                    // Updated branch
                    var (rcLog, logOut) = RunGit(repoPath, $"log --format=\"%h %s (%an)\" {oldSha}..{newSha}", result);
                    if (rcLog == 0 && !string.IsNullOrWhiteSpace(logOut))
                    {
                        ParseAndAddCommits(result, logOut, seenCommits);
                    }
                }
            }

            // Update the checked-out branch/worktree.
            if (PullFfOnly)
            {
                if (ForceSync)
                {
                    TrySyncWorkingTree(repoPath, result);

                    if (SyncAllBranches)
                        TrySyncLocalBranches(repoPath, afterRefs, result);
                }
                else
                {
                    if (SyncAllBranches)
                        TrySyncLocalBranches(repoPath, afterRefs, result);

                    TrySyncWorkingTree(repoPath, result);
                }
            }

            // Submodules: keep superproject-recorded SHAs in sync.
            // Note: this does *not* treat submodules as separate repos for scanning; it updates them via the parent.
            TryUpdateSubmodules(repoPath, result);

            var (rcMod, outMod) = RunGit(repoPath, "submodule status --recursive", result);
            if (rcMod == 0)
            {
                using (var reader = new StringReader(outMod))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.TrimStart().StartsWith("-"))
                        {
                            var parts = line.Trim().Split(' ');
                            if (parts.Length > 1)
                            {
                                result.Logs.Add(new LogItem { Text = $"Uninitialized submodule: {parts[1]}", IsWarning = true });
                            }
                        }
                    }
                }
            }

            return result;
        }

        static void TrySyncLocalBranches(string repoPath, Dictionary<string, string> remoteRefs, RepoResult result)
        {
            var currentBranch = GetCurrentBranch(repoPath);
            var remoteLocalBranchNames = new HashSet<string>(StringComparer.Ordinal);
            var refCommands = new List<string>();
            var newlyCreatedBranches = new List<RemoteBranchRef>();

            foreach (var remoteBranch in GetRemoteBranches(remoteRefs))
            {
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
                    EnsureBranchTracksRemote(repoPath, remoteBranch.LocalBranchName, remoteBranch.RemoteShortName, result);
                    continue;
                }

                if (!TryGetRefSha(repoPath, localRef, out var localSha))
                {
                    refCommands.Add($"create {localRef} {remoteBranch.Sha}");
                    newlyCreatedBranches.Add(remoteBranch);
                    continue;
                }

                if (string.Equals(localSha, remoteBranch.Sha, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ForceSync)
                {
                    refCommands.Add($"update {localRef} {remoteBranch.Sha}");
                    continue;
                }

                var (rcAncestor, _) = RunGit(repoPath, $"merge-base --is-ancestor {localSha} {remoteBranch.Sha}", result);
                if (rcAncestor == 0)
                {
                    FastForwardLocalBranch(repoPath, localRef, localSha, remoteBranch.Sha, result);
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

            if (ForceSync)
                AddLocalBranchDeleteCommands(repoPath, remoteLocalBranchNames, refCommands, result);

            ApplyRefUpdates(repoPath, refCommands, result);

            foreach (var remoteBranch in newlyCreatedBranches)
                EnsureBranchTracksRemote(repoPath, remoteBranch.LocalBranchName, remoteBranch.RemoteShortName, result);
        }

        static List<RemoteBranchRef> GetRemoteBranches(Dictionary<string, string> remoteRefs)
        {
            var branches = new List<RemoteBranchRef>();
            const string prefix = "refs/remotes/";

            foreach (var kvp in remoteRefs.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var refName = kvp.Key;
                if (!refName.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var rest = refName.Substring(prefix.Length);
                var slash = rest.IndexOf('/');
                if (slash <= 0 || slash == rest.Length - 1)
                    continue;

                var remoteName = rest.Substring(0, slash);
                var branchName = rest.Substring(slash + 1);
                if (branchName.Equals("HEAD", StringComparison.Ordinal))
                    continue;

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

        static string? GetCurrentBranch(string repoPath)
        {
            var (rc, output) = RunGit(repoPath, "symbolic-ref --quiet --short HEAD");
            if (rc != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return output.Trim();
        }

        static bool TryGetRefSha(string repoPath, string refName, out string sha)
        {
            sha = "";
            var (rc, output) = RunGit(repoPath, $"rev-parse --verify --quiet {refName}");
            if (rc != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            sha = output.Trim();
            return true;
        }

        static void EnsureBranchTracksRemote(string repoPath, string localBranchName, string remoteShortName, RepoResult result)
        {
            var (rcSet, outSet) = RunGit(repoPath, new[] { "branch", "--set-upstream-to", remoteShortName, localBranchName }, result);
            if (rcSet != 0)
            {
                result.Logs.Add(new LogItem
                {
                    Text = $"Could not set upstream for current branch '{localBranchName}' to '{remoteShortName}':\n{outSet}",
                    IsWarning = true
                });
            }
        }

        static void CreateLocalTrackingBranch(string repoPath, RemoteBranchRef remoteBranch, RepoResult result)
        {
            var (rcCreate, outCreate) = RunGit(repoPath, $"branch --track {remoteBranch.LocalBranchName} {remoteBranch.RemoteShortName}", result);
            if (rcCreate == 0)
            {
                result.Logs.Add(new LogItem
                {
                    Text = $"Created local tracking branch '{remoteBranch.LocalBranchName}' from '{remoteBranch.RemoteShortName}'."
                });
                return;
            }

            var (rcFallback, outFallback) = RunGit(repoPath, $"branch {remoteBranch.LocalBranchName} {remoteBranch.RemoteRefName}", result);
            if (rcFallback == 0)
            {
                result.Logs.Add(new LogItem
                {
                    Text = $"Created local branch '{remoteBranch.LocalBranchName}' from '{remoteBranch.RemoteShortName}'."
                });
                EnsureBranchTracksRemote(repoPath, remoteBranch.LocalBranchName, remoteBranch.RemoteShortName, result);
                return;
            }

            result.Failed = true;
            result.Logs.Add(new LogItem
            {
                Text = $"Could not create local branch '{remoteBranch.LocalBranchName}' from '{remoteBranch.RemoteShortName}':\n{outCreate}\n{outFallback}",
                IsError = true
            });
        }

        static void FastForwardLocalBranch(string repoPath, string localRef, string oldSha, string newSha, RepoResult result)
        {
            var (rc, output) = RunGit(repoPath, $"update-ref {localRef} {newSha} {oldSha}", result);
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

        static void ForceUpdateLocalBranch(string repoPath, string localRef, string newSha, RepoResult result)
        {
            var (rc, output) = RunGit(repoPath, $"update-ref {localRef} {newSha}", result);
            if (rc != 0)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem
                {
                    Text = $"Could not force-update {localRef} to {newSha}:\n{output}",
                    IsError = true
                });
            }
        }

        static void ApplyRefUpdates(string repoPath, List<string> commands, RepoResult result)
        {
            if (commands.Count == 0)
                return;

            var input = string.Join("\n", commands) + "\n";
            var (rc, output) = RunGitWithInput(repoPath, new[] { "update-ref", "--stdin" }, input, result);
            if (rc != 0)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = $"Batch update-ref failed:\n{output}", IsError = true });
            }
        }

        static void AddLocalBranchDeleteCommands(string repoPath, HashSet<string> remoteLocalBranchNames, List<string> commands, RepoResult result)
        {
            var currentBranch = GetCurrentBranch(repoPath);
            var (rc, output) = RunGit(repoPath, "for-each-ref --format=\"%(refname:short)\" refs/heads", result);
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
                if (string.IsNullOrWhiteSpace(branchName))
                    continue;
                if (remoteLocalBranchNames.Contains(branchName))
                    continue;
                if (string.Equals(branchName, currentBranch, StringComparison.Ordinal))
                    continue;

                if (!IsSafeRefName(branchName))
                    continue;

                commands.Add($"delete refs/heads/{branchName}");
                result.Logs.Add(new LogItem
                {
                    Text = $"Queued deletion for local-only branch '{branchName}' because no matching remote branch exists."
                });
            }
        }

        static bool IsSafeRefName(string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
                return false;
            if (branchName.StartsWith("/", StringComparison.Ordinal) || branchName.EndsWith("/", StringComparison.Ordinal))
                return false;
            if (branchName.Contains("..", StringComparison.Ordinal) || branchName.Contains("@{", StringComparison.Ordinal))
                return false;
            if (branchName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                return false;

            return !branchName.Any(ch => char.IsWhiteSpace(ch) || ch == '\\' || ch == '~' || ch == '^' || ch == ':' || ch == '?' || ch == '*' || ch == '[');
        }

        static void TrySyncWorkingTree(string repoPath, RepoResult result)
        {
            if (ForceSync)
            {
                // Best-effort: reset the default branch (origin/HEAD) to match remote.
                var (rcHead, outHead) = RunGit(repoPath, "symbolic-ref -q --short refs/remotes/origin/HEAD", result);
                if (rcHead != 0 || string.IsNullOrWhiteSpace(outHead))
                {
                    result.Failed = true;
                    result.Logs.Add(new LogItem { Text = "Could not determine origin/HEAD; force sync failed.", IsError = true });
                    return;
                }

                // outHead is like: origin/main
                var remoteRef = outHead.Trim();
                var branchName = remoteRef.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
                    ? remoteRef.Substring("origin/".Length)
                    : remoteRef;

                if (CleanUntracked)
                {
                    // Clean first to avoid checkout failure due to untracked files.
                    var (rcCleanPre, outCleanPre) = RunGit(repoPath, new[] { "clean", "-fdx" }, result);
                    if (rcCleanPre != 0)
                    {
                        result.Logs.Add(new LogItem { Text = $"git clean failed:\n{outCleanPre}", IsWarning = true });
                    }
                }

                // checkout -B works across older git versions
                var (rcCo, outCo) = RunGit(repoPath, new[] { "checkout", "-f", "-B", branchName, remoteRef }, result);
                if (rcCo != 0)
                {
                    result.Failed = true;
                    result.Logs.Add(new LogItem { Text = $"Force sync checkout failed:\n{outCo}", IsError = true });
                    return;
                }

                var (rcReset, outReset) = RunGit(repoPath, new[] { "reset", "--hard", remoteRef }, result);
                if (rcReset != 0)
                {
                    result.Failed = true;
                    result.Logs.Add(new LogItem { Text = $"Force sync reset failed:\n{outReset}", IsError = true });
                    return;
                }

                if (CleanUntracked)
                {
                    var (rcClean, outClean) = RunGit(repoPath, new[] { "clean", "-fdx" }, result);
                    if (rcClean != 0)
                    {
                        result.Logs.Add(new LogItem { Text = $"git clean failed:\n{outClean}", IsWarning = true });
                    }
                }

                return;
            }

            // Safe mode: fast-forward only (no merges, no resets).
            var (rcPull, outPull) = RunGitWithSshToHttpsFallback(repoPath, "pull --ff-only --recurse-submodules=no", result);
            if (rcPull != 0)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = $"Pull (ff-only) failed:\n{outPull}", IsError = true });
            }
        }

        static void TryFetchLfsObjects(string repoPath, RepoResult result)
        {
            var (rcVersion, _) = RunGit(repoPath, new[] { "lfs", "version" });
            if (rcVersion != 0)
                return;

            if (!File.Exists(Path.Combine(repoPath, ".gitattributes")))
            {
                var (rcTrack, trackOutput) = RunGit(repoPath, new[] { "lfs", "track" });
                if (rcTrack != 0 || string.IsNullOrWhiteSpace(trackOutput))
                    return;
            }

            var (rcFetch, outFetch) = RunGit(repoPath, new[] { "lfs", "fetch", "--all", "--prune" }, result);
            if (rcFetch != 0)
            {
                result.Logs.Add(new LogItem { Text = $"Git LFS fetch failed:\n{outFetch}", IsWarning = true });
            }
        }

        static void TryUpdateSubmodules(string repoPath, RepoResult result)
        {
            if (!File.Exists(Path.Combine(repoPath, ".gitmodules")))
                return;

            // Keep URLs consistent with .gitmodules
            var (rcSync, outSync) = RunGit(repoPath, "submodule sync --recursive", result);
            if (rcSync != 0)
            {
                result.Logs.Add(new LogItem { Text = $"Submodule sync failed:\n{outSync}", IsWarning = true });
            }

            var args = InitMissingSubmodules
                ? "submodule update --init --recursive"
                : "submodule update --recursive";

            if (ForceSync)
                args += " --force";

            var (rcSub, outSub) = RunGitWithSshToHttpsFallback(repoPath, args, result);
            if (rcSub != 0)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = $"Submodule update failed:\n{outSub}", IsError = true });
                return;
            }

            // Fetch submodule remotes to keep their remote-tracking refs up to date too.
            TryFetchSubmoduleRemotes(repoPath, result);
        }

        static void TryFetchSubmoduleRemotes(string repoPath, RepoResult result)
        {
            var (rc, output) = RunGit(repoPath, "submodule status --recursive", result);
            if (rc != 0 || string.IsNullOrWhiteSpace(output))
                return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StringReader(output))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    // First token begins with status prefix (+/-/U/space) attached to the SHA.
                    if (parts[0].StartsWith("-", StringComparison.Ordinal))
                        continue; // uninitialized

                    var relPath = parts[1];
                    if (!seen.Add(relPath))
                        continue;

                    var subPath = Path.Combine(repoPath, relPath);
                    if (!Directory.Exists(subPath))
                        continue;

                    using (var submoduleLease = TryAcquireRepoMutex(subPath, result))
                    {
                        if (submoduleLease == null)
                            continue;

                        TryCleanupStaleGitLocks(subPath, result);

                        var (rcFetch, outFetch) = RunGitWithSshToHttpsFallback(subPath, "fetch --all --prune --prune-tags --tags --force", result);
                        if (rcFetch != 0)
                        {
                            result.Logs.Add(new LogItem { Text = $"Submodule fetch failed ({relPath}):\n{outFetch}", IsWarning = true });
                        }

                        if (ForceSync && CleanUntracked)
                        {
                            var (rcClean, outClean) = RunGit(subPath, new[] { "clean", "-fdx" }, result);
                            if (rcClean != 0)
                            {
                                result.Logs.Add(new LogItem { Text = $"Submodule clean failed ({relPath}):\n{outClean}", IsWarning = true });
                            }
                        }
                    }
                }
            }
        }

        static void ParseAndAddCommits(RepoResult result, string logOutput, HashSet<string> seenCommits)
        {
            var lines = logOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { ' ' }, 2);
                if (parts.Length < 2) continue;

                var hash = parts[0];
                if (!seenCommits.Contains(hash))
                {
                    seenCommits.Add(hash);
                    result.NewCommitsCount++;
                    // Add purely the message/hash, not indented yet
                    // We can reuse LogItem but maybe differentiate it
                    result.Logs.Add(new LogItem { Text = line, IsCommit = true });
                }
            }
        }

        static Dictionary<string, string> GetRemoteRefs(string repoPath, RepoResult? result = null)
        {
            var refs = new Dictionary<string, string>();
            var (rc, output) = RunGit(repoPath, "for-each-ref --format=\"%(refname) %(objectname)\" refs/remotes", result);
            if (rc == 0)
            {
                using (var reader = new StringReader(output))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var parts = line.Split(' ');
                        if (parts.Length >= 2) refs[parts[0]] = parts[1];
                    }
                }
            }
            return refs;
        }

        static (int, string) RunGit(string cwd, string args, RepoResult? result = null)
        {
            (int rc, string output) lastResult = (-1, "");

            for (int attempt = 0; attempt < GitLockRetryCount; attempt++)
            {
                lastResult = RunGitOnce(cwd, args, result);
                if (lastResult.rc == 0 || !LooksLikeGitLockFailure(lastResult.output) || attempt == GitLockRetryCount - 1)
                    return lastResult;

                TryCleanupStaleGitLocks(cwd, result);
                Thread.Sleep(GitLockRetryDelayMs * (attempt + 1));
            }

            return lastResult;
        }

        static (int, string) RunGit(string cwd, IReadOnlyList<string> args, RepoResult? result = null)
        {
            return RunGitWithInput(cwd, args, null, result);
        }

        static (int, string) RunGitWithInput(string cwd, IReadOnlyList<string> args, string? stdin, RepoResult? result = null)
        {
            (int rc, string output) lastResult = (-1, "");

            for (int attempt = 0; attempt < GitLockRetryCount; attempt++)
            {
                lastResult = RunGitWithInputOnce(cwd, args, stdin, result);
                if (lastResult.rc == 0 || !LooksLikeGitLockFailure(lastResult.output) || attempt == GitLockRetryCount - 1)
                    return lastResult;

                TryCleanupStaleGitLocks(cwd, result);
                Thread.Sleep(GitLockRetryDelayMs * (attempt + 1));
            }

            return lastResult;
        }

        static (int, string) RunGitWithInputOnce(string cwd, IReadOnlyList<string> args, string? stdin, RepoResult? result = null)
        {
            string commandLabel = $"git {FormatArgsForLog(args)}";
            var startedAt = DateTimeOffset.Now;
            var stopwatch = Stopwatch.StartNew();

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
                    psi.ArgumentList.Add(arg);

                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
                psi.Environment["GCM_INTERACTIVE"] = "never";

                var p = Process.Start(psi);
                if (p == null)
                {
                    stopwatch.Stop();
                    RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
                    return (-1, $"Failed to start git process. Command: {commandLabel}\nRepository: {cwd}");
                }

                using (p)
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();

                    if (stdin != null)
                    {
                        p.StandardInput.Write(stdin);
                        p.StandardInput.Close();
                    }

                    if (!p.WaitForExit(GitTimeout))
                    {
                        var timeoutDetails = new StringBuilder();
                        timeoutDetails.AppendLine($"Timeout ({GitTimeout / 1000}s)");
                        timeoutDetails.AppendLine($"Command: {commandLabel}");
                        timeoutDetails.AppendLine($"Repository: {cwd}");

                        try
                        {
                            if (!p.HasExited)
                                p.Kill(entireProcessTree: true);

                            if (!p.WaitForExit(5000))
                                timeoutDetails.AppendLine("Warning: Process did not exit within 5s after kill request.");
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

                        stopwatch.Stop();
                        RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: true);

                        return (-1, timeoutDetails.ToString().Trim());
                    }

                    Task.WaitAll(stdout, stderr);
                    stopwatch.Stop();
                    RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, p.ExitCode, timedOut: false);
                    return (p.ExitCode, (stdout.Result + "\n" + stderr.Result).Trim());
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
                return (-1, $"{ex.Message}\nCommand: {commandLabel}\nRepository: {cwd}");
            }
        }

        static string FormatArgsForLog(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(arg =>
            {
                if (arg.Length == 0)
                    return "\"\"";
                if (arg.Any(char.IsWhiteSpace) || arg.Contains('"'))
                    return "\"" + arg.Replace("\"", "\\\"") + "\"";
                return arg;
            }));
        }

        static (int, string) RunGitOnce(string cwd, string args, RepoResult? result = null)
        {
            string commandLabel = $"git {args}";
            var startedAt = DateTimeOffset.Now;
            var stopwatch = Stopwatch.StartNew();

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

                // Never prompt interactively in automation.
                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
                psi.Environment["GCM_INTERACTIVE"] = "never";

                var p = Process.Start(psi);
                if (p == null)
                {
                    stopwatch.Stop();
                    RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
                    return (-1, $"Failed to start git process. Command: {commandLabel}\nRepository: {cwd}");
                }

                using (p)
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    
                    if (!p.WaitForExit(GitTimeout))
                    {
                        var timeoutDetails = new StringBuilder();
                        timeoutDetails.AppendLine($"Timeout ({GitTimeout / 1000}s)");
                        timeoutDetails.AppendLine($"Command: {commandLabel}");
                        timeoutDetails.AppendLine($"Repository: {cwd}");

                        try
                        {
                            if (!p.HasExited)
                                p.Kill(entireProcessTree: true);

                            if (!p.WaitForExit(5000))
                                timeoutDetails.AppendLine("Warning: Process did not exit within 5s after kill request.");
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

                        stopwatch.Stop();
                        RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: true);

                        return (-1, timeoutDetails.ToString().Trim());
                    }

                    Task.WaitAll(stdout, stderr);
                    stopwatch.Stop();
                    RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, p.ExitCode, timedOut: false);
                    return (p.ExitCode, (stdout.Result + "\n" + stderr.Result).Trim());
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                RecordOperation(result, commandLabel, cwd, startedAt, stopwatch.Elapsed, -1, timedOut: false);
                return (-1, $"{ex.Message}\nCommand: {commandLabel}\nRepository: {cwd}");
            }
        }

        static void RecordOperation(RepoResult? result, string command, string cwd, DateTimeOffset startedAt, TimeSpan elapsed, int exitCode, bool timedOut)
        {
            if (result == null)
                return;

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

        static (int, string) RunGitWithSshToHttpsFallback(string cwd, string args, RepoResult? result = null)
        {
            var (rc, output) = RunGit(cwd, args, result);
            if (rc == 0)
                return (rc, output);

            if (!LooksLikeSshAuthOrHostKeyFailure(output))
                return (rc, output);

            var hosts = ExtractHostsFromText(output);
            if (hosts.Count == 0)
            {
                var (rcRemotes, outRemotes) = RunGit(cwd, "remote -v", result);
                if (rcRemotes == 0 && !string.IsNullOrWhiteSpace(outRemotes))
                    hosts = ExtractHostsFromText(outRemotes);
            }

            if (hosts.Count == 0)
                hosts = ExtractHostsFromGitmodules(cwd);

            var rewritePrefix = BuildSshToHttpsRewritePrefix(hosts);
            if (string.IsNullOrWhiteSpace(rewritePrefix))
                return (rc, output);

            var (rc2, output2) = RunGit(cwd, $"{rewritePrefix} {args}", result);
            if (rc2 == 0)
                return (rc2, output2);

            var combined = new StringBuilder();
            combined.AppendLine(output);
            combined.AppendLine();
            combined.AppendLine("--- retry with ssh->https rewrite ---");
            combined.AppendLine(output2);
            return (rc2, combined.ToString().Trim());
        }

        static bool LooksLikeSshAuthOrHostKeyFailure(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            return output.IndexOf("Host key verification failed", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("Permission denied (publickey", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("Could not read from remote repository", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("fatal: Could not read from remote repository", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool LooksLikeGitLockFailure(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            return output.IndexOf(".lock", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("cannot lock", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("could not lock", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("Unable to create", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("another git process seems to be running", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static HashSet<string> ExtractHostsFromText(string text)
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
                return hosts;

            foreach (Match m in Regex.Matches(text, @"git@([A-Za-z0-9\.-]+):", RegexOptions.IgnoreCase))
            {
                var host = m.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(host)) hosts.Add(host);
            }

            foreach (Match m in Regex.Matches(text, @"ssh://git@([A-Za-z0-9\.-]+)/", RegexOptions.IgnoreCase))
            {
                var host = m.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(host)) hosts.Add(host);
            }

            foreach (Match m in Regex.Matches(text, @"https?://([A-Za-z0-9\.-]+)/", RegexOptions.IgnoreCase))
            {
                var host = m.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(host)) hosts.Add(host);
            }

            return hosts;
        }

        static HashSet<string> ExtractHostsFromGitmodules(string repoPath)
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(repoPath, ".gitmodules");
                if (!File.Exists(path))
                    return hosts;
                var text = File.ReadAllText(path, Encoding.UTF8);
                return ExtractHostsFromText(text);
            }
            catch
            {
                return hosts;
            }
        }

        static string BuildSshToHttpsRewritePrefix(IEnumerable<string> hosts)
        {
            var sb = new StringBuilder();
            foreach (var host in hosts)
            {
                if (string.IsNullOrWhiteSpace(host))
                    continue;

                // Defensive: only allow hostnames.
                if (!Regex.IsMatch(host, @"^[A-Za-z0-9\.-]+$"))
                    continue;

                sb.Append($"-c url.\"https://{host}/\".insteadOf=git@{host}: ");
                sb.Append($"-c url.\"https://{host}/\".insteadOf=ssh://git@{host}/ ");
            }
            return sb.ToString().Trim();
        }

        static void PrintResult(RepoResult res)
        {
            string status;
            ConsoleColor statusColor;

            if (res.Failed)
            {
                status = "[FAILED]";
                statusColor = ConsoleColor.Red;
            }
            else if (res.NewCommitsCount > 0)
            {
                // New Format: [+5 new commits]
                status = $"[+{res.NewCommitsCount} new commits]";
                statusColor = ConsoleColor.Green;
            }
            else
            {
                return; // Don't print OK repos
            }

            Console.ForegroundColor = statusColor;
            Console.Write(res.Failed ? "✗ " : "✔ ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{res.Name,-30} ");
            Console.ForegroundColor = statusColor;
            Console.WriteLine(status);
            Console.ResetColor();

            if (res.Logs.Count > 0)
            {
                for (int i = 0; i < res.Logs.Count; i++)
                {
                    var log = res.Logs[i];
                    var isLast = (i == res.Logs.Count - 1);
                    var prefix = isLast ? TreeLast : TreeBranch;

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"   {prefix} ");
                    
                    if (log.IsError) Console.ForegroundColor = ConsoleColor.Red;
                    else if (log.IsWarning) Console.ForegroundColor = ConsoleColor.Yellow;
                    else if (log.IsCommit) Console.ForegroundColor = ConsoleColor.Gray; 
                    else Console.ForegroundColor = ConsoleColor.Gray;

                    // Support multi-line logs just in case, though commits are typically single line in our format
                    var lines = log.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int j = 0; j < lines.Length; j++)
                    {
                        if (j > 0)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write($"   {TreeVert} ");
                            if (log.IsError) Console.ForegroundColor = ConsoleColor.Red;
                            else if (log.IsWarning) Console.ForegroundColor = ConsoleColor.Yellow;
                            else Console.ForegroundColor = ConsoleColor.Gray;
                        }
                        Console.WriteLine(lines[j]);
                    }
                    Console.ResetColor();
                }
            }
        }

        static void WriteSummary(List<RepoResult> results, TimeSpan elapsed)
        {
            Console.WriteLine("\n\n========================================================");
            Console.WriteLine("                   SUMMARY");
            Console.WriteLine("========================================================");

            // Stats
            Console.WriteLine($"Total Time: {elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"Processed:  {TotalRepos}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"New Commits:{GlobalNewCommitsCount}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed:     {FailCount}");
            Console.ResetColor();
            Console.WriteLine("--------------------------------------------------------");

            // Failures
            if (FailCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n🔴 Failures:");
                foreach (var r in results.Where(x => x.Failed))
                {
                    Console.WriteLine($"  - {r.Name}");
                    foreach(var l in r.Logs.Where(x => x.IsError))
                        Console.WriteLine($"    {l.Text.Replace("\n", "\n    ")}");
                }
                Console.ResetColor();
            }

            // Updates
            if (GlobalNewCommitsCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n🟢 Updates:");
                foreach (var r in results.Where(x => !x.Failed && x.NewCommitsCount > 0))
                {
                    Console.WriteLine($"  - {r.Name} (+{r.NewCommitsCount} new commits)");
                    // We can optionally print the commits here too or keep the summary high-level.
                    // The user liked the "summary" but the previous code printed details in the summary section only for commits.
                    // The user said "practical summary". Let's listing the commits here too is good?
                    // The previous output had "Updates:" with details.
                    // Let's print the top 5 commits or so, or all of them.
                    // Since we already deduplicated, listing them is safe.
                    
                    int shown = 0;
                    foreach(var l in r.Logs.Where(x => x.IsCommit))
                    {
                        Console.WriteLine($"    {l.Text}");
                        shown++;
                        if (shown >= 10) 
                        {
                            Console.WriteLine($"    ... and {r.NewCommitsCount - 10} more");
                            break;
                        }
                    }
                }
                Console.ResetColor();
            }
            
            Console.WriteLine("\n========================================================");

            // Markdown Report
            var orderedResults = results
                .OrderBy(r => r.StartedAt == default ? DateTimeOffset.MaxValue : r.StartedAt)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var workerGroups = orderedResults
                .GroupBy(r => r.WorkerSlot)
                .OrderBy(g => g.Key)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("# Git Update Report");
            sb.AppendLine($"Generated: {DateTime.Now}");
            sb.AppendLine();

            sb.AppendLine("## Run Summary");
            sb.AppendLine($"- Total Repositories: {TotalRepos}");
            sb.AppendLine($"- Requested Workers: {MaxDegreeOfParallelism}");
            sb.AppendLine($"- Successful Repositories: {SuccessCount}");
            sb.AppendLine($"- Failed Repositories: {FailCount}");
            sb.AppendLine($"- Total New Commits: {GlobalNewCommitsCount}");
            sb.AppendLine($"- Wall-clock Elapsed: {elapsed.TotalSeconds:F2}s");
            sb.AppendLine();

            if (VerboseReport)
            {
                sb.AppendLine("## Worker Execution Details");
                foreach (var workerGroup in workerGroups)
                {
                    var workerTotal = TimeSpan.FromTicks(workerGroup.Sum(r => r.Elapsed.Ticks));
                    sb.AppendLine($"### Worker {workerGroup.Key}");
                    sb.AppendLine($"- Repositories Handled: {workerGroup.Count()}");
                    sb.AppendLine($"- Cumulative Repository Time: {workerTotal.TotalSeconds:F2}s");
                    sb.AppendLine();

                    foreach (var res in workerGroup)
                    {
                        sb.AppendLine($"#### {res.Name}");
                        sb.AppendLine($"- Repository Path: `{res.Path}`");
                        sb.AppendLine($"- Started At: {res.StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
                        sb.AppendLine($"- Completed At: {res.CompletedAt:yyyy-MM-dd HH:mm:ss zzz}");
                        sb.AppendLine($"- Total Elapsed: {res.Elapsed.TotalSeconds:F2}s");

                        if (res.Operations.Count > 0)
                        {
                            sb.AppendLine("- Operations:");
                            foreach (var op in res.Operations)
                            {
                                var status = op.TimedOut ? "timeout" : (op.ExitCode == 0 ? "ok" : $"rc={op.ExitCode}");
                                sb.AppendLine($"  - {op.StartedAt:HH:mm:ss} | `{op.Command}` | {op.Elapsed.TotalSeconds:F2}s | {status}");
                            }
                        }
                        else
                        {
                            sb.AppendLine("- Operations: none recorded");
                        }

                        sb.AppendLine();
                    }
                }
            }

            sb.AppendLine("## Repository Result Notes");
            foreach (var res in orderedResults)
            {
                var icon = res.Failed ? "❌" : "✅";
                sb.AppendLine($"## {icon} {res.Name}");
                if (res.Failed) sb.AppendLine("**FAILED**");
                if (res.NewCommitsCount > 0) sb.AppendLine($"- New Commits: {res.NewCommitsCount}");
                if (res.Logs.Count > 0)
                {
                    sb.AppendLine("```");
                    foreach (var log in res.Logs) sb.AppendLine(log.Text);
                    sb.AppendLine("```");
                }
                sb.AppendLine();
            }
            var reportText = sb.ToString();
            var reportPath = GetRunReportPath();
            var latestReportPath = Path.Combine(RootDir, LatestReportFileName);
            File.WriteAllText(reportPath, reportText, Encoding.UTF8);
            File.WriteAllText(latestReportPath, reportText, Encoding.UTF8);
            Console.WriteLine($"Report written to {Path.GetFullPath(reportPath)}");
            Console.WriteLine($"Latest report written to {Path.GetFullPath(latestReportPath)}");
        }

        static string GetRunReportPath()
        {
            return Path.Combine(RootDir, $"git_update_report-{RunId}.md");
        }
    }

    class RepoResult
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int NewCommitsCount { get; set; }
        public bool Failed { get; set; }
        public int WorkerSlot { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset CompletedAt { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<RepoOperation> Operations { get; set; } = new List<RepoOperation>();
        public List<LogItem> Logs { get; set; } = new List<LogItem>();
    }

    class RepoOperation
    {
        public string Command { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public TimeSpan Elapsed { get; set; }
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
    }

    class RemoteBranchRef
    {
        public string RemoteName { get; set; } = "";
        public string BranchName { get; set; } = "";
        public string LocalBranchName { get; set; } = "";
        public string RemoteShortName { get; set; } = "";
        public string RemoteRefName { get; set; } = "";
        public string Sha { get; set; } = "";
    }

    sealed class RepoMutexLease : IDisposable
    {
        readonly Mutex mutex;
        bool disposed;

        public RepoMutexLease(Mutex mutex)
        {
            this.mutex = mutex;
        }

        public void Dispose()
        {
            if (disposed)
                return;

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

    class LogItem
    {
        public string Text { get; set; } = "";
        public bool IsError { get; set; }
        public bool IsWarning { get; set; }
        public bool IsCommit { get; set; } // Replaced IsUpdate with specific IsCommit for styling
    }
}

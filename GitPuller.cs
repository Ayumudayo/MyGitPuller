using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitPuller
{
    internal static class Program
    {
        private static readonly object ConsoleLock = new object();
        private const int DefaultMaxDegreeOfParallelism = 6;
        private const int DefaultGitTimeoutSeconds = 60;
        private const int MinGitTimeoutSeconds = 1;
        private const string CacheFileName = ".git_repo_cache.json";
        private static readonly string RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

        private static int maxDegreeOfParallelism = DefaultMaxDegreeOfParallelism;
        private static bool initMissingSubmodules = true;
        private static bool forceSync = true;
        private static bool cleanUntracked = true;
        private static bool forceRescan;
        private static bool pullFfOnly = true;
        private static bool syncAllBranches = true;
        private static bool staleGitLockCleanup = true;
        private static bool verboseReport;
        private static bool showHelp;
        private static string rootDir = AppContext.BaseDirectory;
        private static int gitTimeoutMilliseconds = DefaultGitTimeoutSeconds * 1000;
        private static TimeSpan staleGitLockAge = TimeSpan.FromMinutes(10);
        private static bool supportsCursorControl = true;

        private static int totalRepos;
        private static int processedCount;
        private static int successCount;
        private static int failCount;
        private static int globalNewCommitsCount;

        private const string TreeVert = "│ ";
        private const string TreeBranch = "├─";
        private const string TreeLast = "└─";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            try
            {
                Console.CursorVisible = false;
            }
            catch
            {
                supportsCursorControl = false;
            }

            try
            {
                ParseArgs(args);
                if (showHelp)
                {
                    PrintUsage();
                    return 0;
                }

                if (!ValidateAndNormalizeSettings())
                {
                    return 1;
                }

                using var rootLease = TryAcquireRootMutex();
                if (rootLease == null)
                {
                    return 1;
                }

                var scanner = new GitRepositoryScanner();
                RepositoryInventory inventory;
                if (!forceRescan && TryLoadCache(out var cachedRepos))
                {
                    Console.WriteLine($"Loaded {cachedRepos.Count} repositories from cache.");
                    inventory = BuildInventoryFromCachedRepoPaths(rootDir, cachedRepos);
                }
                else
                {
                    Console.WriteLine($"Scanning {rootDir} for git repositories...");
                    inventory = scanner.ScanLibraryRoot(rootDir);
                    SaveCache(inventory.Repositories.Select(x => x.Path).ToList());
                }

                ResetRunStats(inventory.Repositories.Count);
                if (totalRepos == 0)
                {
                    Console.WriteLine("No repositories found.");
                    return 0;
                }

                Console.WriteLine($"Found {totalRepos} repositories. Processing with {maxDegreeOfParallelism} workers...");
                Console.WriteLine();

                DrawProgress();

                var options = CreateOptions();
                var request = new GitPullerRunRequest(options, inventory);
                var runner = new GitPullerRunner();
                var progress = new InlineProgress<GitPullerProgressEvent>(HandleProgressEvent);
                var runResult = runner.RunAllAsync(request, progress, CancellationToken.None).GetAwaiter().GetResult();

                ClearCurrentLine();
                if (!string.IsNullOrWhiteSpace(runResult.ErrorMessage))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(runResult.ErrorMessage);
                    Console.ResetColor();
                    return 1;
                }

                WriteSummary(runResult, options);
                return runResult.FailCount > 0 ? 1 : 0;
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
                }
            }
        }

        private static GitPullerOptions CreateOptions()
        {
            return new GitPullerOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                InitMissingSubmodules = initMissingSubmodules,
                ForceSync = forceSync,
                CleanUntracked = cleanUntracked,
                PullFfOnly = pullFfOnly,
                SyncAllBranches = syncAllBranches,
                StaleGitLockCleanup = staleGitLockCleanup,
                VerboseReport = verboseReport,
                GitTimeoutMilliseconds = gitTimeoutMilliseconds,
                StaleGitLockAge = staleGitLockAge
            };
        }

        private static void ResetRunStats(int repositoryCount)
        {
            totalRepos = repositoryCount;
            processedCount = 0;
            successCount = 0;
            failCount = 0;
            globalNewCommitsCount = 0;
        }

        private static void HandleProgressEvent(GitPullerProgressEvent progressEvent)
        {
            lock (ConsoleLock)
            {
                if (!string.IsNullOrWhiteSpace(progressEvent.Message))
                {
                    ClearCurrentLine();
                    if (progressEvent.IsError)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                    }
                    else if (progressEvent.IsWarning)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                    }

                    Console.WriteLine(progressEvent.Message);
                    Console.ResetColor();
                }

                if (progressEvent.Kind == GitPullerProgressEventKind.RepositoryCompleted && progressEvent.RepositoryResult != null)
                {
                    processedCount++;
                    if (progressEvent.RepositoryResult.Failed)
                    {
                        failCount++;
                    }
                    else
                    {
                        successCount++;
                    }

                    globalNewCommitsCount += progressEvent.RepositoryResult.NewCommitsCount;

                    if (progressEvent.RepositoryResult.Failed || progressEvent.RepositoryResult.NewCommitsCount > 0)
                    {
                        ClearCurrentLine();
                        PrintResult(progressEvent.RepositoryResult);
                    }
                }

                DrawProgress();
            }
        }

        private static void ParseArgs(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "-w")
                {
                    if (!TryReadOptionValue(args, ref i, "-w", out var workerCountRaw))
                    {
                        continue;
                    }

                    if (!int.TryParse(workerCountRaw, out var workerCount) || workerCount < 1)
                    {
                        Console.WriteLine($"Warning: Invalid worker count '{workerCountRaw}'. Keeping {maxDegreeOfParallelism}.");
                        continue;
                    }

                    maxDegreeOfParallelism = workerCount;
                }
                else if (args[i] == "--init-missing-submodules")
                {
                    initMissingSubmodules = true;
                }
                else if (args[i] == "--no-init-submodules")
                {
                    initMissingSubmodules = false;
                }
                else if (args[i] == "--rescan")
                {
                    forceRescan = true;
                }
                else if (args[i] == "--force-sync")
                {
                    forceSync = true;
                }
                else if (args[i] == "--clean")
                {
                    cleanUntracked = true;
                }
                else if (args[i] == "--no-pull")
                {
                    pullFfOnly = false;
                }
                else if (args[i] == "--all-branches")
                {
                    syncAllBranches = true;
                }
                else if (args[i] == "--current-branch-only")
                {
                    syncAllBranches = false;
                }
                else if (args[i] == "--stale-lock-minutes")
                {
                    if (!TryReadOptionValue(args, ref i, "--stale-lock-minutes", out var staleMinutesRaw))
                    {
                        continue;
                    }

                    if (!double.TryParse(staleMinutesRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes < 0)
                    {
                        Console.WriteLine($"Warning: Invalid stale lock age '{staleMinutesRaw}'. Keeping {staleGitLockAge.TotalMinutes:F0} minutes.");
                        continue;
                    }

                    staleGitLockAge = TimeSpan.FromMinutes(minutes);
                }
                else if (args[i] == "--no-stale-lock-cleanup")
                {
                    staleGitLockCleanup = false;
                }
                else if (args[i] == "--verbose-report")
                {
                    verboseReport = true;
                }
                else if (args[i] == "--root")
                {
                    if (!TryReadOptionValue(args, ref i, "--root", out var rootRaw))
                    {
                        continue;
                    }

                    rootDir = rootRaw;
                }
                else if (args[i] == "-t" || args[i] == "--timeout")
                {
                    if (!TryReadOptionValue(args, ref i, args[i], out var timeoutRaw))
                    {
                        continue;
                    }

                    if (!int.TryParse(timeoutRaw, out var seconds) || seconds < MinGitTimeoutSeconds)
                    {
                        Console.WriteLine($"Warning: Invalid timeout '{timeoutRaw}'. Keeping {gitTimeoutMilliseconds / 1000}s.");
                        continue;
                    }

                    if (seconds > int.MaxValue / 1000)
                    {
                        Console.WriteLine($"Warning: Timeout '{timeoutRaw}' is too large. Keeping {gitTimeoutMilliseconds / 1000}s.");
                        continue;
                    }

                    gitTimeoutMilliseconds = seconds * 1000;
                }
                else if (args[i] == "-h" || args[i] == "--help")
                {
                    showHelp = true;
                }
                else
                {
                    Console.WriteLine($"Warning: Unknown option '{args[i]}' ignored. Use --help to see valid options.");
                }
            }
        }

        private static void PrintUsage()
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

        private static bool ValidateAndNormalizeSettings()
        {
            if (maxDegreeOfParallelism < 1)
            {
                Console.WriteLine($"Warning: Worker count must be >= 1. Falling back to {DefaultMaxDegreeOfParallelism}.");
                maxDegreeOfParallelism = DefaultMaxDegreeOfParallelism;
            }

            var minTimeoutMs = MinGitTimeoutSeconds * 1000;
            if (gitTimeoutMilliseconds < minTimeoutMs)
            {
                Console.WriteLine($"Warning: Timeout must be >= {MinGitTimeoutSeconds}s. Falling back to {DefaultGitTimeoutSeconds}s.");
                gitTimeoutMilliseconds = DefaultGitTimeoutSeconds * 1000;
            }

            try
            {
                rootDir = Path.GetFullPath(rootDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: Invalid root path '{rootDir}'. {ex.Message}");
                return false;
            }

            if (!Directory.Exists(rootDir))
            {
                Console.WriteLine($"Error: Root directory does not exist: {rootDir}");
                return false;
            }

            return true;
        }

        private static bool TryReadOptionValue(string[] args, ref int index, string option, out string value)
        {
            value = string.Empty;
            var valueIndex = index + 1;
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

        private static bool IsRecognizedOption(string arg)
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

        private static bool TryLoadCache(out List<string> repos)
        {
            repos = new List<string>();
            var cachePath = Path.Combine(rootDir, CacheFileName);
            if (!File.Exists(cachePath))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(cachePath, Encoding.UTF8);
                var cached = JsonSerializer.Deserialize<List<string>>(json);
                if (cached == null)
                {
                    return false;
                }

                var valid = new List<string>();
                foreach (var path in cached)
                {
                    if (!TryNormalizeRepoPath(path, out var normalizedPath)
                        || !IsPathUnderRoot(rootDir, normalizedPath)
                        || !IsGitRepoRoot(normalizedPath, out var isSubmodule)
                        || isSubmodule)
                    {
                        return false;
                    }

                    valid.Add(normalizedPath);
                }

                repos = NormalizeRepoList(valid);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SaveCache(List<string> repos)
        {
            try
            {
                var cachePath = Path.Combine(rootDir, CacheFileName);
                var json = JsonSerializer.Serialize(repos, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(cachePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to save cache: {ex.Message}");
            }
        }

        private static RepositoryInventory BuildInventoryFromCachedRepoPaths(string libraryRoot, IEnumerable<string> repoPaths)
        {
            var normalizedRoot = Path.GetFullPath(libraryRoot);
            var repositories = NormalizeRepoList(repoPaths)
                .Select(repoPath => new RepositoryDescriptor(
                    repoPath,
                    Path.GetFileName(repoPath),
                    GetRepositoryCategory(normalizedRoot, repoPath),
                    TryGetOriginRemoteUrl(repoPath)))
                .ToList();

            return new RepositoryInventory(normalizedRoot, repositories);
        }

        private static bool TryNormalizeRepoPath(string repoPath, out string normalizedPath)
        {
            normalizedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(repoPath))
            {
                return false;
            }

            try
            {
                normalizedPath = Path.GetFullPath(repoPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPathUnderRoot(string root, string path)
        {
            var relativePath = Path.GetRelativePath(root, path);
            return relativePath == "."
                || (!Path.IsPathRooted(relativePath)
                    && !relativePath.Equals("..", StringComparison.Ordinal)
                    && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
        }

        private static string? TryGetOriginRemoteUrl(string repoPath)
        {
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                processStartInfo.ArgumentList.Add("remote");
                processStartInfo.ArgumentList.Add("get-url");
                processStartInfo.ArgumentList.Add("origin");
                processStartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
                processStartInfo.Environment["GCM_INTERACTIVE"] = "never";

                using var process = Process.Start(processStartInfo);
                if (process == null)
                {
                    return null;
                }

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                    }

                    return null;
                }

                Task.WaitAll(stdout, stderr);
                if (process.ExitCode != 0)
                {
                    return null;
                }

                var remoteUrl = stdout.Result.Trim();
                return string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl;
            }
            catch
            {
                return null;
            }
        }

        private static List<string> NormalizeRepoList(IEnumerable<string> repoPaths)
        {
            var repos = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var repoPath in repoPaths)
            {
                if (string.IsNullOrWhiteSpace(repoPath))
                {
                    continue;
                }

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
                {
                    repos.Add(normalized);
                }
            }

            repos.Sort(StringComparer.OrdinalIgnoreCase);
            return repos;
        }

        private static string GetRepositoryCategory(string libraryRoot, string repoPath)
        {
            var relativePath = Path.GetRelativePath(libraryRoot, repoPath);
            var category = Path.GetDirectoryName(relativePath) ?? string.Empty;
            if (string.Equals(category, ".", StringComparison.Ordinal))
            {
                category = string.Empty;
            }

            return category
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static bool IsGitRepoRoot(string path, out bool isSubmoduleWorkingTree)
        {
            isSubmoduleWorkingTree = false;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var gitPath = Path.Combine(path, ".git");
            if (Directory.Exists(gitPath))
            {
                return true;
            }

            if (!File.Exists(gitPath))
            {
                return false;
            }

            try
            {
                var text = File.ReadAllText(gitPath, Encoding.UTF8);
                var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstLine == null)
                {
                    return false;
                }

                const string prefix = "gitdir:";
                if (!firstLine.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var gitdir = firstLine[(firstLine.IndexOf(':') + 1)..].Trim();
                var normalized = gitdir.Replace('/', Path.DirectorySeparatorChar);
                var marker = string.Join(Path.DirectorySeparatorChar.ToString(), new[] { ".git", "modules" });
                if (normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isSubmoduleWorkingTree = true;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static RootMutexLease? TryAcquireRootMutex()
        {
            var normalized = rootDir
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            var mutexName = $"MyGitPuller_Root_{hash}";
            var mutex = new Mutex(false, mutexName);

            try
            {
                if (!mutex.WaitOne(gitTimeoutMilliseconds))
                {
                    mutex.Dispose();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Another GitPuller instance is already processing this root: {rootDir}");
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

            return new RootMutexLease(mutex);
        }

        private static void DrawProgress()
        {
            if (totalRepos == 0 || !supportsCursorControl || Console.IsOutputRedirected)
            {
                return;
            }

            int width;
            try
            {
                width = Math.Min(50, Console.WindowWidth - 30);
            }
            catch
            {
                return;
            }

            if (width < 10)
            {
                width = 10;
            }

            var pct = (double)processedCount / totalRepos;
            var filled = (int)(width * pct);
            var empty = width - filled;
            var bar = new string('█', filled) + new string('░', empty);
            var status = $"\r[{bar}] {processedCount}/{totalRepos} ({pct:P0})";
            Console.Write(status);
        }

        private static void ClearCurrentLine()
        {
            if (!supportsCursorControl || Console.IsOutputRedirected)
            {
                return;
            }

            try
            {
                var currentLineCursor = Console.CursorTop;
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, currentLineCursor);
            }
            catch
            {
                supportsCursorControl = false;
            }
        }

        private static void PrintResult(RepoResult res)
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
                status = $"[+{res.NewCommitsCount} new commits]";
                statusColor = ConsoleColor.Green;
            }
            else
            {
                return;
            }

            Console.ForegroundColor = statusColor;
            Console.Write(res.Failed ? "✗ " : "✔ ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{res.Name,-30} ");
            Console.ForegroundColor = statusColor;
            Console.WriteLine(status);
            Console.ResetColor();

            if (res.Logs.Count == 0)
            {
                return;
            }

            for (var i = 0; i < res.Logs.Count; i++)
            {
                var log = res.Logs[i];
                var isLast = i == res.Logs.Count - 1;
                var prefix = isLast ? TreeLast : TreeBranch;

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"   {prefix} ");

                if (log.IsError)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                }
                else if (log.IsWarning)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                }

                var lines = log.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (var j = 0; j < lines.Length; j++)
                {
                    if (j > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"   {TreeVert} ");
                        Console.ForegroundColor = log.IsError
                            ? ConsoleColor.Red
                            : log.IsWarning
                                ? ConsoleColor.Yellow
                                : ConsoleColor.Gray;
                    }

                    Console.WriteLine(lines[j]);
                }

                Console.ResetColor();
            }
        }

        private static void WriteSummary(GitPullerRunResult runResult, GitPullerOptions options)
        {
            var results = runResult.RepositoryResults;

            Console.WriteLine("\n\n========================================================");
            Console.WriteLine("                   SUMMARY");
            Console.WriteLine("========================================================");
            Console.WriteLine($"Total Time: {runResult.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"Processed:  {runResult.TotalRepositories}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"New Commits:{runResult.TotalNewCommitsCount}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed:     {runResult.FailCount}");
            Console.ResetColor();
            Console.WriteLine("--------------------------------------------------------");

            if (runResult.FailCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n🔴 Failures:");
                foreach (var result in results.Where(x => x.Failed))
                {
                    Console.WriteLine($"  - {result.Name}");
                    foreach (var log in result.Logs.Where(x => x.IsError))
                    {
                        Console.WriteLine($"    {log.Text.Replace("\n", "\n    ")}");
                    }
                }
                Console.ResetColor();
            }

            if (runResult.TotalNewCommitsCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n🟢 Updates:");
                foreach (var result in results.Where(x => !x.Failed && x.NewCommitsCount > 0))
                {
                    Console.WriteLine($"  - {result.Name} (+{result.NewCommitsCount} new commits)");
                    var shown = 0;
                    foreach (var log in result.Logs.Where(x => x.IsCommit))
                    {
                        Console.WriteLine($"    {log.Text}");
                        shown++;
                        if (shown >= 10)
                        {
                            Console.WriteLine($"    ... and {result.NewCommitsCount - 10} more");
                            break;
                        }
                    }
                }
                Console.ResetColor();
            }

            Console.WriteLine("\n========================================================");

            var report = GitPullerReportWriter.WriteReports(rootDir, runResult, options, RunId);
            Console.WriteLine($"Report written to {Path.GetFullPath(report.RunReportPath)}");
            Console.WriteLine($"Latest report written to {Path.GetFullPath(report.LatestReportPath)}");
        }
    }

    internal sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> handler;

        public InlineProgress(Action<T> handler)
        {
            this.handler = handler;
        }

        public void Report(T value)
        {
            handler(value);
        }
    }

    internal sealed class RootMutexLease : IDisposable
    {
        private readonly Mutex mutex;
        private bool disposed;

        public RootMutexLease(Mutex mutex)
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
}

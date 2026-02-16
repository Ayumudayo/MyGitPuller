using System.Diagnostics;
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
        static bool ForceSync = false;
        static bool CleanUntracked = false;
        static bool ForceRescan = false;
        static bool PullFfOnly = true;
        static bool ShowHelp = false;
        static string RootDir = AppContext.BaseDirectory;
        const string CacheFileName = ".git_repo_cache.json";
        static int GitTimeout = 60000; // Default 60s
        const int DefaultGitTimeoutSeconds = 60;
        const int MinGitTimeoutSeconds = 1;

        // Some environments (CI/redirected output) don't support cursor operations.
        static bool SupportsCursorControl = true;

        // Stats
        static int TotalRepos = 0;
        static int ProcessedCount = 0;
        static int SuccessCount = 0;
        static int FailCount = 0;
        static int GlobalNewCommitsCount = 0;

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

                List<string> repos;
                if (!ForceRescan && TryLoadCache(out repos))
                {
                    Console.WriteLine($"Loaded {repos.Count} repositories from cache.");
                }
                else
                {
                    Console.WriteLine($"Scanning {RootDir} for git repositories...");
                    repos = FindGitRepos(RootDir);
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
                    var res = ProcessRepo(repo);
                    
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
                return 0;
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
                    // Destructive: can discard local branch/worktree state to match remote.
                    ForceSync = true;
                }
                else if (args[i] == "--clean")
                {
                    // Destructive: remove untracked files/dirs (git clean -fdx).
                    CleanUntracked = true;
                }
                else if (args[i] == "--no-pull")
                {
                    PullFfOnly = false;
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
            Console.WriteLine("  --force-sync                Force sync to origin/HEAD (destructive)");
            Console.WriteLine("  --clean                     With --force-sync, remove untracked files (destructive)");
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

                // Verify paths exist
                var valid = cached.Where(p => IsGitRepoRoot(p, out bool isSubmodule) && !isSubmodule).ToList();
                if (valid.Count != cached.Count) return false; // Invalidate if any missing

                repos = valid;
                return true;
            }
            catch
            {
                return false;
            }
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

        static RepoResult ProcessRepo(string repoPath)
        {
            var result = new RepoResult { Path = repoPath, Name = Path.GetFileName(repoPath) };

            if (!IsGitRepoRoot(repoPath, out bool isSubmoduleRepo) || isSubmoduleRepo)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = "Not a supported git repository.", IsError = true });
                return result;
            }

            var beforeRefs = GetRemoteRefs(repoPath);
            
            // Retry logic for fetch
            int retries = 3;
            int rc = -1;
            string outText = "";
            
            while (retries > 0)
            {
                (rc, outText) = RunGitWithSshToHttpsFallback(repoPath, "fetch --all --prune --tags --force");
                if (rc == 0) break;
                
                // If failed, try to prune explicit remote first to clear bad refs
                if (retries < 3) // Don't do it strictly on first attempt if we want, but valid to do it if failed
                {
                     RunGitWithSshToHttpsFallback(repoPath, "remote prune origin");
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

            var afterRefs = GetRemoteRefs(repoPath);
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
                    var (rcLog, logOut) = RunGit(repoPath, $"log -1 --format=\"%h %s (%an)\" {newSha}");
                    if (rcLog == 0 && !string.IsNullOrWhiteSpace(logOut))
                    {
                        ParseAndAddCommits(result, logOut, seenCommits);
                    }
                }
                else if (oldSha != newSha)
                {
                    // Updated branch
                    var (rcLog, logOut) = RunGit(repoPath, $"log --format=\"%h %s (%an)\" {oldSha}..{newSha}");
                    if (rcLog == 0 && !string.IsNullOrWhiteSpace(logOut))
                    {
                        ParseAndAddCommits(result, logOut, seenCommits);
                    }
                }
            }

            // Update the checked-out branch/worktree.
            if (PullFfOnly)
            {
                TrySyncWorkingTree(repoPath, result);
            }

            // Submodules: keep superproject-recorded SHAs in sync.
            // Note: this does *not* treat submodules as separate repos for scanning; it updates them via the parent.
            TryUpdateSubmodules(repoPath, result);

            var (rcMod, outMod) = RunGit(repoPath, "submodule status --recursive");
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

        static void TrySyncWorkingTree(string repoPath, RepoResult result)
        {
            if (ForceSync)
            {
                // Best-effort: reset the default branch (origin/HEAD) to match remote.
                var (rcHead, outHead) = RunGit(repoPath, "symbolic-ref -q --short refs/remotes/origin/HEAD");
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
                    var (rcCleanPre, outCleanPre) = RunGit(repoPath, "clean -fdx");
                    if (rcCleanPre != 0)
                    {
                        result.Logs.Add(new LogItem { Text = $"git clean failed:\n{outCleanPre}", IsWarning = true });
                    }
                }

                // checkout -B works across older git versions
                var (rcCo, outCo) = RunGit(repoPath, $"checkout -f -B {branchName} {remoteRef}");
                if (rcCo != 0)
                {
                    result.Failed = true;
                    result.Logs.Add(new LogItem { Text = $"Force sync checkout failed:\n{outCo}", IsError = true });
                    return;
                }

                var (rcReset, outReset) = RunGit(repoPath, $"reset --hard {remoteRef}");
                if (rcReset != 0)
                {
                    result.Failed = true;
                    result.Logs.Add(new LogItem { Text = $"Force sync reset failed:\n{outReset}", IsError = true });
                    return;
                }

                if (CleanUntracked)
                {
                    var (rcClean, outClean) = RunGit(repoPath, "clean -fdx");
                    if (rcClean != 0)
                    {
                        result.Logs.Add(new LogItem { Text = $"git clean failed:\n{outClean}", IsWarning = true });
                    }
                }

                return;
            }

            // Safe mode: fast-forward only (no merges, no resets).
            var (rcPull, outPull) = RunGitWithSshToHttpsFallback(repoPath, "pull --ff-only --recurse-submodules=no");
            if (rcPull != 0)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = $"Pull (ff-only) failed:\n{outPull}", IsError = true });
            }
        }

        static void TryUpdateSubmodules(string repoPath, RepoResult result)
        {
            if (!File.Exists(Path.Combine(repoPath, ".gitmodules")))
                return;

            // Keep URLs consistent with .gitmodules
            var (rcSync, outSync) = RunGit(repoPath, "submodule sync --recursive");
            if (rcSync != 0)
            {
                result.Logs.Add(new LogItem { Text = $"Submodule sync failed:\n{outSync}", IsWarning = true });
            }

            var args = InitMissingSubmodules
                ? "submodule update --init --recursive"
                : "submodule update --recursive";

            if (ForceSync)
                args += " --force";

            var (rcSub, outSub) = RunGitWithSshToHttpsFallback(repoPath, args);
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
            var (rc, output) = RunGit(repoPath, "submodule status --recursive");
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

                    var (rcFetch, outFetch) = RunGitWithSshToHttpsFallback(subPath, "fetch --all --prune --tags --force");
                    if (rcFetch != 0)
                    {
                        result.Logs.Add(new LogItem { Text = $"Submodule fetch failed ({relPath}):\n{outFetch}", IsWarning = true });
                    }

                    if (ForceSync && CleanUntracked)
                    {
                        var (rcClean, outClean) = RunGit(subPath, "clean -fdx");
                        if (rcClean != 0)
                        {
                            result.Logs.Add(new LogItem { Text = $"Submodule clean failed ({relPath}):\n{outClean}", IsWarning = true });
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

        static Dictionary<string, string> GetRemoteRefs(string repoPath)
        {
            var refs = new Dictionary<string, string>();
            var (rc, output) = RunGit(repoPath, "for-each-ref --format=\"%(refname) %(objectname)\" refs/remotes");
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

        static (int, string) RunGit(string cwd, string args)
        {
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
                    return (-1, "Failed to start git process.");

                using (p)
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    
                    if (!p.WaitForExit(GitTimeout))
                    {
                        try { p.Kill(); } catch { }
                        return (-1, $"Timeout ({GitTimeout/1000}s)");
                    }

                    Task.WaitAll(stdout, stderr);
                    return (p.ExitCode, (stdout.Result + "\n" + stderr.Result).Trim());
                }
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        static (int, string) RunGitWithSshToHttpsFallback(string cwd, string args)
        {
            var (rc, output) = RunGit(cwd, args);
            if (rc == 0)
                return (rc, output);

            if (!LooksLikeSshAuthOrHostKeyFailure(output))
                return (rc, output);

            var hosts = ExtractHostsFromText(output);
            if (hosts.Count == 0)
            {
                var (rcRemotes, outRemotes) = RunGit(cwd, "remote -v");
                if (rcRemotes == 0 && !string.IsNullOrWhiteSpace(outRemotes))
                    hosts = ExtractHostsFromText(outRemotes);
            }

            if (hosts.Count == 0)
                hosts = ExtractHostsFromGitmodules(cwd);

            var rewritePrefix = BuildSshToHttpsRewritePrefix(hosts);
            if (string.IsNullOrWhiteSpace(rewritePrefix))
                return (rc, output);

            var (rc2, output2) = RunGit(cwd, $"{rewritePrefix} {args}");
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
            var sb = new StringBuilder();
            sb.AppendLine("# Git Update Report");
            sb.AppendLine($"Generated: {DateTime.Now}");
            sb.AppendLine();
            
            foreach (var res in results.OrderBy(r => r.Name))
            {
                if (res.NewCommitsCount == 0 && !res.Failed && res.Logs.Count == 0) continue;
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
            File.WriteAllText("git_update_report.md", sb.ToString(), Encoding.UTF8);
            Console.WriteLine($"Report written to {Path.GetFullPath("git_update_report.md")}");
        }
    }

    class RepoResult
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int NewCommitsCount { get; set; }
        public bool Failed { get; set; }
        public List<LogItem> Logs { get; set; } = new List<LogItem>();
    }

    class LogItem
    {
        public string Text { get; set; } = "";
        public bool IsError { get; set; }
        public bool IsWarning { get; set; }
        public bool IsCommit { get; set; } // Replaced IsUpdate with specific IsCommit for styling
    }
}

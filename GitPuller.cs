using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitPuller
{
    class Program
    {
        static readonly object ConsoleLock = new object();
        static int MaxDegreeOfParallelism = 6;
        static bool InitMissingSubmodules = false;
        static bool ForceRescan = false;
        static string RootDir = ".";
        const string CacheFileName = ".git_repo_cache.json";

        // Stats
        static int TotalRepos = 0;
        static int ProcessedCount = 0;
        static int SuccessCount = 0;
        static int FailCount = 0;
        static int UpdateCount = 0;

        // Tree characters
        const string TreeVert = "│ ";
        const string TreeBranch = "├─";
        const string TreeLast = "└─";

        static void Main(string[] args)
        {
            // Force UTF-8
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            Console.CursorVisible = false;

            ParseArgs(args);

            RootDir = Path.GetFullPath(RootDir);
            
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
                return;
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
                    if (res.Updated > 0 || res.Deleted > 0) UpdateCount++;

                    results.Add(res);
                    
                    // Only print to main stream if there's something interesting (Error or Update)
                    if (res.Failed || res.Updated > 0 || res.Deleted > 0)
                    {
                        ClearCurrentLine();
                        PrintResult(res);
                    }
                    
                    DrawProgress();
                }
            });

            sw.Stop();
            ClearCurrentLine(); // Clear final progress bar
            Console.CursorVisible = true;

            WriteSummary(results, sw.Elapsed);
        }

        static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-w" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int w)) MaxDegreeOfParallelism = w;
                    i++;
                }
                else if (args[i] == "--init-missing-submodules")
                {
                    InitMissingSubmodules = true;
                }
                else if (args[i] == "--rescan")
                {
                    ForceRescan = true;
                }
                else if (args[i] == "--root" && i + 1 < args.Length)
                {
                    RootDir = args[i + 1];
                    i++;
                }
            }
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
                var valid = cached.Where(p => Directory.Exists(Path.Combine(p, ".git"))).ToList();
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

            int width = Math.Min(50, Console.WindowWidth - 30);
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
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }

        static List<string> FindGitRepos(string root)
        {
            var gitDirs = Directory.GetDirectories(root, ".git", SearchOption.AllDirectories);
            var repos = new List<string>();
            foreach (var dir in gitDirs) repos.Add(Directory.GetParent(dir).FullName);
            
            repos.Sort();
            var topLevelRepos = new List<string>();
            foreach (var r in repos)
            {
                if (topLevelRepos.Any(parent => r.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    continue;
                topLevelRepos.Add(r);
            }
            return topLevelRepos;
        }

        static RepoResult ProcessRepo(string repoPath)
        {
            var result = new RepoResult { Path = repoPath, Name = Path.GetFileName(repoPath) };

            if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            {
                result.Failed = true;
                result.Logs.Add(new LogItem { Text = "Not a git repository.", IsError = true });
                return result;
            }

            var beforeRefs = GetRemoteRefs(repoPath);
            
            // Retry logic for fetch
            int retries = 2;
            int rc = -1;
            string outText = "";
            
            while (retries > 0)
            {
                (rc, outText) = RunGit(repoPath, "fetch --all --prune --tags");
                if (rc == 0) break;
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

            foreach (var kvp in afterRefs)
            {
                var refName = kvp.Key;
                var newSha = kvp.Value;
                
                if (!beforeRefs.TryGetValue(refName, out var oldSha))
                {
                    result.Updated++;
                    result.Logs.Add(new LogItem { Text = $"{refName} (new: {newSha.Substring(0, 7)})", IsUpdate = true });
                    // Get log for new branch? Maybe just last commit
                    var (rcLog, logOut) = RunGit(repoPath, $"log -1 --format=\"%h %s (%an)\" {newSha}");
                    if (rcLog == 0 && !string.IsNullOrWhiteSpace(logOut))
                        result.Logs.Add(new LogItem { Text = $"  {logOut.Trim()}" });
                }
                else if (oldSha != newSha)
                {
                    result.Updated++;
                    result.Logs.Add(new LogItem { Text = $"{refName} ({oldSha.Substring(0, 7)}..{newSha.Substring(0, 7)})", IsUpdate = true });
                    // Get changelog
                    var (rcLog, logOut) = RunGit(repoPath, $"log --format=\"%h %s (%an)\" {oldSha}..{newSha}");
                    if (rcLog == 0 && !string.IsNullOrWhiteSpace(logOut))
                    {
                        var lines = logOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines.Take(10)) // Limit to 10 lines
                            result.Logs.Add(new LogItem { Text = $"  {line}" });
                        if (lines.Length > 10)
                            result.Logs.Add(new LogItem { Text = $"  ... and {lines.Length - 10} more" });
                    }
                }
            }

            foreach (var kvp in beforeRefs)
            {
                if (!afterRefs.ContainsKey(kvp.Key))
                {
                    result.Deleted++;
                    result.Logs.Add(new LogItem { Text = $"{kvp.Key} (deleted)", IsUpdate = true });
                }
            }

            if (InitMissingSubmodules)
            {
                var (rcSub, outSub) = RunGit(repoPath, "submodule update --init --recursive");
                if (rcSub != 0)
                {
                    result.Logs.Add(new LogItem { Text = $"Submodule init failed:\n{outSub}", IsError = true });
                }
            }

            var (rcMod, outMod) = RunGit(repoPath, "submodule status --recursive");
            if (rcMod == 0)
            {
                using (var reader = new StringReader(outMod))
                {
                    string line;
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

        static Dictionary<string, string> GetRemoteRefs(string repoPath)
        {
            var refs = new Dictionary<string, string>();
            var (rc, output) = RunGit(repoPath, "for-each-ref --format=\"%(refname) %(objectname)\" refs/remotes");
            if (rc == 0)
            {
                using (var reader = new StringReader(output))
                {
                    string line;
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

                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    
                    if (!p.WaitForExit(10000))
                    {
                        try { p.Kill(); } catch { }
                        return (-1, "Timeout");
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

        static void PrintResult(RepoResult res)
        {
            string status;
            ConsoleColor statusColor;

            if (res.Failed)
            {
                status = "[FAILED]";
                statusColor = ConsoleColor.Red;
            }
            else if (res.Updated > 0 || res.Deleted > 0)
            {
                status = $"[UPDATED +{res.Updated} -{res.Deleted}]";
                statusColor = ConsoleColor.Green;
            }
            else
            {
                return; // Don't print OK repos to console to reduce noise
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
                    else if (log.IsUpdate) Console.ForegroundColor = ConsoleColor.Cyan;
                    else Console.ForegroundColor = ConsoleColor.Gray;

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
            Console.WriteLine($"Updated:    {UpdateCount}");
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
            if (UpdateCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n🟢 Updates:");
                foreach (var r in results.Where(x => !x.Failed && (x.Updated > 0 || x.Deleted > 0)))
                {
                    Console.WriteLine($"  - {r.Name} (+{r.Updated}/-{r.Deleted})");
                    foreach(var l in r.Logs.Where(x => x.IsUpdate))
                        Console.WriteLine($"    {l.Text}");
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
                if (res.Updated == 0 && res.Deleted == 0 && !res.Failed && res.Logs.Count == 0) continue;
                var icon = res.Failed ? "❌" : "✅";
                sb.AppendLine($"## {icon} {res.Name}");
                if (res.Failed) sb.AppendLine("**FAILED**");
                if (res.Updated > 0) sb.AppendLine($"- Updated: {res.Updated}");
                if (res.Deleted > 0) sb.AppendLine($"- Deleted: {res.Deleted}");
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
        public string Path { get; set; }
        public string Name { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public bool Failed { get; set; }
        public List<LogItem> Logs { get; set; } = new List<LogItem>();
    }

    class LogItem
    {
        public string Text { get; set; }
        public bool IsError { get; set; }
        public bool IsWarning { get; set; }
        public bool IsUpdate { get; set; }
    }
}

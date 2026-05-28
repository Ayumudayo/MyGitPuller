using System.Diagnostics;
using System.Text;

namespace GitPuller;

public sealed class GitRepositoryScanner
{
    public RepositoryInventory ScanLibraryRoot(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            throw new ArgumentException("Library root is required.", nameof(libraryRoot));
        }

        var normalizedRoot = Path.GetFullPath(libraryRoot);
        var repositories = GitRepositorySupport.FindGitRepos(normalizedRoot)
            .Select(repoPath => GitRepositorySupport.CreateRepositoryDescriptor(
                normalizedRoot,
                repoPath,
                TryGetOriginRemoteUrl(repoPath)))
            .ToList();

        return new RepositoryInventory(normalizedRoot, repositories);
    }

    private static string? TryGetOriginRemoteUrl(string repoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
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

            psi.ArgumentList.Add("remote");
            psi.ArgumentList.Add("get-url");
            psi.ArgumentList.Add("origin");
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";

            using var process = Process.Start(psi);
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
}

internal static class GitRepositorySupport
{
    public static List<string> FindGitRepos(string root)
    {
        var repositories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (IsIgnoredDirName(name))
            {
                continue;
            }

            if (IsGitRepoRoot(directory, out var isSubmoduleRepo) && !isSubmoduleRepo)
            {
                repositories.Add(directory);
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var childName = Path.GetFileName(child);
                    if (childName.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsIgnoredDirName(childName) || IsReparsePoint(child))
                    {
                        continue;
                    }

                    pending.Push(child);
                }
            }
            catch
            {
            }
        }

        repositories.Sort(StringComparer.OrdinalIgnoreCase);
        return repositories;
    }

    public static List<string> NormalizeRepoList(IEnumerable<string> repoPaths)
    {
        var repositories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repoPath in repoPaths)
        {
            if (string.IsNullOrWhiteSpace(repoPath))
            {
                continue;
            }

            var normalized = NormalizeRepoPath(repoPath);
            if (seen.Add(normalized))
            {
                repositories.Add(normalized);
            }
        }

        repositories.Sort(StringComparer.OrdinalIgnoreCase);
        return repositories;
    }

    public static RepositoryDescriptor CreateRepositoryDescriptor(string libraryRoot, string repoPath, string? remoteUrl)
    {
        var normalizedRoot = Path.GetFullPath(libraryRoot);
        var normalizedRepoPath = NormalizeRepoPath(repoPath);
        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedRepoPath);
        var category = Path.GetDirectoryName(relativePath) ?? string.Empty;
        if (string.Equals(category, ".", StringComparison.Ordinal))
        {
            category = string.Empty;
        }

        category = category.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        return new RepositoryDescriptor(
            normalizedRepoPath,
            Path.GetFileName(normalizedRepoPath),
            category,
            remoteUrl);
    }

    public static string NormalizeRepoPath(string repoPath)
    {
        try
        {
            return Path.GetFullPath(repoPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return repoPath.Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static bool IsGitRepoRoot(string path, out bool isSubmoduleWorkingTree)
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

    public static string ResolveGitDirPath(string repoPath)
    {
        var gitPath = Path.Combine(repoPath, ".git");
        if (Directory.Exists(gitPath))
        {
            return Path.GetFullPath(gitPath);
        }

        if (!File.Exists(gitPath))
        {
            return Path.GetFullPath(repoPath);
        }

        var text = File.ReadAllText(gitPath, Encoding.UTF8);
        var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        const string prefix = "gitdir:";
        if (firstLine == null || !firstLine.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(repoPath);
        }

        var gitdir = firstLine[(firstLine.IndexOf(':') + 1)..].Trim();
        return Path.IsPathRooted(gitdir)
            ? Path.GetFullPath(gitdir)
            : Path.GetFullPath(Path.Combine(repoPath, gitdir));
    }

    public static string GetRepoMutexIdentityPath(string repoPath)
    {
        var resolvedGitDir = ResolveGitDirPath(repoPath);
        var normalized = resolvedGitDir.Replace('/', Path.DirectorySeparatorChar);
        var worktreesMarker = string.Join(Path.DirectorySeparatorChar.ToString(), new[] { ".git", "worktrees" });
        var worktreesIndex = normalized.IndexOf(worktreesMarker, StringComparison.OrdinalIgnoreCase);
        if (worktreesIndex >= 0)
        {
            return normalized[..(worktreesIndex + ".git".Length)];
        }

        return normalized;
    }

    private static bool IsReparsePoint(string path)
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

    private static bool IsIgnoredDirName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
    }
}

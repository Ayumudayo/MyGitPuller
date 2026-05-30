using System.Diagnostics;
using System.Text;

namespace GitPuller;

public sealed class GitRepositoryScanner
{
    public RepositoryInventory ScanLibraryRoot(string libraryRoot)
    {
        return ScanLibraryRoot(libraryRoot, CancellationToken.None);
    }

    public RepositoryInventory ScanLibraryRoot(string libraryRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            throw new ArgumentException("Library root is required.", nameof(libraryRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = Path.GetFullPath(libraryRoot);
        var repoPaths = GitRepositorySupport.FindGitRepos(normalizedRoot, cancellationToken);
        var repositories = new List<RepositoryDescriptor>(repoPaths.Count);
        foreach (var repoPath in repoPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            repositories.Add(GitRepositorySupport.CreateRepositoryDescriptor(
                normalizedRoot,
                repoPath,
                TryGetOriginRemoteUrl(repoPath, cancellationToken)));
        }

        return new RepositoryInventory(normalizedRoot, repositories);
    }

    private static string? TryGetOriginRemoteUrl(string repoPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TryReadOriginRemoteUrlFromGitConfig(repoPath)
            ?? TryGetOriginRemoteUrlFromGit(repoPath, cancellationToken);
    }

    internal static string? TryReadOriginRemoteUrlFromGitConfig(string repoPath)
    {
        foreach (var configPath in GetCandidateGitConfigPaths(repoPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }

                var remoteUrl = TryParseOriginRemoteUrl(File.ReadAllText(configPath, Encoding.UTF8));
                if (!string.IsNullOrWhiteSpace(remoteUrl))
                {
                    return remoteUrl;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    internal static string? TryParseOriginRemoteUrl(string gitConfigText)
    {
        if (string.IsNullOrWhiteSpace(gitConfigText))
        {
            return null;
        }

        var inOriginRemoteSection = false;
        string? originRemoteUrl = null;
        foreach (var line in EnumerateLogicalLines(gitConfigText))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            var sectionLine = StripInlineComment(trimmed).Trim();
            if (sectionLine.StartsWith('['))
            {
                if (!sectionLine.EndsWith(']'))
                {
                    return null;
                }

                inOriginRemoteSection = IsOriginRemoteSection(sectionLine[1..^1]);
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            if (HasMalformedQuotedValue(trimmed[(separatorIndex + 1)..]))
            {
                return null;
            }

            if (!inOriginRemoteSection)
            {
                continue;
            }

            var key = trimmed[..separatorIndex].Trim();
            if (!key.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = NormalizeConfigValue(trimmed[(separatorIndex + 1)..]);
            if (originRemoteUrl is null)
            {
                originRemoteUrl = value;
            }
        }

        return originRemoteUrl;
    }

    private static IEnumerable<string> EnumerateLogicalLines(string gitConfigText)
    {
        using var reader = new StringReader(gitConfigText);
        var builder = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.EndsWith('\\'))
            {
                builder.Append(line[..^1]);
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(line);
                yield return builder.ToString();
                builder.Clear();
                continue;
            }

            yield return line;
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static bool HasMalformedQuotedValue(string value)
    {
        var uncommented = StripInlineComment(value).Trim();
        return uncommented.StartsWith('"') && NormalizeConfigValue(value) is null;
    }

    private static string? NormalizeConfigValue(string value)
    {
        var uncommented = StripInlineComment(value).Trim();
        if (uncommented.Length == 0)
        {
            return null;
        }

        if (uncommented.StartsWith('"'))
        {
            if (uncommented.Length < 2 || !uncommented.EndsWith('"'))
            {
                return null;
            }

            uncommented = UnescapeQuotedConfigValue(uncommented[1..^1]);
        }

        return string.IsNullOrWhiteSpace(uncommented) ? null : uncommented;
    }

    private static string StripInlineComment(string value)
    {
        var inQuotes = false;
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inQuotes && current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes
                && (current == '#' || current == ';'))
            {
                return value[..i];
            }
        }

        return value;
    }

    private static string? UnescapeQuotedConfigValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current != '\\' || i == value.Length - 1)
            {
                builder.Append(current);
                continue;
            }

            var next = value[++i];
            var unescaped = next switch
            {
                'n' => '\n',
                't' => '\t',
                'b' => '\b',
                '"' => '"',
                '\\' => '\\',
                _ => (char?)null
            };
            if (unescaped is null)
            {
                return null;
            }

            builder.Append(unescaped.Value);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> GetCandidateGitConfigPaths(string repoPath)
    {
        string resolvedGitDir;
        try
        {
            resolvedGitDir = GitRepositorySupport.ResolveGitDirPath(repoPath);
        }
        catch
        {
            yield break;
        }

        yield return Path.Combine(resolvedGitDir, "config");

        string identityGitDir;
        try
        {
            identityGitDir = GitRepositorySupport.GetRepoMutexIdentityPath(repoPath);
        }
        catch
        {
            yield break;
        }

        yield return Path.Combine(identityGitDir, "config");
    }

    private static bool IsOriginRemoteSection(string section)
    {
        var trimmed = section.Trim();
        const string remotePrefix = "remote";
        if (!trimmed.StartsWith(remotePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.StartsWith("remote.", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed["remote.".Length..].Equals("origin", StringComparison.OrdinalIgnoreCase);
        }

        var remainder = trimmed[remotePrefix.Length..].Trim();
        return remainder.Equals("\"origin\"", StringComparison.Ordinal);
    }

    private static string? TryGetOriginRemoteUrlFromGit(string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            var stopwatch = Stopwatch.StartNew();
            while (!process.WaitForExit(100))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    KillProcessTree(process);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (stopwatch.ElapsedMilliseconds < 10000)
                {
                    continue;
                }

                KillProcessTree(process);
                return null;
            }

            try
            {
                Task.WaitAll([stdout, stderr], cancellationToken);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var remoteUrl = stdout.Result.Trim();
            return string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static void KillProcessTree(Process process)
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
    }
}

internal static class GitRepositorySupport
{
    public static List<string> FindGitRepos(string root)
    {
        return FindGitRepos(root, CancellationToken.None);
    }

    public static List<string> FindGitRepos(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedRoot = NormalizeRepoPath(root);
        var repositories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(normalizedRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (IsIgnoredDirName(name))
            {
                continue;
            }

            if (IsGitRepoRoot(directory, out var isSubmoduleRepo) && !isSubmoduleRepo)
            {
                if (!string.Equals(directory, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    repositories.Add(directory);
                    continue;
                }
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException)
            {
                throw;
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
            || name.Equals(".mygitpuller", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
    }
}

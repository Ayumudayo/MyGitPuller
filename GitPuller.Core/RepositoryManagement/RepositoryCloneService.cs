using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GitPuller;

public sealed class RepositoryCloneService
{
    private static readonly HashSet<string> ReservedTargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".mygitpuller",
        ".vs",
        "bin",
        "obj",
        "node_modules"
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public RepositoryAddPreview Preview(RepositoryAddRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var libraryRoot = NormalizeRoot(request.LibraryRoot);
        var remoteUrl = request.RemoteUrl?.Trim() ?? string.Empty;
        var categoryText = request.Category?.Trim() ?? string.Empty;

        if (!TryDeriveRepositoryName(remoteUrl, out var repositoryName, out var repositoryNameFailure))
        {
            return CreateInvalidPreview(
                libraryRoot,
                categoryText,
                remoteUrl,
                repositoryName: string.Empty,
                targetPath: string.Empty,
                repositoryNameFailure
                ?? CreateInvalidRequestDiagnostic(
                    title: "Clone URL is invalid",
                    explanation: "The clone source must be a valid Git URL, SSH remote, or local repository path.",
                    evidence: string.IsNullOrWhiteSpace(remoteUrl) ? "Clone source was empty." : remoteUrl,
                    relatedPath: null));
        }

        if (!TryNormalizeCategorySegments(categoryText, out var categorySegments, out var normalizedCategory, out var categoryFailure))
        {
            return CreateInvalidPreview(
                libraryRoot,
                categoryText,
                remoteUrl,
                repositoryName,
                targetPath: string.Empty,
                categoryFailure!);
        }

        if (!TryBuildTargetPath(libraryRoot, categorySegments, repositoryName, out var targetPath, out var pathFailure))
        {
            return CreateInvalidPreview(
                libraryRoot,
                normalizedCategory,
                remoteUrl,
                repositoryName,
                targetPath: string.Empty,
                pathFailure!);
        }

        var conflictDiagnostic = DetectClonePathConflict(targetPath);
        if (conflictDiagnostic != null)
        {
            return CreateInvalidPreview(
                libraryRoot,
                normalizedCategory,
                remoteUrl,
                repositoryName,
                targetPath,
                conflictDiagnostic);
        }

        var repository = new RepositoryDescriptor(targetPath, repositoryName, normalizedCategory, remoteUrl);
        return new RepositoryAddPreview(
            libraryRoot,
            normalizedCategory,
            remoteUrl,
            repositoryName,
            targetPath,
            repository,
            Diagnostic: null);
    }

    public RepositoryAddResult Clone(RepositoryAddRequest request)
    {
        return Clone(request, new GitPullerOptions(), CancellationToken.None);
    }

    public RepositoryAddResult Clone(RepositoryAddRequest request, GitPullerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var preview = Preview(request);
        if (!preview.IsValid || preview.Repository is null)
        {
            return new RepositoryAddResult(preview, Repository: null, preview.Diagnostic, GitResult: null);
        }

        var gitResult = RunClone(preview, options, cancellationToken);
        if (gitResult.Failed)
        {
            var diagnostic = gitResult.Diagnostic ?? GitFailureClassifier.Classify(gitResult);
            return new RepositoryAddResult(preview, Repository: null, diagnostic, gitResult);
        }

        return new RepositoryAddResult(preview, preview.Repository, Diagnostic: null, gitResult);
    }

    private static RepoResult RunClone(RepositoryAddPreview preview, GitPullerOptions options, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var result = new RepoResult
        {
            Path = preview.TargetPath,
            Name = preview.RepositoryName,
            StartedAt = startedAt,
            WorkerSlot = 1
        };

        var parentDirectory = Path.GetDirectoryName(preview.TargetPath)
            ?? throw new InvalidOperationException($"Clone target path does not have a parent directory: {preview.TargetPath}");

        Directory.CreateDirectory(parentDirectory);

        var operation = new RepoOperation
        {
            Command = $"git clone {QuoteArgument(preview.RemoteUrl)} {QuoteArgument(preview.TargetPath)}",
            WorkingDirectory = parentDirectory,
            StartedAt = startedAt
        };
        result.Operations.Add(operation);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = parentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        processStartInfo.ArgumentList.Add("clone");
        processStartInfo.ArgumentList.Add(preview.RemoteUrl);
        processStartInfo.ArgumentList.Add(preview.TargetPath);
        processStartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        processStartInfo.Environment["GCM_INTERACTIVE"] = "never";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                result.Failed = true;
                result.Logs.Add(new LogItem
                {
                    Text = $"Failed to start git process. Command: {operation.Command}",
                    IsError = true
                });
                result.Diagnostic = CreateUnknownGitFailure(result, "Git clone process could not be started.", operation.Command);
                return FinalizeResult(result, operation, exitCode: -1);
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            var timeoutMilliseconds = Math.Max(1, options.GitTimeoutMilliseconds);
            var timeoutStopwatch = Stopwatch.StartNew();
            using var cancellationRegistration = cancellationToken.Register(static state =>
            {
                if (state is Process processToCancel)
                {
                    TryKillProcess(processToCancel);
                }
            }, process);

            while (!process.WaitForExit(Math.Min(100, timeoutMilliseconds)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timeoutStopwatch.ElapsedMilliseconds < timeoutMilliseconds)
                {
                    continue;
                }

                TryKillProcess(process);

                Task.WaitAll(standardOutput, standardError);
                result.Failed = true;
                result.Logs.Add(new LogItem
                {
                    Text = $"Timeout ({timeoutMilliseconds} ms){Environment.NewLine}Command: {operation.Command}",
                    IsError = true
                });
                var timedOutResult = FinalizeResult(result, operation, exitCode: -1, timedOut: true);
                timedOutResult.Diagnostic = GitFailureClassifier.Classify(timedOutResult);
                return timedOutResult;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Task.WaitAll(standardOutput, standardError);
            var stdout = standardOutput.Result.Trim();
            var stderr = standardError.Result.Trim();

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                result.Logs.Add(new LogItem { Text = stdout });
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                result.Logs.Add(new LogItem
                {
                    Text = stderr,
                    IsError = process.ExitCode != 0
                });
            }

            result.Failed = process.ExitCode != 0;
            if (result.Failed)
            {
                result.Diagnostic = GitFailureClassifier.Classify(result);
            }

            return FinalizeResult(result, operation, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Failed = true;
            result.Logs.Add(new LogItem
            {
                Text = $"git clone failed before completion: {ex.Message}",
                IsError = true
            });
            result.Diagnostic = CreateUnknownGitFailure(result, ex.Message, operation.Command);
            return FinalizeResult(result, operation, exitCode: -1);
        }
    }

    private static RepoResult FinalizeResult(RepoResult result, RepoOperation operation, int exitCode, bool timedOut = false)
    {
        var completedAt = DateTimeOffset.UtcNow;
        result.CompletedAt = completedAt;
        result.Elapsed = completedAt - result.StartedAt;
        operation.ExitCode = exitCode;
        operation.TimedOut = timedOut;
        operation.Elapsed = completedAt - operation.StartedAt;
        return result;
    }

    private static FailureDiagnostic? DetectClonePathConflict(string targetPath)
    {
        if (File.Exists(targetPath))
        {
            return CreateClonePathConflict(targetPath, $"Destination path already exists as a file: {targetPath}");
        }

        if (!Directory.Exists(targetPath))
        {
            return null;
        }

        return Directory.EnumerateFileSystemEntries(targetPath).Any()
            ? CreateClonePathConflict(targetPath, $"Destination path already exists and is not an empty directory: {targetPath}")
            : null;
    }

    private static bool TryBuildTargetPath(
        string libraryRoot,
        IReadOnlyList<string> categorySegments,
        string repositoryName,
        out string targetPath,
        out FailureDiagnostic? diagnostic)
    {
        var pathSegments = new string[categorySegments.Count + 2];
        pathSegments[0] = libraryRoot;
        for (var index = 0; index < categorySegments.Count; index++)
        {
            pathSegments[index + 1] = categorySegments[index];
        }

        pathSegments[^1] = repositoryName;
        targetPath = GitRepositorySupport.NormalizeRepoPath(Path.Combine(pathSegments));

        if (!IsPathUnderRoot(targetPath, libraryRoot))
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Clone target escapes the library root",
                explanation: "The requested category or repository name resolves outside the configured library root.",
                evidence: targetPath,
                relatedPath: targetPath);
            return false;
        }

        var configRoot = GitRepositorySupport.NormalizeRepoPath(Path.Combine(libraryRoot, ".mygitpuller"));
        if (IsPathUnderRoot(targetPath, configRoot) || string.Equals(targetPath, configRoot, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Clone target points into .mygitpuller",
                explanation: "Repository categories and folder names cannot place repositories inside the reserved .mygitpuller area.",
                evidence: targetPath,
                relatedPath: targetPath);
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static bool TryNormalizeCategorySegments(
        string category,
        out string[] categorySegments,
        out string normalizedCategory,
        out FailureDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            categorySegments = [];
            normalizedCategory = string.Empty;
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category is required",
                explanation: "The clone workflow requires an explicit category and does not infer one from the repository URL.",
                evidence: "Category was empty.",
                relatedPath: null);
            return false;
        }

        categorySegments = category
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        if (categorySegments.Length == 0)
        {
            normalizedCategory = string.Empty;
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category is required",
                explanation: "The clone workflow requires an explicit category and does not infer one from the repository URL.",
                evidence: "Category was empty after trimming.",
                relatedPath: null);
            return false;
        }

        for (var index = 0; index < categorySegments.Length; index++)
        {
            if (!TryValidatePathSegment(categorySegments[index], out diagnostic))
            {
                normalizedCategory = string.Join('/', categorySegments);
                return false;
            }
        }

        normalizedCategory = string.Join('/', categorySegments);
        diagnostic = null;
        return true;
    }

    private static bool TryDeriveRepositoryName(string remoteUrl, out string repositoryName, out FailureDiagnostic? diagnostic)
    {
        repositoryName = string.Empty;
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return false;
        }

        if (TryDeriveRepositoryNameFromUri(remoteUrl, out repositoryName))
        {
            return TryValidatePathSegment(repositoryName, out diagnostic);
        }

        if (TryDeriveRepositoryNameFromScpLikeRemote(remoteUrl, out repositoryName))
        {
            return TryValidatePathSegment(repositoryName, out diagnostic);
        }

        if (IsUnsupportedUriLikeRemote(remoteUrl))
        {
            repositoryName = string.Empty;
            return false;
        }

        if (TryDeriveRepositoryNameFromLocalPath(remoteUrl, out repositoryName))
        {
            return TryValidatePathSegment(repositoryName, out diagnostic);
        }

        repositoryName = string.Empty;
        return false;
    }

    private static bool TryDeriveRepositoryNameFromUri(string remoteUrl, out string repositoryName)
    {
        repositoryName = string.Empty;
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.IsFile
            && !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeSsh, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        repositoryName = ExtractRepositoryName(uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath));
        return !string.IsNullOrWhiteSpace(repositoryName);
    }

    private static bool TryDeriveRepositoryNameFromScpLikeRemote(string remoteUrl, out string repositoryName)
    {
        repositoryName = string.Empty;
        var match = Regex.Match(remoteUrl, @"^(?<host>[^\s/:]+):(?<path>[^\\].+)$", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var host = match.Groups["host"].Value;
        if (!IsSupportedScpLikeHost(host))
        {
            return false;
        }

        var path = match.Groups["path"].Value;
        if (path.IndexOf('/') < 0)
        {
            return false;
        }

        repositoryName = ExtractRepositoryName(path);
        return !string.IsNullOrWhiteSpace(repositoryName);
    }

    private static bool TryDeriveRepositoryNameFromLocalPath(string remoteUrl, out string repositoryName)
    {
        repositoryName = string.Empty;
        if (!Path.IsPathRooted(remoteUrl)
            && remoteUrl.IndexOf(Path.DirectorySeparatorChar) < 0
            && remoteUrl.IndexOf(Path.AltDirectorySeparatorChar) < 0)
        {
            return false;
        }

        repositoryName = ExtractRepositoryName(remoteUrl);
        return !string.IsNullOrWhiteSpace(repositoryName);
    }

    private static string ExtractRepositoryName(string pathOrUrl)
    {
        var trimmed = pathOrUrl.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(trimmed);
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name;
    }

    private static bool TryValidatePathSegment(string value, out FailureDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category or repository name is empty",
                explanation: "Category path segments and repository folder names must not be empty.",
                evidence: "Encountered an empty category or repository segment.",
                relatedPath: null);
            return false;
        }

        if (string.Equals(value, ".", StringComparison.Ordinal) || string.Equals(value, "..", StringComparison.Ordinal))
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category escapes the library root",
                explanation: "Category and folder names cannot traverse outside the configured library root.",
                evidence: value,
                relatedPath: null);
            return false;
        }

        if (value.EndsWith(' ') || value.EndsWith('.'))
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category or repository name has trailing characters",
                explanation: "Category and repository folder names cannot end with a trailing period or space because Windows normalizes them to a different path.",
                evidence: value,
                relatedPath: null);
            return false;
        }

        var normalizedValue = NormalizeTargetSegment(value);
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category or repository name normalizes to an empty segment",
                explanation: "Category and repository folder names must remain non-empty after Windows path normalization.",
                evidence: value,
                relatedPath: null);
            return false;
        }

        if (ReservedTargetNames.Contains(normalizedValue))
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category or repository name is reserved",
                explanation: "Repository categories and folder names cannot normalize into reserved directories such as .mygitpuller, .git, .vs, bin, obj, or node_modules.",
                evidence: normalizedValue,
                relatedPath: null);
            return false;
        }

        if (IsReservedDeviceName(normalizedValue))
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category or repository name is reserved",
                explanation: "Category and repository folder names cannot use reserved DOS device names such as CON, PRN, AUX, NUL, COM1-COM9, or LPT1-LPT9.",
                evidence: normalizedValue,
                relatedPath: null);
            return false;
        }

        if (normalizedValue.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            diagnostic = CreateInvalidRequestDiagnostic(
                title: "Category or repository name is invalid",
                explanation: "Category and repository folder names must be valid filesystem path segments.",
                evidence: normalizedValue,
                relatedPath: null);
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static bool IsUnsupportedUriLikeRemote(string remoteUrl)
    {
        if (LooksLikeUnsupportedAbsoluteUri(remoteUrl))
        {
            return true;
        }

        return remoteUrl.Contains(':', StringComparison.Ordinal)
            && !Path.IsPathRooted(remoteUrl);
    }

    private static bool LooksLikeUnsupportedAbsoluteUri(string remoteUrl)
    {
        return remoteUrl.Contains("://", StringComparison.Ordinal);
    }

    private static string NormalizeTargetSegment(string value)
    {
        return value.TrimEnd(' ', '.');
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = GitRepositorySupport.NormalizeRepoPath(path);
        var normalizedRoot = GitRepositorySupport.NormalizeRepoPath(root);
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            throw new ArgumentException("Library root is required.", nameof(libraryRoot));
        }

        return Path.GetFullPath(libraryRoot);
    }

    private static RepositoryAddPreview CreateInvalidPreview(
        string libraryRoot,
        string category,
        string remoteUrl,
        string repositoryName,
        string targetPath,
        FailureDiagnostic diagnostic)
    {
        return new RepositoryAddPreview(
            libraryRoot,
            category,
            remoteUrl,
            repositoryName,
            targetPath,
            Repository: null,
            diagnostic);
    }

    private static RepositoryAddPreview CreateInvalidPreview(
        string libraryRoot,
        string category,
        string remoteUrl,
        string repositoryName,
        string targetPath,
        string title,
        string explanation,
        string evidence)
    {
        return CreateInvalidPreview(
            libraryRoot,
            category,
            remoteUrl,
            repositoryName,
            targetPath,
            CreateInvalidRequestDiagnostic(title, explanation, evidence, relatedPath: null));
    }

    private static FailureDiagnostic CreateInvalidRequestDiagnostic(string title, string explanation, string evidence, string? relatedPath)
    {
        return new FailureDiagnostic(
            FailureCategory.InvalidCloneRequest,
            RetryPolicy.BlockedUntilAction,
            DiagnosticSeverity.Error,
            title,
            explanation,
            "Fix the clone input and try again.",
            evidence,
            relatedPath,
            RelatedCommand: null);
    }

    private static FailureDiagnostic CreateClonePathConflict(string targetPath, string evidence)
    {
        return new FailureDiagnostic(
            FailureCategory.ClonePathConflict,
            RetryPolicy.BlockedUntilAction,
            DiagnosticSeverity.Error,
            "Clone destination path is blocked",
            "The repository path already exists or contains files that block clone.",
            "Choose a different category or clear the existing folder before retrying.",
            evidence,
            targetPath,
            RelatedCommand: null);
    }

    private static FailureDiagnostic CreateUnknownGitFailure(RepoResult result, string evidence, string relatedCommand)
    {
        return new FailureDiagnostic(
            FailureCategory.UnknownGitFailure,
            RetryPolicy.Unknown,
            DiagnosticSeverity.Error,
            "Git clone failed",
            "The clone command failed before a more specific failure category could be identified.",
            "Inspect the Git output and retry after correcting the failure.",
            evidence,
            string.IsNullOrWhiteSpace(result.Path) ? null : result.Path,
            relatedCommand);
    }

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static void TryKillProcess(Process process)
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

    private static bool IsReservedDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var deviceCandidate = value.Split('.', 2)[0];
        return ReservedDeviceNames.Contains(deviceCandidate);
    }

    private static bool IsSupportedScpLikeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Contains('@', StringComparison.Ordinal))
        {
            return true;
        }

        return host.Contains('-', StringComparison.Ordinal)
            || host.Contains('.', StringComparison.Ordinal)
            || host.Contains('_', StringComparison.Ordinal);
    }
}

public sealed record RepositoryAddPreview(
    string LibraryRoot,
    string Category,
    string RemoteUrl,
    string RepositoryName,
    string TargetPath,
    RepositoryDescriptor? Repository,
    FailureDiagnostic? Diagnostic)
{
    public bool IsValid => Diagnostic is null && Repository is not null;
}

public sealed record RepositoryAddResult(
    RepositoryAddPreview Preview,
    RepositoryDescriptor? Repository,
    FailureDiagnostic? Diagnostic,
    RepoResult? GitResult)
{
    public bool Succeeded => Diagnostic is null && Repository is not null;
    public bool GitCommandExecuted => GitResult is not null;
}

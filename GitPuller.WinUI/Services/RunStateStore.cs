using System.Text.Json;
using System.Text.Json.Serialization;
using GitPuller;
using GitPuller_WinUI.ViewModels;

namespace GitPuller_WinUI.Services;

public interface IRunStateStore
{
    PersistedRunState? Load(string libraryRoot);

    void Save(PersistedRunState state);
}

public enum PersistedRunStatus
{
    Running,
    Completed,
    Failed,
    Canceled,
    Interrupted
}

public sealed class PersistedRunState
{
    public string LibraryRoot { get; set; } = string.Empty;
    public PersistedRunStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int CompletedRepositories { get; set; }
    public int TotalRepositories { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? WarningMessage { get; set; }
    public string? LatestReportPath { get; set; }
    public List<PersistedRepositoryResult> RepositoryResults { get; set; } = [];
}

public sealed class PersistedRepositoryResult
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public RepositoryResultStatus Status { get; set; }
    public int NewCommitsCount { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public FailureDiagnostic? Diagnostic { get; set; }
    public List<string> LogLines { get; set; } = [];

    public static PersistedRepositoryResult FromViewModel(RepositoryResultViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new PersistedRepositoryResult
        {
            Name = result.Name,
            Category = result.Category,
            Path = result.Path,
            RemoteUrl = result.RemoteUrl,
            Status = result.Status,
            NewCommitsCount = result.NewCommitsCount,
            ElapsedMilliseconds = Math.Max(0, (long)Math.Round(result.Elapsed.TotalMilliseconds)),
            CompletedAt = result.CompletedAt,
            Diagnostic = result.Diagnostic,
            LogLines = result.LogLines.ToList()
        };
    }

    public RepositoryResultViewModel ToViewModel()
    {
        return new RepositoryResultViewModel(
            Name,
            Category,
            Path,
            RemoteUrl,
            Status,
            NewCommitsCount,
            TimeSpan.FromMilliseconds(Math.Max(0, ElapsedMilliseconds)),
            Diagnostic,
            LogLines,
            CompletedAt);
    }
}

public sealed class JsonRunStateStore : IRunStateStore
{
    private const string RunStateFileName = "run-state.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public PersistedRunState? Load(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return null;
        }

        try
        {
            var path = GetRunStatePath(libraryRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<PersistedRunState>(
                File.ReadAllText(path),
                SerializerOptions);
            if (state is null || string.IsNullOrWhiteSpace(state.LibraryRoot))
            {
                return null;
            }

            state.RepositoryResults = state.RepositoryResults?.OfType<PersistedRepositoryResult>().ToList() ?? [];
            foreach (var result in state.RepositoryResults)
            {
                result.Name ??= string.Empty;
                result.Category ??= string.Empty;
                result.Path ??= string.Empty;
                result.RemoteUrl ??= string.Empty;
                result.LogLines ??= [];
            }

            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public void Save(PersistedRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.LibraryRoot))
        {
            return;
        }

        try
        {
            var path = GetRunStatePath(state.LibraryRoot);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $"{Path.GetRandomFileName()}.tmp");
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, SerializerOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Run-state persistence is best effort; sync itself should not fail because this cache cannot be written.
        }
    }

    public static string GetRunStatePath(string libraryRoot)
    {
        return Path.Combine(Path.GetFullPath(libraryRoot), ".mygitpuller", RunStateFileName);
    }
}

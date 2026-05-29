using System.Text.Json;

namespace GitPuller_WinUI.Services;

public interface IAppSettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public sealed record AppSettings(
    string? SelectedLibraryRoot,
    IReadOnlyList<string> RecentLibraryRoots)
{
    public static AppSettings Empty { get; } = new(null, []);
}

public sealed class JsonAppSettingsService : IAppSettingsService
{
    private const int MaxRecentLibraryRoots = 12;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string settingsPath;

    public JsonAppSettingsService(string? settingsPath = null)
    {
        this.settingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? GetDefaultSettingsPath()
            : Path.GetFullPath(settingsPath);
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            return AppSettings.Empty;
        }

        try
        {
            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            var persisted = JsonSerializer.Deserialize<PersistedAppSettings>(json, SerializerOptions);
            return Normalize(new AppSettings(
                persisted?.SelectedLibraryRoot,
                persisted?.RecentLibraryRoots ?? []));
        }
        catch (JsonException)
        {
            return AppSettings.Empty;
        }
        catch (IOException)
        {
            return AppSettings.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return AppSettings.Empty;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(
            new PersistedAppSettings
            {
                SelectedLibraryRoot = normalized.SelectedLibraryRoot,
                RecentLibraryRoots = normalized.RecentLibraryRoots.ToList()
            },
            SerializerOptions);
        var tempPath = $"{settingsPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, settingsPath, overwrite: true);
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        var selectedRoot = NormalizePath(settings.SelectedLibraryRoot);
        var recentRoots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateCandidateRoots(selectedRoot, settings.RecentLibraryRoots))
        {
            if (seen.Add(root))
            {
                recentRoots.Add(root);
            }

            if (recentRoots.Count >= MaxRecentLibraryRoots)
            {
                break;
            }
        }

        return new AppSettings(selectedRoot, recentRoots);
    }

    private static IEnumerable<string> EnumerateCandidateRoots(
        string? selectedRoot,
        IEnumerable<string>? recentRoots)
    {
        if (!string.IsNullOrWhiteSpace(selectedRoot))
        {
            yield return selectedRoot;
        }

        if (recentRoots is null)
        {
            yield break;
        }

        foreach (var root in recentRoots)
        {
            var normalized = NormalizePath(root);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
    }

    private static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsRoot = string.IsNullOrWhiteSpace(localAppData)
            ? Environment.CurrentDirectory
            : localAppData;
        return Path.Combine(settingsRoot, "MyGitPuller", "appsettings.json");
    }

    private sealed class PersistedAppSettings
    {
        public string? SelectedLibraryRoot { get; set; }

        public List<string> RecentLibraryRoots { get; set; } = [];
    }
}

using System.Text;
using System.Text.Json;

namespace GitPuller;

public sealed class LibraryConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetDefaultConfigPath(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        return Path.Combine(Path.GetFullPath(libraryRoot), ".mygitpuller", "config.json");
    }

    public async Task<LibraryConfig> LoadAsync(string libraryRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);

        var normalizedLibraryRoot = Path.GetFullPath(libraryRoot);
        var configPath = GetDefaultConfigPath(normalizedLibraryRoot);
        if (!File.Exists(configPath))
        {
            return CreateDefaultConfig(normalizedLibraryRoot);
        }

        var json = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
        try
        {
            var config = JsonSerializer.Deserialize<LibraryConfig>(json, SerializerOptions) ?? new LibraryConfig();
            return NormalizeConfig(config, normalizedLibraryRoot);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Library configuration file is invalid JSON: {configPath}",
                ex);
        }
    }

    public async Task SaveAsync(LibraryConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.LibraryRoot);

        var normalized = NormalizeConfig(config, Path.GetFullPath(config.LibraryRoot));
        var configPath = GetDefaultConfigPath(normalized.LibraryRoot);
        var configDirectory = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(configDirectory);
        var json = JsonSerializer.Serialize(normalized, SerializerOptions);
        var tempPath = Path.Combine(configDirectory, Path.GetRandomFileName() + ".tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            if (File.Exists(configPath))
            {
                File.Replace(tempPath, configPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, configPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static LibraryConfig CreateDefaultConfig(string libraryRoot)
    {
        return new LibraryConfig
        {
            LibraryRoot = Path.GetFullPath(libraryRoot),
            DefaultOptions = new GitPullerOptions()
        };
    }

    private static LibraryConfig NormalizeConfig(LibraryConfig config, string libraryRoot)
    {
        return new LibraryConfig
        {
            LibraryRoot = Path.GetFullPath(libraryRoot),
            Categories = (config.Categories ?? [])
                .OfType<string>()
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Select(category => category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Repositories = (config.Repositories ?? [])
                .OfType<LibraryRepositoryConfig>()
                .Select(NormalizeRepository)
                .ToList(),
            RemovedRepositories = (config.RemovedRepositories ?? [])
                .OfType<RemovedRepositoryRecord>()
                .Select(NormalizeRemovedRepository)
                .ToList(),
            DefaultOptions = config.DefaultOptions ?? new GitPullerOptions()
        };
    }

    private static LibraryRepositoryConfig NormalizeRepository(LibraryRepositoryConfig repository)
    {
        return new LibraryRepositoryConfig
        {
            Name = repository.Name?.Trim() ?? string.Empty,
            Path = NormalizePath(repository.Path),
            Category = NormalizeCategory(repository.Category),
            RemoteUrl = NormalizeOptionalText(repository.RemoteUrl)
        };
    }

    private static RemovedRepositoryRecord NormalizeRemovedRepository(RemovedRepositoryRecord repository)
    {
        return new RemovedRepositoryRecord
        {
            Name = repository.Name?.Trim() ?? string.Empty,
            OriginalPath = NormalizePath(repository.OriginalPath),
            RemovedPath = NormalizePath(repository.RemovedPath),
            Category = NormalizeCategory(repository.Category),
            RemoteUrl = NormalizeOptionalText(repository.RemoteUrl),
            RemovedAt = repository.RemovedAt
        };
    }

    private static string NormalizeCategory(string? category)
    {
        return string.IsNullOrWhiteSpace(category) ? string.Empty : category.Trim();
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : GitRepositorySupport.NormalizeRepoPath(path);
    }
}

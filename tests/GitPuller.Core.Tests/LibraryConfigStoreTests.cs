using System.Text;
using GitPuller;

namespace GitPuller.Core.Tests;

public sealed class LibraryConfigStoreTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "MyGitPuller.Core.Tests", Guid.NewGuid().ToString("N"));

    public LibraryConfigStoreTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public async Task LoadAsync_WhenConfigIsMissing_ReturnsDefaults()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        Directory.CreateDirectory(libraryRoot);

        var store = new LibraryConfigStore();

        var config = await store.LoadAsync(libraryRoot, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(libraryRoot), config.LibraryRoot);
        Assert.Empty(config.Categories);
        Assert.Empty(config.Repositories);
        Assert.Empty(config.RemovedRepositories);
        Assert.Equal(new GitPullerOptions(), config.DefaultOptions);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsConfigState()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        Directory.CreateDirectory(libraryRoot);

        var removedAt = new DateTimeOffset(2026, 5, 28, 10, 15, 30, TimeSpan.FromHours(9));
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins", "Tools"],
            Repositories =
            [
                new LibraryRepositoryConfig
                {
                    Name = "RepoA",
                    Path = Path.Combine(libraryRoot, "Plugins", "RepoA"),
                    Category = "Plugins",
                    RemoteUrl = "git@github.com:example/repo-a.git"
                }
            ],
            RemovedRepositories =
            [
                new RemovedRepositoryRecord
                {
                    Name = "RepoB",
                    OriginalPath = Path.Combine(libraryRoot, "Tools", "RepoB"),
                    RemovedPath = Path.Combine(libraryRoot, ".mygitpuller", "removed", "Tools", "RepoB"),
                    Category = "Tools",
                    RemoteUrl = "https://example.invalid/repo-b.git",
                    RemovedAt = removedAt
                }
            ],
            DefaultOptions = new GitPullerOptions
            {
                MaxDegreeOfParallelism = 3,
                InitMissingSubmodules = false,
                ForceSync = false,
                CleanUntracked = false,
                PullFfOnly = false,
                SyncAllBranches = false,
                StaleGitLockCleanup = false,
                VerboseReport = true,
                GitTimeoutMilliseconds = 120000,
                StaleGitLockAge = TimeSpan.FromMinutes(30)
            }
        };

        var store = new LibraryConfigStore();
        await store.SaveAsync(config, CancellationToken.None);

        var loaded = await store.LoadAsync(libraryRoot, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(libraryRoot), loaded.LibraryRoot);
        Assert.Equal(["Plugins", "Tools"], loaded.Categories);
        var repository = Assert.Single(loaded.Repositories);
        Assert.Equal("RepoA", repository.Name);
        Assert.Equal(Path.GetFullPath(Path.Combine(libraryRoot, "Plugins", "RepoA")), repository.Path);
        Assert.Equal("Plugins", repository.Category);
        Assert.Equal("git@github.com:example/repo-a.git", repository.RemoteUrl);

        var removed = Assert.Single(loaded.RemovedRepositories);
        Assert.Equal("RepoB", removed.Name);
        Assert.Equal(Path.GetFullPath(Path.Combine(libraryRoot, "Tools", "RepoB")), removed.OriginalPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(libraryRoot, ".mygitpuller", "removed", "Tools", "RepoB")), removed.RemovedPath);
        Assert.Equal("Tools", removed.Category);
        Assert.Equal("https://example.invalid/repo-b.git", removed.RemoteUrl);
        Assert.Equal(removedAt, removed.RemovedAt);
        Assert.Equal(config.DefaultOptions, loaded.DefaultOptions);
    }

    [Fact]
    public async Task LoadAsync_IgnoresUnknownJsonFields()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        Directory.CreateDirectory(Path.Combine(libraryRoot, ".mygitpuller"));

        var configPath = LibraryConfigStore.GetDefaultConfigPath(libraryRoot);
        var json = """
            {
              "libraryRoot": "LIBRARY_ROOT",
              "categories": ["Plugins"],
              "repositories": [
                {
                  "name": "RepoA",
                  "path": "REPO_PATH",
                  "category": "Plugins",
                  "remoteUrl": "https://example.invalid/repo-a.git",
                  "futureField": "ignored"
                }
              ],
              "defaultOptions": {
                "maxDegreeOfParallelism": 2,
                "mysteryFlag": true
              },
              "uiState": {
                "selectedCategory": "Plugins"
              }
            }
            """
            .Replace("LIBRARY_ROOT", libraryRoot.Replace("\\", "\\\\"))
            .Replace("REPO_PATH", Path.Combine(libraryRoot, "Plugins", "RepoA").Replace("\\", "\\\\"));

        await File.WriteAllTextAsync(configPath, json, Encoding.UTF8);

        var store = new LibraryConfigStore();
        var config = await store.LoadAsync(libraryRoot, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(libraryRoot), config.LibraryRoot);
        Assert.Equal(["Plugins"], config.Categories);
        Assert.Equal(2, config.DefaultOptions.MaxDegreeOfParallelism);
        Assert.Single(config.Repositories);
    }

    [Fact]
    public async Task LoadAsync_WhenConfigJsonIsMalformed_ThrowsInvalidOperationExceptionWithConfigPath()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        Directory.CreateDirectory(Path.Combine(libraryRoot, ".mygitpuller"));

        var configPath = LibraryConfigStore.GetDefaultConfigPath(libraryRoot);
        await File.WriteAllTextAsync(configPath, "{ \"categories\": [", Encoding.UTF8);

        var store = new LibraryConfigStore();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync(libraryRoot, CancellationToken.None));

        Assert.Contains(configPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public void GetDefaultConfigPath_ReturnsLibraryScopedConfigLocation()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");

        var configPath = LibraryConfigStore.GetDefaultConfigPath(libraryRoot);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(libraryRoot), ".mygitpuller", "config.json"),
            configPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}

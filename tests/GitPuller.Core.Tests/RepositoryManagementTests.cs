using System.Diagnostics;
using GitPuller;

namespace GitPuller.Core.Tests;

public sealed class RepositoryManagementTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "MyGitPuller.Core.Tests", Guid.NewGuid().ToString("N"));

    public RepositoryManagementTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void RemoveRepository_MovesActiveRepositoryIntoLibraryRemovedArea_AndStoresMetadata()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        var beforeRemoval = DateTimeOffset.UtcNow;

        var removed = service.RemoveRepository(config, repository);

        var expectedRemovedPath = Path.Combine(
            Path.GetFullPath(libraryRoot),
            ".mygitpuller",
            "removed",
            "Plugins",
            "RepoA");

        Assert.False(Directory.Exists(repositoryPath));
        Assert.True(Directory.Exists(expectedRemovedPath));
        Assert.Equal(Path.GetFullPath(repositoryPath), removed.OriginalPath);
        Assert.Equal(expectedRemovedPath, removed.RemovedPath);
        Assert.Equal("Plugins", removed.Category);
        Assert.Equal("git@github.com:example/repo-a.git", removed.RemoteUrl);
        Assert.InRange(removed.RemovedAt, beforeRemoval.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(removed, Assert.Single(config.RemovedRepositories));
        Assert.Empty(config.Repositories);
    }

    [Fact]
    public void RestoreRepository_MovesRemovedRepositoryBackToOriginalPath_WhenPathIsFree()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        var removed = service.RemoveRepository(config, repository);

        var restored = service.RestoreRepository(config, removed);

        Assert.True(Directory.Exists(repositoryPath));
        Assert.False(Directory.Exists(removed.RemovedPath));
        Assert.Equal(Path.GetFullPath(repositoryPath), restored.Path);
        Assert.Equal("RepoA", restored.Name);
        Assert.Equal("Plugins", restored.Category);
        Assert.Equal("git@github.com:example/repo-a.git", restored.RemoteUrl);
        Assert.Empty(config.RemovedRepositories);
        var active = Assert.Single(config.Repositories);
        Assert.Equal(Path.GetFullPath(repositoryPath), active.Path);
    }

    [Fact]
    public void RestoreRepositoryAs_SupportsAlternateCategoryAndPath()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        var removed = service.RemoveRepository(config, repository);
        var alternatePath = Path.Combine(libraryRoot, "Archive", "RepoA-Restored");

        var restored = service.RestoreRepositoryAs(config, removed, "Archive", alternatePath);

        Assert.False(Directory.Exists(repositoryPath));
        Assert.True(Directory.Exists(alternatePath));
        Assert.Equal(Path.GetFullPath(alternatePath), restored.Path);
        Assert.Equal("Archive", restored.Category);
        Assert.Empty(config.RemovedRepositories);
        var active = Assert.Single(config.Repositories);
        Assert.Equal(Path.GetFullPath(alternatePath), active.Path);
        Assert.Equal("Archive", active.Category);
    }

    [Fact]
    public void RestoreRepository_ThrowsWhenOriginalPathIsOccupied()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        var removed = service.RemoveRepository(config, repository);
        Directory.CreateDirectory(repositoryPath);

        var exception = Assert.Throws<InvalidOperationException>(() => service.RestoreRepository(config, removed));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.Single(config.RemovedRepositories);
    }

    [Fact]
    public void PermanentlyDelete_RemovesRemovedRepositoryFolderAndMetadata()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        var removed = service.RemoveRepository(config, repository);

        service.PermanentlyDelete(config, removed);

        Assert.False(Directory.Exists(removed.RemovedPath));
        Assert.Empty(config.RemovedRepositories);
        Assert.Empty(config.Repositories);
    }

    [Fact]
    public void ScanLibraryRoot_NeverReturnsRepositoriesInsideMyGitPullerRemoved()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", RemoteUrl: null);
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        service.RemoveRepository(config, repository);

        var scanner = new GitRepositoryScanner();
        var inventory = scanner.ScanLibraryRoot(libraryRoot);

        Assert.Empty(inventory.Repositories);
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

    private static LibraryConfig CreateConfig(string libraryRoot, RepositoryDescriptor repository)
    {
        return new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins"],
            Repositories =
            [
                new LibraryRepositoryConfig
                {
                    Name = repository.Name,
                    Path = repository.Path,
                    Category = repository.Category,
                    RemoteUrl = repository.RemoteUrl
                }
            ]
        };
    }

    private static string CreateRepositoryDirectory(string libraryRoot, string category, string name)
    {
        var repositoryPath = Path.Combine(libraryRoot, category, name);
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        File.WriteAllText(Path.Combine(repositoryPath, "README.md"), $"# {name}");
        return repositoryPath;
    }
}

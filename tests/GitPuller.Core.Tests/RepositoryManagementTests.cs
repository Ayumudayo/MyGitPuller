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
    public void RestoreRepositoryAs_RejectsDestinationInsideMyGitPuller_UsingMixedCaseAndTrailingSeparator()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        var removed = service.RemoveRepository(config, repository);
        var hiddenDestination = Path.Combine(libraryRoot.ToUpperInvariant(), ".MYGITPULLER", "active", "RepoA") + Path.DirectorySeparatorChar;

        var exception = Assert.Throws<InvalidOperationException>(() => service.RestoreRepositoryAs(config, removed, "Hidden", hiddenDestination));

        Assert.Contains(".mygitpuller", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.Empty(config.Repositories);
        Assert.Equal(removed, Assert.Single(config.RemovedRepositories));
        Assert.False(Directory.Exists(Path.Combine(libraryRoot, ".mygitpuller", "active", "RepoA")));
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
    public void RemoveRepository_InitializesNullCollectionsBeforeFilesystemMutation()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = null!,
            Repositories = null!,
            RemovedRepositories = null!
        };
        var service = new RepositoryRemovalService();

        var removed = service.RemoveRepository(config, repository);

        Assert.False(Directory.Exists(repositoryPath));
        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.NotNull(config.Repositories);
        Assert.NotNull(config.RemovedRepositories);
        Assert.NotNull(config.Categories);
        Assert.Empty(config.Repositories);
        Assert.Equal(removed, Assert.Single(config.RemovedRepositories));
    }

    [Fact]
    public void RemoveRepository_SanitizesNullEntriesBeforeFilesystemMutation()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Repositories = [null!],
            RemovedRepositories = [null!]
        };
        var service = new RepositoryRemovalService();

        var removed = service.RemoveRepository(config, repository);

        Assert.False(Directory.Exists(repositoryPath));
        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.Equal(removed, Assert.Single(config.RemovedRepositories));
        Assert.Empty(config.Repositories);
    }

    [Fact]
    public void RestoreRepositoryAs_InitializesNullCollectionsBeforeFilesystemMutation()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var removedPath = CreateRepositoryDirectory(Path.Combine(libraryRoot, ".mygitpuller", "removed"), "Plugins", "RepoA");
        var removed = new RemovedRepositoryRecord
        {
            Name = "RepoA",
            OriginalPath = Path.Combine(libraryRoot, "Plugins", "RepoA"),
            RemovedPath = removedPath,
            Category = "Plugins",
            RemoteUrl = "git@github.com:example/repo-a.git",
            RemovedAt = DateTimeOffset.UtcNow
        };
        var destinationPath = Path.Combine(libraryRoot, "Plugins", "RepoA");
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = null!,
            Repositories = null!,
            RemovedRepositories = null!
        };
        var service = new RepositoryRemovalService();

        var restored = service.RestoreRepositoryAs(config, removed, "Plugins", destinationPath);

        Assert.True(Directory.Exists(destinationPath));
        Assert.False(Directory.Exists(removedPath));
        Assert.NotNull(config.Repositories);
        Assert.NotNull(config.RemovedRepositories);
        Assert.NotNull(config.Categories);
        var active = Assert.Single(config.Repositories);
        Assert.Equal(restored.Path, active.Path);
        Assert.Empty(config.RemovedRepositories);
        Assert.Contains("Plugins", config.Categories);
    }

    [Fact]
    public void RestoreRepositoryAs_SanitizesNullEntriesBeforeFilesystemMutation()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var removedPath = CreateRepositoryDirectory(Path.Combine(libraryRoot, ".mygitpuller", "removed"), "Plugins", "RepoA");
        var removed = new RemovedRepositoryRecord
        {
            Name = "RepoA",
            OriginalPath = Path.Combine(libraryRoot, "Plugins", "RepoA"),
            RemovedPath = removedPath,
            Category = "Plugins",
            RemoteUrl = "git@github.com:example/repo-a.git",
            RemovedAt = DateTimeOffset.UtcNow
        };
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Repositories = [null!],
            RemovedRepositories = [null!, removed]
        };
        var service = new RepositoryRemovalService();
        var destinationPath = Path.Combine(libraryRoot, "Archive", "RepoA");

        var restored = service.RestoreRepositoryAs(config, removed, "Archive", destinationPath);

        Assert.True(Directory.Exists(destinationPath));
        Assert.False(Directory.Exists(removedPath));
        var active = Assert.Single(config.Repositories);
        Assert.Equal(restored.Path, active.Path);
        Assert.Empty(config.RemovedRepositories);
    }

    [Fact]
    public void PermanentlyDelete_InitializesNullRemovedRepositoriesBeforeFilesystemMutation()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var removedPath = CreateRepositoryDirectory(Path.Combine(libraryRoot, ".mygitpuller", "removed"), "Plugins", "RepoA");
        var removed = new RemovedRepositoryRecord
        {
            Name = "RepoA",
            OriginalPath = Path.Combine(libraryRoot, "Plugins", "RepoA"),
            RemovedPath = removedPath,
            Category = "Plugins",
            RemoteUrl = "git@github.com:example/repo-a.git",
            RemovedAt = DateTimeOffset.UtcNow
        };
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            RemovedRepositories = null!
        };
        var service = new RepositoryRemovalService();

        service.PermanentlyDelete(config, removed);

        Assert.False(Directory.Exists(removedPath));
        Assert.NotNull(config.RemovedRepositories);
        Assert.Empty(config.RemovedRepositories);
    }

    [Fact]
    public void PermanentlyDelete_SanitizesNullEntriesBeforeFilesystemMutation()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var removedPath = CreateRepositoryDirectory(Path.Combine(libraryRoot, ".mygitpuller", "removed"), "Plugins", "RepoA");
        var removed = new RemovedRepositoryRecord
        {
            Name = "RepoA",
            OriginalPath = Path.Combine(libraryRoot, "Plugins", "RepoA"),
            RemovedPath = removedPath,
            Category = "Plugins",
            RemoteUrl = "git@github.com:example/repo-a.git",
            RemovedAt = DateTimeOffset.UtcNow
        };
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            RemovedRepositories = [null!, removed]
        };
        var service = new RepositoryRemovalService();

        service.PermanentlyDelete(config, removed);

        Assert.False(Directory.Exists(removedPath));
        Assert.Empty(config.RemovedRepositories);
    }

    [Fact]
    public void RemoveRepository_RejectsRepositoryPathOutsideLibraryRoot()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var outsideRoot = Path.Combine(tempRoot, "Outside");
        var outsideRepositoryPath = CreateRepositoryDirectory(outsideRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(outsideRepositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.RemoveRepository(config, repository));

        Assert.Contains("Repository path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(libraryRoot), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(outsideRepositoryPath));
        Assert.Empty(config.RemovedRepositories);
        Assert.Single(config.Repositories);
        Assert.Equal(NormalizePath(outsideRepositoryPath), NormalizePath(config.Repositories[0].Path));
    }

    [Fact]
    public void RestoreRepositoryAs_RejectsDestinationPathOutsideLibraryRoot()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        var removed = service.RemoveRepository(config, repository);
        var outsideDestination = Path.Combine(tempRoot, "OutsideRestore", "RepoA");

        var exception = Assert.Throws<InvalidOperationException>(() => service.RestoreRepositoryAs(config, removed, "Plugins", outsideDestination));

        Assert.Contains("Restore destination path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(libraryRoot), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.Empty(config.Repositories);
        Assert.Equal(removed, Assert.Single(config.RemovedRepositories));
        Assert.False(Directory.Exists(outsideDestination));
    }

    [Fact]
    public void PermanentlyDelete_RejectsRemovedRepositoryPathOutsideRemovedArea()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var outsideRemovedPath = CreateRepositoryDirectory(Path.Combine(tempRoot, "OutsideRemoved"), "Plugins", "RepoA");
        var removed = new RemovedRepositoryRecord
        {
            Name = "RepoA",
            OriginalPath = Path.Combine(libraryRoot, "Plugins", "RepoA"),
            RemovedPath = outsideRemovedPath,
            Category = "Plugins",
            RemoteUrl = "git@github.com:example/repo-a.git",
            RemovedAt = DateTimeOffset.UtcNow
        };
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            RemovedRepositories = [removed]
        };
        var service = new RepositoryRemovalService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.PermanentlyDelete(config, removed));

        Assert.Contains("Removed repository path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(Path.GetFullPath(libraryRoot), ".mygitpuller", "removed"), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(outsideRemovedPath));
        Assert.Equal(removed, Assert.Single(config.RemovedRepositories));
    }

    [Fact]
    public void PermanentlyDelete_RejectsRemovedRootContainerItself()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var removedRoot = Path.Combine(Path.GetFullPath(libraryRoot), ".mygitpuller", "removed");
        var firstRemovedRepository = CreateRepositoryDirectory(removedRoot, "Plugins", "RepoA");
        var secondRemovedRepository = CreateRepositoryDirectory(removedRoot, "Tools", "RepoB");
        var removed = new RemovedRepositoryRecord
        {
            Name = "removed",
            OriginalPath = Path.Combine(libraryRoot, "Plugins", "RepoA"),
            RemovedPath = removedRoot,
            Category = string.Empty,
            RemoteUrl = null,
            RemovedAt = DateTimeOffset.UtcNow
        };
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            RemovedRepositories = [removed]
        };
        var service = new RepositoryRemovalService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.PermanentlyDelete(config, removed));

        Assert.Contains("Removed repository path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(removedRoot, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(removedRoot));
        Assert.True(Directory.Exists(firstRemovedRepository));
        Assert.True(Directory.Exists(secondRemovedRepository));
        Assert.Equal(removed, Assert.Single(config.RemovedRepositories));
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

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

using System.Diagnostics;
using System.Net;
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
    public void PreparePermanentDelete_RemovesMetadataAndReturnsGuardedPathWithoutDeletingFolder()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var repositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        var repository = new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", "git@github.com:example/repo-a.git");
        var config = CreateConfig(libraryRoot, repository);
        var service = new RepositoryRemovalService();
        var removed = service.RemoveRepository(config, repository);

        var removedPath = service.PreparePermanentDelete(config, removed);

        Assert.Equal(removed.RemovedPath, removedPath);
        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.Empty(config.RemovedRepositories);
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

    [Fact]
    public void ScanLibraryRoot_WhenLibraryRootIsGitRepo_ReturnsOnlyChildRepositoriesOutsideMyGitPuller()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        Directory.CreateDirectory(Path.Combine(libraryRoot, ".git"));
        var childRepositoryPath = CreateRepositoryDirectory(libraryRoot, "Plugins", "RepoA");
        CreateRepositoryDirectory(Path.Combine(libraryRoot, ".mygitpuller", "removed"), "Hidden", "RepoHidden");

        var scanner = new GitRepositoryScanner();
        var inventory = scanner.ScanLibraryRoot(libraryRoot);

        var repository = Assert.Single(inventory.Repositories);
        Assert.Equal(NormalizePath(childRepositoryPath), NormalizePath(repository.Path));
        Assert.Equal("RepoA", repository.Name);
        Assert.Equal("Plugins", repository.Category);
        Assert.DoesNotContain(inventory.Repositories, x => string.Equals(NormalizePath(x.Path), NormalizePath(libraryRoot), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(inventory.Repositories, x => x.Path.Contains(".mygitpuller", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Preview_DerivesRepositoryNameCategoryAndTargetPath_FromHttpsUrl()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "https://github.com/goatcorp/Dalamud.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.True(preview.IsValid);
        Assert.Null(preview.Diagnostic);
        Assert.Equal("Dalamud", preview.RepositoryName);
        Assert.Equal("Plugins", preview.Category);
        Assert.Equal(Path.Combine(Path.GetFullPath(libraryRoot), "Plugins", "Dalamud"), preview.TargetPath);
        Assert.NotNull(preview.Repository);
        Assert.Equal("Dalamud", preview.Repository.Name);
        Assert.Equal("Plugins", preview.Repository.Category);
        Assert.Equal("https://github.com/goatcorp/Dalamud.git", preview.Repository.RemoteUrl);
    }

    [Fact]
    public void Preview_UsesFolderNameOverrideForRepositoryNameAndTargetPath()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "https://github.com/goatcorp/Dalamud.git",
            "Dalamud-local");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.True(preview.IsValid);
        Assert.Null(preview.Diagnostic);
        Assert.Equal("Dalamud-local", preview.RepositoryName);
        Assert.Equal(Path.Combine(Path.GetFullPath(libraryRoot), "Plugins", "Dalamud-local"), preview.TargetPath);
        Assert.NotNull(preview.Repository);
        Assert.Equal("Dalamud-local", preview.Repository.Name);
        Assert.Equal("https://github.com/goatcorp/Dalamud.git", preview.Repository.RemoteUrl);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\Escape")]
    [InlineData(".git")]
    [InlineData("RepoWithTrailingSpace ")]
    [InlineData("RepoWithTrailingDot.")]
    [InlineData("CON")]
    public void Preview_RejectsInvalidFolderNameOverrideBeforeGitRuns(string folderNameOverride)
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "https://github.com/goatcorp/Dalamud.git",
            folderNameOverride);
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);
        var cloneResult = service.Clone(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, preview.Diagnostic.Category);
        Assert.Null(preview.Repository);
        Assert.False(cloneResult.Succeeded);
        Assert.False(cloneResult.GitCommandExecuted);
    }

    [Fact]
    public void Preview_RejectsMissingCategory_WithoutInferringOne()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            string.Empty,
            "https://github.com/goatcorp/Dalamud.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, preview.Diagnostic.Category);
        Assert.Contains("Category", preview.Diagnostic.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Null(preview.Repository);
    }

    [Fact]
    public void Preview_ClassifiesExistingNonEmptyTargetDirectoryAsClonePathConflict()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var targetPath = Path.Combine(libraryRoot, "Plugins", "Dalamud");
        Directory.CreateDirectory(targetPath);
        File.WriteAllText(Path.Combine(targetPath, "blocking.txt"), "occupied");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "https://github.com/goatcorp/Dalamud.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.ClonePathConflict, preview.Diagnostic.Category);
        Assert.Equal(targetPath, preview.Diagnostic.RelatedPath);
        Assert.Contains("already exists", preview.Diagnostic.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_RejectsInvalidUrlBeforeGitRuns()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "not a git url");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, preview.Diagnostic.Category);
        Assert.Contains("URL", preview.Diagnostic.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Null(preview.Repository);
    }

    [Fact]
    public void Preview_RejectsCategoryTraversalOutsideLibraryRoot()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "..\\Outside",
            "https://github.com/goatcorp/Dalamud.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, preview.Diagnostic.Category);
        Assert.Contains("outside", preview.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_RejectsReservedMyGitPullerCategory()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            ".mygitpuller\\hidden",
            "https://github.com/goatcorp/Dalamud.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, preview.Diagnostic.Category);
        Assert.Contains(".mygitpuller", preview.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_RejectsRepositoryNameThatEndsWithTrailingDotOrSpace()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var service = new RepositoryCloneService();

        var previewWithDot = service.Preview(new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "https://example.com/.mygitpuller..git"));

        var previewWithSpace = service.Preview(new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "https://example.com/RepoWithSpace%20.git"));

        Assert.False(previewWithDot.IsValid);
        Assert.NotNull(previewWithDot.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, previewWithDot.Diagnostic.Category);
        Assert.Contains("trailing", previewWithDot.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);

        Assert.False(previewWithSpace.IsValid);
        Assert.NotNull(previewWithSpace.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, previewWithSpace.Diagnostic.Category);
        Assert.Contains("trailing", previewWithSpace.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_RejectsCategorySegmentThatEndsWithTrailingDotOrSpace()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var service = new RepositoryCloneService();

        var previewWithDot = service.Preview(new RepositoryAddRequest(
            libraryRoot,
            "Plugins.",
            "https://github.com/goatcorp/Dalamud.git"));

        var previewWithSpace = service.Preview(new RepositoryAddRequest(
            libraryRoot,
            "Plugins \\Archive",
            "https://github.com/goatcorp/Dalamud.git"));

        Assert.False(previewWithDot.IsValid);
        Assert.NotNull(previewWithDot.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, previewWithDot.Diagnostic.Category);
        Assert.Contains("trailing", previewWithDot.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);

        Assert.False(previewWithSpace.IsValid);
        Assert.NotNull(previewWithSpace.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, previewWithSpace.Diagnostic.Category);
        Assert.Contains("trailing", previewWithSpace.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_RejectsUnsupportedAbsoluteUriScheme_WithoutTreatingItAsLocalPath()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "nosuchscheme://example.com/repo.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, preview.Diagnostic.Category);
        Assert.Contains("valid Git URL", preview.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_AcceptsScpLikeAliasRemote_AndDerivesRepositoryName()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "github-bf:owner/Dalamud.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.True(preview.IsValid);
        Assert.Equal("Dalamud", preview.RepositoryName);
        Assert.Equal(Path.Combine(Path.GetFullPath(libraryRoot), "Plugins", "Dalamud"), preview.TargetPath);
        Assert.Equal("github-bf:owner/Dalamud.git", preview.Repository!.RemoteUrl);
    }

    [Fact]
    public void Preview_RejectsSchemeLikeRemoteWithoutHostPathShape()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var service = new RepositoryCloneService();

        var noSuchSchemePreview = service.Preview(new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "nosuchscheme:repo.git"));

        var mailToPreview = service.Preview(new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "mailto:repo.git"));

        Assert.False(noSuchSchemePreview.IsValid);
        Assert.NotNull(noSuchSchemePreview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, noSuchSchemePreview.Diagnostic.Category);

        Assert.False(mailToPreview.IsValid);
        Assert.NotNull(mailToPreview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, mailToPreview.Diagnostic.Category);
    }

    [Fact]
    public void Preview_RejectsUnsupportedSchemeLikeRemoteWithSlashPath()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var service = new RepositoryCloneService();

        var noSuchSchemePreview = service.Preview(new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "nosuchscheme:owner/repo.git"));

        var mailToPreview = service.Preview(new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "mailto:owner/repo.git"));

        Assert.False(noSuchSchemePreview.IsValid);
        Assert.NotNull(noSuchSchemePreview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, noSuchSchemePreview.Diagnostic.Category);

        Assert.False(mailToPreview.IsValid);
        Assert.NotNull(mailToPreview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, mailToPreview.Diagnostic.Category);
    }

    [Fact]
    public void Preview_RejectsReservedDosDeviceRepositoryName()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            "https://example.com/CON.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, preview.Diagnostic.Category);
        Assert.Contains("reserved", preview.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Preview_RejectsReservedDosDeviceCategoryName()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(
            libraryRoot,
            "NUL",
            "https://github.com/goatcorp/Dalamud.git");
        var service = new RepositoryCloneService();

        var preview = service.Preview(request);

        Assert.False(preview.IsValid);
        Assert.NotNull(preview.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, preview.Diagnostic.Category);
        Assert.Contains("reserved", preview.Diagnostic.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clone_ClonesFromLocalBareRepository_AndReturnsConfigReadyDescriptor()
    {
        var scenarioRoot = Path.Combine(tempRoot, "CloneFromLocalBareRepository");
        var libraryRoot = Path.Combine(scenarioRoot, "Library");
        var remotePath = CreateBareRemoteRepository(scenarioRoot, "Dalamud");
        var request = new RepositoryAddRequest(libraryRoot, "Plugins", remotePath);
        var service = new RepositoryCloneService();

        var result = service.Clone(request);

        Assert.True(result.Succeeded);
        Assert.True(result.GitCommandExecuted);
        Assert.Null(result.Diagnostic);
        Assert.NotNull(result.Repository);
        Assert.Equal(Path.Combine(Path.GetFullPath(libraryRoot), "Plugins", "Dalamud"), result.Repository.Path);
        Assert.Equal("Dalamud", result.Repository.Name);
        Assert.Equal("Plugins", result.Repository.Category);
        Assert.Equal(remotePath, result.Repository.RemoteUrl);
        Assert.True(Directory.Exists(result.Repository.Path));
        Assert.True(Directory.Exists(Path.Combine(result.Repository.Path, ".git")));
        Assert.Equal("seed", File.ReadAllText(Path.Combine(result.Repository.Path, "README.md")));
    }

    [Fact]
    public void Clone_RejectsInvalidUrlWithoutRunningGit()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(libraryRoot, "Plugins", "definitely not a clone source");
        var service = new RepositoryCloneService();

        var result = service.Clone(request);

        Assert.False(result.Succeeded);
        Assert.False(result.GitCommandExecuted);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, result.Diagnostic.Category);
        Assert.Null(result.Repository);
        Assert.False(Directory.Exists(Path.Combine(libraryRoot, "Plugins", "definitely not a clone source")));
    }

    [Fact]
    public void Clone_WithOptionsOverload_PreservesValidationShortCircuit()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library");
        var request = new RepositoryAddRequest(libraryRoot, "Plugins", "definitely not a clone source");
        var service = new RepositoryCloneService();

        var result = service.Clone(
            request,
            new GitPullerOptions
            {
                GitTimeoutMilliseconds = 1234
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.GitCommandExecuted);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(FailureCategory.InvalidCloneRequest, result.Diagnostic.Category);
    }

    [Fact]
    public void Clone_WithCanceledToken_ThrowsOperationCanceledException()
    {
        var scenarioRoot = Path.Combine(tempRoot, "CloneCanceled");
        var libraryRoot = Path.Combine(scenarioRoot, "Library");
        var remotePath = CreateBareRemoteRepository(scenarioRoot, "Dalamud");
        var request = new RepositoryAddRequest(libraryRoot, "Plugins", remotePath);
        var service = new RepositoryCloneService();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() => service.Clone(
            request,
            new GitPullerOptions
            {
                GitTimeoutMilliseconds = 5000
            },
            cancellationTokenSource.Token));

        Assert.Equal(cancellationTokenSource.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Clone_WhenCanceledAfterGitStarts_ThrowsOperationCanceledException()
    {
        var libraryRoot = Path.Combine(tempRoot, "CloneCanceledInFlight", "Library");
        var service = new RepositoryCloneService();
        using var server = new HangingGitHttpEndpoint();
        var request = new RepositoryAddRequest(
            libraryRoot,
            "Plugins",
            server.RepositoryUrl);
        using var cancellationTokenSource = new CancellationTokenSource();

        var cloneTask = Task.Run(() => service.Clone(
            request,
            new GitPullerOptions
            {
                GitTimeoutMilliseconds = 30000
            },
            cancellationTokenSource.Token));

        await server.WaitForRequestAsync();
        cancellationTokenSource.CancelAfter(300);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(async () => await cloneTask);

        Assert.Equal(cancellationTokenSource.Token, exception.CancellationToken);
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

    private static string CreateBareRemoteRepository(string scenarioRoot, string repositoryName)
    {
        var remotePath = Path.Combine(scenarioRoot, $"{repositoryName}.git");
        var seedPath = Path.Combine(scenarioRoot, "seed");

        Directory.CreateDirectory(scenarioRoot);

        RunGit(scenarioRoot, "init", "--bare", remotePath);
        RunGit(scenarioRoot, "clone", remotePath, seedPath);
        RunGit(seedPath, "config", "user.name", "Test User");
        RunGit(seedPath, "config", "user.email", "test@example.invalid");
        RunGit(seedPath, "checkout", "-b", "main");
        File.WriteAllText(Path.Combine(seedPath, "README.md"), "seed");
        RunGit(seedPath, "add", "README.md");
        RunGit(seedPath, "commit", "-m", "Initial commit");
        RunGit(seedPath, "push", "-u", "origin", "main");
        RunGit(remotePath, "symbolic-ref", "HEAD", "refs/heads/main");

        return Path.GetFullPath(remotePath);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        processStartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        processStartInfo.Environment["GCM_INTERACTIVE"] = "never";

        using var process = Process.Start(processStartInfo);
        Assert.NotNull(process);

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000), $"git command timed out: git {string.Join(' ', arguments)}");
        Task.WaitAll(standardOutput, standardError);

        var output = (standardOutput.Result + Environment.NewLine + standardError.Result).Trim();
        Assert.True(
            process.ExitCode == 0,
            $"git command failed ({process.ExitCode}): git {string.Join(' ', arguments)}{Environment.NewLine}{output}");
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private sealed class HangingGitHttpEndpoint : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource disposeTokenSource = new();
        private readonly Task serverTask;
        private readonly TaskCompletionSource<bool> requestSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HangingGitHttpEndpoint()
        {
            var prefix = $"http://127.0.0.1:{GetFreeTcpPort()}/";
            listener.Prefixes.Add(prefix);
            listener.Start();
            RepositoryUrl = prefix + "repo.git";
            serverTask = Task.Run(RunAsync);
        }

        public string RepositoryUrl { get; }

        public Task WaitForRequestAsync()
        {
            return requestSeen.Task;
        }

        public void Dispose()
        {
            disposeTokenSource.Cancel();

            try
            {
                listener.Stop();
            }
            catch
            {
            }

            try
            {
                serverTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            listener.Close();
            disposeTokenSource.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                using var registration = disposeTokenSource.Token.Register(() =>
                {
                    try
                    {
                        listener.Stop();
                    }
                    catch
                    {
                    }
                });

                while (!disposeTokenSource.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    requestSeen.TrySetResult(true);

                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, disposeTokenSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    try
                    {
                        context.Response.Abort();
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                requestSeen.TrySetCanceled(disposeTokenSource.Token);
            }
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}

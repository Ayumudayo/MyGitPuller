namespace GitPuller;

public sealed class RepositoryRemovalService
{
    public RemovedRepositoryRecord RemoveRepository(LibraryConfig config, RepositoryDescriptor repository)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(repository);

        var libraryRoot = GetLibraryRoot(config);
        var repositoryPath = GitRepositorySupport.NormalizeRepoPath(repository.Path);
        EnsurePathIsUnderRoot(repositoryPath, libraryRoot, "Repository path");

        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Repository directory was not found: {repositoryPath}");
        }

        var removedRoot = GetRemovedRoot(libraryRoot);
        var category = NormalizeCategory(repository.Category);
        var removedPath = Path.Combine(removedRoot, category, repository.Name);
        removedPath = GitRepositorySupport.NormalizeRepoPath(removedPath);
        EnsurePathIsUnderRoot(removedPath, removedRoot, "Removed repository path");

        if (Directory.Exists(removedPath) || File.Exists(removedPath))
        {
            throw new InvalidOperationException($"A removed repository already exists at '{removedPath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(removedPath)!);
        Directory.Move(repositoryPath, removedPath);

        var removed = new RemovedRepositoryRecord
        {
            Name = repository.Name,
            OriginalPath = repositoryPath,
            RemovedPath = removedPath,
            Category = category,
            RemoteUrl = repository.RemoteUrl,
            RemovedAt = DateTimeOffset.UtcNow
        };

        config.Repositories.RemoveAll(existing =>
            string.Equals(GitRepositorySupport.NormalizeRepoPath(existing.Path), repositoryPath, StringComparison.OrdinalIgnoreCase));
        config.RemovedRepositories.Add(removed);
        return removed;
    }

    public RepositoryDescriptor RestoreRepository(LibraryConfig config, RemovedRepositoryRecord removedRepository)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(removedRepository);

        return RestoreRepositoryAs(config, removedRepository, removedRepository.Category, removedRepository.OriginalPath);
    }

    public RepositoryDescriptor RestoreRepositoryAs(LibraryConfig config, RemovedRepositoryRecord removedRepository, string category, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(removedRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var libraryRoot = GetLibraryRoot(config);
        var removedRoot = GetRemovedRoot(libraryRoot);
        var normalizedRemovedPath = GitRepositorySupport.NormalizeRepoPath(removedRepository.RemovedPath);
        var normalizedDestinationPath = GitRepositorySupport.NormalizeRepoPath(destinationPath);
        var normalizedCategory = NormalizeCategory(category);

        EnsurePathIsUnderRoot(normalizedRemovedPath, removedRoot, "Removed repository path");
        EnsurePathIsUnderRoot(normalizedDestinationPath, libraryRoot, "Restore destination path");

        if (!Directory.Exists(normalizedRemovedPath))
        {
            throw new DirectoryNotFoundException($"Removed repository directory was not found: {normalizedRemovedPath}");
        }

        if (Directory.Exists(normalizedDestinationPath) || File.Exists(normalizedDestinationPath))
        {
            throw new InvalidOperationException($"Restore destination already exists: {normalizedDestinationPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(normalizedDestinationPath)!);
        Directory.Move(normalizedRemovedPath, normalizedDestinationPath);

        RemoveRemovedMetadata(config, removedRepository);

        var restored = new LibraryRepositoryConfig
        {
            Name = Path.GetFileName(normalizedDestinationPath),
            Path = normalizedDestinationPath,
            Category = normalizedCategory,
            RemoteUrl = removedRepository.RemoteUrl
        };

        config.Repositories.RemoveAll(existing =>
            string.Equals(GitRepositorySupport.NormalizeRepoPath(existing.Path), normalizedDestinationPath, StringComparison.OrdinalIgnoreCase));
        config.Repositories.Add(restored);

        if (!string.IsNullOrWhiteSpace(normalizedCategory)
            && !config.Categories.Contains(normalizedCategory, StringComparer.OrdinalIgnoreCase))
        {
            config.Categories.Add(normalizedCategory);
        }

        return new RepositoryDescriptor(restored.Path, restored.Name, restored.Category, restored.RemoteUrl);
    }

    public void PermanentlyDelete(LibraryConfig config, RemovedRepositoryRecord removedRepository)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(removedRepository);

        var libraryRoot = GetLibraryRoot(config);
        var removedRoot = GetRemovedRoot(libraryRoot);
        var removedPath = GitRepositorySupport.NormalizeRepoPath(removedRepository.RemovedPath);
        EnsurePathIsUnderRoot(removedPath, removedRoot, "Removed repository path");

        if (Directory.Exists(removedPath))
        {
            Directory.Delete(removedPath, recursive: true);
        }

        RemoveRemovedMetadata(config, removedRepository);
    }

    private static string GetLibraryRoot(LibraryConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config.LibraryRoot);
        return Path.GetFullPath(config.LibraryRoot);
    }

    private static string GetRemovedRoot(string libraryRoot)
    {
        return Path.Combine(libraryRoot, ".mygitpuller", "removed");
    }

    private static void RemoveRemovedMetadata(LibraryConfig config, RemovedRepositoryRecord removedRepository)
    {
        var removedPath = GitRepositorySupport.NormalizeRepoPath(removedRepository.RemovedPath);
        config.RemovedRepositories.RemoveAll(existing =>
            string.Equals(GitRepositorySupport.NormalizeRepoPath(existing.RemovedPath), removedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsurePathIsUnderRoot(string path, string root, string description)
    {
        var normalizedPath = GitRepositorySupport.NormalizeRepoPath(path);
        var normalizedRoot = GitRepositorySupport.NormalizeRepoPath(root);
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{description} must stay under '{normalizedRoot}'.");
        }
    }

    private static string NormalizeCategory(string? category)
    {
        return string.IsNullOrWhiteSpace(category) ? string.Empty : category.Trim();
    }
}

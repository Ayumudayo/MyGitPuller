using GitPuller;

namespace GitPuller_WinUI.Services;

public interface IRepositoryManagementService
{
    RepositoryAddPreview PreviewAddRepository(RepositoryAddRequest request);

    Task<RepositoryAddWorkflowResult> CloneRepositoryAsync(
        RepositoryAddRequest request,
        GitPullerOptions options,
        CancellationToken cancellationToken);

    Task<GitPullerLibraryLoadResult> SaveDefaultOptionsAsync(
        string libraryRoot,
        GitPullerOptions options,
        CancellationToken cancellationToken);

    Task<GitPullerLibraryLoadResult> RestoreRepositoryAsync(
        string libraryRoot,
        RemovedRepositoryRecord removedRepository,
        CancellationToken cancellationToken);

    Task<GitPullerLibraryLoadResult> RestoreRepositoryAsAsync(
        string libraryRoot,
        RemovedRepositoryRecord removedRepository,
        string category,
        string folderName,
        CancellationToken cancellationToken);

    Task<GitPullerLibraryLoadResult> PermanentlyDeleteRepositoryAsync(
        string libraryRoot,
        RemovedRepositoryRecord removedRepository,
        CancellationToken cancellationToken);
}

public sealed record RepositoryAddWorkflowResult(
    RepositoryAddResult CloneResult,
    GitPullerLibraryLoadResult? LibraryLoadResult)
{
    public bool Succeeded => CloneResult.Succeeded && LibraryLoadResult is not null;
}

public interface IRepositoryManagementConfigStore
{
    Task<LibraryConfig> LoadAsync(string libraryRoot, CancellationToken cancellationToken);

    Task SaveAsync(LibraryConfig config, CancellationToken cancellationToken);
}

public interface IRemovedRepositoryDirectoryDeleter
{
    void Delete(string removedPath);
}

public sealed class CoreRepositoryManagementService : IRepositoryManagementService
{
    private const string RestoreValidationRemoteUrl = "https://example.invalid/restore.git";

    private readonly IRepositoryManagementConfigStore configStore;
    private readonly GitRepositoryScanner scanner;
    private readonly RepositoryCloneService cloneService;
    private readonly RepositoryRemovalService removalService;
    private readonly IRemovedRepositoryDirectoryDeleter removedRepositoryDirectoryDeleter;

    public CoreRepositoryManagementService(
        LibraryConfigStore? configStore = null,
        GitRepositoryScanner? scanner = null,
        RepositoryCloneService? cloneService = null,
        RepositoryRemovalService? removalService = null,
        IRemovedRepositoryDirectoryDeleter? removedRepositoryDirectoryDeleter = null)
        : this(
            new LibraryConfigStoreAdapter(configStore ?? new LibraryConfigStore()),
            scanner,
            cloneService,
            removalService,
            removedRepositoryDirectoryDeleter)
    {
    }

    public CoreRepositoryManagementService(
        IRepositoryManagementConfigStore configStore,
        GitRepositoryScanner? scanner = null,
        RepositoryCloneService? cloneService = null,
        RepositoryRemovalService? removalService = null,
        IRemovedRepositoryDirectoryDeleter? removedRepositoryDirectoryDeleter = null)
    {
        this.configStore = configStore;
        this.scanner = scanner ?? new GitRepositoryScanner();
        this.cloneService = cloneService ?? new RepositoryCloneService();
        this.removalService = removalService ?? new RepositoryRemovalService();
        this.removedRepositoryDirectoryDeleter = removedRepositoryDirectoryDeleter ?? RemovedRepositoryDirectoryDeleter.Instance;
    }

    public RepositoryAddPreview PreviewAddRepository(RepositoryAddRequest request)
    {
        return cloneService.Preview(request);
    }

    public async Task<RepositoryAddWorkflowResult> CloneRepositoryAsync(
        RepositoryAddRequest request,
        GitPullerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var config = await configStore.LoadAsync(request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        var normalizedRequest = request with { LibraryRoot = config.LibraryRoot };
        var cloneResult = await Task.Run(
            () => cloneService.Clone(normalizedRequest, options, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (!cloneResult.Succeeded || cloneResult.Repository is null)
        {
            return new RepositoryAddWorkflowResult(cloneResult, LibraryLoadResult: null);
        }

        EnsureMutableCollections(config);
        AddRepositoryMetadata(config, cloneResult.Repository);
        try
        {
            await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(cloneResult.Repository.Path);
            throw;
        }

        var savedConfig = await configStore.LoadAsync(config.LibraryRoot, cancellationToken).ConfigureAwait(false);
        return new RepositoryAddWorkflowResult(cloneResult, CreateLoadResult(savedConfig));
    }

    public async Task<GitPullerLibraryLoadResult> SaveDefaultOptionsAsync(
        string libraryRoot,
        GitPullerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(options);

        var config = await configStore.LoadAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
        config.DefaultOptions = options;
        await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);

        var savedConfig = await configStore.LoadAsync(config.LibraryRoot, cancellationToken).ConfigureAwait(false);
        return CreateLoadResult(savedConfig);
    }

    public async Task<GitPullerLibraryLoadResult> RestoreRepositoryAsync(
        string libraryRoot,
        RemovedRepositoryRecord removedRepository,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(removedRepository);

        var config = await configStore.LoadAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
        var restored = removalService.RestoreRepository(config, removedRepository);
        try
        {
            await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryMoveDirectory(restored.Path, removedRepository.RemovedPath);
            throw;
        }

        var savedConfig = await configStore.LoadAsync(config.LibraryRoot, cancellationToken).ConfigureAwait(false);
        return CreateLoadResult(savedConfig);
    }

    public async Task<GitPullerLibraryLoadResult> RestoreRepositoryAsAsync(
        string libraryRoot,
        RemovedRepositoryRecord removedRepository,
        string category,
        string folderName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(removedRepository);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            throw new InvalidOperationException("Restore-as folder name is required.");
        }

        var config = await configStore.LoadAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
        var validationPreview = cloneService.Preview(new RepositoryAddRequest(
            config.LibraryRoot,
            category,
            RestoreValidationRemoteUrl,
            folderName));
        if (!validationPreview.IsValid)
        {
            throw new InvalidOperationException(BuildDiagnosticMessage(validationPreview.Diagnostic));
        }

        var restored = removalService.RestoreRepositoryAs(
            config,
            removedRepository,
            validationPreview.Category,
            validationPreview.TargetPath);
        try
        {
            await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryMoveDirectory(restored.Path, removedRepository.RemovedPath);
            throw;
        }

        var savedConfig = await configStore.LoadAsync(config.LibraryRoot, cancellationToken).ConfigureAwait(false);
        return CreateLoadResult(savedConfig);
    }

    public async Task<GitPullerLibraryLoadResult> PermanentlyDeleteRepositoryAsync(
        string libraryRoot,
        RemovedRepositoryRecord removedRepository,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(removedRepository);

        var config = await configStore.LoadAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
        var removedPath = removalService.PreparePermanentDelete(config, removedRepository);
        await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(removedPath))
        {
            try
            {
                removedRepositoryDirectoryDeleter.Delete(removedPath);
            }
            catch
            {
                await RestoreRemovedRepositoryMetadataAsync(
                    config.LibraryRoot,
                    removedRepository,
                    cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        var savedConfig = await configStore.LoadAsync(config.LibraryRoot, cancellationToken).ConfigureAwait(false);
        return CreateLoadResult(savedConfig);
    }

    private GitPullerLibraryLoadResult CreateLoadResult(LibraryConfig config)
    {
        var inventory = scanner.ScanLibraryRoot(config.LibraryRoot);
        return new GitPullerLibraryLoadResult(
            config.LibraryRoot,
            config.DefaultOptions,
            inventory,
            config.RemovedRepositories.ToArray(),
            config.Categories.ToArray());
    }

    private async Task RestoreRemovedRepositoryMetadataAsync(
        string libraryRoot,
        RemovedRepositoryRecord removedRepository,
        CancellationToken cancellationToken)
    {
        var config = await configStore.LoadAsync(libraryRoot, cancellationToken).ConfigureAwait(false);
        EnsureMutableCollections(config);
        config.RemovedRepositories.RemoveAll(existing => PathsEqual(existing.RemovedPath, removedRepository.RemovedPath));
        config.RemovedRepositories.Add(CloneRemovedRepositoryRecord(removedRepository));
        await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private static void AddRepositoryMetadata(LibraryConfig config, RepositoryDescriptor repository)
    {
        config.Repositories.RemoveAll(existing =>
            PathsEqual(existing.Path, repository.Path));
        config.Repositories.Add(new LibraryRepositoryConfig
        {
            Name = repository.Name,
            Path = repository.Path,
            Category = repository.Category,
            RemoteUrl = repository.RemoteUrl
        });

        if (!string.IsNullOrWhiteSpace(repository.Category)
            && !config.Categories.Contains(repository.Category, StringComparer.OrdinalIgnoreCase))
        {
            config.Categories.Add(repository.Category);
        }
    }

    private static RemovedRepositoryRecord CloneRemovedRepositoryRecord(RemovedRepositoryRecord removedRepository)
    {
        return new RemovedRepositoryRecord
        {
            Name = removedRepository.Name,
            OriginalPath = removedRepository.OriginalPath,
            RemovedPath = removedRepository.RemovedPath,
            Category = removedRepository.Category,
            RemoteUrl = removedRepository.RemoteUrl,
            RemovedAt = removedRepository.RemovedAt
        };
    }

    private static void EnsureMutableCollections(LibraryConfig config)
    {
        config.Categories ??= [];
        config.Repositories ??= [];
        config.RemovedRepositories ??= [];
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryMoveDirectory(string sourcePath, string destinationPath)
    {
        try
        {
            if (!Directory.Exists(sourcePath) || Directory.Exists(destinationPath) || File.Exists(destinationPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            Directory.Move(sourcePath, destinationPath);
        }
        catch
        {
        }
    }

    private static void ClearReadOnlyAttributes(string directoryPath)
    {
        foreach (var fileSystemPath in Directory.EnumerateFileSystemEntries(
            directoryPath,
            "*",
            SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(fileSystemPath, File.GetAttributes(fileSystemPath) & ~FileAttributes.ReadOnly);
            }
            catch
            {
            }
        }

        try
        {
            File.SetAttributes(directoryPath, File.GetAttributes(directoryPath) & ~FileAttributes.ReadOnly);
        }
        catch
        {
        }
    }

    private static string BuildDiagnosticMessage(FailureDiagnostic? diagnostic)
    {
        if (diagnostic is null)
        {
            return "The restore target is invalid.";
        }

        return $"{diagnostic.Title}: {diagnostic.Explanation} {diagnostic.Evidence}".Trim();
    }

    private sealed class LibraryConfigStoreAdapter : IRepositoryManagementConfigStore
    {
        private readonly LibraryConfigStore inner;

        public LibraryConfigStoreAdapter(LibraryConfigStore inner)
        {
            this.inner = inner;
        }

        public Task<LibraryConfig> LoadAsync(string libraryRoot, CancellationToken cancellationToken)
        {
            return inner.LoadAsync(libraryRoot, cancellationToken);
        }

        public Task SaveAsync(LibraryConfig config, CancellationToken cancellationToken)
        {
            return inner.SaveAsync(config, cancellationToken);
        }
    }

    private sealed class RemovedRepositoryDirectoryDeleter : IRemovedRepositoryDirectoryDeleter
    {
        public static RemovedRepositoryDirectoryDeleter Instance { get; } = new();

        private RemovedRepositoryDirectoryDeleter()
        {
        }

        public void Delete(string removedPath)
        {
            ClearReadOnlyAttributes(removedPath);
            Directory.Delete(removedPath, recursive: true);
        }
    }
}

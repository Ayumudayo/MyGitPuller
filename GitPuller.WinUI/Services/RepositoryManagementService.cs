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

public sealed class CoreRepositoryManagementService : IRepositoryManagementService
{
    private const string RestoreValidationRemoteUrl = "https://example.invalid/restore.git";

    private readonly LibraryConfigStore configStore;
    private readonly GitRepositoryScanner scanner;
    private readonly RepositoryCloneService cloneService;
    private readonly RepositoryRemovalService removalService;

    public CoreRepositoryManagementService(
        LibraryConfigStore? configStore = null,
        GitRepositoryScanner? scanner = null,
        RepositoryCloneService? cloneService = null,
        RepositoryRemovalService? removalService = null)
    {
        this.configStore = configStore ?? new LibraryConfigStore();
        this.scanner = scanner ?? new GitRepositoryScanner();
        this.cloneService = cloneService ?? new RepositoryCloneService();
        this.removalService = removalService ?? new RepositoryRemovalService();
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
        await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);

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
        removalService.RestoreRepository(config, removedRepository);
        await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);

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

        removalService.RestoreRepositoryAs(
            config,
            removedRepository,
            validationPreview.Category,
            validationPreview.TargetPath);
        await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);

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
        removalService.PermanentlyDelete(config, removedRepository);
        await configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);

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

    private static string BuildDiagnosticMessage(FailureDiagnostic? diagnostic)
    {
        if (diagnostic is null)
        {
            return "The restore target is invalid.";
        }

        return $"{diagnostic.Title}: {diagnostic.Explanation} {diagnostic.Evidence}".Trim();
    }
}

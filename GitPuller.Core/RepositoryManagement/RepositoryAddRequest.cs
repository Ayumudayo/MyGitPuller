namespace GitPuller;

public sealed record RepositoryAddRequest(
    string LibraryRoot,
    string Category,
    string RemoteUrl,
    string? FolderNameOverride = null);

namespace GitPuller;

public sealed class LibraryConfig
{
    public string LibraryRoot { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = [];
    public List<LibraryRepositoryConfig> Repositories { get; set; } = [];
    public List<RemovedRepositoryRecord> RemovedRepositories { get; set; } = [];
    public GitPullerOptions DefaultOptions { get; set; } = new();
}

public sealed class LibraryRepositoryConfig
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? RemoteUrl { get; set; }
}

public sealed class RemovedRepositoryRecord
{
    public string Name { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string RemovedPath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? RemoteUrl { get; set; }
    public DateTimeOffset RemovedAt { get; set; }
}

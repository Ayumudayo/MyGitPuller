using GitPuller;

namespace GitPuller_WinUI.ViewModels;

public sealed class RemovedRepositoryViewModel
{
    private RemovedRepositoryViewModel(
        RemovedRepositoryRecord record,
        bool removedPathExists,
        bool originalPathExists)
    {
        Name = string.IsNullOrWhiteSpace(record.Name) ? "(unnamed repository)" : record.Name;
        Category = string.IsNullOrWhiteSpace(record.Category) ? "(uncategorized)" : record.Category;
        OriginalPath = record.OriginalPath;
        RemovedPath = record.RemovedPath;
        RemoteUrl = record.RemoteUrl ?? string.Empty;
        RemovedAt = record.RemovedAt;
        RemovedPathExists = removedPathExists;
        OriginalPathExists = originalPathExists;
    }

    public string Name { get; }
    public string Category { get; }
    public string OriginalPath { get; }
    public string RemovedPath { get; }
    public string RemoteUrl { get; }
    public DateTimeOffset RemovedAt { get; }
    public bool RemovedPathExists { get; }
    public bool OriginalPathExists { get; }
    public bool CanRestore => RemovedPathExists && !OriginalPathExists;

    public string RestoreStateText
    {
        get
        {
            if (!RemovedPathExists)
            {
                return "Removed folder is missing";
            }

            return OriginalPathExists
                ? "Original path is occupied"
                : "Ready to restore";
        }
    }

    public static RemovedRepositoryViewModel FromRecord(
        RemovedRepositoryRecord record,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? pathExists = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        directoryExists ??= Directory.Exists;
        pathExists ??= path => Directory.Exists(path) || File.Exists(path);

        var removedPathExists = !string.IsNullOrWhiteSpace(record.RemovedPath)
            && directoryExists(record.RemovedPath);
        var originalPathExists = !string.IsNullOrWhiteSpace(record.OriginalPath)
            && pathExists(record.OriginalPath);

        return new RemovedRepositoryViewModel(record, removedPathExists, originalPathExists);
    }
}

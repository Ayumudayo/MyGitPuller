using Windows.Storage;
using Windows.System;

namespace GitPuller_WinUI.Services;

public interface IFileSystemLauncher
{
    Task<bool> LaunchPathAsync(string path);

    Task<bool> LaunchUriAsync(string uri);
}

public sealed class WinUiFileSystemLauncher : IFileSystemLauncher
{
    public async Task<bool> LaunchPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            return await Launcher.LaunchFolderPathAsync(path).AsTask().ConfigureAwait(false);
        }

        if (!File.Exists(path))
        {
            return false;
        }

        var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
        return await Launcher.LaunchFileAsync(file).AsTask().ConfigureAwait(false);
    }

    public async Task<bool> LaunchUriAsync(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
        {
            return false;
        }

        return await Launcher.LaunchUriAsync(parsedUri).AsTask().ConfigureAwait(false);
    }
}

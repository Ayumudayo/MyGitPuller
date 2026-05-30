namespace GitPuller_WinUI.ViewModels;

public sealed record LibraryRootDialogState(
    bool IsPrimaryButtonEnabled,
    bool IsErrorOpen,
    string ErrorMessage)
{
    public static LibraryRootDialogState Create(
        string? candidateRoot,
        bool canChangeLibraryRoot,
        bool hasRunError,
        string? runErrorMessage,
        string? localErrorMessage = null)
    {
        var hasBlankRoot = string.IsNullOrWhiteSpace(candidateRoot);
        var isPrimaryButtonEnabled = canChangeLibraryRoot && !hasBlankRoot;

        if (!hasBlankRoot && hasRunError)
        {
            return new LibraryRootDialogState(
                isPrimaryButtonEnabled,
                IsErrorOpen: true,
                runErrorMessage ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(localErrorMessage))
        {
            return new LibraryRootDialogState(
                isPrimaryButtonEnabled,
                IsErrorOpen: true,
                localErrorMessage);
        }

        if (hasBlankRoot)
        {
            return new LibraryRootDialogState(
                IsPrimaryButtonEnabled: false,
                IsErrorOpen: true,
                "Library root is required.");
        }

        if (hasRunError)
        {
            return new LibraryRootDialogState(
                isPrimaryButtonEnabled,
                IsErrorOpen: true,
                runErrorMessage ?? string.Empty);
        }

        return new LibraryRootDialogState(
            isPrimaryButtonEnabled,
            IsErrorOpen: false,
            string.Empty);
    }
}

using System.ComponentModel;
using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Views.Dialogs;

public sealed partial class ChangeLibraryRootDialog : ContentDialog, IDisposable
{
    private readonly MainShellViewModel viewModel;
    private readonly Func<Task<string?>> pickLibraryRootAsync;
    private string? localErrorMessage;
    private bool isDisposed;

    public ChangeLibraryRootDialog(
        MainShellViewModel viewModel,
        XamlRoot xamlRoot,
        Func<Task<string?>> pickLibraryRootAsync)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.pickLibraryRootAsync = pickLibraryRootAsync ?? throw new ArgumentNullException(nameof(pickLibraryRootAsync));

        InitializeComponent();
        XamlRoot = xamlRoot;

        LibraryRootBox.Text = viewModel.LibraryRoot;
        RecentRootsBox.ItemsSource = viewModel.RecentLibraryRoots.ToArray();
        RecentRootsBox.SelectedItem = viewModel.RecentLibraryRoots.FirstOrDefault(root =>
            string.Equals(root, viewModel.LibraryRoot, StringComparison.OrdinalIgnoreCase));

        BrowseButton.Click += BrowseButton_Click;
        LibraryRootBox.TextChanged += LibraryRootBox_TextChanged;
        RecentRootsBox.SelectionChanged += RecentRootsBox_SelectionChanged;
        PrimaryButtonClick += ChangeLibraryRootDialog_PrimaryButtonClick;
        Closed += ChangeLibraryRootDialog_Closed;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;

        UpdateRootDialogState();
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            localErrorMessage = null;
            UpdateRootDialogState();
            var selectedPath = await pickLibraryRootAsync();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                LibraryRootBox.Text = selectedPath;
            }
            else
            {
                UpdateRootDialogState();
            }
        }
        catch (Exception ex)
        {
            localErrorMessage = $"Folder picker failed: {ex.Message}";
            UpdateRootDialogState();
        }
    }

    private void LibraryRootBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        localErrorMessage = null;
        UpdateRootDialogState();
    }

    private void RecentRootsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentRootsBox.SelectedItem is string selectedRoot)
        {
            LibraryRootBox.Text = selectedRoot;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainShellViewModel.CanChangeLibraryRoot)
            or nameof(MainShellViewModel.HasRunError)
            or nameof(MainShellViewModel.RunErrorMessage))
        {
            UpdateRootDialogState();
        }
    }

    private async void ChangeLibraryRootDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (string.IsNullOrWhiteSpace(LibraryRootBox.Text))
            {
                RootErrorBar.IsOpen = true;
                RootErrorBar.Message = "Library root is required.";
                args.Cancel = true;
                UpdateRootDialogState();
                return;
            }

            localErrorMessage = null;
            await viewModel.ChangeLibraryRootAsync(LibraryRootBox.Text);
            args.Cancel = viewModel.HasRunError;
            UpdateRootDialogState();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ChangeLibraryRootDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        isDisposed = true;
    }

    private void UpdateRootDialogState()
    {
        var state = LibraryRootDialogState.Create(
            LibraryRootBox.Text,
            viewModel.CanChangeLibraryRoot,
            viewModel.HasRunError,
            viewModel.RunErrorMessage,
            localErrorMessage);

        IsPrimaryButtonEnabled = state.IsPrimaryButtonEnabled;
        RootErrorBar.IsOpen = state.IsErrorOpen;
        RootErrorBar.Message = state.ErrorMessage;
    }
}

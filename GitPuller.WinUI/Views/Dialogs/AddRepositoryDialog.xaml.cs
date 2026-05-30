using System.ComponentModel;
using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Views.Dialogs;

public sealed partial class AddRepositoryDialog : ContentDialog, IDisposable
{
    private readonly MainShellViewModel viewModel;
    private bool isDisposed;

    public AddRepositoryDialog(MainShellViewModel viewModel, XamlRoot xamlRoot)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        XamlRoot = xamlRoot;

        UrlBox.Text = viewModel.AddRepositoryUrl;
        CategoryBox.ItemsSource = CreateCategoryNames();
        CategoryBox.SelectedItem = ((string[])CategoryBox.ItemsSource).FirstOrDefault(category =>
            string.Equals(category, viewModel.AddRepositoryCategoryName, StringComparison.OrdinalIgnoreCase));
        FolderBox.Text = viewModel.AddRepositoryFolderName;
        CurrentRootText.Text = viewModel.LibraryRoot;
        ToolTipService.SetToolTip(
            FolderHelpIcon,
            "Optional local folder name override. Leave it empty to derive the folder from the repository URL; it does not change the category or remote repository.");

        UrlBox.TextChanged += UrlBox_TextChanged;
        CategoryBox.SelectionChanged += CategoryBox_SelectionChanged;
        NewCategoryButton.Click += NewCategoryButton_Click;
        NewCategoryBox.TextChanged += NewCategoryBox_TextChanged;
        FolderBox.TextChanged += FolderBox_TextChanged;
        PrimaryButtonClick += AddRepositoryDialog_PrimaryButtonClick;
        Closed += AddRepositoryDialog_Closed;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;

        UpdateDialogState();
    }

    private string[] CreateCategoryNames()
    {
        return viewModel.Categories
            .Select(category => category.Name)
            .Concat(string.IsNullOrWhiteSpace(viewModel.AddRepositoryCategoryName)
                ? []
                : [viewModel.AddRepositoryCategoryName])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        viewModel.AddRepositoryUrl = UrlBox.Text;
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is string selectedCategory)
        {
            viewModel.AddRepositoryCategoryName = selectedCategory;
            NewCategoryBox.Visibility = Visibility.Collapsed;
        }
    }

    private void NewCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        CategoryBox.SelectedItem = null;
        NewCategoryBox.Visibility = Visibility.Visible;
        NewCategoryBox.Focus(FocusState.Programmatic);
        viewModel.AddRepositoryCategoryName = NewCategoryBox.Text;
    }

    private void NewCategoryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NewCategoryBox.Visibility == Visibility.Visible)
        {
            viewModel.AddRepositoryCategoryName = NewCategoryBox.Text;
        }
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        viewModel.AddRepositoryFolderName = FolderBox.Text;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainShellViewModel.CanCloneRepository)
            or nameof(MainShellViewModel.AddRepositoryTargetPathPreview)
            or nameof(MainShellViewModel.AddRepositoryDiagnosticTitle)
            or nameof(MainShellViewModel.AddRepositoryDiagnosticExplanation)
            or nameof(MainShellViewModel.AddRepositoryDiagnosticEvidence)
            or nameof(MainShellViewModel.HasAddRepositoryError)
            or nameof(MainShellViewModel.AddRepositoryErrorMessage))
        {
            UpdateDialogState();
        }
    }

    private async void AddRepositoryDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await viewModel.CloneRepositoryAsync();
            args.Cancel = viewModel.HasAddRepositoryError;
            UpdateDialogState();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void AddRepositoryDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
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

    private void UpdateDialogState()
    {
        IsPrimaryButtonEnabled = viewModel.CanCloneRepository;
        PreviewText.Text = viewModel.AddRepositoryTargetPathPreview;
        DiagnosticTitleText.Text = viewModel.AddRepositoryDiagnosticTitle;
        DiagnosticExplanationText.Text = viewModel.AddRepositoryDiagnosticExplanation;
        DiagnosticEvidenceText.Text = viewModel.AddRepositoryDiagnosticEvidence;
        ErrorBar.IsOpen = viewModel.HasAddRepositoryError;
        ErrorBar.Message = viewModel.AddRepositoryErrorMessage;
    }
}

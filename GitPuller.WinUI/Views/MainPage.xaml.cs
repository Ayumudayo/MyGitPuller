using System.ComponentModel;
using GitPuller_WinUI.Services;
using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Views;

public sealed partial class MainPage : Page
{
    private bool suppressFolderTreeSelectionChanged;
    private readonly long isPaneOpenCallbackToken;
    private readonly long paneDisplayModeCallbackToken;

    public MainShellViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = MainShellViewModel.CreateDefault(
            new DispatcherQueueViewModelDispatcher(DispatcherQueue.GetForCurrentThread()));

        InitializeComponent();
        isPaneOpenCallbackToken = CategoryNavigation.RegisterPropertyChangedCallback(
            NavigationView.IsPaneOpenProperty,
            (_, _) => UpdatePaneContentVisibility());
        paneDisplayModeCallbackToken = CategoryNavigation.RegisterPropertyChangedCallback(
            NavigationView.PaneDisplayModeProperty,
            (_, _) => UpdatePaneContentVisibility());

        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        RebuildFolderTree();
        UpdateRetryButtonVisibility();
        UpdatePaneContentVisibility();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        UpdatePaneContentVisibility();
        await ViewModel.InitializeAsync();
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        CategoryNavigation.UnregisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, isPaneOpenCallbackToken);
        CategoryNavigation.UnregisterPropertyChangedCallback(NavigationView.PaneDisplayModeProperty, paneDisplayModeCallbackToken);
        Unloaded -= MainPage_Unloaded;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainShellViewModel.HasSelectedResult)
            or nameof(MainShellViewModel.IsSelectedResultRetryPrimary)
            or nameof(MainShellViewModel.IsSelectedResultRetrySecondary)
            or nameof(MainShellViewModel.SelectedResultCanRetry))
        {
            UpdateRetryButtonVisibility();
        }

        if (e.PropertyName is nameof(MainShellViewModel.RepositoryTreeNodes))
        {
            RebuildFolderTree();
        }

        if (e.PropertyName is nameof(MainShellViewModel.SelectedFolderNode))
        {
            SynchronizeSelectedFolderNode();
        }
    }

    private void UpdateRetryButtonVisibility()
    {
        if (PrimaryRetrySelectedDetailButton is null || SecondaryRetrySelectedDetailButton is null)
        {
            return;
        }

        var showPrimary = ViewModel.HasSelectedResult && ViewModel.IsSelectedResultRetryPrimary;
        var showSecondary = ViewModel.HasSelectedResult && !ViewModel.IsSelectedResultRetryPrimary;

        PrimaryRetrySelectedDetailButton.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryRetrySelectedDetailButton.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePaneContentVisibility()
    {
        if (PaneHeaderHost is null || PaneContentHost is null || CategoryNavigation is null)
        {
            return;
        }

        var shouldShowPaneContent = CategoryNavigation.IsPaneOpen
            || CategoryNavigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Left;
        var visibility = shouldShowPaneContent ? Visibility.Visible : Visibility.Collapsed;

        PaneHeaderHost.Visibility = visibility;
        PaneContentHost.Visibility = visibility;
    }

    private async void AddRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginAddRepository(ViewModel.RepositoryUrlToAdd, ViewModel.SelectedCategory?.Name);
        await ShowAddRepositoryDialogAsync();
    }

    private void FolderTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (suppressFolderTreeSelectionChanged)
        {
            return;
        }

        ViewModel.SelectedFolderNode = sender.SelectedNode?.Content as RepositoryFolderNodeViewModel;
    }

    private async Task ShowAddRepositoryDialogAsync()
    {
        var libraryRootBox = new TextBox
        {
            Header = "Library root",
            IsReadOnly = true,
            Text = ViewModel.LibraryRoot,
            TextWrapping = TextWrapping.Wrap
        };
        var urlBox = new TextBox
        {
            Header = "Clone URL",
            PlaceholderText = "https://github.com/owner/repository.git",
            Text = ViewModel.AddRepositoryUrl,
            TextWrapping = TextWrapping.Wrap
        };
        var categoryBox = new TextBox
        {
            Header = "Category",
            PlaceholderText = "Plugins",
            Text = ViewModel.AddRepositoryCategoryName,
            TextWrapping = TextWrapping.Wrap
        };
        var folderBox = new TextBox
        {
            Header = "Folder name",
            PlaceholderText = "repository-folder",
            Text = ViewModel.AddRepositoryFolderName,
            TextWrapping = TextWrapping.Wrap
        };
        var previewText = new TextBlock
        {
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var diagnosticTitle = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var diagnosticExplanation = new TextBlock
        {
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var diagnosticEvidence = new TextBlock
        {
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var errorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "Clone failed"
        };

        var content = new ScrollViewer
        {
            MaxHeight = 640,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    libraryRootBox,
                    categoryBox,
                    urlBox,
                    folderBox,
                    new TextBlock
                    {
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Text = "Target path preview"
                    },
                    previewText,
                    diagnosticTitle,
                    diagnosticExplanation,
                    diagnosticEvidence,
                    errorBar
                }
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add repository from URL",
            PrimaryButtonText = "Clone",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };

        void UpdateDialogState()
        {
            dialog.IsPrimaryButtonEnabled = ViewModel.CanCloneRepository;
            previewText.Text = ViewModel.AddRepositoryTargetPathPreview;
            diagnosticTitle.Text = ViewModel.AddRepositoryDiagnosticTitle;
            diagnosticExplanation.Text = ViewModel.AddRepositoryDiagnosticExplanation;
            diagnosticEvidence.Text = ViewModel.AddRepositoryDiagnosticEvidence;
            errorBar.IsOpen = ViewModel.HasAddRepositoryError;
            errorBar.Message = ViewModel.AddRepositoryErrorMessage;
        }

        PropertyChangedEventHandler propertyChanged = (_, args) =>
        {
            if (args.PropertyName is nameof(MainShellViewModel.CanCloneRepository)
                or nameof(MainShellViewModel.AddRepositoryTargetPathPreview)
                or nameof(MainShellViewModel.AddRepositoryDiagnosticTitle)
                or nameof(MainShellViewModel.AddRepositoryDiagnosticExplanation)
                or nameof(MainShellViewModel.AddRepositoryDiagnosticEvidence)
                or nameof(MainShellViewModel.HasAddRepositoryError)
                or nameof(MainShellViewModel.AddRepositoryErrorMessage))
            {
                UpdateDialogState();
            }
        };

        urlBox.TextChanged += (_, _) => ViewModel.AddRepositoryUrl = urlBox.Text;
        categoryBox.TextChanged += (_, _) => ViewModel.AddRepositoryCategoryName = categoryBox.Text;
        folderBox.TextChanged += (_, _) => ViewModel.AddRepositoryFolderName = folderBox.Text;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await ViewModel.CloneRepositoryAsync();
                args.Cancel = ViewModel.HasAddRepositoryError;
                UpdateDialogState();
            }
            finally
            {
                deferral.Complete();
            }
        };

        ViewModel.PropertyChanged += propertyChanged;
        try
        {
            UpdateDialogState();
            await dialog.ShowAsync();
        }
        finally
        {
            ViewModel.PropertyChanged -= propertyChanged;
        }
    }

    private void AdvancedWorkersNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        ViewModel.AdvancedWorkers = NormalizeNumberBoxValue(args.NewValue);
    }

    private void AdvancedTimeoutNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        ViewModel.AdvancedTimeoutSeconds = NormalizeNumberBoxValue(args.NewValue);
    }

    private void AdvancedStaleLockNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        ViewModel.AdvancedStaleLockMinutes = NormalizeNumberBoxValue(args.NewValue);
    }

    private async void RestoreRemovedRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RestoreRemovedRepositoryAsync(GetRemovedRepository(sender));
    }

    private async void RestoreRemovedRepositoryAsButton_Click(object sender, RoutedEventArgs e)
    {
        var removedRepository = GetRemovedRepository(sender);
        if (removedRepository is null)
        {
            return;
        }

        await ShowRestoreRemovedRepositoryAsDialogAsync(removedRepository);
    }

    private async Task ShowRestoreRemovedRepositoryAsDialogAsync(RemovedRepositoryViewModel removedRepository)
    {
        var categoryBox = new TextBox
        {
            Header = "Category",
            Text = removedRepository.Record.Category,
            TextWrapping = TextWrapping.Wrap
        };
        var folderBox = new TextBox
        {
            Header = "Folder name",
            Text = removedRepository.Name,
            TextWrapping = TextWrapping.Wrap
        };
        var errorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "Restore failed",
            IsOpen = ViewModel.HasRemovedRepositoryError,
            Message = ViewModel.RemovedRepositoryErrorMessage
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Restore {removedRepository.Name} as",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    categoryBox,
                    folderBox,
                    errorBar
                }
            }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await ViewModel.RestoreRemovedRepositoryAsAsync(
                    removedRepository,
                    categoryBox.Text,
                    folderBox.Text);
                args.Cancel = ViewModel.HasRemovedRepositoryError;
                errorBar.IsOpen = ViewModel.HasRemovedRepositoryError;
                errorBar.Message = ViewModel.RemovedRepositoryErrorMessage;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private async void OpenRemovedRepositoryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.OpenRemovedFolderAsync(GetRemovedRepository(sender));
    }

    private async void OpenRemovedRepositoryOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.OpenRemovedOriginalFolderAsync(GetRemovedRepository(sender));
    }

    private async void OpenRemovedRepositoryRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.OpenRemovedRemoteAsync(GetRemovedRepository(sender));
    }

    private async void PermanentlyDeleteRemovedRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        var removedRepository = GetRemovedRepository(sender);
        if (removedRepository is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Permanently delete {removedRepository.Name}?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = removedRepository.RemovedPath,
                TextWrapping = TextWrapping.WrapWholeWords
            }
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.PermanentlyDeleteRemovedRepositoryAsync(removedRepository);
        }
    }

    private static RemovedRepositoryViewModel? GetRemovedRepository(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as RemovedRepositoryViewModel;
    }

    private static int NormalizeNumberBoxValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Round(value));
    }

    private void RebuildFolderTree()
    {
        if (FolderTreeView is null)
        {
            return;
        }

        var expandedFolderNames = GetExpandedFolderNames();
        var hasPriorExpansionState = FolderTreeView.RootNodes.Count > 0;

        suppressFolderTreeSelectionChanged = true;
        try
        {
            FolderTreeView.RootNodes.Clear();
            foreach (var rootNode in ViewModel.RepositoryTreeNodes)
            {
                FolderTreeView.RootNodes.Add(CreateTreeNode(
                    rootNode,
                    expandedFolderNames,
                    hasPriorExpansionState));
            }

            SynchronizeSelectedFolderNode();
        }
        finally
        {
            suppressFolderTreeSelectionChanged = false;
        }
    }

    private void SynchronizeSelectedFolderNode()
    {
        if (FolderTreeView is null)
        {
            return;
        }

        suppressFolderTreeSelectionChanged = true;
        try
        {
            FolderTreeView.SelectedNode = FindSelectedTreeNode(ViewModel.SelectedFolderNode);
        }
        finally
        {
            suppressFolderTreeSelectionChanged = false;
        }
    }

    private HashSet<string> GetExpandedFolderNames()
    {
        var expandedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootNode in FolderTreeView.RootNodes)
        {
            CollectExpandedFolderNames(rootNode, expandedFolderNames);
        }

        return expandedFolderNames;
    }

    private static void CollectExpandedFolderNames(TreeViewNode treeNode, ISet<string> expandedFolderNames)
    {
        if (treeNode.Content is RepositoryFolderNodeViewModel folderNode
            && treeNode.IsExpanded
            && !folderNode.IsAllRepositories)
        {
            expandedFolderNames.Add(folderNode.FullCategoryName);
        }

        foreach (var childNode in treeNode.Children)
        {
            CollectExpandedFolderNames(childNode, expandedFolderNames);
        }
    }

    private static TreeViewNode CreateTreeNode(
        RepositoryFolderNodeViewModel folderNode,
        IReadOnlySet<string> expandedFolderNames,
        bool hasPriorExpansionState)
    {
        var hasChildren = folderNode.Children.Count > 0;
        var treeNode = new TreeViewNode
        {
            Content = folderNode,
            IsExpanded = hasChildren
                && (!hasPriorExpansionState || expandedFolderNames.Contains(folderNode.FullCategoryName))
        };

        foreach (var child in folderNode.Children)
        {
            treeNode.Children.Add(CreateTreeNode(child, expandedFolderNames, hasPriorExpansionState));
        }

        return treeNode;
    }

    private TreeViewNode? FindSelectedTreeNode(RepositoryFolderNodeViewModel? selectedFolderNode)
    {
        foreach (var rootNode in FolderTreeView.RootNodes)
        {
            var match = FindSelectedTreeNodeRecursive(rootNode, selectedFolderNode);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static TreeViewNode? FindSelectedTreeNodeRecursive(
        TreeViewNode treeNode,
        RepositoryFolderNodeViewModel? selectedFolderNode)
    {
        if (treeNode.Content is RepositoryFolderNodeViewModel folderNode
            && selectedFolderNode is not null
            && string.Equals(folderNode.FullCategoryName, selectedFolderNode.FullCategoryName, StringComparison.OrdinalIgnoreCase)
            && folderNode.IsAllRepositories == selectedFolderNode.IsAllRepositories)
        {
            return treeNode;
        }

        foreach (var childNode in treeNode.Children)
        {
            var match = FindSelectedTreeNodeRecursive(childNode, selectedFolderNode);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private sealed class DispatcherQueueViewModelDispatcher : IViewModelDispatcher
    {
        private readonly DispatcherQueue dispatcherQueue;

        public DispatcherQueueViewModelDispatcher(DispatcherQueue dispatcherQueue)
        {
            this.dispatcherQueue = dispatcherQueue;
        }

        public void Enqueue(Action action)
        {
            if (dispatcherQueue.HasThreadAccess)
            {
                action();
                return;
            }

            dispatcherQueue.TryEnqueue(() => action());
        }
    }
}

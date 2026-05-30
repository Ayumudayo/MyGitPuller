using System.Collections.Specialized;
using System.ComponentModel;
using Windows.Foundation;
using Windows.Storage.Pickers;
using GitPuller_WinUI.Services;
using GitPuller_WinUI.ViewModels;
using GitPuller_WinUI.Views.Dialogs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace GitPuller_WinUI.Views;

public sealed partial class MainPage : Page
{
    private bool suppressFolderTreeSelectionChanged;
    private PaneResizeTarget? activePaneResizeTarget;
    private ContentDialog? activeRemovedRepositoriesDialog;
    private Point lastResizePointerPoint;

    public MainShellViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = MainShellViewModel.CreateDefault(
            new DispatcherQueueViewModelDispatcher(DispatcherQueue.GetForCurrentThread()));

        InitializeComponent();
        DataContext = ViewModel;

        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        RebuildFolderTree();
        UpdateRetryButtonVisibility();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        await ViewModel.InitializeAsync();
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
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

        if (e.PropertyName is nameof(MainShellViewModel.SelectedFolderNode)
            or nameof(MainShellViewModel.SelectedTreeNode))
        {
            SynchronizeSelectedFolderNode();
        }
    }

    private void UpdateRetryButtonVisibility()
    {
        if (PrimaryRetrySelectedDetailButton is null
            || SecondaryRetrySelectedDetailButton is null
            || PrimaryRetrySelectedDetailButtonHost is null
            || SecondaryRetrySelectedDetailButtonHost is null
            || PrimaryRetrySelectedDetailToolTipTarget is null
            || SecondaryRetrySelectedDetailToolTipTarget is null)
        {
            return;
        }

        var showPrimary = ViewModel.HasSelectedResult && ViewModel.IsSelectedResultRetryPrimary;
        var showSecondary = ViewModel.HasSelectedResult && !showPrimary;

        PrimaryRetrySelectedDetailButtonHost.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryRetrySelectedDetailButtonHost.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
        PrimaryRetrySelectedDetailButton.IsEnabled = ViewModel.SelectedResultCanRetry;
        SecondaryRetrySelectedDetailButton.IsEnabled = ViewModel.SelectedResultCanRetry;
        PrimaryRetrySelectedDetailToolTipTarget.Visibility = ViewModel.SelectedResultCanRetry
            ? Visibility.Collapsed
            : Visibility.Visible;
        SecondaryRetrySelectedDetailToolTipTarget.Visibility = ViewModel.SelectedResultCanRetry
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void AddRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginAddRepository(ViewModel.RepositoryUrlToAdd, ViewModel.SelectedCategory?.Name);
        await ShowAddRepositoryDialogAsync();
    }

    private async void ChangeLibraryRootButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowChangeLibraryRootDialogAsync();
    }

    private void AllFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedResultFilter = RepositoryResultFilter.All;
    }

    private void FailedFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedResultFilter = RepositoryResultFilter.Failed;
    }

    private void WarningFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedResultFilter = RepositoryResultFilter.Warning;
    }

    private void UpdatedFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedResultFilter = RepositoryResultFilter.Updated;
    }

    private void CleanFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedResultFilter = RepositoryResultFilter.Clean;
    }

    private void PaneResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement handle || !TryGetPaneResizeTarget(handle, out var target))
        {
            return;
        }

        activePaneResizeTarget = target;
        lastResizePointerPoint = e.GetCurrentPoint(MockupShellRoot).Position;
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PaneResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (activePaneResizeTarget is null)
        {
            return;
        }

        var currentPoint = e.GetCurrentPoint(MockupShellRoot).Position;
        var horizontalChange = currentPoint.X - lastResizePointerPoint.X;
        var verticalChange = currentPoint.Y - lastResizePointerPoint.Y;
        lastResizePointerPoint = currentPoint;

        switch (activePaneResizeTarget)
        {
            case PaneResizeTarget.Sidebar:
                ResizeSidebar(horizontalChange);
                break;
            case PaneResizeTarget.Details:
                ResizeDetails(verticalChange);
                break;
            case PaneResizeTarget.DetailsColumn:
                ResizeDetailsColumns(horizontalChange);
                break;
        }

        e.Handled = true;
    }

    private void PaneResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement handle)
        {
            handle.ReleasePointerCapture(e.Pointer);
        }

        activePaneResizeTarget = null;
        e.Handled = true;
    }

    private void ResizeSidebar(double horizontalChange)
    {
        var requestedWidth = SidebarColumn.ActualWidth + horizontalChange;
        SidebarColumn.Width = new GridLength(Clamp(
            requestedWidth,
            SidebarColumn.MinWidth,
            SidebarColumn.MaxWidth));
    }

    private void ResizeDetails(double verticalChange)
    {
        var requestedHeight = DetailsRow.ActualHeight - verticalChange;
        DetailsRow.Height = new GridLength(Clamp(
            requestedHeight,
            DetailsRow.MinHeight,
            DetailsRow.MaxHeight));
    }

    private void ResizeDetailsColumns(double horizontalChange)
    {
        var totalWidth = DetailsSplitGrid.ActualWidth;
        if (totalWidth <= 0)
        {
            return;
        }

        var minimumRightWidth = DetailsRepositoryInfoColumn.MinWidth;
        var maximumLeftWidth = Math.Max(DetailsDiagnosticsColumn.MinWidth, totalWidth - minimumRightWidth);
        var requestedLeftWidth = DetailsDiagnosticsColumn.ActualWidth + horizontalChange;
        var leftWidth = Clamp(requestedLeftWidth, DetailsDiagnosticsColumn.MinWidth, maximumLeftWidth);
        var rightWidth = Math.Max(minimumRightWidth, totalWidth - leftWidth);

        DetailsDiagnosticsColumn.Width = new GridLength(leftWidth);
        DetailsRepositoryInfoColumn.Width = new GridLength(rightWidth);
    }

    private static bool TryGetPaneResizeTarget(object sender, out PaneResizeTarget target)
    {
        target = PaneResizeTarget.Sidebar;
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return false;
        }

        return Enum.TryParse(tag, ignoreCase: true, out target);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return minimum;
        }

        return Math.Min(Math.Max(value, minimum), maximum);
    }

    private enum PaneResizeTarget
    {
        Sidebar,
        Details,
        DetailsColumn
    }

    private void FolderTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (suppressFolderTreeSelectionChanged)
        {
            return;
        }

        ViewModel.SelectedTreeNode = sender.SelectedNode?.Content as RepositoryTreeNodeViewModel;
    }

    private async Task ShowAddRepositoryDialogAsync()
    {
        using var dialog = new AddRepositoryDialog(ViewModel, XamlRoot);
        await dialog.ShowAsync();
    }

    private async Task ShowChangeLibraryRootDialogAsync()
    {
        using var dialog = new ChangeLibraryRootDialog(ViewModel, XamlRoot, PickLibraryRootAsync);
        await dialog.ShowAsync();
    }

    private async Task<string?> PickLibraryRootAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");

        if (MainWindow.ActiveWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(MainWindow.ActiveWindow));
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void AdvancedOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowAdvancedOptionsDialogAsync();
    }

    private async Task ShowAdvancedOptionsDialogAsync()
    {
        using var dialog = new AdvancedOptionsDialog(ViewModel, XamlRoot);
        await dialog.ShowAsync();
    }

    private async void RemovedRepositoriesButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowRemovedRepositoriesDialogAsync();
    }

    private async Task ShowRemovedRepositoriesDialogAsync()
    {
        var emptyMessage = new TextBlock
        {
            Text = "No removed repositories are waiting for restore or permanent deletion.",
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var listView = new ListView
        {
            Name = "RemovedRepositoryDialogList",
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource = ViewModel.RemovedRepositories,
            ItemTemplate = (DataTemplate)Resources["RemovedRepositoryDialogItemTemplate"]
        };
        var dialogContent = new Grid
        {
            Children =
            {
                emptyMessage,
                listView
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Removed repositories",
            CloseButtonText = "Close",
            Content = new ScrollViewer
            {
                MaxHeight = 640,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = dialogContent
            }
        };

        void UpdateRemovedRepositoryDialogState()
        {
            var hasRemovedRepositories = ViewModel.RemovedRepositories.Count > 0;
            emptyMessage.Visibility = hasRemovedRepositories ? Visibility.Collapsed : Visibility.Visible;
            listView.Visibility = hasRemovedRepositories ? Visibility.Visible : Visibility.Collapsed;
        }

        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => UpdateRemovedRepositoryDialogState();
        ViewModel.RemovedRepositories.CollectionChanged += collectionChanged;
        activeRemovedRepositoriesDialog = dialog;
        try
        {
            UpdateRemovedRepositoryDialogState();
            await dialog.ShowAsync();
        }
        finally
        {
            ViewModel.RemovedRepositories.CollectionChanged -= collectionChanged;
            if (ReferenceEquals(activeRemovedRepositoriesDialog, dialog))
            {
                activeRemovedRepositoriesDialog = null;
            }
        }
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

        activeRemovedRepositoriesDialog?.Hide();
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

        activeRemovedRepositoriesDialog?.Hide();
        await ConfirmPermanentDeleteRemovedRepositoryAsync(removedRepository);
    }

    private async Task ConfirmPermanentDeleteRemovedRepositoryAsync(RemovedRepositoryViewModel removedRepository)
    {
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
            FolderTreeView.SelectedNode = FindSelectedTreeNode(ViewModel.SelectedTreeNode ?? ViewModel.SelectedFolderNode);
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
        if (treeNode.Content is RepositoryTreeNodeViewModel folderNode
            && folderNode.IsFolder
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
        RepositoryTreeNodeViewModel folderNode,
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

    private TreeViewNode? FindSelectedTreeNode(RepositoryTreeNodeViewModel? selectedTreeNode)
    {
        foreach (var rootNode in FolderTreeView.RootNodes)
        {
            var match = FindSelectedTreeNodeRecursive(rootNode, selectedTreeNode);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static TreeViewNode? FindSelectedTreeNodeRecursive(
        TreeViewNode treeNode,
        RepositoryTreeNodeViewModel? selectedTreeNode)
    {
        if (treeNode.Content is RepositoryTreeNodeViewModel treeNodeViewModel
            && selectedTreeNode is not null
            && treeNodeViewModel.Kind == selectedTreeNode.Kind
            && treeNodeViewModel.IsAllRepositories == selectedTreeNode.IsAllRepositories
            && (treeNodeViewModel.IsRepository
                ? string.Equals(treeNodeViewModel.FullPath, selectedTreeNode.FullPath, StringComparison.OrdinalIgnoreCase)
                : string.Equals(treeNodeViewModel.FullCategoryName, selectedTreeNode.FullCategoryName, StringComparison.OrdinalIgnoreCase)))
        {
            return treeNode;
        }

        foreach (var childNode in treeNode.Children)
        {
            var match = FindSelectedTreeNodeRecursive(childNode, selectedTreeNode);
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

        public Task EnqueueAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
            {
                completion.SetException(new InvalidOperationException("Failed to enqueue view-model action."));
            }

            return completion.Task;
        }
    }
}

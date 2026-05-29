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
        if (PrimaryRetrySelectedDetailButton is null || SecondaryRetrySelectedDetailButton is null)
        {
            return;
        }

        var showPrimary = ViewModel.HasSelectedResult && ViewModel.IsSelectedResultRetryPrimary;
        var showSecondary = ViewModel.HasSelectedResult && !ViewModel.IsSelectedResultRetryPrimary;

        PrimaryRetrySelectedDetailButton.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryRetrySelectedDetailButton.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginAddRepository(ViewModel.RepositoryUrlToAdd, ViewModel.SelectedCategory?.Name);
        await ShowAddRepositoryDialogAsync();
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

    private async void AdvancedOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowAdvancedOptionsDialogAsync();
    }

    private async Task ShowAdvancedOptionsDialogAsync()
    {
        var workersBox = new NumberBox
        {
            Header = "Workers",
            Minimum = 1,
            Maximum = 64,
            Value = ViewModel.AdvancedWorkers
        };
        var timeoutBox = new NumberBox
        {
            Header = "Git timeout seconds",
            Minimum = 1,
            Maximum = 3600,
            Value = ViewModel.AdvancedTimeoutSeconds
        };
        var staleLockBox = new NumberBox
        {
            Header = "Stale lock minutes",
            Minimum = 1,
            Maximum = 1440,
            Value = ViewModel.AdvancedStaleLockMinutes
        };
        var syncAllBranchesBox = new CheckBox
        {
            Content = "Sync all accessible branches",
            IsChecked = ViewModel.AdvancedSyncAllBranches
        };
        var staleLockCleanupBox = new CheckBox
        {
            Content = "Disable stale lock cleanup",
            IsChecked = ViewModel.AdvancedNoStaleLockCleanup
        };
        var verboseReportBox = new CheckBox
        {
            Content = "Verbose report",
            IsChecked = ViewModel.AdvancedVerboseReport
        };
        var initMissingSubmodulesBox = new CheckBox
        {
            Content = "Initialize missing submodules",
            IsChecked = ViewModel.AdvancedInitMissingSubmodules
        };
        var statusBar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = "Options",
            IsOpen = ViewModel.HasAdvancedOptionsStatus,
            Message = ViewModel.AdvancedOptionsStatusMessage
        };
        var errorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "Save failed",
            IsOpen = ViewModel.HasAdvancedOptionsError,
            Message = ViewModel.AdvancedOptionsErrorMessage
        };

        workersBox.ValueChanged += AdvancedWorkersNumberBox_ValueChanged;
        timeoutBox.ValueChanged += AdvancedTimeoutNumberBox_ValueChanged;
        staleLockBox.ValueChanged += AdvancedStaleLockNumberBox_ValueChanged;
        syncAllBranchesBox.Checked += (_, _) => ViewModel.AdvancedSyncAllBranches = true;
        syncAllBranchesBox.Unchecked += (_, _) => ViewModel.AdvancedSyncAllBranches = false;
        staleLockCleanupBox.Checked += (_, _) => ViewModel.AdvancedNoStaleLockCleanup = true;
        staleLockCleanupBox.Unchecked += (_, _) => ViewModel.AdvancedNoStaleLockCleanup = false;
        verboseReportBox.Checked += (_, _) => ViewModel.AdvancedVerboseReport = true;
        verboseReportBox.Unchecked += (_, _) => ViewModel.AdvancedVerboseReport = false;
        initMissingSubmodulesBox.Checked += (_, _) => ViewModel.AdvancedInitMissingSubmodules = true;
        initMissingSubmodulesBox.Unchecked += (_, _) => ViewModel.AdvancedInitMissingSubmodules = false;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Advanced sync options",
            PrimaryButtonText = "Save defaults",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                MaxHeight = 640,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        workersBox,
                        timeoutBox,
                        staleLockBox,
                        syncAllBranchesBox,
                        staleLockCleanupBox,
                        verboseReportBox,
                        initMissingSubmodulesBox,
                        statusBar,
                        errorBar
                    }
                }
            }
        };

        void UpdateDialogState()
        {
            dialog.IsPrimaryButtonEnabled = ViewModel.CanSaveAdvancedOptions;
            statusBar.IsOpen = ViewModel.HasAdvancedOptionsStatus;
            statusBar.Message = ViewModel.AdvancedOptionsStatusMessage;
            errorBar.IsOpen = ViewModel.HasAdvancedOptionsError;
            errorBar.Message = ViewModel.AdvancedOptionsErrorMessage;
        }

        PropertyChangedEventHandler propertyChanged = (_, args) =>
        {
            if (args.PropertyName is nameof(MainShellViewModel.CanSaveAdvancedOptions)
                or nameof(MainShellViewModel.AdvancedOptionsStatusMessage)
                or nameof(MainShellViewModel.AdvancedOptionsErrorMessage)
                or nameof(MainShellViewModel.HasAdvancedOptionsStatus)
                or nameof(MainShellViewModel.HasAdvancedOptionsError))
            {
                UpdateDialogState();
            }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await ViewModel.SaveAdvancedOptionsAsync();
                args.Cancel = ViewModel.HasAdvancedOptionsError;
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
            workersBox.ValueChanged -= AdvancedWorkersNumberBox_ValueChanged;
            timeoutBox.ValueChanged -= AdvancedTimeoutNumberBox_ValueChanged;
            staleLockBox.ValueChanged -= AdvancedStaleLockNumberBox_ValueChanged;
        }
    }

    private async void RemovedRepositoriesButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowRemovedRepositoriesDialogAsync();
    }

    private async Task ShowRemovedRepositoriesDialogAsync()
    {
        var listPanel = new StackPanel
        {
            Spacing = 10
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
                Content = listPanel
            }
        };

        if (ViewModel.RemovedRepositories.Count == 0)
        {
            listPanel.Children.Add(new TextBlock
            {
                Text = "No removed repositories are waiting for restore or permanent deletion.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }
        else
        {
            foreach (var removedRepository in ViewModel.RemovedRepositories)
            {
                listPanel.Children.Add(CreateRemovedRepositoryRow(removedRepository, dialog));
            }
        }

        await dialog.ShowAsync();
    }

    private FrameworkElement CreateRemovedRepositoryRow(
        RemovedRepositoryViewModel removedRepository,
        ContentDialog ownerDialog)
    {
        var nameText = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = removedRepository.Name,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var pathText = new TextBlock
        {
            Text = removedRepository.RemovedPath,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var categoryText = new TextBlock
        {
            Text = removedRepository.Record.Category,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var restoreButton = new Button
        {
            Content = "Restore",
            IsEnabled = removedRepository.CanRestore
        };
        var restoreAsButton = new Button
        {
            Content = "Restore as"
        };
        var openButton = new Button
        {
            Content = "Open folder"
        };
        var deleteButton = new Button
        {
            Content = "Delete"
        };

        restoreButton.Click += async (_, _) => await ViewModel.RestoreRemovedRepositoryAsync(removedRepository);
        restoreAsButton.Click += async (_, _) =>
        {
            ownerDialog.Hide();
            await ShowRestoreRemovedRepositoryAsDialogAsync(removedRepository);
        };
        openButton.Click += async (_, _) => await ViewModel.OpenRemovedFolderAsync(removedRepository);
        deleteButton.Click += async (_, _) =>
        {
            ownerDialog.Hide();
            await ConfirmPermanentDeleteRemovedRepositoryAsync(removedRepository);
        };

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                restoreButton,
                restoreAsButton,
                openButton,
                deleteButton
            }
        };

        return new Border
        {
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    nameText,
                    categoryText,
                    pathText,
                    actionPanel
                }
            }
        };
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

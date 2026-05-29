using System.ComponentModel;
using Windows.Foundation;
using Windows.Storage.Pickers;
using GitPuller_WinUI.Services;
using GitPuller_WinUI.ViewModels;
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
        if (PrimaryRetrySelectedDetailButton is null || SecondaryRetrySelectedDetailButton is null)
        {
            return;
        }

        var showPrimary = ViewModel.HasSelectedResult && ViewModel.SelectedResultCanRetry && ViewModel.IsSelectedResultRetryPrimary;
        var showSecondary = ViewModel.HasSelectedResult && ViewModel.SelectedResultCanRetry && ViewModel.IsSelectedResultRetrySecondary;

        PrimaryRetrySelectedDetailButton.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryRetrySelectedDetailButton.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
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
        var urlBox = new TextBox
        {
            Header = "Clone URL",
            PlaceholderText = "https://github.com/owner/repository.git",
            Text = ViewModel.AddRepositoryUrl,
            TextWrapping = TextWrapping.Wrap
        };

        var categoryNames = ViewModel.Categories
            .Select(category => category.Name)
            .Concat(string.IsNullOrWhiteSpace(ViewModel.AddRepositoryCategoryName)
                ? []
                : [ViewModel.AddRepositoryCategoryName])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var categoryBox = new ComboBox
        {
            Header = "Category",
            ItemsSource = categoryNames,
            PlaceholderText = "Choose category",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        categoryBox.SelectedItem = categoryNames.FirstOrDefault(category =>
            string.Equals(category, ViewModel.AddRepositoryCategoryName, StringComparison.OrdinalIgnoreCase));
        var newCategoryBox = new TextBox
        {
            PlaceholderText = "New category",
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var newCategoryButton = new Button
        {
            Content = "+ New",
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetColumn(newCategoryButton, 1);

        var folderHelpIcon = new FontIcon
        {
            FontSize = 12,
            Glyph = "\uE946"
        };
        ToolTipService.SetToolTip(
            folderHelpIcon,
            "Optional local folder name override. Leave it empty to derive the folder from the repository URL; it does not change the category or remote repository.");
        var folderBox = new TextBox
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Folder name" },
                    new Border
                    {
                        Padding = new Thickness(6, 1, 6, 2),
                        CornerRadius = new CornerRadius(8),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            FontSize = 11,
                            Text = "Optional"
                        }
                    },
                    folderHelpIcon
                }
            },
            PlaceholderText = "Leave empty to use the repository name",
            Text = ViewModel.AddRepositoryFolderName,
            TextWrapping = TextWrapping.Wrap
        };
        var previewText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        var diagnosticTitle = new TextBlock
        {
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var diagnosticExplanation = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        var diagnosticEvidence = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        var errorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "Clone failed"
        };
        var currentRootText = new TextBlock
        {
            Text = ViewModel.LibraryRoot,
            TextWrapping = TextWrapping.Wrap
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
                    urlBox,
                    new Grid
                    {
                        ColumnSpacing = 8,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                            new ColumnDefinition { Width = GridLength.Auto }
                        },
                        Children =
                        {
                            categoryBox,
                            newCategoryButton
                        }
                    },
                    newCategoryBox,
                    folderBox,
                    new TextBlock
                    {
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Text = "Target path preview"
                    },
                    previewText,
                    new TextBlock
                    {
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Text = "Current library root"
                    },
                    currentRootText,
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
            Title = "Add repository",
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
        categoryBox.SelectionChanged += (_, _) =>
        {
            if (categoryBox.SelectedItem is string selectedCategory)
            {
                ViewModel.AddRepositoryCategoryName = selectedCategory;
                newCategoryBox.Visibility = Visibility.Collapsed;
            }
        };
        newCategoryButton.Click += (_, _) =>
        {
            categoryBox.SelectedItem = null;
            newCategoryBox.Visibility = Visibility.Visible;
            newCategoryBox.Focus(FocusState.Programmatic);
            ViewModel.AddRepositoryCategoryName = newCategoryBox.Text;
        };
        newCategoryBox.TextChanged += (_, _) =>
        {
            if (newCategoryBox.Visibility == Visibility.Visible)
            {
                ViewModel.AddRepositoryCategoryName = newCategoryBox.Text;
            }
        };
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

    private async Task ShowChangeLibraryRootDialogAsync()
    {
        var rootBox = new TextBox
        {
            Header = "Library root",
            Text = ViewModel.LibraryRoot,
            TextWrapping = TextWrapping.Wrap
        };
        var recentRoots = ViewModel.RecentLibraryRoots.ToArray();
        var recentRootBox = new ComboBox
        {
            Header = "Recent roots",
            ItemsSource = recentRoots,
            PlaceholderText = "Select a recent library root",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        recentRootBox.SelectedItem = recentRoots.FirstOrDefault(root =>
            string.Equals(root, ViewModel.LibraryRoot, StringComparison.OrdinalIgnoreCase));
        var browseButton = new Button
        {
            Content = "Browse"
        };
        var rootErrorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "Library root error",
            IsOpen = false
        };
        browseButton.Click += async (_, _) =>
        {
            var selectedPath = await PickLibraryRootAsync();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                rootBox.Text = selectedPath;
            }
        };
        recentRootBox.SelectionChanged += (_, _) =>
        {
            if (recentRootBox.SelectedItem is string selectedRoot)
            {
                rootBox.Text = selectedRoot;
            }
        };

        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                recentRootBox,
                rootBox,
                rootErrorBar,
                browseButton
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Change library root",
            PrimaryButtonText = "Use root",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };

        void UpdateRootDialogState()
        {
            var hasBlankRoot = string.IsNullOrWhiteSpace(rootBox.Text);
            dialog.IsPrimaryButtonEnabled = ViewModel.CanChangeLibraryRoot
                && !hasBlankRoot;
            rootErrorBar.IsOpen = hasBlankRoot || ViewModel.HasRunError;
            rootErrorBar.Message = hasBlankRoot
                ? "Library root is required."
                : ViewModel.RunErrorMessage;
        }

        rootBox.TextChanged += (_, _) =>
        {
            UpdateRootDialogState();
        };

        PropertyChangedEventHandler propertyChanged = (_, args) =>
        {
            if (args.PropertyName is nameof(MainShellViewModel.CanChangeLibraryRoot)
                or nameof(MainShellViewModel.HasRunError)
                or nameof(MainShellViewModel.RunErrorMessage))
            {
                UpdateRootDialogState();
            }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (string.IsNullOrWhiteSpace(rootBox.Text))
                {
                    rootErrorBar.IsOpen = true;
                    rootErrorBar.Message = "Library root is required.";
                    args.Cancel = true;
                    UpdateRootDialogState();
                    return;
                }

                await ViewModel.ChangeLibraryRootAsync(rootBox.Text);
                args.Cancel = ViewModel.HasRunError;
                UpdateRootDialogState();
            }
            finally
            {
                deferral.Complete();
            }
        };

        ViewModel.PropertyChanged += propertyChanged;
        try
        {
            UpdateRootDialogState();
            await dialog.ShowAsync();
        }
        finally
        {
            ViewModel.PropertyChanged -= propertyChanged;
        }
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

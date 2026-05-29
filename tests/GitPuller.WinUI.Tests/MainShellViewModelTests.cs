using System.ComponentModel;
using GitPuller;
using GitPuller_WinUI.Services;
using GitPuller_WinUI.ViewModels;

namespace GitPuller.WinUI.Tests;

public sealed class MainShellViewModelTests
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "MyGitPullerWinUITests");
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public void VisibleResults_SortsFailedWarningUpdatedClean_WhenCleanRowsShown()
    {
        var viewModel = CreateViewModel(
            Result("clean", RepositoryResultStatus.Clean),
            Result("updated", RepositoryResultStatus.Updated),
            Result("warning", RepositoryResultStatus.Warning),
            Result("failed", RepositoryResultStatus.Failed));
        viewModel.ShowCleanRepositories = true;

        var orderedStatuses = viewModel.VisibleResults.Select(result => result.Status).ToArray();

        Assert.Equal(
            [
                RepositoryResultStatus.Failed,
                RepositoryResultStatus.Warning,
                RepositoryResultStatus.Updated,
                RepositoryResultStatus.Clean
            ],
            orderedStatuses);
    }

    [Fact]
    public void VisibleResults_AllFilterIncludesCleanRowsByDefault()
    {
        var viewModel = CreateViewModel(
            Result("clean", RepositoryResultStatus.Clean),
            Result("updated", RepositoryResultStatus.Updated));

        Assert.Equal(RepositoryResultFilter.All, viewModel.SelectedResultFilter);
        Assert.Contains(viewModel.VisibleResults, result => result.Status == RepositoryResultStatus.Updated);
        Assert.Contains(viewModel.VisibleResults, result => result.Status == RepositoryResultStatus.Clean);
    }

    [Fact]
    public void VisibleResults_FiltersBySelectedStatusFilter()
    {
        var viewModel = CreateViewModel(
            Result("failed", RepositoryResultStatus.Failed),
            Result("warning", RepositoryResultStatus.Warning),
            Result("updated", RepositoryResultStatus.Updated),
            Result("clean", RepositoryResultStatus.Clean));

        viewModel.SelectedResultFilter = RepositoryResultFilter.Warning;

        Assert.Equal(["warning"], viewModel.VisibleResults.Select(result => result.Name).ToArray());
    }

    [Fact]
    public void VisibleResults_FiltersBySearchTextAcrossNamePathAndCategory()
    {
        var viewModel = CreateViewModel(
            Result("BossMod", RepositoryResultStatus.Failed, category: "Dalamud Plugins/CombatReborn"),
            Result("ChronoCore", RepositoryResultStatus.Updated, category: "FF14_CS/ProjectChronofoil"));

        viewModel.RepositorySearchText = "chrono";

        Assert.Equal(["ChronoCore"], viewModel.VisibleResults.Select(result => result.Name).ToArray());
    }

    [Fact]
    public void RepositoryResult_ExposesRetryButtonStateFromRetryPolicy()
    {
        var retryable = Result(
            "retryable",
            RepositoryResultStatus.Failed,
            Diagnostic(RetryPolicy.Recommended, DiagnosticSeverity.Error));
        var blocked = Result(
            "blocked",
            RepositoryResultStatus.Failed,
            Diagnostic(RetryPolicy.BlockedUntilAction, DiagnosticSeverity.Error));

        Assert.Equal(RetryActionState.EnabledPrimary, retryable.RetryActionState);
        Assert.True(retryable.CanRetry);
        Assert.Equal(RetryActionState.Disabled, blocked.RetryActionState);
        Assert.False(blocked.CanRetry);
    }

    [Theory]
    [InlineData(RepositoryResultStatus.Failed, "\uE783")]
    [InlineData(RepositoryResultStatus.Warning, "\uE7BA")]
    [InlineData(RepositoryResultStatus.Updated, "\uE896")]
    [InlineData(RepositoryResultStatus.Clean, "\uE73E")]
    public void RepositoryResult_StatusIcon_ReturnsExpectedGlyph(
        RepositoryResultStatus status,
        string expectedGlyph)
    {
        var result = Result(status.ToString(), status);

        Assert.Equal(expectedGlyph, result.StatusIcon);
    }

    [Theory]
    [InlineData(RepositoryResultStatus.Failed, "GitPullerFailedBrush")]
    [InlineData(RepositoryResultStatus.Warning, "GitPullerWarningBrush")]
    [InlineData(RepositoryResultStatus.Updated, "GitPullerUpdatedBrush")]
    [InlineData(RepositoryResultStatus.Clean, "GitPullerCleanBrush")]
    public void RepositoryResult_StatusResourceKey_ReturnsExpectedBrushKey(
        RepositoryResultStatus status,
        string expectedResourceKey)
    {
        var result = Result(status.ToString(), status);

        Assert.Equal(expectedResourceKey, result.StatusResourceKey);
    }

    [Theory]
    [InlineData(RepositoryResultStatus.Failed)]
    [InlineData(RepositoryResultStatus.Warning)]
    [InlineData(RepositoryResultStatus.Updated)]
    [InlineData(RepositoryResultStatus.Clean)]
    public void RepositoryResult_StatusVisualMetadata_RemainsPureViewModelData(RepositoryResultStatus status)
    {
        var result = Result(status.ToString(), status);

        Assert.False(string.IsNullOrWhiteSpace(result.StatusIcon));
        Assert.False(string.IsNullOrWhiteSpace(result.StatusResourceKey));
        Assert.DoesNotContain("Brush", result.StatusIcon, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusVisual_MainPageUsesThemeResourceStatusBadgeTemplates()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml");

        Assert.Contains("RepositoryResultStatusBadgeTemplateSelector", xaml, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource GitPullerFailedBrush}", xaml, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource GitPullerWarningBrush}", xaml, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource GitPullerUpdatedBrush}", xaml, StringComparison.Ordinal);
        Assert.Contains("{ThemeResource GitPullerCleanBrush}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceKeyToBrushConverter", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusVisual_AppResourcesDoesNotRegisterBrushConverter()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Resources", "AppResources.xaml");

        Assert.DoesNotContain("ResourceKeyToBrushConverter", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("xmlns:converters", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusVisual_AppResourcesUseAcceptedPastelDarkPalette()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Resources", "AppResources.xaml");

        Assert.Contains("Color=\"#FF9AA3\"", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Color=\"#F6CF79\"", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Color=\"#A7EC98\"", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Color=\"#A9CFF7\"", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedMockupShell_MainPageUsesCustomMockupRegions()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml");

        Assert.Contains("x:Name=\"MockupShellRoot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RepositorySidebar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SyncBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StatusFilterBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResultTable\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailsPane\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FooterStatusBar\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedMockupShell_DoesNotUseNavigationViewAsPrimaryShell()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml");

        Assert.DoesNotContain("<NavigationView", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PaneCustomContent", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AddRepositoryGrid", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderTree_MainPageUsesRuntimeBindingsForTreeViewNodeContent()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml");
        var treeTemplateStart = xaml.IndexOf("<TreeView.ItemTemplate>", StringComparison.Ordinal);
        var treeTemplateEnd = xaml.IndexOf("</TreeView.ItemTemplate>", StringComparison.Ordinal);

        Assert.True(treeTemplateStart >= 0);
        Assert.True(treeTemplateEnd > treeTemplateStart);

        var treeTemplate = xaml[treeTemplateStart..treeTemplateEnd];
        Assert.DoesNotContain("x:DataType=\"vm:RepositoryFolderNodeViewModel\"", treeTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("{x:Bind", treeTemplate, StringComparison.Ordinal);
        Assert.Contains("{Binding Content.Name}", treeTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedMockupShell_ExposesSubtleResizablePaneBoundaries()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml");
        var codeBehind = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml.cs");

        Assert.Contains("x:Name=\"SidebarResizeHandle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailsResizeHandle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailsColumnResizeHandle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:PaneResizeHandle", xaml, StringComparison.Ordinal);
        Assert.Contains("CursorShape=\"SizeWestEast\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CursorShape=\"SizeNorthSouth\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"PaneResizeHandle_PointerPressed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerMoved=\"PaneResizeHandle_PointerMoved\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerReleased=\"PaneResizeHandle_PointerReleased\"", xaml, StringComparison.Ordinal);
        var handleCode = ReadRepositoryFile("GitPuller.WinUI", "Controls", "PaneResizeHandle.cs");
        Assert.Contains("ProtectedCursor", handleCode, StringComparison.Ordinal);
        Assert.Contains("InputSystemCursorShape.SizeWestEast", handleCode, StringComparison.Ordinal);
        Assert.Contains("Opacity = 0.35", handleCode, StringComparison.Ordinal);
        Assert.Contains("Opacity = 0.70", handleCode, StringComparison.Ordinal);
        Assert.Contains("ResizeSidebar", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ResizeDetails", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ResizeDetailsColumns", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeHandles_UseHairlineVisualInsideLargerHitTarget()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml");

        Assert.Contains("x:Key=\"VerticalPaneResizeHandleStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HorizontalPaneResizeHandleStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource VerticalPaneResizeHandleStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource HorizontalPaneResizeHandleStyle}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border Background=\"{ThemeResource GitPullerAccentBrush}\" />", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsPane_ShowsRetryPolicyAndRecommendedAction()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml");

        Assert.Contains("Recommended action", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedResultRetryPolicyText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedResultRetryPolicyDescription}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SelectedResultSuggestedAction}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsPane_PresentsRetryGuidanceBeforeLongEvidence()
    {
        var xaml = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml");

        var retryPolicyIndex = xaml.IndexOf("SelectedResultRetryPolicyText", StringComparison.Ordinal);
        var suggestedActionIndex = xaml.IndexOf("SelectedResultSuggestedAction", StringComparison.Ordinal);
        var evidenceIndex = xaml.IndexOf("SelectedResultEvidence", StringComparison.Ordinal);

        Assert.True(retryPolicyIndex >= 0);
        Assert.True(suggestedActionIndex >= 0);
        Assert.True(evidenceIndex >= 0);
        Assert.True(retryPolicyIndex < evidenceIndex);
        Assert.True(suggestedActionIndex < evidenceIndex);
    }

    [Fact]
    public void DetailsPane_HidesRetryButtonsWhenSelectedResultCannotRetry()
    {
        var codeBehind = ReadRepositoryFile("GitPuller.WinUI", "Views", "MainPage.xaml.cs");

        Assert.Contains(
            "ViewModel.HasSelectedResult && ViewModel.SelectedResultCanRetry && ViewModel.IsSelectedResultRetryPrimary",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.HasSelectedResult && ViewModel.SelectedResultCanRetry && ViewModel.IsSelectedResultRetrySecondary",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryResult_FromResult_UsesWarningDiagnosticForSuccessfulWarningLog()
    {
        var result = new RepoResult
        {
            Path = Path.Combine(TestRoot, "Plugins", "WarningRepo"),
            Name = "WarningRepo",
            Failed = false,
            Elapsed = TimeSpan.FromSeconds(1)
        };
        result.Logs.Add(new LogItem
        {
            Text = "Git LFS fetch failed:\nTimeout (60s)\nCommand: git lfs fetch --all --prune",
            IsWarning = true
        });
        result.Diagnostic = GitFailureClassifier.Classify(result);

        var viewModel = RepositoryResultViewModel.FromResult(result, Descriptor("Plugins", "WarningRepo"));

        Assert.Equal(RepositoryResultStatus.Warning, viewModel.Status);
        Assert.DoesNotContain("No failure signals detected.", viewModel.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("No retry needed", viewModel.DiagnosticTitle, StringComparison.Ordinal);
        Assert.Contains("Git LFS fetch failed", viewModel.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void CanAddRepositoryFromUrl_RequiresSelectedCategory()
    {
        var viewModel = CreateViewModel();
        viewModel.RepositoryUrlToAdd = "https://github.com/example/very-long-repository-name.git";

        Assert.False(viewModel.CanAddRepositoryFromUrl);

        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Name == "Plugins");

        Assert.True(viewModel.CanAddRepositoryFromUrl);
    }

    [Fact]
    public void RemovedRepository_CanRestoreOnlyWhenRemovedPathExistsAndOriginalPathIsFree()
    {
        var record = new RemovedRepositoryRecord
        {
            Name = "DeletedRepo",
            Category = "Plugins",
            RemovedPath = Path.Combine(TestRoot, ".mygitpuller", "removed", "Plugins", "DeletedRepo"),
            OriginalPath = Path.Combine(TestRoot, "Plugins", "DeletedRepo"),
            RemoteUrl = "https://github.com/example/deleted-repo.git",
            RemovedAt = DateTimeOffset.UtcNow
        };

        var restorable = RemovedRepositoryViewModel.FromRecord(
            record,
            path => path == record.RemovedPath,
            path => path != record.OriginalPath);
        var missingRemovedFolder = RemovedRepositoryViewModel.FromRecord(
            record,
            _ => false,
            _ => false);
        var occupiedOriginalPath = RemovedRepositoryViewModel.FromRecord(
            record,
            path => path == record.RemovedPath,
            _ => true);

        Assert.True(restorable.CanRestore);
        Assert.False(missingRemovedFolder.CanRestore);
        Assert.False(occupiedOriginalPath.CanRestore);
    }

    [Fact]
    public void CategoryNavigationItems_ProjectsAllRepositoriesAndCategoriesFromSharedCollection()
    {
        var viewModel = CreateViewModel(
            Result("failed", RepositoryResultStatus.Failed),
            Result("updated", RepositoryResultStatus.Updated),
            Result("testing", RepositoryResultStatus.Updated, category: "Testing"));
        var addedCategory = new CategoryNavigationItemViewModel("Testing", @"E:\FF14\Repos\MyRepos\Testing", 0, 0);

        viewModel.Categories.Add(addedCategory);

        Assert.True(viewModel.CategoryNavigationItems[0].IsAllRepositories);
        Assert.Equal("All repositories", viewModel.CategoryNavigationItems[0].Name);
        Assert.Same(addedCategory, viewModel.CategoryNavigationItems.Single(item => item.Name == "Testing"));

        viewModel.SelectedNavigationItem = addedCategory;

        Assert.Same(addedCategory, viewModel.SelectedCategory);
        Assert.Equal(["testing"], viewModel.VisibleResults.Select(result => result.Name).ToArray());

        viewModel.SelectedNavigationItem = viewModel.CategoryNavigationItems[0];

        Assert.Null(viewModel.SelectedCategory);
        Assert.Equal(3, viewModel.VisibleResults.Count);
    }

    [Fact]
    public async Task RepositoryTree_BuildsNestedFoldersWithAggregateCounts()
    {
        var viewModel = await CreateHierarchicalTreeViewModelAsync();

        var treeNodes = GetRequiredListProperty(viewModel, "RepositoryTreeNodes");
        Assert.Equal(4, treeNodes.Count);

        var allRepositoriesNode = treeNodes[0];
        Assert.True(GetRequiredPropertyValue<bool>(allRepositoriesNode, "IsAllRepositories"));
        Assert.Equal("All repositories", GetRequiredPropertyValue<string>(allRepositoriesNode, "Name"));
        Assert.Equal(5, GetRequiredPropertyValue<int>(allRepositoriesNode, "RepositoryCount"));
        Assert.Equal(2, GetRequiredPropertyValue<int>(allRepositoriesNode, "AttentionCount"));

        var dalamudNode = treeNodes[1];
        Assert.Equal("Dalamud Plugins", GetRequiredPropertyValue<string>(dalamudNode, "Name"));
        Assert.Equal("Dalamud Plugins", GetRequiredPropertyValue<string>(dalamudNode, "FullCategoryName"));
        Assert.Equal(Path.Combine(TestRoot, "Dalamud Plugins"), GetRequiredPropertyValue<string>(dalamudNode, "FullPath"));
        Assert.Equal("Dalamud Plugins (2 repos)", GetRequiredPropertyValue<string>(dalamudNode, "DisplayName"));
        Assert.Equal("1 repository needs review", GetRequiredPropertyValue<string>(dalamudNode, "AttentionText"));

        var dalamudChildren = GetRequiredListProperty(dalamudNode, "Children");
        Assert.Equal(["CombatReborn", "Punish"], dalamudChildren.Select(child => GetRequiredPropertyValue<string>(child, "Name")).ToArray());
        Assert.All(dalamudChildren, child => Assert.Equal(1, GetRequiredPropertyValue<int>(child, "RepositoryCount")));

        var ff14Node = treeNodes[2];
        Assert.Equal("FF14_CS", GetRequiredPropertyValue<string>(ff14Node, "Name"));
        Assert.Equal(2, GetRequiredPropertyValue<int>(ff14Node, "RepositoryCount"));
        Assert.Equal(1, GetRequiredPropertyValue<int>(ff14Node, "AttentionCount"));
        var ff14Children = GetRequiredListProperty(ff14Node, "Children");
        var chronofoilNode = Assert.Single(ff14Children, child =>
            GetRequiredPropertyValue<bool>(child, "IsFolder"));
        Assert.Equal("ProjectChronofoil", GetRequiredPropertyValue<string>(chronofoilNode, "Name"));
        Assert.Equal("FF14_CS/ProjectChronofoil", GetRequiredPropertyValue<string>(chronofoilNode, "FullCategoryName"));

        var ff14RepositoryNode = Assert.Single(ff14Children, child =>
            GetRequiredPropertyValue<bool>(child, "IsRepository"));
        Assert.Equal("FF14_CS", GetRequiredPropertyValue<string>(ff14RepositoryNode, "Name"));
        Assert.Equal(Path.Combine(TestRoot, "FF14_CS", "FF14_CS"), GetRequiredPropertyValue<string>(ff14RepositoryNode, "FullPath"));
    }

    [Fact]
    public async Task RepositoryTree_AddsRepositoryLeavesBelowCategoryFolders()
    {
        var viewModel = await CreateHierarchicalTreeViewModelAsync();

        var combatNode = FindTreeNode(viewModel, "Dalamud Plugins/CombatReborn");
        var children = GetRequiredListProperty(combatNode, "Children");

        Assert.Contains(children, child =>
            GetRequiredPropertyValue<bool>(child, "IsRepository")
            && GetRequiredPropertyValue<string>(child, "Name") == "CombatReborn");
    }

    [Fact]
    public async Task SelectRepositoryTreeNode_SelectsMatchingResultWithoutChangingFolderFilter()
    {
        var viewModel = await CreateHierarchicalTreeViewModelAsync();
        var folderNode = FindTreeNode(viewModel, "Dalamud Plugins");
        SetRequiredPropertyValue(viewModel, "SelectedFolderNode", folderNode);
        var repoNode = FindRepositoryTreeNode(viewModel, "CombatReborn");

        viewModel.SelectRepositoryTreeNode(repoNode);

        Assert.Equal("Dalamud Plugins", viewModel.SelectedCategoryName);
        Assert.Equal(["CombatReborn", "Punish"], viewModel.VisibleResults.Select(result => result.Name).ToArray());
        Assert.Equal("CombatReborn", viewModel.SelectedResultName);
    }

    [Fact]
    public async Task SelectedFolderNode_WhenChildFolderSelected_FiltersExactSubtreeAndUpdatesSelectedCategoryName()
    {
        var viewModel = await CreateHierarchicalTreeViewModelAsync();

        var childNode = FindTreeNode(viewModel, "Dalamud Plugins/CombatReborn");
        SetRequiredPropertyValue(viewModel, "SelectedFolderNode", childNode);

        Assert.Equal("Dalamud Plugins/CombatReborn", viewModel.SelectedCategoryName);
        Assert.Equal(["CombatReborn"], viewModel.VisibleResults.Select(result => result.Name).ToArray());
    }

    [Fact]
    public async Task SelectedFolderNode_WhenChildFolderSelected_UpdatesAddRepositoryCategory()
    {
        var viewModel = await CreateHierarchicalTreeViewModelAsync();
        viewModel.RepositoryUrlToAdd = "https://github.com/example/new-plugin.git";

        var childNode = FindTreeNode(viewModel, "Dalamud Plugins/CombatReborn");
        SetRequiredPropertyValue(viewModel, "SelectedFolderNode", childNode);

        Assert.NotNull(viewModel.SelectedCategory);
        Assert.Equal("Dalamud Plugins/CombatReborn", viewModel.SelectedCategory.Name);
        Assert.True(viewModel.CanAddRepositoryFromUrl);

        viewModel.BeginAddRepository();

        Assert.Equal("Dalamud Plugins/CombatReborn", viewModel.AddRepositoryCategoryName);

        SetRequiredPropertyValue(viewModel, "SelectedFolderNode", viewModel.RepositoryTreeNodes[0]);

        Assert.Null(viewModel.SelectedCategory);
        Assert.False(viewModel.CanAddRepositoryFromUrl);
    }

    [Fact]
    public async Task SelectedFolderNode_WhenParentFolderSelected_FiltersDescendants()
    {
        var viewModel = await CreateHierarchicalTreeViewModelAsync();
        viewModel.ShowCleanRepositories = true;

        var parentNode = FindTreeNode(viewModel, "Dalamud Plugins");
        SetRequiredPropertyValue(viewModel, "SelectedFolderNode", parentNode);

        Assert.Equal(
            ["CombatReborn", "Punish"],
            viewModel.VisibleResults.Select(result => result.Name).ToArray());
    }

    [Fact]
    public void RepositoryResultCollectionMutation_RaisesDerivedPropertiesAndRefreshesVisibleResults()
    {
        var viewModel = CreateViewModel(Result("updated", RepositoryResultStatus.Updated));
        var changedProperties = new List<string>();
        viewModel.PropertyChanged += TrackChangedProperty(changedProperties);

        viewModel.RepositoryResults.Add(Result("failed", RepositoryResultStatus.Failed));

        Assert.Equal(1, viewModel.FailedCount);
        Assert.Equal("2 of 2 repositories shown", viewModel.ResultSummary);
        Assert.Equal(
            [RepositoryResultStatus.Failed, RepositoryResultStatus.Updated],
            viewModel.VisibleResults.Select(result => result.Status).ToArray());
        Assert.Contains(nameof(MainShellViewModel.VisibleResults), changedProperties);
        Assert.Contains(nameof(MainShellViewModel.FailedCount), changedProperties);
        Assert.Contains(nameof(MainShellViewModel.ResultSummary), changedProperties);
        Assert.Contains(nameof(MainShellViewModel.CategoryNavigationItems), changedProperties);
    }

    [Fact]
    public void RemovedRepositoryCollectionMutation_RaisesDerivedCount()
    {
        var viewModel = CreateViewModel();
        var changedProperties = new List<string>();
        viewModel.PropertyChanged += TrackChangedProperty(changedProperties);

        viewModel.RemovedRepositories.Add(RemovedRepositoryViewModel.FromRecord(
            new RemovedRepositoryRecord
            {
                Name = "Removed",
                Category = "Plugins",
                RemovedPath = Path.Combine(TestRoot, ".mygitpuller", "removed", "Plugins", "Removed"),
                OriginalPath = Path.Combine(TestRoot, "Plugins", "Removed"),
                RemovedAt = DateTimeOffset.UtcNow
            },
            _ => true,
            _ => false));

        Assert.Equal(1, viewModel.RemovedRepositoryCount);
        Assert.Contains(nameof(MainShellViewModel.RemovedRepositoryCount), changedProperties);
    }

    [Fact]
    public void AddRepositoryPreview_DisablesCloneUntilCorePreviewIsValid()
    {
        var repositoryService = new FakeRepositoryManagementService();
        repositoryService.PreviewHandler = request => request.RemoteUrl.Contains("valid", StringComparison.OrdinalIgnoreCase)
            ? ValidAddPreview(request)
            : InvalidAddPreview(request, "Clone URL is invalid");
        var viewModel = CreateViewModel(repositoryManagementService: repositoryService);

        viewModel.AddRepositoryUrl = "not a git url";
        viewModel.AddRepositoryCategoryName = "Plugins";
        viewModel.UpdateAddRepositoryPreview();

        Assert.False(viewModel.CanCloneRepository);
        Assert.Contains("invalid", viewModel.AddRepositoryDiagnosticTitle, StringComparison.OrdinalIgnoreCase);

        viewModel.AddRepositoryUrl = "https://github.com/example/valid.git";
        viewModel.AddRepositoryFolderName = "valid-local";
        viewModel.UpdateAddRepositoryPreview();

        Assert.True(viewModel.CanCloneRepository);
        Assert.Equal(Path.Combine(TestRoot, "Plugins", "valid-local"), viewModel.AddRepositoryTargetPathPreview);
        Assert.Equal("valid-local", repositoryService.LastPreviewRequest?.FolderNameOverride);
    }

    [Fact]
    public async Task CloneRepositoryAsync_WhenPreviewIsValid_RefreshesConfigAndListState()
    {
        var repositoryService = new FakeRepositoryManagementService();
        repositoryService.PreviewHandler = ValidAddPreview;
        repositoryService.CloneHandler = (request, options, _) =>
        {
            var preview = ValidAddPreview(request);
            var repository = preview.Repository!;
            var cloneResult = new RepositoryAddResult(
                preview,
                repository,
                Diagnostic: null,
                GitResult: new RepoResult
                {
                    Path = repository.Path,
                    Name = repository.Name,
                    Elapsed = TimeSpan.FromSeconds(1)
                });
            return Task.FromResult(new RepositoryAddWorkflowResult(
                cloneResult,
                LoadResult(
                    new GitPullerOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism },
                    [repository],
                    ["Plugins", "Tools"])));
        };
        var viewModel = CreateViewModel(repositoryManagementService: repositoryService);
        viewModel.AddRepositoryUrl = "https://github.com/example/new-repo.git";
        viewModel.AddRepositoryCategoryName = "Tools";
        viewModel.AddRepositoryFolderName = "new-repo-local";
        viewModel.UpdateAddRepositoryPreview();

        await viewModel.CloneRepositoryAsync();

        Assert.False(viewModel.HasAddRepositoryError);
        Assert.Equal("new-repo-local", repositoryService.LastCloneRequest?.FolderNameOverride);
        Assert.Contains(viewModel.Categories, category => category.Name == "Tools");
        var result = Assert.Single(viewModel.RepositoryResults, repository => repository.Name == "new-repo-local");
        Assert.Equal(RepositoryResultStatus.Clean, result.Status);
        Assert.Equal(string.Empty, viewModel.RepositoryUrlToAdd);
        Assert.Equal(string.Empty, viewModel.AddRepositoryUrl);
    }

    [Fact]
    public async Task SaveAdvancedOptionsAsync_PersistsChangedDefaults()
    {
        var loadedOptions = new GitPullerOptions
        {
            MaxDegreeOfParallelism = 2,
            GitTimeoutMilliseconds = 90000,
            SyncAllBranches = false,
            StaleGitLockCleanup = false,
            StaleGitLockAge = TimeSpan.FromMinutes(25),
            VerboseReport = true,
            InitMissingSubmodules = false
        };
        var service = new FakeGitPullerSyncService(LoadResult(loadedOptions, [], ["Plugins"]));
        var repositoryService = new FakeRepositoryManagementService();
        GitPullerOptions? savedOptions = null;
        repositoryService.SaveOptionsHandler = (libraryRoot, options, _) =>
        {
            savedOptions = options;
            return Task.FromResult(LoadResult(options, [], ["Plugins"]));
        };
        var viewModel = new MainShellViewModel(
            TestRoot,
            service,
            repositoryManagementService: repositoryService);

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.AdvancedWorkers);
        Assert.Equal(90, viewModel.AdvancedTimeoutSeconds);
        Assert.False(viewModel.AdvancedSyncAllBranches);
        Assert.True(viewModel.AdvancedNoStaleLockCleanup);
        Assert.Equal(25, viewModel.AdvancedStaleLockMinutes);
        Assert.True(viewModel.AdvancedVerboseReport);
        Assert.False(viewModel.AdvancedInitMissingSubmodules);

        viewModel.AdvancedWorkers = 4;
        viewModel.AdvancedTimeoutSeconds = 45;
        viewModel.AdvancedSyncAllBranches = true;
        viewModel.AdvancedNoStaleLockCleanup = false;
        viewModel.AdvancedStaleLockMinutes = 7;
        viewModel.AdvancedVerboseReport = false;
        viewModel.AdvancedInitMissingSubmodules = true;

        await viewModel.SaveAdvancedOptionsAsync();

        Assert.NotNull(savedOptions);
        Assert.Equal(4, savedOptions.MaxDegreeOfParallelism);
        Assert.Equal(45000, savedOptions.GitTimeoutMilliseconds);
        Assert.True(savedOptions.SyncAllBranches);
        Assert.True(savedOptions.StaleGitLockCleanup);
        Assert.Equal(TimeSpan.FromMinutes(7), savedOptions.StaleGitLockAge);
        Assert.False(savedOptions.VerboseReport);
        Assert.True(savedOptions.InitMissingSubmodules);
        Assert.Contains("saved", viewModel.AdvancedOptionsStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovedRepositoryActions_CallManagementServiceAndRefreshState()
    {
        var restoreRecord = RemovedRecord("RestoreMe");
        var deleteRecord = RemovedRecord("DeleteMe");
        var repositoryService = new FakeRepositoryManagementService();
        repositoryService.RestoreHandler = (libraryRoot, record, _) =>
        {
            Assert.Equal(TestRoot, libraryRoot);
            Assert.Equal(restoreRecord.RemovedPath, record.RemovedPath);
            return Task.FromResult(LoadResult(new GitPullerOptions(), [], ["Plugins"]));
        };
        repositoryService.DeleteHandler = (_, record, _) =>
        {
            Assert.Equal(deleteRecord.RemovedPath, record.RemovedPath);
            return Task.FromResult(LoadResult(new GitPullerOptions(), [], ["Plugins"]));
        };
        var viewModel = new MainShellViewModel(
            TestRoot,
            [new CategoryNavigationItemViewModel("Plugins", Path.Combine(TestRoot, "Plugins"), 0, 0)],
            [],
            [
                RemovedRepositoryViewModel.FromRecord(restoreRecord, _ => true, _ => false),
                RemovedRepositoryViewModel.FromRecord(deleteRecord, _ => true, _ => false)
            ],
            repositoryManagementService: repositoryService);

        await viewModel.RestoreRemovedRepositoryAsync(viewModel.RemovedRepositories[0]);
        await viewModel.PermanentlyDeleteRemovedRepositoryAsync(RemovedRepositoryViewModel.FromRecord(deleteRecord, _ => true, _ => false));

        Assert.Equal(1, repositoryService.RestoreCallCount);
        Assert.Equal(1, repositoryService.DeleteCallCount);
        Assert.Empty(viewModel.RemovedRepositories);
    }

    [Fact]
    public async Task RemovedRepositoryRestoreAs_CallsManagementServiceWithCategoryAndFolderName()
    {
        var restoreRecord = RemovedRecord("RestoreAsMe");
        var repositoryService = new FakeRepositoryManagementService();
        repositoryService.RestoreAsHandler = (libraryRoot, record, category, folderName, _) =>
        {
            Assert.Equal(TestRoot, libraryRoot);
            Assert.Equal(restoreRecord.RemovedPath, record.RemovedPath);
            Assert.Equal("Tools", category);
            Assert.Equal("RestoredLocalName", folderName);
            return Task.FromResult(LoadResult(new GitPullerOptions(), [], ["Plugins", "Tools"]));
        };
        var viewModel = new MainShellViewModel(
            TestRoot,
            [new CategoryNavigationItemViewModel("Plugins", Path.Combine(TestRoot, "Plugins"), 0, 0)],
            [],
            [RemovedRepositoryViewModel.FromRecord(restoreRecord, _ => true, _ => false)],
            repositoryManagementService: repositoryService);

        await viewModel.RestoreRemovedRepositoryAsAsync(viewModel.RemovedRepositories[0], "Tools", "RestoredLocalName");

        Assert.Equal(1, repositoryService.RestoreAsCallCount);
        Assert.Empty(viewModel.RemovedRepositories);
    }

    [Fact]
    public async Task CoreRepositoryManagementService_RestoreAsRejectsBlankFolderName()
    {
        var libraryRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        var removedPath = Path.Combine(libraryRoot, ".mygitpuller", "removed", "Plugins", "BlankNameRepo");
        Directory.CreateDirectory(removedPath);
        var removed = new RemovedRepositoryRecord
        {
            Name = "BlankNameRepo",
            Category = "Plugins",
            OriginalPath = Path.Combine(libraryRoot, "Plugins", "BlankNameRepo"),
            RemovedPath = removedPath,
            RemoteUrl = "https://github.com/example/BlankNameRepo.git",
            RemovedAt = DateTimeOffset.UtcNow
        };
        var config = new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins"],
            RemovedRepositories = [removed]
        };
        var configStore = new LibraryConfigStore();
        await configStore.SaveAsync(config, CancellationToken.None);
        var service = new CoreRepositoryManagementService(configStore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreRepositoryAsAsync(libraryRoot, removed, "Plugins", " ", CancellationToken.None));

        Assert.Contains("folder name", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(removedPath));
        Assert.False(Directory.Exists(Path.Combine(libraryRoot, "Plugins", "restore")));
    }

    [Fact]
    public async Task CoreRepositoryManagementService_CloneRollsBackRepositoryFolder_WhenConfigSaveFails()
    {
        var scenarioRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        var libraryRoot = Path.Combine(scenarioRoot, "Library");
        var remotePath = CreateBareRemoteRepository(scenarioRoot, "RepoA");
        var store = new FailingRepositoryManagementConfigStore(new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins"]
        })
        {
            ThrowOnSave = true
        };
        var service = new CoreRepositoryManagementService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CloneRepositoryAsync(
            new RepositoryAddRequest(libraryRoot, "Plugins", remotePath),
            new GitPullerOptions(),
            CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(libraryRoot, "Plugins", "RepoA")));
        Assert.Empty(store.PersistedConfig.Repositories);
    }

    [Fact]
    public async Task CoreRepositoryManagementService_RestoreRollsBackMovedFolder_WhenConfigSaveFails()
    {
        var libraryRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        var removed = RemovedRecord("RestoreRollback", libraryRoot);
        CreateRepositoryDirectory(removed.RemovedPath);
        var store = new FailingRepositoryManagementConfigStore(new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins"],
            RemovedRepositories = [removed]
        })
        {
            ThrowOnSave = true
        };
        var service = new CoreRepositoryManagementService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreRepositoryAsync(libraryRoot, removed, CancellationToken.None));

        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.False(Directory.Exists(removed.OriginalPath));
        Assert.Single(store.PersistedConfig.RemovedRepositories);
    }

    [Fact]
    public async Task CoreRepositoryManagementService_RestoreAsRollsBackMovedFolder_WhenConfigSaveFails()
    {
        var libraryRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        var removed = RemovedRecord("RestoreAsRollback", libraryRoot);
        var alternatePath = Path.Combine(libraryRoot, "Tools", "RestoreAsLocal");
        CreateRepositoryDirectory(removed.RemovedPath);
        var store = new FailingRepositoryManagementConfigStore(new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins", "Tools"],
            RemovedRepositories = [removed]
        })
        {
            ThrowOnSave = true
        };
        var service = new CoreRepositoryManagementService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreRepositoryAsAsync(libraryRoot, removed, "Tools", "RestoreAsLocal", CancellationToken.None));

        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.False(Directory.Exists(alternatePath));
        Assert.Single(store.PersistedConfig.RemovedRepositories);
    }

    [Fact]
    public async Task CoreRepositoryManagementService_PermanentDeleteDoesNotDeleteFolder_WhenConfigSaveFails()
    {
        var libraryRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        var removed = RemovedRecord("DeleteRollback", libraryRoot);
        CreateRepositoryDirectory(removed.RemovedPath);
        var store = new FailingRepositoryManagementConfigStore(new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins"],
            RemovedRepositories = [removed]
        })
        {
            ThrowOnSave = true
        };
        var deleter = new RecordingRemovedRepositoryDirectoryDeleter();
        var service = new CoreRepositoryManagementService(
            store,
            removedRepositoryDirectoryDeleter: deleter);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PermanentlyDeleteRepositoryAsync(libraryRoot, removed, CancellationToken.None));

        Assert.True(Directory.Exists(removed.RemovedPath));
        Assert.Single(store.PersistedConfig.RemovedRepositories);
        Assert.Equal(1, store.SaveCallCount);
        Assert.Equal(0, deleter.DeleteCallCount);
    }

    [Fact]
    public async Task CoreRepositoryManagementService_PermanentDeleteKeepsMetadata_WhenPhysicalDeleteFailsAfterPreflight()
    {
        var libraryRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        var removed = RemovedRecord("DeletePhysicalFailure", libraryRoot);
        CreateRepositoryDirectory(removed.RemovedPath);
        var store = new FailingRepositoryManagementConfigStore(new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins"],
            RemovedRepositories = [removed]
        });
        var deleter = new ThrowingRemovedRepositoryDirectoryDeleter();
        var service = new CoreRepositoryManagementService(
            store,
            removedRepositoryDirectoryDeleter: deleter);

        await Assert.ThrowsAsync<IOException>(() =>
            service.PermanentlyDeleteRepositoryAsync(libraryRoot, removed, CancellationToken.None));

        Assert.True(Directory.Exists(removed.RemovedPath));
        var persistedRemoved = Assert.Single(store.PersistedConfig.RemovedRepositories);
        Assert.Equal(removed.RemovedPath, persistedRemoved.RemovedPath);
        Assert.Equal(1, deleter.DeleteCallCount);
        Assert.Equal(1, store.SaveCallCount);
    }

    [Fact]
    public async Task CoreRepositoryManagementService_PermanentDeleteKeepsPersistedMetadata_WhenFinalSaveFailsAfterDelete()
    {
        var libraryRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        var removed = RemovedRecord("DeleteFinalSaveFailure", libraryRoot);
        CreateRepositoryDirectory(removed.RemovedPath);
        var store = new FailingRepositoryManagementConfigStore(new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins"],
            RemovedRepositories = [removed]
        })
        {
            ThrowOnSaveCall = 2
        };
        var deleter = new RecordingRemovedRepositoryDirectoryDeleter();
        var service = new CoreRepositoryManagementService(
            store,
            removedRepositoryDirectoryDeleter: deleter);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PermanentlyDeleteRepositoryAsync(libraryRoot, removed, CancellationToken.None));

        Assert.False(Directory.Exists(removed.RemovedPath));
        var persistedRemoved = Assert.Single(store.PersistedConfig.RemovedRepositories);
        Assert.Equal(removed.RemovedPath, persistedRemoved.RemovedPath);
        Assert.Equal(1, deleter.DeleteCallCount);
        Assert.Equal(2, store.SaveCallCount);
    }

    [Fact]
    public async Task CoreRepositoryManagementService_PermanentDeleteRemovesFolderAndMetadata_WhenDeleteAndFinalSaveSucceed()
    {
        var libraryRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        var removed = RemovedRecord("DeleteSuccess", libraryRoot);
        CreateRepositoryDirectory(removed.RemovedPath);
        var store = new FailingRepositoryManagementConfigStore(new LibraryConfig
        {
            LibraryRoot = libraryRoot,
            Categories = ["Plugins"],
            RemovedRepositories = [removed]
        });
        var deleter = new RecordingRemovedRepositoryDirectoryDeleter();
        var service = new CoreRepositoryManagementService(
            store,
            removedRepositoryDirectoryDeleter: deleter);

        await service.PermanentlyDeleteRepositoryAsync(libraryRoot, removed, CancellationToken.None);

        Assert.False(Directory.Exists(removed.RemovedPath));
        Assert.Empty(store.PersistedConfig.RemovedRepositories);
        Assert.Equal(1, deleter.DeleteCallCount);
        Assert.Equal(2, store.SaveCallCount);
    }

    [Fact]
    public async Task LauncherActions_InvokeInjectedLauncherForRepositoryAndRemovedTargets()
    {
        var launcher = new FakeFileSystemLauncher();
        var removedRecord = RemovedRecord("RemovedRepo");
        var viewModel = new MainShellViewModel(
            TestRoot,
            [new CategoryNavigationItemViewModel("Plugins", Path.Combine(TestRoot, "Plugins"), 1, 0)],
            [Result("OpenMe", RepositoryResultStatus.Updated)],
            [RemovedRepositoryViewModel.FromRecord(removedRecord, _ => true, _ => false)],
            launcher: launcher);

        await viewModel.OpenSelectedRepositoryFolderAsync();
        await viewModel.OpenSelectedRemoteAsync();
        await viewModel.OpenLibraryFolderAsync();
        await viewModel.OpenRemovedFolderAsync(viewModel.RemovedRepositories[0]);
        await viewModel.OpenRemovedOriginalFolderAsync(viewModel.RemovedRepositories[0]);
        await viewModel.OpenRemovedRemoteAsync(viewModel.RemovedRepositories[0]);

        Assert.Contains(Path.Combine(TestRoot, "Plugins", "OpenMe"), launcher.LaunchedPaths);
        Assert.Contains(TestRoot, launcher.LaunchedPaths);
        Assert.Contains(removedRecord.RemovedPath, launcher.LaunchedPaths);
        Assert.Contains(removedRecord.OriginalPath, launcher.LaunchedPaths);
        Assert.Contains("https://github.com/example/OpenMe.git", launcher.LaunchedUris);
        Assert.Contains(removedRecord.RemoteUrl, launcher.LaunchedUris);
    }

    [Fact]
    public async Task OpenLatestReport_UsesActiveLibraryRootReport()
    {
        var libraryRoot = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryRoot);
        var reportPath = Path.Combine(libraryRoot, GitPullerReportWriter.LatestReportFileName);
        await File.WriteAllTextAsync(reportPath, "report");
        var launcher = new FakeFileSystemLauncher();
        var viewModel = new MainShellViewModel(
            libraryRoot,
            [],
            [],
            [],
            launcher: launcher);

        Assert.Equal(reportPath, viewModel.LatestReportPath);
        Assert.True(viewModel.CanOpenLatestReport);

        await viewModel.OpenLatestReportAsync();

        Assert.Contains(reportPath, launcher.LaunchedPaths);
    }

    [Fact]
    public async Task RunSyncAsync_ExposesFreshLatestReportPathAndStatusMessage()
    {
        var libraryRoot = Path.Combine(TestRoot, "libraries", Guid.NewGuid().ToString("N"));
        var repository = new RepositoryDescriptor(
            Path.Combine(libraryRoot, "Plugins", "ReportRepo"),
            "ReportRepo",
            "Plugins",
            "https://github.com/example/ReportRepo.git");
        var loadResult = new GitPullerLibraryLoadResult(
            libraryRoot,
            new GitPullerOptions(),
            new RepositoryInventory(libraryRoot, [repository]),
            [],
            ["Plugins"]);
        var latestReportRoot = Path.Combine(TestRoot, "reports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(latestReportRoot);
        var latestReportPath = Path.Combine(latestReportRoot, GitPullerReportWriter.LatestReportFileName);
        await File.WriteAllTextAsync(latestReportPath, "# Git Update Report");

        var service = new FakeGitPullerSyncService(loadResult);
        service.RunAllAsyncHandler = (_, _, _) => Task.FromResult(new GitPullerRunResult
        {
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            RepositoryResults = [RepoResultFor(repository, failed: false, newCommits: 2, diagnostic: null)],
            LatestReportPath = latestReportPath,
            RunReportPath = Path.Combine(latestReportRoot, "git_update_report-20260529-120000-000.md")
        });

        var launcher = new FakeFileSystemLauncher();
        var viewModel = new MainShellViewModel(libraryRoot, service, launcher: launcher);

        await viewModel.RunSyncAsync();

        Assert.Equal(latestReportPath, viewModel.LatestReportPath);
        Assert.True(viewModel.CanOpenLatestReport);
        Assert.Contains(GitPullerReportWriter.LatestReportFileName, viewModel.RunStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSyncAsync_QueuedProgressDoesNotOverwriteFinalReportStatus_AndRetryStillUpdatesProgress()
    {
        var libraryRoot = Path.Combine(TestRoot, "libraries", Guid.NewGuid().ToString("N"));
        var repository = new RepositoryDescriptor(
            Path.Combine(libraryRoot, "Plugins", "QueuedRepo"),
            "QueuedRepo",
            "Plugins",
            "https://github.com/example/QueuedRepo.git");
        var loadResult = new GitPullerLibraryLoadResult(
            libraryRoot,
            new GitPullerOptions(),
            new RepositoryInventory(libraryRoot, [repository]),
            [],
            ["Plugins"]);
        var latestReportRoot = Path.Combine(TestRoot, "reports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(latestReportRoot);
        var latestReportPath = Path.Combine(latestReportRoot, GitPullerReportWriter.LatestReportFileName);
        await File.WriteAllTextAsync(latestReportPath, "# Git Update Report");

        var service = new FakeGitPullerSyncService(loadResult);
        service.RunAllAsyncHandler = (_, progress, _) =>
        {
            var failedResult = RepoResultFor(
                repository,
                failed: true,
                newCommits: 0,
                Diagnostic(RetryPolicy.Recommended, DiagnosticSeverity.Error));
            progress?.Report(GitPullerProgressEvent.RunStarted(1));
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(repository, failedResult, 1, 1));

            return Task.FromResult(new GitPullerRunResult
            {
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                RepositoryResults = [failedResult],
                LatestReportPath = latestReportPath,
                RunReportPath = Path.Combine(latestReportRoot, "git_update_report-20260529-120000-000.md")
            });
        };
        service.RetryRepositoryAsyncHandler = (_, _, progress, _) =>
        {
            var retryResult = RepoResultFor(repository, failed: false, newCommits: 3, diagnostic: null);
            progress?.Report(GitPullerProgressEvent.RepositoryStarted(repository, 1, 0));
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(repository, retryResult, 1, 1));

            return Task.FromResult(retryResult);
        };

        var dispatcher = new QueuedViewModelDispatcher();
        var viewModel = new MainShellViewModel(
            libraryRoot,
            service,
            dispatcher,
            launcher: new FakeFileSystemLauncher());

        var runTask = viewModel.RunSyncAsync();
        dispatcher.FlushAll();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(GitPullerReportWriter.LatestReportFileName, viewModel.RunStatusMessage, StringComparison.Ordinal);
        Assert.Equal(latestReportPath, viewModel.LatestReportPath);
        Assert.True(viewModel.CanOpenLatestReport);
        Assert.True(viewModel.RetrySelectedCommand.CanExecute(null));

        var retryTask = viewModel.RetrySelectedAsync();
        dispatcher.FlushAll();
        await retryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Retry completed: QueuedRepo", viewModel.RunStatusMessage, StringComparison.Ordinal);
        Assert.Equal("1 of 1 repositories completed", viewModel.RunProgressText);
        Assert.Equal(RepositoryResultStatus.Updated, Assert.Single(viewModel.RepositoryResults).Status);
    }

    [Fact]
    public async Task CoreGitPullerSyncService_RunAllAsync_WritesReportsAndReturnsPaths()
    {
        var scenarioRoot = Path.Combine(TestRoot, "sync-service", Guid.NewGuid().ToString("N"));
        var libraryRoot = Path.Combine(scenarioRoot, "library");
        var repositoryPath = Path.Combine(libraryRoot, "Plugins", "ReportRepo");
        Directory.CreateDirectory(Path.GetDirectoryName(repositoryPath)!);

        var remotePath = CreateBareRemoteRepository(scenarioRoot, "report-repo");
        RunGit(scenarioRoot, "clone", "--branch", "main", remotePath, repositoryPath);

        var request = new GitPullerRunRequest(
            new GitPullerOptions(),
            new RepositoryInventory(
                libraryRoot,
                [
                    new RepositoryDescriptor(
                        repositoryPath,
                        "ReportRepo",
                        "Plugins",
                        remotePath)
                ]));

        var service = new CoreGitPullerSyncService();
        var result = await service.RunAllAsync(request, progress: null, CancellationToken.None);

        Assert.Equal(Path.Combine(libraryRoot, GitPullerReportWriter.LatestReportFileName), result.LatestReportPath);
        Assert.NotNull(result.RunReportPath);
        Assert.True(File.Exists(result.LatestReportPath));
        Assert.True(File.Exists(result.RunReportPath));
        Assert.Contains("# Git Update Report", await File.ReadAllTextAsync(result.LatestReportPath));
    }

    [Theory]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("git@github-bf:bloooowfish/MyGitPuller.git", "https://github.com/bloooowfish/MyGitPuller")]
    [InlineData("ssh://git@github.com/owner/repo.git", "https://github.com/owner/repo")]
    public async Task LauncherActions_NormalizesBrowserableGitRemotes(string remoteUrl, string expectedBrowserUrl)
    {
        var launcher = new FakeFileSystemLauncher();
        var viewModel = new MainShellViewModel(
            TestRoot,
            [new CategoryNavigationItemViewModel("Plugins", Path.Combine(TestRoot, "Plugins"), 1, 0)],
            [Result("RemoteRepo", RepositoryResultStatus.Updated, remoteUrl: remoteUrl)],
            [],
            launcher: launcher);

        Assert.True(viewModel.CanOpenSelectedRemote);

        await viewModel.OpenSelectedRemoteAsync();

        Assert.Contains(expectedBrowserUrl, launcher.LaunchedUris);
    }

    [Fact]
    public async Task LauncherActions_HidesRemoteAction_WhenGitRemoteHasNoBrowserMapping()
    {
        var launcher = new FakeFileSystemLauncher();
        var viewModel = new MainShellViewModel(
            TestRoot,
            [new CategoryNavigationItemViewModel("Plugins", Path.Combine(TestRoot, "Plugins"), 1, 0)],
            [Result("RemoteRepo", RepositoryResultStatus.Updated, remoteUrl: "git@internal:owner/repo.git")],
            [],
            launcher: launcher);

        Assert.False(viewModel.CanOpenSelectedRemote);

        await viewModel.OpenSelectedRemoteAsync();

        Assert.Empty(launcher.LaunchedUris);
    }

    [Fact]
    public async Task RunSyncAsync_LoadsLibraryUsesRootScopedRequestAndAppendsCompletedResults()
    {
        var failedRepository = Descriptor("Plugins", "FailedRepo");
        var updatedRepository = Descriptor("Tools", "UpdatedRepo");
        var loadResult = LoadResult(failedRepository, updatedRepository);
        var service = new FakeGitPullerSyncService(loadResult);
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GitPullerRunRequest? capturedRequest = null;
        service.RunAllAsyncHandler = async (request, progress, cancellationToken) =>
        {
            capturedRequest = request;
            progress?.Report(GitPullerProgressEvent.RunStarted(request.Inventory.Repositories.Count));
            progress?.Report(GitPullerProgressEvent.RepositoryStarted(failedRepository, 2, 0));
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(
                failedRepository,
                RepoResultFor(failedRepository, failed: true, newCommits: 0, Diagnostic(RetryPolicy.Recommended, DiagnosticSeverity.Error)),
                totalRepositories: 2,
                completedRepositories: 1));

            await releaseRun.Task.WaitAsync(cancellationToken);

            progress?.Report(GitPullerProgressEvent.RepositoryStarted(updatedRepository, 2, 1));
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(
                updatedRepository,
                RepoResultFor(updatedRepository, failed: false, newCommits: 3, diagnostic: null),
                totalRepositories: 2,
                completedRepositories: 2));

            return new GitPullerRunResult
            {
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                RepositoryResults =
                [
                    RepoResultFor(failedRepository, failed: true, newCommits: 0, Diagnostic(RetryPolicy.Recommended, DiagnosticSeverity.Error)),
                    RepoResultFor(updatedRepository, failed: false, newCommits: 3, diagnostic: null)
                ]
            };
        };

        var viewModel = new MainShellViewModel(TestRoot, service);
        var runTask = viewModel.RunSyncAsync();

        await service.WaitForFirstRepositoryCompletionAsync();

        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.RunSyncCommand.CanExecute(null));
        Assert.Equal(1, viewModel.RunProgressCompleted);
        Assert.Equal(2, viewModel.RunProgressTotal);
        Assert.Equal("1 of 2 repositories completed", viewModel.RunProgressText);
        Assert.Equal(["FailedRepo"], viewModel.RepositoryResults.Select(result => result.Name).ToArray());

        releaseRun.SetResult();
        await runTask;

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.RunSyncCommand.CanExecute(null));
        Assert.Same(loadResult.CreateRunRequest().Inventory, capturedRequest?.Inventory);
        Assert.Equal(TestRoot, capturedRequest?.Inventory.LibraryRoot);
        Assert.Equal(
            [RepositoryResultStatus.Failed, RepositoryResultStatus.Updated],
            viewModel.VisibleResults.Select(result => result.Status).ToArray());
        Assert.Equal("2 of 2 repositories completed", viewModel.RunProgressText);
    }

    [Fact]
    public async Task RunSyncAsync_PreservesSelectedPath_WhenSelectionTemporarilyClearsDuringLiveSortUpdate()
    {
        var selectedRepository = Descriptor("Tools", "UpdatedRepo");
        var laterFailedRepository = Descriptor("Plugins", "FailedRepo");
        var service = new FakeGitPullerSyncService(LoadResult(selectedRepository, laterFailedRepository));
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RunAllAsyncHandler = async (_, progress, cancellationToken) =>
        {
            var selectedResult = RepoResultFor(selectedRepository, failed: false, newCommits: 2, diagnostic: null);
            var laterFailedResult = RepoResultFor(
                laterFailedRepository,
                failed: true,
                newCommits: 0,
                Diagnostic(RetryPolicy.Recommended, DiagnosticSeverity.Error));

            progress?.Report(GitPullerProgressEvent.RunStarted(2));
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(selectedRepository, selectedResult, 2, 1));

            await releaseRun.Task.WaitAsync(cancellationToken);

            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(laterFailedRepository, laterFailedResult, 2, 2));

            return new GitPullerRunResult
            {
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                RepositoryResults = []
            };
        };

        var viewModel = new MainShellViewModel(TestRoot, service);
        var runTask = viewModel.RunSyncAsync();

        await service.WaitForFirstRepositoryCompletionAsync();

        var initiallySelected = Assert.Single(viewModel.RepositoryResults);
        Assert.Same(initiallySelected, viewModel.SelectedResult);

        viewModel.SelectedResult = null;

        releaseRun.SetResult();
        await runTask;

        Assert.Equal(selectedRepository.Path, viewModel.SelectedResult?.Path);
        Assert.Equal(
            [RepositoryResultStatus.Failed, RepositoryResultStatus.Updated],
            viewModel.VisibleResults.Select(result => result.Status).ToArray());
    }

    [Fact]
    public async Task RunSyncAsync_SelectsReplacementInstanceForTrackedPath_WhenSelectedResultIsReplaced()
    {
        var selectedRepository = Descriptor("Tools", "UpdatedRepo");
        var laterFailedRepository = Descriptor("Plugins", "FailedRepo");
        var service = new FakeGitPullerSyncService(LoadResult(selectedRepository, laterFailedRepository));
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RunAllAsyncHandler = async (_, progress, cancellationToken) =>
        {
            var initialSelectedResult = RepoResultFor(selectedRepository, failed: false, newCommits: 1, diagnostic: null);
            var replacementSelectedResult = RepoResultFor(selectedRepository, failed: false, newCommits: 4, diagnostic: null);
            var laterFailedResult = RepoResultFor(
                laterFailedRepository,
                failed: true,
                newCommits: 0,
                Diagnostic(RetryPolicy.Recommended, DiagnosticSeverity.Error));

            progress?.Report(GitPullerProgressEvent.RunStarted(2));
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(selectedRepository, initialSelectedResult, 2, 1));

            await releaseRun.Task.WaitAsync(cancellationToken);

            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(laterFailedRepository, laterFailedResult, 2, 2));

            return new GitPullerRunResult
            {
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                RepositoryResults =
                [
                    replacementSelectedResult,
                    laterFailedResult
                ]
            };
        };

        var viewModel = new MainShellViewModel(TestRoot, service);
        var runTask = viewModel.RunSyncAsync();

        await service.WaitForFirstRepositoryCompletionAsync();

        var initiallySelected = Assert.Single(viewModel.RepositoryResults);
        Assert.Same(initiallySelected, viewModel.SelectedResult);

        viewModel.SelectedResult = null;

        releaseRun.SetResult();
        await runTask;

        var replacement = Assert.Single(viewModel.RepositoryResults, result => result.Path == selectedRepository.Path);
        Assert.NotSame(initiallySelected, replacement);
        Assert.Equal(4, replacement.NewCommitsCount);
        Assert.Same(replacement, viewModel.SelectedResult);
    }

    [Fact]
    public async Task RetrySelectedAsync_UsesPreviousRunRequestAndReplacesSelectedRepositoryResult()
    {
        var repository = Descriptor("Plugins", "RetryMe");
        var loadResult = LoadResult(repository);
        var service = new FakeGitPullerSyncService(loadResult);
        service.RunAllAsyncHandler = (request, progress, _) =>
        {
            var failedResult = RepoResultFor(
                repository,
                failed: true,
                newCommits: 0,
                Diagnostic(RetryPolicy.Recommended, DiagnosticSeverity.Error));
            progress?.Report(GitPullerProgressEvent.RunStarted(1));
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(repository, failedResult, 1, 1));
            return Task.FromResult(new GitPullerRunResult
            {
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                RepositoryResults = [failedResult]
            });
        };

        GitPullerRunRequest? retryRequest = null;
        service.RetryRepositoryAsyncHandler = (request, repoPath, progress, _) =>
        {
            retryRequest = request;
            var retryResult = RepoResultFor(repository, failed: false, newCommits: 2, diagnostic: null);
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(repository, retryResult, 1, 1));
            return Task.FromResult(retryResult);
        };

        var viewModel = new MainShellViewModel(TestRoot, service);
        await viewModel.RunSyncAsync();
        var failedViewModel = Assert.Single(viewModel.RepositoryResults);
        Assert.Equal(RepositoryResultStatus.Failed, failedViewModel.Status);
        Assert.True(viewModel.RetrySelectedCommand.CanExecute(null));

        await viewModel.RetrySelectedAsync();

        var replacement = Assert.Single(viewModel.RepositoryResults);
        Assert.Same(loadResult.CreateRunRequest().Inventory, retryRequest?.Inventory);
        Assert.Equal(repository.Path, replacement.Path);
        Assert.Equal(RepositoryResultStatus.Updated, replacement.Status);
        Assert.Same(replacement, viewModel.SelectedResult);
    }

    [Fact]
    public async Task RunSyncAsync_ExposesLoadFailureAsStatusInsteadOfThrowing()
    {
        var service = new FakeGitPullerSyncService(LoadResult());
        service.LoadLibraryAsyncHandler = (_, _) => throw new InvalidOperationException("Config file is invalid.");
        var viewModel = new MainShellViewModel(TestRoot, service);

        await viewModel.RunSyncAsync();

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.HasRunError);
        Assert.Contains("Config file is invalid.", viewModel.RunErrorMessage);
        Assert.Empty(viewModel.RepositoryResults);
    }

    [Fact]
    public async Task InitializeAsync_MarksBusyAndPreventsRunSyncOverlapDuringSlowLoad()
    {
        var repository = Descriptor("Plugins", "SlowInitRepo");
        var service = new FakeGitPullerSyncService(LoadResult(repository));
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.LoadLibraryAsyncHandler = async (_, cancellationToken) =>
        {
            if (service.LoadCallCount == 1)
            {
                firstLoadStarted.SetResult();
                await releaseFirstLoad.Task.WaitAsync(cancellationToken);
            }

            return LoadResult(repository);
        };

        var viewModel = new MainShellViewModel(TestRoot, service);
        var initializeTask = viewModel.InitializeAsync();
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.RunSyncCommand.CanExecute(null));

        await viewModel.RunSyncAsync();

        Assert.Equal(1, service.LoadCallCount);
        Assert.Equal(0, service.RunAllCallCount);

        releaseFirstLoad.SetResult();
        await initializeTask;

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.RunSyncCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunSyncAsync_LoadFailureAfterSuccessfulRunClearsStaleResultsAndRetryState()
    {
        var repository = Descriptor("Plugins", "PreviouslyFailedRepo");
        var loadResult = LoadResult(repository);
        var service = new FakeGitPullerSyncService(loadResult);
        var failReload = false;
        service.LoadLibraryAsyncHandler = (_, _) => failReload
            ? throw new InvalidOperationException("Reload failed.")
            : Task.FromResult(loadResult);
        service.RunAllAsyncHandler = (request, progress, _) =>
        {
            var failedResult = RepoResultFor(
                repository,
                failed: true,
                newCommits: 0,
                Diagnostic(RetryPolicy.Recommended, DiagnosticSeverity.Error));
            progress?.Report(GitPullerProgressEvent.RepositoryCompleted(repository, failedResult, 1, 1));
            return Task.FromResult(new GitPullerRunResult
            {
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                RepositoryResults = [failedResult]
            });
        };

        var viewModel = new MainShellViewModel(TestRoot, service);
        await viewModel.RunSyncAsync();

        Assert.Single(viewModel.RepositoryResults);
        Assert.True(viewModel.RetrySelectedCommand.CanExecute(null));

        failReload = true;
        await viewModel.RunSyncAsync();

        Assert.True(viewModel.HasRunError);
        Assert.Empty(viewModel.RepositoryResults);
        Assert.Null(viewModel.SelectedResult);
        Assert.False(viewModel.RetrySelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunSyncAsync_RunCompletedProgressEventIsSingleFinalizationSource()
    {
        var repository = Descriptor("Tools", "ProgressCompletedRepo");
        var progressResult = RepoResultFor(repository, failed: false, newCommits: 4, diagnostic: null);
        var service = new FakeGitPullerSyncService(LoadResult(repository));
        service.RunAllAsyncHandler = (_, progress, _) =>
        {
            progress?.Report(GitPullerProgressEvent.RunCompleted(new GitPullerRunResult
            {
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                RepositoryResults = [progressResult]
            }));

            return Task.FromResult(new GitPullerRunResult
            {
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                RepositoryResults = []
            });
        };

        var viewModel = new MainShellViewModel(TestRoot, service);
        await viewModel.RunSyncAsync();

        var result = Assert.Single(viewModel.RepositoryResults);
        Assert.Equal(repository.Path, result.Path);
        Assert.Equal(1, viewModel.RunProgressCompleted);
        Assert.Equal(1, viewModel.RunProgressTotal);
        Assert.Equal("1 of 1 repositories completed", viewModel.RunProgressText);
    }

    private static MainShellViewModel CreateViewModel(
        params RepositoryResultViewModel[] results)
    {
        return CreateViewModel(
            repositoryManagementService: null,
            launcher: null,
            results);
    }

    private static MainShellViewModel CreateViewModel(
        IRepositoryManagementService? repositoryManagementService,
        IFileSystemLauncher? launcher = null,
        params RepositoryResultViewModel[] results)
    {
        return new MainShellViewModel(
            TestRoot,
            [
                new CategoryNavigationItemViewModel("Plugins", Path.Combine(TestRoot, "Plugins"), 2, 1),
                new CategoryNavigationItemViewModel("Tools", Path.Combine(TestRoot, "Tools"), 1, 0)
            ],
            results,
            [],
            repositoryManagementService: repositoryManagementService,
            launcher: launcher);
    }

    private static async Task<MainShellViewModel> CreateHierarchicalTreeViewModelAsync()
    {
        var repositories = new[]
        {
            Descriptor("Dalamud Plugins/CombatReborn", "CombatReborn"),
            Descriptor("Dalamud Plugins/Punish", "Punish"),
            Descriptor("FF14_CS", "FF14_CS"),
            Descriptor("FF14_CS/ProjectChronofoil", "ProjectChronofoil"),
            Descriptor("Utils", "Utils")
        };
        var categories = new[]
        {
            "Dalamud Plugins",
            "Dalamud Plugins/CombatReborn",
            "Dalamud Plugins/Punish",
            "FF14_CS",
            "FF14_CS/ProjectChronofoil",
            "Utils"
        };
        var syncService = new FakeGitPullerSyncService(LoadResult(new GitPullerOptions(), repositories, categories));
        var viewModel = new MainShellViewModel(TestRoot, syncService);
        await viewModel.InitializeAsync();

        viewModel.RepositoryResults.Add(Result(
            "CombatReborn",
            RepositoryResultStatus.Failed,
            category: "Dalamud Plugins/CombatReborn"));
        viewModel.RepositoryResults.Add(Result(
            "Punish",
            RepositoryResultStatus.Clean,
            category: "Dalamud Plugins/Punish"));
        viewModel.RepositoryResults.Add(Result(
            "FF14_CS",
            RepositoryResultStatus.Updated,
            category: "FF14_CS"));
        viewModel.RepositoryResults.Add(Result(
            "ProjectChronofoil",
            RepositoryResultStatus.Warning,
            category: "FF14_CS/ProjectChronofoil"));
        viewModel.RepositoryResults.Add(Result(
            "Utils",
            RepositoryResultStatus.Updated,
            category: "Utils"));

        return viewModel;
    }

    private static RepositoryResultViewModel Result(
        string name,
        RepositoryResultStatus status,
        FailureDiagnostic? diagnostic = null,
        string category = "Plugins",
        string? remoteUrl = null)
    {
        return new RepositoryResultViewModel(
            name,
            category,
            Path.Combine(TestRoot, category, name),
            remoteUrl ?? $"https://github.com/example/{name}.git",
            status,
            newCommitsCount: status == RepositoryResultStatus.Updated ? 3 : 0,
            elapsed: TimeSpan.FromSeconds(2),
            diagnostic,
            [$"{name} diagnostic text that should remain available for wrapping in the shell."]);
    }

    private static FailureDiagnostic Diagnostic(RetryPolicy retryPolicy, DiagnosticSeverity severity)
    {
        return new FailureDiagnostic(
            FailureCategory.NetworkTimeout,
            retryPolicy,
            severity,
            "Diagnostic title",
            "Diagnostic explanation",
            "Diagnostic suggested action",
            "Diagnostic evidence",
            RelatedPath: null,
            RelatedCommand: "git fetch --all --prune");
    }

    private static FailureDiagnostic InvalidAddDiagnostic(string title)
    {
        return new FailureDiagnostic(
            FailureCategory.InvalidCloneRequest,
            RetryPolicy.BlockedUntilAction,
            DiagnosticSeverity.Error,
            title,
            "The add request is invalid.",
            "Fix the clone input and preview it again.",
            title,
            RelatedPath: null,
            RelatedCommand: null);
    }

    private static RepositoryDescriptor Descriptor(string category, string name)
    {
        return new RepositoryDescriptor(
            Path.Combine(TestRoot, category, name),
            name,
            category,
            $"https://github.com/example/{name}.git");
    }

    private static RepositoryAddPreview ValidAddPreview(RepositoryAddRequest request)
    {
        var repositoryName = string.IsNullOrWhiteSpace(request.FolderNameOverride)
            ? Path.GetFileNameWithoutExtension(new Uri(request.RemoteUrl).AbsolutePath)
            : request.FolderNameOverride;
        var targetPath = Path.Combine(TestRoot, request.Category, repositoryName);
        var repository = new RepositoryDescriptor(
            targetPath,
            repositoryName,
            request.Category,
            request.RemoteUrl);

        return new RepositoryAddPreview(
            TestRoot,
            request.Category,
            request.RemoteUrl,
            repositoryName,
            targetPath,
            repository,
            Diagnostic: null);
    }

    private static RepositoryAddPreview InvalidAddPreview(RepositoryAddRequest request, string title)
    {
        return new RepositoryAddPreview(
            TestRoot,
            request.Category,
            request.RemoteUrl,
            request.FolderNameOverride ?? string.Empty,
            TargetPath: string.Empty,
            Repository: null,
            Diagnostic: InvalidAddDiagnostic(title));
    }

    private static RemovedRepositoryRecord RemovedRecord(string name)
    {
        return RemovedRecord(name, TestRoot);
    }

    private static RemovedRepositoryRecord RemovedRecord(string name, string libraryRoot)
    {
        return new RemovedRepositoryRecord
        {
            Name = name,
            Category = "Plugins",
            OriginalPath = Path.Combine(libraryRoot, "Plugins", name),
            RemovedPath = Path.Combine(libraryRoot, ".mygitpuller", "removed", "Plugins", name),
            RemoteUrl = $"https://github.com/example/{name}.git",
            RemovedAt = DateTimeOffset.UtcNow
        };
    }

    private static void CreateRepositoryDirectory(string repositoryPath)
    {
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        File.WriteAllText(Path.Combine(repositoryPath, "README.md"), Path.GetFileName(repositoryPath));
    }

    private static string CreateBareRemoteRepository(string scenarioRoot, string repositoryName)
    {
        var remotePath = Path.Combine(scenarioRoot, $"{repositoryName}.git");
        var seedPath = Path.Combine(scenarioRoot, "seed");

        Directory.CreateDirectory(scenarioRoot);

        RunGit(scenarioRoot, "init", "--bare", remotePath);
        RunGit(scenarioRoot, "clone", remotePath, seedPath);
        RunGit(seedPath, "config", "user.name", "Test User");
        RunGit(seedPath, "config", "user.email", "test@example.invalid");
        RunGit(seedPath, "checkout", "-b", "main");
        File.WriteAllText(Path.Combine(seedPath, "README.md"), "seed");
        RunGit(seedPath, "add", "README.md");
        RunGit(seedPath, "commit", "-m", "Initial commit");
        RunGit(seedPath, "push", "-u", "origin", "main");
        RunGit(remotePath, "symbolic-ref", "HEAD", "refs/heads/main");

        return Path.GetFullPath(remotePath);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var processStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        processStartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        processStartInfo.Environment["GCM_INTERACTIVE"] = "never";

        using var process = System.Diagnostics.Process.Start(processStartInfo);
        Assert.NotNull(process);

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000), $"git command timed out: git {string.Join(' ', arguments)}");
        Task.WaitAll(standardOutput, standardError);

        var output = (standardOutput.Result + Environment.NewLine + standardError.Result).Trim();
        Assert.True(
            process.ExitCode == 0,
            $"git command failed ({process.ExitCode}): git {string.Join(' ', arguments)}{Environment.NewLine}{output}");
    }

    private static GitPullerLibraryLoadResult LoadResult(params RepositoryDescriptor[] repositories)
    {
        return LoadResult(new GitPullerOptions(), repositories, ["Plugins", "Tools"]);
    }

    private static GitPullerLibraryLoadResult LoadResult(
        GitPullerOptions options,
        IReadOnlyList<RepositoryDescriptor> repositories,
        IReadOnlyList<string> configuredCategories,
        IReadOnlyList<RemovedRepositoryRecord>? removedRepositories = null)
    {
        return new GitPullerLibraryLoadResult(
            TestRoot,
            options,
            new RepositoryInventory(TestRoot, repositories),
            removedRepositories ?? [],
            configuredCategories);
    }

    private static RepoResult RepoResultFor(
        RepositoryDescriptor repository,
        bool failed,
        int newCommits,
        FailureDiagnostic? diagnostic)
    {
        var result = new RepoResult
        {
            Path = repository.Path,
            Name = repository.Name,
            Failed = failed,
            NewCommitsCount = newCommits,
            Elapsed = TimeSpan.FromSeconds(1),
            Diagnostic = diagnostic
        };
        result.Logs.Add(new LogItem
        {
            Text = failed ? "fatal: simulated failure" : "fast-forwarded simulated repository",
            IsError = failed,
            IsCommit = newCommits > 0
        });
        return result;
    }

    private static PropertyChangedEventHandler TrackChangedProperty(List<string> changedProperties)
    {
        return (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
            {
                changedProperties.Add(args.PropertyName);
            }
        };
    }

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var path = Path.Combine([RepositoryRoot, .. relativeSegments]);
        return File.ReadAllText(path);
    }

    private static object FindTreeNode(MainShellViewModel viewModel, string fullCategoryName)
    {
        foreach (var rootNode in GetRequiredListProperty(viewModel, "RepositoryTreeNodes"))
        {
            var match = FindTreeNodeRecursive(rootNode, fullCategoryName);
            if (match is not null)
            {
                return match;
            }
        }

        throw new Xunit.Sdk.XunitException($"Tree node '{fullCategoryName}' was not found.");
    }

    private static object? FindTreeNodeRecursive(object node, string fullCategoryName)
    {
        if (GetRequiredPropertyValue<bool>(node, "IsFolder")
            && string.Equals(
            GetRequiredPropertyValue<string>(node, "FullCategoryName"),
            fullCategoryName,
            StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in GetRequiredListProperty(node, "Children"))
        {
            var match = FindTreeNodeRecursive(child, fullCategoryName);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static RepositoryTreeNodeViewModel FindRepositoryTreeNode(
        MainShellViewModel viewModel,
        string repositoryName)
    {
        foreach (var rootNode in viewModel.RepositoryTreeNodes)
        {
            var match = FindRepositoryTreeNodeRecursive(rootNode, repositoryName);
            if (match is not null)
            {
                return match;
            }
        }

        throw new Xunit.Sdk.XunitException($"Repository tree node '{repositoryName}' was not found.");
    }

    private static RepositoryTreeNodeViewModel? FindRepositoryTreeNodeRecursive(
        RepositoryTreeNodeViewModel node,
        string repositoryName)
    {
        if (node.IsRepository
            && string.Equals(node.Name, repositoryName, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindRepositoryTreeNodeRecursive(child, repositoryName);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static IReadOnlyList<object> GetRequiredListProperty(object instance, string propertyName)
    {
        var value = GetRequiredPropertyValue<object>(instance, propertyName);
        return Assert.IsAssignableFrom<System.Collections.IEnumerable>(value)
            .Cast<object>()
            .ToArray();
    }

    private static T GetRequiredPropertyValue<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = property.GetValue(instance);
        Assert.NotNull(value);
        return Assert.IsAssignableFrom<T>(value);
    }

    private static void SetRequiredPropertyValue(object instance, string propertyName, object value)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        property.SetValue(instance, value);
    }

    private sealed class FailingRepositoryManagementConfigStore : IRepositoryManagementConfigStore
    {
        public FailingRepositoryManagementConfigStore(LibraryConfig persistedConfig)
        {
            PersistedConfig = CloneConfig(persistedConfig);
        }

        public LibraryConfig PersistedConfig { get; private set; }
        public bool ThrowOnSave { get; set; }
        public int? ThrowOnSaveCall { get; set; }
        public int SaveCallCount { get; private set; }

        public Task<LibraryConfig> LoadAsync(string libraryRoot, CancellationToken cancellationToken)
        {
            return Task.FromResult(CloneConfig(PersistedConfig));
        }

        public Task SaveAsync(LibraryConfig config, CancellationToken cancellationToken)
        {
            SaveCallCount++;
            if (ThrowOnSave || ThrowOnSaveCall == SaveCallCount)
            {
                throw new InvalidOperationException("Injected config save failure.");
            }

            PersistedConfig = CloneConfig(config);
            return Task.CompletedTask;
        }

        private static LibraryConfig CloneConfig(LibraryConfig config)
        {
            return new LibraryConfig
            {
                LibraryRoot = config.LibraryRoot,
                Categories = [.. config.Categories],
                Repositories = config.Repositories.Select(repository => new LibraryRepositoryConfig
                {
                    Name = repository.Name,
                    Path = repository.Path,
                    Category = repository.Category,
                    RemoteUrl = repository.RemoteUrl
                }).ToList(),
                RemovedRepositories = config.RemovedRepositories.Select(removed => new RemovedRepositoryRecord
                {
                    Name = removed.Name,
                    OriginalPath = removed.OriginalPath,
                    RemovedPath = removed.RemovedPath,
                    Category = removed.Category,
                    RemoteUrl = removed.RemoteUrl,
                    RemovedAt = removed.RemovedAt
                }).ToList(),
                DefaultOptions = config.DefaultOptions
            };
        }
    }

    private sealed class RecordingRemovedRepositoryDirectoryDeleter : IRemovedRepositoryDirectoryDeleter
    {
        private int deleteCallCount;

        public int DeleteCallCount => deleteCallCount;

        public void Delete(string removedPath)
        {
            Interlocked.Increment(ref deleteCallCount);
            Directory.Delete(removedPath, recursive: true);
        }
    }

    private sealed class ThrowingRemovedRepositoryDirectoryDeleter : IRemovedRepositoryDirectoryDeleter
    {
        private int deleteCallCount;

        public int DeleteCallCount => deleteCallCount;

        public void Delete(string removedPath)
        {
            Interlocked.Increment(ref deleteCallCount);
            throw new IOException("Injected physical delete failure.");
        }
    }

    private sealed class FakeRepositoryManagementService : IRepositoryManagementService
    {
        private int restoreCallCount;
        private int restoreAsCallCount;
        private int deleteCallCount;

        public Func<RepositoryAddRequest, RepositoryAddPreview>? PreviewHandler { get; set; }
        public Func<RepositoryAddRequest, GitPullerOptions, CancellationToken, Task<RepositoryAddWorkflowResult>>? CloneHandler { get; set; }
        public Func<string, GitPullerOptions, CancellationToken, Task<GitPullerLibraryLoadResult>>? SaveOptionsHandler { get; set; }
        public Func<string, RemovedRepositoryRecord, CancellationToken, Task<GitPullerLibraryLoadResult>>? RestoreHandler { get; set; }
        public Func<string, RemovedRepositoryRecord, string, string, CancellationToken, Task<GitPullerLibraryLoadResult>>? RestoreAsHandler { get; set; }
        public Func<string, RemovedRepositoryRecord, CancellationToken, Task<GitPullerLibraryLoadResult>>? DeleteHandler { get; set; }
        public RepositoryAddRequest? LastPreviewRequest { get; private set; }
        public RepositoryAddRequest? LastCloneRequest { get; private set; }
        public int RestoreCallCount => restoreCallCount;
        public int RestoreAsCallCount => restoreAsCallCount;
        public int DeleteCallCount => deleteCallCount;

        public RepositoryAddPreview PreviewAddRepository(RepositoryAddRequest request)
        {
            LastPreviewRequest = request;
            return PreviewHandler?.Invoke(request)
                ?? InvalidAddPreview(request, "Preview handler was not configured");
        }

        public Task<RepositoryAddWorkflowResult> CloneRepositoryAsync(
            RepositoryAddRequest request,
            GitPullerOptions options,
            CancellationToken cancellationToken)
        {
            LastCloneRequest = request;
            return CloneHandler?.Invoke(request, options, cancellationToken)
                ?? Task.FromResult(new RepositoryAddWorkflowResult(
                    new RepositoryAddResult(
                        InvalidAddPreview(request, "Clone handler was not configured"),
                        Repository: null,
                        Diagnostic: InvalidAddDiagnostic("Clone handler was not configured"),
                        GitResult: null),
                    LibraryLoadResult: null));
        }

        public Task<GitPullerLibraryLoadResult> SaveDefaultOptionsAsync(
            string libraryRoot,
            GitPullerOptions options,
            CancellationToken cancellationToken)
        {
            return SaveOptionsHandler?.Invoke(libraryRoot, options, cancellationToken)
                ?? Task.FromResult(LoadResult(options, [], ["Plugins", "Tools"]));
        }

        public Task<GitPullerLibraryLoadResult> RestoreRepositoryAsync(
            string libraryRoot,
            RemovedRepositoryRecord removedRepository,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref restoreCallCount);
            return RestoreHandler?.Invoke(libraryRoot, removedRepository, cancellationToken)
                ?? Task.FromResult(LoadResult(new GitPullerOptions(), [], ["Plugins", "Tools"]));
        }

        public Task<GitPullerLibraryLoadResult> RestoreRepositoryAsAsync(
            string libraryRoot,
            RemovedRepositoryRecord removedRepository,
            string category,
            string folderName,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref restoreAsCallCount);
            return RestoreAsHandler?.Invoke(libraryRoot, removedRepository, category, folderName, cancellationToken)
                ?? Task.FromResult(LoadResult(new GitPullerOptions(), [], ["Plugins", "Tools"]));
        }

        public Task<GitPullerLibraryLoadResult> PermanentlyDeleteRepositoryAsync(
            string libraryRoot,
            RemovedRepositoryRecord removedRepository,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref deleteCallCount);
            return DeleteHandler?.Invoke(libraryRoot, removedRepository, cancellationToken)
                ?? Task.FromResult(LoadResult(new GitPullerOptions(), [], ["Plugins", "Tools"]));
        }
    }

    private sealed class FakeFileSystemLauncher : IFileSystemLauncher
    {
        public List<string> LaunchedPaths { get; } = [];
        public List<string?> LaunchedUris { get; } = [];

        public Task<bool> LaunchPathAsync(string path)
        {
            LaunchedPaths.Add(path);
            return Task.FromResult(true);
        }

        public Task<bool> LaunchUriAsync(string uri)
        {
            LaunchedUris.Add(uri);
            return Task.FromResult(true);
        }
    }

    private sealed class QueuedViewModelDispatcher : IViewModelDispatcher
    {
        private readonly Queue<Action> actions = new();

        public void Enqueue(Action action)
        {
            actions.Enqueue(action);
        }

        public Task EnqueueAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            actions.Enqueue(() =>
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
            });

            return completion.Task;
        }

        public void FlushAll()
        {
            while (actions.Count > 0)
            {
                actions.Dequeue()();
            }
        }
    }

    private sealed class FakeGitPullerSyncService : IGitPullerSyncService
    {
        private readonly GitPullerLibraryLoadResult loadResult;
        private readonly TaskCompletionSource firstRepositoryCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int loadCallCount;
        private int runAllCallCount;
        private int retryCallCount;

        public FakeGitPullerSyncService(GitPullerLibraryLoadResult loadResult)
        {
            this.loadResult = loadResult;
        }

        public Func<string, CancellationToken, Task<GitPullerLibraryLoadResult>>? LoadLibraryAsyncHandler { get; set; }
        public Func<GitPullerRunRequest, IProgress<GitPullerProgressEvent>?, CancellationToken, Task<GitPullerRunResult>>? RunAllAsyncHandler { get; set; }
        public Func<GitPullerRunRequest, string, IProgress<GitPullerProgressEvent>?, CancellationToken, Task<RepoResult>>? RetryRepositoryAsyncHandler { get; set; }
        public int LoadCallCount => loadCallCount;
        public int RunAllCallCount => runAllCallCount;
        public int RetryCallCount => retryCallCount;

        public string GetDefaultLibraryRoot()
        {
            return loadResult.LibraryRoot;
        }

        public Task<GitPullerLibraryLoadResult> LoadLibraryAsync(string libraryRoot, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loadCallCount);
            return LoadLibraryAsyncHandler?.Invoke(libraryRoot, cancellationToken)
                ?? Task.FromResult(loadResult);
        }

        public Task<GitPullerRunResult> RunAllAsync(
            GitPullerRunRequest request,
            IProgress<GitPullerProgressEvent>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runAllCallCount);
            var trackingProgress = progress is null
                ? null
                : new TrackingProgress(progress, firstRepositoryCompletion);
            return RunAllAsyncHandler?.Invoke(request, trackingProgress, cancellationToken)
                ?? Task.FromResult(new GitPullerRunResult { RepositoryResults = [] });
        }

        public Task<RepoResult> RetryRepositoryAsync(
            GitPullerRunRequest previousRunRequest,
            string repoPath,
            IProgress<GitPullerProgressEvent>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref retryCallCount);
            return RetryRepositoryAsyncHandler?.Invoke(previousRunRequest, repoPath, progress, cancellationToken)
                ?? Task.FromResult(new RepoResult { Path = repoPath, Name = Path.GetFileName(repoPath) });
        }

        public Task WaitForFirstRepositoryCompletionAsync()
        {
            return firstRepositoryCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private sealed class TrackingProgress : IProgress<GitPullerProgressEvent>
        {
            private readonly IProgress<GitPullerProgressEvent> inner;
            private readonly TaskCompletionSource firstRepositoryCompletion;

            public TrackingProgress(
                IProgress<GitPullerProgressEvent> inner,
                TaskCompletionSource firstRepositoryCompletion)
            {
                this.inner = inner;
                this.firstRepositoryCompletion = firstRepositoryCompletion;
            }

            public void Report(GitPullerProgressEvent value)
            {
                inner.Report(value);
                if (value.Kind == GitPullerProgressEventKind.RepositoryCompleted)
                {
                    firstRepositoryCompletion.TrySetResult();
                }
            }
        }
    }
}

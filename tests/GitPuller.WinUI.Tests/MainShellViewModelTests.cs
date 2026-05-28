using System.ComponentModel;
using GitPuller;
using GitPuller_WinUI.ViewModels;

namespace GitPuller.WinUI.Tests;

public sealed class MainShellViewModelTests
{
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
    public void VisibleResults_HidesCleanRowsByDefault()
    {
        var viewModel = CreateViewModel(
            Result("clean", RepositoryResultStatus.Clean),
            Result("updated", RepositoryResultStatus.Updated));

        Assert.False(viewModel.ShowCleanRepositories);
        Assert.DoesNotContain(viewModel.VisibleResults, result => result.Status == RepositoryResultStatus.Clean);
        Assert.Contains(viewModel.VisibleResults, result => result.Status == RepositoryResultStatus.Updated);
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
            RemovedPath = @"E:\Library\.mygitpuller\removed\Plugins\DeletedRepo",
            OriginalPath = @"E:\Library\Plugins\DeletedRepo",
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
                RemovedPath = @"E:\Library\.mygitpuller\removed\Plugins\Removed",
                OriginalPath = @"E:\Library\Plugins\Removed",
                RemovedAt = DateTimeOffset.UtcNow
            },
            _ => true,
            _ => false));

        Assert.Equal(1, viewModel.RemovedRepositoryCount);
        Assert.Contains(nameof(MainShellViewModel.RemovedRepositoryCount), changedProperties);
    }

    private static MainShellViewModel CreateViewModel(params RepositoryResultViewModel[] results)
    {
        return new MainShellViewModel(
            @"E:\FF14\Repos\MyRepos",
            [
                new CategoryNavigationItemViewModel("Plugins", @"E:\FF14\Repos\MyRepos\Plugins", 2, 1),
                new CategoryNavigationItemViewModel("Tools", @"E:\FF14\Repos\MyRepos\Tools", 1, 0)
            ],
            results,
            []);
    }

    private static RepositoryResultViewModel Result(
        string name,
        RepositoryResultStatus status,
        FailureDiagnostic? diagnostic = null,
        string category = "Plugins")
    {
        return new RepositoryResultViewModel(
            name,
            category,
            @$"E:\FF14\Repos\MyRepos\{category}\{name}",
            $"https://github.com/example/{name}.git",
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
}

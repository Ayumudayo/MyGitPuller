using System.ComponentModel;
using GitPuller;
using GitPuller_WinUI.Services;
using GitPuller_WinUI.ViewModels;

namespace GitPuller.WinUI.Tests;

public sealed class MainShellViewModelTests
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "MyGitPullerWinUITests");

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

    private static MainShellViewModel CreateViewModel(params RepositoryResultViewModel[] results)
    {
        return new MainShellViewModel(
            TestRoot,
            [
                new CategoryNavigationItemViewModel("Plugins", Path.Combine(TestRoot, "Plugins"), 2, 1),
                new CategoryNavigationItemViewModel("Tools", Path.Combine(TestRoot, "Tools"), 1, 0)
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
            Path.Combine(TestRoot, category, name),
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

    private static RepositoryDescriptor Descriptor(string category, string name)
    {
        return new RepositoryDescriptor(
            Path.Combine(TestRoot, category, name),
            name,
            category,
            $"https://github.com/example/{name}.git");
    }

    private static GitPullerLibraryLoadResult LoadResult(params RepositoryDescriptor[] repositories)
    {
        return new GitPullerLibraryLoadResult(
            TestRoot,
            new GitPullerOptions(),
            new RepositoryInventory(TestRoot, repositories),
            [],
            ["Plugins", "Tools"]);
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

    private sealed class FakeGitPullerSyncService : IGitPullerSyncService
    {
        private readonly GitPullerLibraryLoadResult loadResult;
        private readonly TaskCompletionSource firstRepositoryCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeGitPullerSyncService(GitPullerLibraryLoadResult loadResult)
        {
            this.loadResult = loadResult;
        }

        public Func<string, CancellationToken, Task<GitPullerLibraryLoadResult>>? LoadLibraryAsyncHandler { get; set; }
        public Func<GitPullerRunRequest, IProgress<GitPullerProgressEvent>?, CancellationToken, Task<GitPullerRunResult>>? RunAllAsyncHandler { get; set; }
        public Func<GitPullerRunRequest, string, IProgress<GitPullerProgressEvent>?, CancellationToken, Task<RepoResult>>? RetryRepositoryAsyncHandler { get; set; }

        public string GetDefaultLibraryRoot()
        {
            return loadResult.LibraryRoot;
        }

        public Task<GitPullerLibraryLoadResult> LoadLibraryAsync(string libraryRoot, CancellationToken cancellationToken)
        {
            return LoadLibraryAsyncHandler?.Invoke(libraryRoot, cancellationToken)
                ?? Task.FromResult(loadResult);
        }

        public Task<GitPullerRunResult> RunAllAsync(
            GitPullerRunRequest request,
            IProgress<GitPullerProgressEvent>? progress,
            CancellationToken cancellationToken)
        {
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

using System.Diagnostics;
using GitPuller;

namespace GitPuller.Core.Tests;

public sealed class GitPullerContractsAndRunnerTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "MyGitPuller.Core.Tests", Guid.NewGuid().ToString("N"));

    public GitPullerContractsAndRunnerTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void GitPullerOptions_DefaultsMatchDestructiveBackupMode()
    {
        var options = new GitPullerOptions();

        Assert.True(options.ForceSync);
        Assert.True(options.CleanUntracked);
        Assert.True(options.SyncAllBranches);
        Assert.Equal(6, options.MaxDegreeOfParallelism);
    }

    [Fact]
    public void RunRequest_PreservesSuppliedLibraryRootScope()
    {
        var libraryRoot = Path.Combine(tempRoot, "Library Root");
        var repositoryPath = Path.Combine(libraryRoot, "Plugins", "RepoA");
        var inventory = new RepositoryInventory(
            libraryRoot,
            new[]
            {
                new RepositoryDescriptor(repositoryPath, "RepoA", "Plugins", RemoteUrl: null)
            });

        var request = new GitPullerRunRequest(new GitPullerOptions(), inventory);

        Assert.Equal(libraryRoot, request.Inventory.LibraryRoot);
        Assert.Single(request.Inventory.Repositories);
        Assert.Equal(repositoryPath, request.Inventory.Repositories[0].Path);
        Assert.StartsWith(request.Inventory.LibraryRoot, request.Inventory.Repositories[0].Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunRepositoryAsync_RejectsSingleRepositoryOutsideInventory_WithZeroOperations()
    {
        var libraryRoot = Path.Combine(tempRoot, "inventory-root");
        Directory.CreateDirectory(libraryRoot);

        var inventoryRepository = Path.Combine(libraryRoot, "RepoInInventory");
        var outsideRepository = Path.Combine(tempRoot, "outside-root", "RepoOutsideInventory");

        var request = new GitPullerRunRequest(
            new GitPullerOptions(),
            new RepositoryInventory(
                libraryRoot,
                new[]
                {
                    new RepositoryDescriptor(inventoryRepository, "RepoInInventory", string.Empty, RemoteUrl: null)
                }));

        var runner = new GitPullerRunner();
        var result = await runner.RunRepositoryAsync(request, outsideRepository, progress: null, CancellationToken.None);

        Assert.True(result.Failed);
        Assert.Empty(result.Operations);
        Assert.Equal(outsideRepository, result.Path);
        Assert.Equal("RepoOutsideInventory", result.Name);
        Assert.NotEqual(default, result.StartedAt);
        Assert.NotEqual(default, result.CompletedAt);
        Assert.True(result.CompletedAt >= result.StartedAt);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.True(result.WorkerSlot > 0);
        Assert.Contains(result.Logs, log => log.IsError && log.Text.Contains("not part of the selected repository inventory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunRepositoryAsync_ForSingleInventoryRepository_StampsTimingAndWorkerSlot()
    {
        var repository = CreateTrackedRepository("single-repo");
        var request = new GitPullerRunRequest(
            new GitPullerOptions(),
            new RepositoryInventory(repository.LibraryRoot, new[] { repository.Descriptor }));

        var runner = new GitPullerRunner();
        var invocationStartedAt = DateTimeOffset.Now;
        var result = await runner.RunRepositoryAsync(request, repository.Descriptor.Path, progress: null, CancellationToken.None);
        var invocationCompletedAt = DateTimeOffset.Now;

        Assert.False(result.Failed);
        Assert.Equal(repository.Descriptor.Path, result.Path);
        Assert.Equal(repository.Descriptor.Name, result.Name);
        Assert.NotEmpty(result.Operations);
        Assert.NotEqual(default, result.StartedAt);
        Assert.NotEqual(default, result.CompletedAt);
        Assert.True(result.StartedAt >= invocationStartedAt.AddSeconds(-1));
        Assert.True(result.CompletedAt <= invocationCompletedAt.AddSeconds(1));
        Assert.True(result.CompletedAt >= result.StartedAt);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.True(result.WorkerSlot > 0);
    }

    [Fact]
    public async Task RunRepositoryAsync_DoesNotFetchLfs_WhenGitAttributesHasNoLfsFilters()
    {
        var repository = CreateTrackedRepository(
            "non-lfs-gitattributes",
            gitAttributesText:
                "# Auto detect text files and perform LF normalization\n"
                + "* text=auto eol=lf\n"
                + "*.cs text diff=csharp\n");
        var request = new GitPullerRunRequest(
            new GitPullerOptions(),
            new RepositoryInventory(repository.LibraryRoot, new[] { repository.Descriptor }));

        var runner = new GitPullerRunner();
        var result = await runner.RunRepositoryAsync(request, repository.Descriptor.Path, progress: null, CancellationToken.None);

        Assert.False(result.Failed);
        Assert.DoesNotContain(
            result.Operations,
            operation => operation.Command.Contains("git lfs fetch --all --prune", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Logs,
            log => log.Text.Contains("Git LFS fetch failed", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private TrackedRepository CreateTrackedRepository(string scenarioName, string? gitAttributesText = null)
    {
        var scenarioRoot = Path.Combine(tempRoot, scenarioName);
        var remotePath = Path.Combine(scenarioRoot, "remote.git");
        var seedPath = Path.Combine(scenarioRoot, "seed");
        var libraryRoot = Path.Combine(scenarioRoot, "library");
        var repositoryPath = Path.Combine(libraryRoot, "RepoOne");

        Directory.CreateDirectory(scenarioRoot);
        Directory.CreateDirectory(libraryRoot);

        RunGit(scenarioRoot, "init", "--bare", remotePath);
        RunGit(scenarioRoot, "clone", remotePath, seedPath);
        RunGit(seedPath, "config", "user.name", "Test User");
        RunGit(seedPath, "config", "user.email", "test@example.invalid");
        RunGit(seedPath, "checkout", "-b", "main");

        File.WriteAllText(Path.Combine(seedPath, "README.md"), "seed");
        if (gitAttributesText is not null)
        {
            File.WriteAllText(Path.Combine(seedPath, ".gitattributes"), gitAttributesText);
        }

        RunGit(seedPath, "add", "README.md");
        if (gitAttributesText is not null)
        {
            RunGit(seedPath, "add", ".gitattributes");
        }

        RunGit(seedPath, "commit", "-m", "Initial commit");
        RunGit(seedPath, "push", "-u", "origin", "main");
        RunGit(remotePath, "symbolic-ref", "HEAD", "refs/heads/main");

        RunGit(scenarioRoot, "clone", "--branch", "main", remotePath, repositoryPath);

        return new TrackedRepository(
            libraryRoot,
            new RepositoryDescriptor(repositoryPath, "RepoOne", string.Empty, remotePath));
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var processStartInfo = new ProcessStartInfo
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

        using var process = Process.Start(processStartInfo);
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

    private sealed record TrackedRepository(string LibraryRoot, RepositoryDescriptor Descriptor);
}

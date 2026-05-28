namespace GitPuller.Core.Tests;

public sealed class GitPullerReportWriterTests : IDisposable
{
    private readonly string tempRoot;

    public GitPullerReportWriterTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "MyGitPullerReportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void WriteReports_WritesTimestampedAndLatestReportsUnderLibraryRoot()
    {
        var runResult = CreateRunResult();

        var written = GitPullerReportWriter.WriteReports(
            tempRoot,
            runResult,
            new GitPullerOptions { MaxDegreeOfParallelism = 3, VerboseReport = true },
            runId: "test-run");

        Assert.Equal(Path.Combine(tempRoot, "git_update_report-test-run.md"), written.RunReportPath);
        Assert.Equal(Path.Combine(tempRoot, GitPullerReportWriter.LatestReportFileName), written.LatestReportPath);
        Assert.True(File.Exists(written.RunReportPath));
        Assert.True(File.Exists(written.LatestReportPath));

        var latestReport = File.ReadAllText(written.LatestReportPath);
        Assert.Contains("# Git Update Report", latestReport);
        Assert.Contains("- Requested Workers: 3", latestReport);
        Assert.Contains("## Worker Execution Details", latestReport);
        Assert.Contains("git fetch --all", latestReport);
    }

    [Fact]
    public void BuildReport_LeavesOperationDetailsOut_WhenVerboseReportIsDisabled()
    {
        var report = GitPullerReportWriter.BuildReport(
            CreateRunResult(),
            new GitPullerOptions { VerboseReport = false });

        Assert.DoesNotContain("## Worker Execution Details", report);
        Assert.DoesNotContain("Operations:", report);
        Assert.Contains("UpdatedRepo", report);
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

    private static GitPullerRunResult CreateRunResult()
    {
        var result = new RepoResult
        {
            Name = "UpdatedRepo",
            Path = @"E:\Repos\UpdatedRepo",
            NewCommitsCount = 2,
            WorkerSlot = 1,
            StartedAt = DateTimeOffset.Now.AddSeconds(-2),
            CompletedAt = DateTimeOffset.Now,
            Elapsed = TimeSpan.FromSeconds(2)
        };
        result.Operations.Add(new RepoOperation
        {
            Command = "git fetch --all",
            WorkingDirectory = result.Path,
            StartedAt = result.StartedAt,
            Elapsed = TimeSpan.FromSeconds(1),
            ExitCode = 0
        });
        result.Logs.Add(new LogItem
        {
            Text = "abc123 Update",
            IsCommit = true
        });

        return new GitPullerRunResult
        {
            RepositoryResults = [result],
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            Elapsed = TimeSpan.FromSeconds(2)
        };
    }
}

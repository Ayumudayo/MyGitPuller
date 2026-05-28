using System.Text;

namespace GitPuller;

public sealed record GitPullerReportWriteResult(
    string RunReportPath,
    string LatestReportPath,
    string ReportText);

public static class GitPullerReportWriter
{
    public const string LatestReportFileName = "git_update_report.md";

    public static GitPullerReportWriteResult WriteReports(
        string libraryRoot,
        GitPullerRunResult runResult,
        GitPullerOptions options,
        string? runId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(runResult);
        ArgumentNullException.ThrowIfNull(options);

        var normalizedRoot = Path.GetFullPath(libraryRoot);
        Directory.CreateDirectory(normalizedRoot);

        var reportText = BuildReport(runResult, options);
        var reportPath = Path.Combine(normalizedRoot, $"git_update_report-{GetRunId(runId)}.md");
        var latestReportPath = Path.Combine(normalizedRoot, LatestReportFileName);

        File.WriteAllText(reportPath, reportText, Encoding.UTF8);
        File.WriteAllText(latestReportPath, reportText, Encoding.UTF8);

        return new GitPullerReportWriteResult(reportPath, latestReportPath, reportText);
    }

    public static string BuildReport(GitPullerRunResult runResult, GitPullerOptions options)
    {
        ArgumentNullException.ThrowIfNull(runResult);
        ArgumentNullException.ThrowIfNull(options);

        var orderedResults = runResult.RepositoryResults
            .OrderBy(result => result.StartedAt == default ? DateTimeOffset.MaxValue : result.StartedAt)
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workerGroups = orderedResults
            .GroupBy(result => result.WorkerSlot)
            .OrderBy(group => group.Key)
            .ToList();

        var reportBuilder = new StringBuilder();
        reportBuilder.AppendLine("# Git Update Report");
        reportBuilder.AppendLine($"Generated: {DateTime.Now}");
        reportBuilder.AppendLine();
        reportBuilder.AppendLine("## Run Summary");
        reportBuilder.AppendLine($"- Total Repositories: {runResult.TotalRepositories}");
        reportBuilder.AppendLine($"- Requested Workers: {options.MaxDegreeOfParallelism}");
        reportBuilder.AppendLine($"- Successful Repositories: {runResult.SuccessCount}");
        reportBuilder.AppendLine($"- Failed Repositories: {runResult.FailCount}");
        reportBuilder.AppendLine($"- Total New Commits: {runResult.TotalNewCommitsCount}");
        reportBuilder.AppendLine($"- Wall-clock Elapsed: {runResult.Elapsed.TotalSeconds:F2}s");
        reportBuilder.AppendLine();

        if (options.VerboseReport)
        {
            reportBuilder.AppendLine("## Worker Execution Details");
            foreach (var workerGroup in workerGroups)
            {
                var workerTotal = TimeSpan.FromTicks(workerGroup.Sum(result => result.Elapsed.Ticks));
                reportBuilder.AppendLine($"### Worker {workerGroup.Key}");
                reportBuilder.AppendLine($"- Repositories Handled: {workerGroup.Count()}");
                reportBuilder.AppendLine($"- Cumulative Repository Time: {workerTotal.TotalSeconds:F2}s");
                reportBuilder.AppendLine();

                foreach (var result in workerGroup)
                {
                    reportBuilder.AppendLine($"#### {result.Name}");
                    reportBuilder.AppendLine($"- Repository Path: `{result.Path}`");
                    reportBuilder.AppendLine($"- Started At: {result.StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
                    reportBuilder.AppendLine($"- Completed At: {result.CompletedAt:yyyy-MM-dd HH:mm:ss zzz}");
                    reportBuilder.AppendLine($"- Total Elapsed: {result.Elapsed.TotalSeconds:F2}s");

                    if (result.Operations.Count > 0)
                    {
                        reportBuilder.AppendLine("- Operations:");
                        foreach (var operation in result.Operations)
                        {
                            var status = operation.TimedOut
                                ? "timeout"
                                : operation.ExitCode == 0 ? "ok" : $"rc={operation.ExitCode}";
                            reportBuilder.AppendLine($"  - {operation.StartedAt:HH:mm:ss} | `{operation.Command}` | {operation.Elapsed.TotalSeconds:F2}s | {status}");
                        }
                    }
                    else
                    {
                        reportBuilder.AppendLine("- Operations: none recorded");
                    }

                    reportBuilder.AppendLine();
                }
            }
        }

        reportBuilder.AppendLine("## Repository Result Notes");
        foreach (var result in orderedResults)
        {
            var icon = result.Failed ? "❌" : "✅";
            reportBuilder.AppendLine($"## {icon} {result.Name}");
            if (result.Failed)
            {
                reportBuilder.AppendLine("**FAILED**");
            }

            if (result.NewCommitsCount > 0)
            {
                reportBuilder.AppendLine($"- New Commits: {result.NewCommitsCount}");
            }

            if (result.Logs.Count > 0)
            {
                reportBuilder.AppendLine("```");
                foreach (var log in result.Logs)
                {
                    reportBuilder.AppendLine(log.Text);
                }

                reportBuilder.AppendLine("```");
            }

            reportBuilder.AppendLine();
        }

        return reportBuilder.ToString();
    }

    private static string GetRunId(string? runId)
    {
        return string.IsNullOrWhiteSpace(runId)
            ? DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")
            : runId.Trim();
    }
}

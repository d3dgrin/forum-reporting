namespace Reporting.Api.Reports;

/// <summary>
/// TEMPORARY (Iteration 2 only): lets the API run end-to-end before persistence exists.
/// Replaced in Iteration 3 by the PostgreSQL implementation, which enforces uniqueness with a unique index
/// and counts atomically in one transaction. Not used in production; state is lost on restart.
/// </summary>
public sealed class InMemoryReportService : IReportService
{
    public const int BlockThreshold = 5;

    private readonly Lock _lock = new();
    private readonly HashSet<(string Reporter, string Reported)> _pairs = [];
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public Task<SubmitReportResult> SubmitAsync(NewReport report, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_pairs.Add((report.ReporterUserId, report.ReportedUserId)))
            {
                return Task.FromResult<SubmitReportResult>(new SubmitReportResult.Duplicate());
            }

            var count = _counts[report.ReportedUserId] = _counts.GetValueOrDefault(report.ReportedUserId) + 1;
            return Task.FromResult<SubmitReportResult>(
                new SubmitReportResult.Created(Guid.NewGuid(), count, count >= BlockThreshold));
        }
    }
}

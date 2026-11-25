namespace Reporting.Api.Reports;

/// <summary>
/// Request body. Deliberately has NO reporter or reported-user fields: the reporter comes from the
/// authenticated identity and the reported user is the post's author (looked up in Posts).
/// </summary>
public sealed record SubmitReportRequest(string? PostId, string? Reason);

/// <param name="BlockRequested">
/// STATE, not transition: true when a block has been requested for the reported user, whether by this report
/// (the 5th) or an earlier one. The block request event itself is created exactly once (Iteration 3:
/// conditional update on <c>block_requested_at IS NULL</c>).
/// </param>
public sealed record SubmitReportResponse(Guid ReportId, string ReportedUserId, int UniqueReportCount, bool BlockRequested);

/// <summary>A validated report ready to be stored.</summary>
public sealed record NewReport(string ReporterUserId, string ReportedUserId, string PostId, string Reason);

public abstract record SubmitReportResult
{
    private SubmitReportResult() { }

    /// <summary>Stored and counted. <paramref name="BlockRequested"/> is state: true once a block has been requested for the user (by this or an earlier report).</summary>
    public sealed record Created(Guid ReportId, int UniqueReportCount, bool BlockRequested) : SubmitReportResult;

    /// <summary>This reporter has already reported this user (on any post). Nothing stored, nothing counted.</summary>
    public sealed record Duplicate : SubmitReportResult;
}

public interface IReportService
{
    Task<SubmitReportResult> SubmitAsync(NewReport report, CancellationToken cancellationToken);
}

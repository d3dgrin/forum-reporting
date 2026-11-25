using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reporting.Api.Persistence;

namespace Reporting.Api.Reports;

/// <param name="BlockRequestedAt">
/// Non-null = Reporting has requested a block. Until Account.Api processes the event, the account is
/// effectively "BlockPending" (eventual consistency).
/// </param>
public sealed record ReportSummaryResponse(string UserId, int UniqueReportCount, int BlockThreshold, DateTimeOffset? BlockRequestedAt);

public static class ReportSummaryEndpoint
{
    /// <summary>
    /// GET /api/reports/users/{userId}/summary. Demo/support view.
    /// Production: restricted to a moderator/support role, not any authenticated user.
    /// </summary>
    public static async Task<IResult> HandleAsync(
        string userId, ReportingDbContext db, IOptions<ReportingOptions> options, CancellationToken cancellationToken)
    {
        if (!Identifiers.IsValidUserId(userId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["userId"] = ["Invalid user ID."] });
        }

        var counter = await db.ReportedUserCounters.AsNoTracking()
            .SingleOrDefaultAsync(c => c.ReportedUserId == userId, cancellationToken);

        return TypedResults.Ok(new ReportSummaryResponse(
            userId, counter?.UniqueReportCount ?? 0, options.Value.BlockThreshold, counter?.BlockRequestedAt));
    }
}

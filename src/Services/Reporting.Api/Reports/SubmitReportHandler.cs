using Reporting.Api.Posts;

namespace Reporting.Api.Reports;

/// <summary>
/// Application flow for POST /api/reports: validate → look up the post's author → reject self-reports → store.
/// Duplicate prevention and counting happen in <see cref="IReportService"/> (database-enforced, Iteration 3).
/// </summary>
public sealed class SubmitReportHandler(IPostsClient posts, IReportService reports, ILogger<SubmitReportHandler> logger)
{
    public const int RetryAfterSeconds = 5;

    public async Task<IResult> HandleAsync(string reporterUserId, SubmitReportRequest? request, CancellationToken cancellationToken)
    {
        if (!SubmitReportValidator.TryValidate(request, out var input, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        PostInfo? post;
        try
        {
            post = await posts.FindPostAsync(input.PostId, cancellationToken);
        }
        catch (PostsServiceUnavailableException ex)
        {
            logger.LogWarning(ex, "Posts service unavailable while reporting post {PostId}", input.PostId);
            return new RetryAfterResult(RetryAfterSeconds, TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Posts service unavailable", detail: "The report could not be checked right now. Please retry."));
        }

        if (post is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Post not found", detail: $"Post '{input.PostId}' does not exist.");
        }

        if (string.Equals(post.AuthorId, reporterUserId, StringComparison.Ordinal))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Self-report not allowed", detail: "You cannot report your own post.");
        }

        var result = await reports.SubmitAsync(
            new NewReport(reporterUserId, post.AuthorId, post.PostId, input.Reason), cancellationToken);

        switch (result)
        {
            case SubmitReportResult.Created created:
                logger.LogInformation(
                    "Report {ReportId}: {ReporterUserId} reported {ReportedUserId} (post {PostId}); unique count {Count}, block requested {BlockRequested}",
                    created.ReportId, reporterUserId, post.AuthorId, post.PostId, created.UniqueReportCount, created.BlockRequested);
                return TypedResults.Created((string?)null,
                    new SubmitReportResponse(created.ReportId, post.AuthorId, created.UniqueReportCount, created.BlockRequested));

            case SubmitReportResult.Duplicate:
                logger.LogInformation("Duplicate report ignored: {ReporterUserId} already reported {ReportedUserId}",
                    reporterUserId, post.AuthorId);
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Already reported", detail: "You have already reported this user. Duplicate reports are not counted.");

            default:
                throw new InvalidOperationException($"Unexpected result {result.GetType().Name}.");
        }
    }
}

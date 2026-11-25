using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Reporting.Api.Reports;

namespace Reporting.UnitTests;

public class SubmitReportHandlerTests
{
    private readonly FakePostsClient _posts = new FakePostsClient()
        .With("post-1", "target-1")
        .With("post-11", "reporter-1");

    private static SubmitReportHandler Handler(FakePostsClient posts, RecordingReportService reports) =>
        new(posts, reports, NullLogger<SubmitReportHandler>.Instance);

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    [Fact]
    public async Task Created_maps_to_201_with_count_and_targets_the_post_author()
    {
        var reportId = Guid.NewGuid();
        var reports = new RecordingReportService(new SubmitReportResult.Created(reportId, 3, false));

        var result = await Handler(_posts, reports).HandleAsync("reporter-1", new SubmitReportRequest("post-1", " Spam "), TestContext.Current.CancellationToken);

        var created = result.ShouldBeOfType<Created<SubmitReportResponse>>();
        created.Value.ShouldBe(new SubmitReportResponse(reportId, "target-1", 3, false));
        reports.Calls.ShouldHaveSingleItem().ShouldBe(new NewReport("reporter-1", "target-1", "post-1", "Spam"));
    }

    [Fact]
    public async Task Created_at_threshold_reports_block_requested()
    {
        var reports = new RecordingReportService(new SubmitReportResult.Created(Guid.NewGuid(), 5, true));

        var result = await Handler(_posts, reports).HandleAsync("reporter-5", new SubmitReportRequest("post-1", "Spam"), TestContext.Current.CancellationToken);

        result.ShouldBeOfType<Created<SubmitReportResponse>>().Value!.BlockRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Duplicate_maps_to_409()
    {
        var reports = new RecordingReportService(new SubmitReportResult.Duplicate());

        var result = await Handler(_posts, reports).HandleAsync("reporter-2", new SubmitReportRequest("post-1", "Spam"), TestContext.Current.CancellationToken);

        StatusOf(result).ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Self_report_maps_to_422_and_is_not_stored()
    {
        var reports = new RecordingReportService(new SubmitReportResult.Duplicate());

        var result = await Handler(_posts, reports).HandleAsync("reporter-1", new SubmitReportRequest("post-11", "Spam"), TestContext.Current.CancellationToken);

        StatusOf(result).ShouldBe(StatusCodes.Status422UnprocessableEntity);
        reports.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Self_report_check_is_case_sensitive_like_the_ids()
    {
        // IDs are opaque/ordinal: "Reporter-1" is a different user from "reporter-1".
        var reports = new RecordingReportService(new SubmitReportResult.Created(Guid.NewGuid(), 1, false));

        var result = await Handler(_posts, reports).HandleAsync("Reporter-1", new SubmitReportRequest("post-11", "Spam"), TestContext.Current.CancellationToken);

        result.ShouldBeOfType<Created<SubmitReportResponse>>();
    }

    [Fact]
    public async Task Unknown_post_maps_to_404_and_is_not_stored()
    {
        var reports = new RecordingReportService(new SubmitReportResult.Duplicate());

        var result = await Handler(_posts, reports).HandleAsync("reporter-1", new SubmitReportRequest("post-999", "Spam"), TestContext.Current.CancellationToken);

        StatusOf(result).ShouldBe(StatusCodes.Status404NotFound);
        reports.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Posts_unavailable_maps_to_503_and_is_not_stored()
    {
        _posts.Unavailable = true;
        var reports = new RecordingReportService(new SubmitReportResult.Duplicate());

        var result = await Handler(_posts, reports).HandleAsync("reporter-1", new SubmitReportRequest("post-1", "Spam"), TestContext.Current.CancellationToken);

        StatusOf(result).ShouldBe(StatusCodes.Status503ServiceUnavailable);
        result.ShouldBeOfType<RetryAfterResult>().Seconds.ShouldBe(SubmitReportHandler.RetryAfterSeconds);
        reports.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invalid_input_maps_to_400_without_calling_posts_or_store()
    {
        _posts.Unavailable = true; // would turn into 503 if Posts were called
        var reports = new RecordingReportService(new SubmitReportResult.Duplicate());

        var result = await Handler(_posts, reports).HandleAsync("reporter-1", new SubmitReportRequest("a/b", ""), TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ValidationProblem>().ProblemDetails.Errors.Keys.ShouldBe(["postId", "reason"], ignoreOrder: true);
        reports.Calls.ShouldBeEmpty();
    }
}

using System.Net;
using System.Net.Http.Json;
using Reporting.Api.Reports;

namespace Reporting.IntegrationTests;

/// <summary>
/// Endpoint wiring: authentication, model binding, status codes, ProblemDetails.
/// Only the Posts service is faked; the app's own IReportService registration is used as-is.
/// Each test uses its own reported user, so tests don't depend on each other's counts or order.
/// </summary>
public class ReportsEndpointTests(ReportingApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ClientAs(string? userId)
    {
        var client = factory.CreateClient();
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Add("X-Demo-UserId", userId);
        }
        return client;
    }

    private Task<HttpResponseMessage> Report(string? reporter, string postId, string reason = "Harassment") =>
        ClientAs(reporter).PostAsJsonAsync("/api/reports", new { postId, reason }, Ct);

    [Fact]
    public async Task No_identity_is_401()
    {
        var response = await Report(null, factory.Posts.NewPostBy(TestUsers.New("target")));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Invalid_identity_header_is_401()
    {
        var response = await Report("bad user", factory.Posts.NewPostBy(TestUsers.New("target")));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task First_report_is_201_with_count_1()
    {
        var target = TestUsers.New("target");

        var response = await Report(TestUsers.New("reporter"), factory.Posts.NewPostBy(target));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<SubmitReportResponse>(Ct))!;
        body.ReportedUserId.ShouldBe(target);
        body.UniqueReportCount.ShouldBe(1);
        body.BlockRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task Same_reporter_on_another_post_by_same_author_is_409_and_not_counted()
    {
        var target = TestUsers.New("target");
        var reporter = TestUsers.New("reporter");

        (await Report(reporter, factory.Posts.NewPostBy(target))).StatusCode.ShouldBe(HttpStatusCode.Created);
        var duplicate = await Report(reporter, factory.Posts.NewPostBy(target), "Different reason");

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        duplicate.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        // Not counted: the next distinct reporter is #2.
        var next = await Report(TestUsers.New("reporter"), factory.Posts.NewPostBy(target));
        (await next.Content.ReadFromJsonAsync<SubmitReportResponse>(Ct))!.UniqueReportCount.ShouldBe(2);
    }

    [Fact]
    public async Task Five_distinct_reporters_request_a_block_and_it_stays_requested()
    {
        var target = TestUsers.New("target");
        var post = factory.Posts.NewPostBy(target);

        var responses = new List<SubmitReportResponse>();
        for (var i = 1; i <= 6; i++)
        {
            var response = await Report(TestUsers.New($"r{i}"), post);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            responses.Add((await response.Content.ReadFromJsonAsync<SubmitReportResponse>(Ct))!);
        }

        responses.Select(r => r.UniqueReportCount).ShouldBe([1, 2, 3, 4, 5, 6]);
        responses.Select(r => r.BlockRequested).ShouldBe([false, false, false, false, true, true]); // state, not transition
    }

    [Fact]
    public async Task Body_cannot_choose_reporter_or_target()
    {
        var target = TestUsers.New("target");
        var response = await ClientAs(TestUsers.New("reporter")).PostAsJsonAsync("/api/reports",
            new { postId = factory.Posts.NewPostBy(target), reason = "Spam", reporterUserId = "someone-else", reportedUserId = "victim" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<SubmitReportResponse>(Ct))!.ReportedUserId.ShouldBe(target);
    }

    [Fact]
    public async Task Self_report_is_422()
    {
        var user = TestUsers.New("user");

        var response = await Report(user, factory.Posts.NewPostBy(user));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Unknown_post_is_404()
    {
        var response = await Report(TestUsers.New("reporter"), "post-does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Posts_outage_is_503_with_retry_after()
    {
        var response = await Report(TestUsers.New("reporter"), FakePosts.DownPostId);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(SubmitReportHandler.RetryAfterSeconds));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Invalid_body_is_400_problem()
    {
        var response = await Report(TestUsers.New("reporter"), "a/b", "");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Malformed_json_is_400()
    {
        var content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        var response = await ClientAs(TestUsers.New("reporter")).PostAsync("/api/reports", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

using Reporting.Api.Posts;
using Reporting.Api.Reports;

namespace Reporting.UnitTests;

internal sealed class FakePostsClient : IPostsClient
{
    private readonly Dictionary<string, PostInfo> _posts = new(StringComparer.Ordinal);
    public bool Unavailable { get; set; }

    public FakePostsClient With(string postId, string authorId)
    {
        _posts[postId] = new PostInfo(postId, authorId);
        return this;
    }

    public Task<PostInfo?> FindPostAsync(string postId, CancellationToken cancellationToken) =>
        Unavailable
            ? throw new PostsServiceUnavailableException("down")
            : Task.FromResult(_posts.GetValueOrDefault(postId));
}

internal sealed class RecordingReportService(SubmitReportResult result) : IReportService
{
    public List<NewReport> Calls { get; } = [];

    public Task<SubmitReportResult> SubmitAsync(NewReport report, CancellationToken cancellationToken)
    {
        Calls.Add(report);
        return Task.FromResult(result);
    }
}

/// <summary>Returns a canned response and records the request URI.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        return Task.FromResult(respond(request));
    }
}

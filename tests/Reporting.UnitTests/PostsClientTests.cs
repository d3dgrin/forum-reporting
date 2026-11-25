using System.Net;
using System.Text;
using Reporting.Api.Posts;

namespace Reporting.UnitTests;

public class PostsClientTests
{
    private static (PostsClient Client, StubHttpHandler Handler) Create(HttpStatusCode status, string? body = null)
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(status)
        {
            Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://posts.test/") };
        return (new PostsClient(http), handler);
    }

    [Fact]
    public async Task Ok_returns_the_post()
    {
        var (client, handler) = Create(HttpStatusCode.OK, """{"postId":"post-1","authorId":"target-1","title":"ignored"}""");

        var post = await client.FindPostAsync("post-1", TestContext.Current.CancellationToken);

        post.ShouldBe(new PostInfo("post-1", "target-1"));
        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/posts/post-1");
    }

    [Theory]
    [InlineData("""{"type":"x","title":"Post not found","status":404}""")]
    [InlineData(null)] // e.g. unknown route: empty body
    public async Task Any_404_means_not_found(string? body)
    {
        var (client, _) = Create(HttpStatusCode.NotFound, body);

        (await client.FindPostAsync("post-9", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Other_errors_are_unavailable(HttpStatusCode status)
    {
        var (client, _) = Create(status);

        await Should.ThrowAsync<PostsServiceUnavailableException>(() => client.FindPostAsync("post-1", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"postId":"post-1","authorId":""}""")]
    [InlineData("""{"postId":"post-1","authorId":null}""")]
    [InlineData("""{"postId":"post-1"}""")]
    [InlineData("""{"postId":"post-1","authorId":"a b"}""")]  // not a valid user ID
    [InlineData("""{"postId":"post-1","authorId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")] // 65 chars
    [InlineData("""{"postId":"post-2","authorId":"target-1"}""")] // mismatched post
    [InlineData("null")]
    [InlineData("not json")]
    public async Task Unusable_200_is_unavailable_never_guessed(string body)
    {
        var (client, _) = Create(HttpStatusCode.OK, body);

        await Should.ThrowAsync<PostsServiceUnavailableException>(() => client.FindPostAsync("post-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Transport_failure_is_unavailable()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("connection refused"));
        var client = new PostsClient(new HttpClient(handler) { BaseAddress = new Uri("http://posts.test/") });

        await Should.ThrowAsync<PostsServiceUnavailableException>(() => client.FindPostAsync("post-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_id_is_escaped_in_the_path()
    {
        var (client, handler) = Create(HttpStatusCode.NotFound);

        await client.FindPostAsync("a/b?c", TestContext.Current.CancellationToken);

        handler.LastRequestUri!.AbsoluteUri.ShouldBe("http://posts.test/api/posts/a%2Fb%3Fc");
    }
}

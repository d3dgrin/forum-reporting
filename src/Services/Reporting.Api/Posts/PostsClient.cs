using System.Net;
using System.Text.Json;

namespace Reporting.Api.Posts;

/// <summary>Reporting's own view of a post: only what it needs (no shared DTO with Posts.Api).</summary>
public sealed record PostInfo(string PostId, string AuthorId);

public interface IPostsClient
{
    /// <summary>Returns the post, or <c>null</c> if it does not exist.</summary>
    /// <exception cref="PostsServiceUnavailableException">Posts service unreachable, timed out, or replied with an unusable response.</exception>
    Task<PostInfo?> FindPostAsync(string postId, CancellationToken cancellationToken);
}

public sealed class PostsServiceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class PostsClientOptions
{
    public const string SectionName = "Services:Posts";

    public required Uri BaseUrl { get; init; }
    public int TimeoutSeconds { get; init; } = 3;
}

/// <summary>Typed HttpClient for the (existing) Posts service. Lifetime managed by IHttpClientFactory.</summary>
public sealed class PostsClient(HttpClient http) : IPostsClient
{
    public async Task<PostInfo?> FindPostAsync(string postId, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            // Escaped even though callers validate the format: defence in depth against path injection.
            response = await http.GetAsync($"api/posts/{Uri.EscapeDataString(postId)}", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new PostsServiceUnavailableException("Posts service is unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PostsServiceUnavailableException("Posts service timed out.", ex);
        }

        using (response)
        {
            // Any 404 (ProblemDetails or empty body) means "no such post".
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PostsServiceUnavailableException($"Posts service returned {(int)response.StatusCode}.");
            }

            PostInfo? post;
            try
            {
                post = await response.Content.ReadFromJsonAsync<PostInfo>(cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new PostsServiceUnavailableException("Posts service returned an invalid body.", ex);
            }

            // The author decides whom the report targets, so a missing/mismatched answer must never be guessed around.
            // The author becomes the stored reported-user ID, so it must meet the same format as any user ID.
            if (post is null || !Identifiers.IsValidUserId(post.AuthorId) || !string.Equals(post.PostId, postId, StringComparison.Ordinal))
            {
                throw new PostsServiceUnavailableException("Posts service returned an incomplete or mismatched post.");
            }

            return post;
        }
    }
}

using System.Collections.Frozen;

namespace Posts.Api.Posts;

public sealed record Post(string PostId, string AuthorId, string Title);

/// <summary>
/// STUB: stands in for the existing Posts Service. Read-only, in-memory, seeded with demo data.
/// The only thing Reporting needs from it is "does this post exist, and who wrote it?".
/// IDs are opaque and case-sensitive (ordinal comparison).
/// </summary>
public sealed class PostsCatalog
{
    private readonly FrozenDictionary<string, Post> _posts;

    public PostsCatalog(IEnumerable<Post> posts) =>
        _posts = posts.ToFrozenDictionary(p => p.PostId, StringComparer.Ordinal);

    public Post? Find(string postId) => _posts.GetValueOrDefault(postId);

    public IReadOnlyCollection<Post> All() => _posts.Values;

    /// <summary>
    /// Demo data:
    /// <list type="bullet">
    /// <item>target-1 wrote post-1..3 (main demo: gets reported by 5 users and blocked)</item>
    /// <item>target-2 wrote post-4 (stays below threshold)</item>
    /// <item>reporter-1..6 each wrote one post (post-11..16), used for self-report checks</item>
    /// </list>
    /// </summary>
    public static PostsCatalog CreateSeeded() => new(
    [
        new("post-1", "target-1", "First post by target-1"),
        new("post-2", "target-1", "Second post by target-1"),
        new("post-3", "target-1", "Third post by target-1"),
        new("post-4", "target-2", "Only post by target-2"),
        .. Enumerable.Range(1, 6).Select(i => new Post($"post-{10 + i}", $"reporter-{i}", $"Post by reporter-{i}")),
    ]);
}

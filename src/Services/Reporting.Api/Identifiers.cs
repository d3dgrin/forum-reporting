using System.Text.RegularExpressions;

namespace Reporting.Api;

/// <summary>
/// Format rules for the opaque IDs Reporting accepts from outside.
/// User IDs are treated like OIDC <c>sub</c> claims: opaque and case-sensitive (ordinal), never normalised.
/// </summary>
public static partial class Identifiers
{
    public const int MaxLength = 64;

    public static bool IsValidUserId(string? value) => value is not null && UserIdRegex().IsMatch(value);

    public static bool IsValidPostId(string? value) => value is not null && PostIdRegex().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9._@-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PostIdRegex();
}

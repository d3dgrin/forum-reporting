namespace Reporting.Api.Reports;

public static class SubmitReportValidator
{
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 500;

    /// <summary>Validates the body. On success returns the normalised values (reason trimmed).</summary>
    public static bool TryValidate(
        SubmitReportRequest? request,
        out (string PostId, string Reason) normalized,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        normalized = default;

        if (request is null)
        {
            errors["body"] = ["A JSON body with postId and reason is required."];
            return false;
        }

        if (string.IsNullOrEmpty(request.PostId))
        {
            errors["postId"] = ["postId is required."];
        }
        else if (!Identifiers.IsValidPostId(request.PostId))
        {
            errors["postId"] = [$"postId must be 1-{Identifiers.MaxLength} characters: letters, digits or '-'."];
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            errors["reason"] = ["reason is required."];
        }
        else if (reason.Length is < ReasonMinLength or > ReasonMaxLength)
        {
            errors["reason"] = [$"reason must be {ReasonMinLength}-{ReasonMaxLength} characters."];
        }
        else if (reason.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            // e.g. U+0000, which PostgreSQL text columns reject.
            errors["reason"] = ["reason must not contain control characters."];
        }
        else if (!reason.Any(char.IsLetterOrDigit))
        {
            // Trim() doesn't remove invisible characters such as U+200B (zero-width space).
            errors["reason"] = ["reason must contain letters or digits."];
        }

        if (errors.Count > 0)
        {
            return false;
        }

        normalized = (request.PostId!, reason!);
        return true;
    }
}

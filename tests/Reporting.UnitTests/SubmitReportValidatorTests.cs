using Reporting.Api.Reports;

namespace Reporting.UnitTests;

public class SubmitReportValidatorTests
{
    [Fact]
    public void Valid_request_is_accepted_and_reason_trimmed()
    {
        var ok = SubmitReportValidator.TryValidate(new SubmitReportRequest("post-1", "  Harassment  "), out var input, out var errors);

        ok.ShouldBeTrue();
        errors.ShouldBeEmpty();
        input.PostId.ShouldBe("post-1");
        input.Reason.ShouldBe("Harassment");
    }

    [Fact]
    public void Null_body_is_rejected()
    {
        SubmitReportValidator.TryValidate(null, out _, out var errors).ShouldBeFalse();
        errors.ShouldContainKey("body");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("post 1")]
    [InlineData("../admin")]
    public void Invalid_postId_is_rejected(string? postId)
    {
        SubmitReportValidator.TryValidate(new SubmitReportRequest(postId, "Harassment"), out _, out var errors).ShouldBeFalse();
        errors.ShouldContainKey("postId");
    }

    [Fact]
    public void Too_long_postId_is_rejected()
    {
        SubmitReportValidator.TryValidate(new SubmitReportRequest(new string('p', 65), "Harassment"), out _, out var errors).ShouldBeFalse();
        errors.ShouldContainKey("postId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("ab")]
    [InlineData("  ab  ")] // too short after trimming
    public void Missing_or_short_reason_is_rejected(string? reason)
    {
        SubmitReportValidator.TryValidate(new SubmitReportRequest("post-1", reason), out _, out var errors).ShouldBeFalse();
        errors.ShouldContainKey("reason");
    }

    [Theory]
    [InlineData("\u0000\u0000\u0000")]      // NUL: PostgreSQL text rejects it
    [InlineData("Spam\u0007bell")]           // other control char
    [InlineData("​​​")]      // zero-width spaces survive Trim()
    [InlineData("!!! ???")]                  // no letter or digit
    public void Control_or_content_free_reason_is_rejected(string reason)
    {
        SubmitReportValidator.TryValidate(new SubmitReportRequest("post-1", reason), out _, out var errors).ShouldBeFalse();
        errors.ShouldContainKey("reason");
    }

    [Theory]
    [InlineData("Spam\nwith a newline\tand tab")]
    [InlineData("Образливий вміст")]         // non-Latin letters are fine
    public void Multiline_and_unicode_reason_is_accepted(string reason) =>
        SubmitReportValidator.TryValidate(new SubmitReportRequest("post-1", reason), out _, out _).ShouldBeTrue();

    [Fact]
    public void Too_long_reason_is_rejected()
    {
        SubmitReportValidator.TryValidate(new SubmitReportRequest("post-1", new string('r', 501)), out _, out var errors).ShouldBeFalse();
        errors.ShouldContainKey("reason");
    }

    [Fact]
    public void Reason_at_bounds_is_accepted()
    {
        SubmitReportValidator.TryValidate(new SubmitReportRequest("post-1", "abc"), out _, out _).ShouldBeTrue();
        SubmitReportValidator.TryValidate(new SubmitReportRequest("post-1", new string('r', 500)), out _, out _).ShouldBeTrue();
    }

    [Fact]
    public void All_errors_are_reported_together()
    {
        SubmitReportValidator.TryValidate(new SubmitReportRequest(null, null), out _, out var errors).ShouldBeFalse();
        errors.Keys.ShouldBe(["postId", "reason"], ignoreOrder: true);
    }
}

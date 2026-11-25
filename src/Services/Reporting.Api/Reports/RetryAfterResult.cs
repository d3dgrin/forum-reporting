namespace Reporting.Api.Reports;

/// <summary>Decorates a result with a <c>Retry-After</c> header (used for 503 responses).</summary>
public sealed class RetryAfterResult(int seconds, IResult inner) : IResult, IStatusCodeHttpResult
{
    public int Seconds { get; } = seconds;
    public IResult Inner { get; } = inner;

    public int? StatusCode => (Inner as IStatusCodeHttpResult)?.StatusCode;

    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.Headers.RetryAfter = Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Inner.ExecuteAsync(httpContext);
    }
}

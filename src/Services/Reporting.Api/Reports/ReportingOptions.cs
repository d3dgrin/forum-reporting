namespace Reporting.Api.Reports;

public sealed class ReportingOptions
{
    public const string SectionName = "Reporting";

    /// <summary>Number of distinct reporters at which a block is requested. Requirement: 5.</summary>
    public int BlockThreshold { get; init; } = 5;
}

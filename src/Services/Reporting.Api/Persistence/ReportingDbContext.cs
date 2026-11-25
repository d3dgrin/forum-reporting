using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Reporting.Api.Persistence;

/// <summary>Reporting's own database (reporting_db). No other service reads or writes it.</summary>
public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportedUserCounter> ReportedUserCounters => Set<ReportedUserCounter>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Report>(e =>
        {
            e.ToTable("reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.ReporterUserId).HasMaxLength(Identifiers.MaxLength);
            e.Property(x => x.ReportedUserId).HasMaxLength(Identifiers.MaxLength);
            e.Property(x => x.PostId).HasMaxLength(Identifiers.MaxLength);
            e.Property(x => x.Reason).HasMaxLength(global::Reporting.Api.Reports.SubmitReportValidator.ReasonMaxLength);

            // THE business rule: one report per reporter per reported user, regardless of the post.
            // Enforced by the database, so concurrent requests cannot both succeed.
            e.HasIndex(x => new { x.ReporterUserId, x.ReportedUserId })
                .IsUnique()
                .HasDatabaseName("ux_reports_reporter_reported");

            e.HasIndex(x => x.ReportedUserId).HasDatabaseName("ix_reports_reported_user_id");
        });

        modelBuilder.Entity<ReportedUserCounter>(e =>
        {
            e.ToTable("reported_user_counters", t =>
                t.HasCheckConstraint("ck_reported_user_counters_count_positive", "unique_report_count > 0"));
            e.HasKey(x => x.ReportedUserId);
            e.Property(x => x.ReportedUserId).HasMaxLength(Identifiers.MaxLength);
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(200);
            e.Property(x => x.Payload).HasColumnType("jsonb");

            // The publisher only ever scans unpublished rows in creation order.
            e.HasIndex(x => x.CreatedAt)
                .HasFilter("published_at IS NULL")
                .HasDatabaseName("ix_outbox_messages_unpublished");
        });
    }
}

/// <summary>Lets `dotnet ef` create migrations without starting the web host.</summary>
public sealed class ReportingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ReportingDbContext>
{
    public ReportingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ReportingDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only")
            .UseSnakeCaseNamingConvention()
            .Options);
}

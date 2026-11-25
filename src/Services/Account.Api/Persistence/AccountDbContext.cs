using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Account.Api.Persistence;

public enum AccountStatus
{
    Active,
    Blocked,
}

public sealed class UserAccount
{
    public required string UserId { get; set; }
    public AccountStatus Status { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public string? BlockReason { get; set; }
}

/// <summary>Inbox/dedup table: one row per consumed event. Its primary key makes processing idempotent.</summary>
public sealed class ProcessedMessage
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Account's own database (account_db). Owns account status; nobody else writes it.</summary>
public sealed class AccountDbContext(DbContextOptions<AccountDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Accounts => Set<UserAccount>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    /// <summary>Demo users (match the Posts.Api stub seed).</summary>
    public static readonly string[] DemoUserIds =
        ["target-1", "target-2", .. Enumerable.Range(1, 6).Select(i => $"reporter-{i}")];

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.BlockReason).HasMaxLength(200);
            e.HasData(DemoUserIds.Select(id => new UserAccount { UserId = id, Status = AccountStatus.Active }));
        });

        modelBuilder.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("processed_messages");
            e.HasKey(x => x.EventId);
        });
    }
}

public sealed class AccountDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AccountDbContext>
{
    public AccountDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AccountDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only")
            .UseSnakeCaseNamingConvention()
            .Options);
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ForumReporting.Contracts.Events;
using ForumReporting.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reporting.Api.Persistence;
using Reporting.Api.Reports;

namespace Reporting.IntegrationTests;

/// <summary>
/// Business rules against REAL PostgreSQL (the guarantees come from the unique index, row locks and the
/// transaction, which an in-memory provider would not reproduce). Numbers refer to the plan's essential tests.
/// </summary>
public class ReportPersistenceTests(ReportingApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Submits through the real IReportService in its own DI scope (own DbContext and connection).</summary>
    private async Task<SubmitReportResult> Submit(string reporter, string reported, string? postId = null, string reason = "Harassment")
    {
        await using var scope = factory.NewScope();
        var service = scope.ServiceProvider.GetRequiredService<IReportService>();
        return await service.SubmitAsync(new NewReport(reporter, reported, postId ?? $"p-{Guid.NewGuid():N}", reason), Ct);
    }

    private async Task<(int Reports, int? Count, DateTimeOffset? BlockRequestedAt, int Outbox)> State(string reported)
    {
        await using var scope = factory.NewScope();
        var db = factory.Db(scope);
        var reports = await db.Reports.CountAsync(r => r.ReportedUserId == reported, Ct);
        var counter = await db.ReportedUserCounters.AsNoTracking().SingleOrDefaultAsync(c => c.ReportedUserId == reported, Ct);
        var outbox = await db.OutboxMessages
            .CountAsync(m => m.EventType == UserBlockRequested.EventType && EF.Functions.JsonContains(m.Payload, JsonSerializer.Serialize(new { userId = reported })), Ct);
        return (reports, counter?.UniqueReportCount, counter?.BlockRequestedAt, outbox);
    }

    [Fact] // 1
    public async Task First_report_is_stored_and_counter_becomes_1()
    {
        var target = TestUsers.New("target");

        var result = await Submit(TestUsers.New("r"), target, "post-a", "  kept as given  ");

        var created = result.ShouldBeOfType<SubmitReportResult.Created>();
        created.UniqueReportCount.ShouldBe(1);
        created.BlockRequested.ShouldBeFalse();
        (await State(target)).ShouldBe((1, 1, null, 0));

        await using var scope = factory.NewScope();
        var row = await factory.Db(scope).Reports.SingleAsync(r => r.Id == created.ReportId, Ct);
        row.ReportedUserId.ShouldBe(target);
        row.PostId.ShouldBe("post-a");
        row.Reason.ShouldBe("  kept as given  "); // service stores what the handler validated; trimming is the handler's job
    }

    [Fact] // 2
    public async Task Same_reporter_on_a_different_post_by_the_same_author_is_a_duplicate_and_not_counted()
    {
        var target = TestUsers.New("target");
        var reporter = TestUsers.New("r");

        (await Submit(reporter, target, "post-a")).ShouldBeOfType<SubmitReportResult.Created>();
        (await Submit(reporter, target, "post-b", "Another reason")).ShouldBeOfType<SubmitReportResult.Duplicate>();

        (await State(target)).ShouldBe((1, 1, null, 0));
    }

    [Fact]
    public async Task Same_reporter_can_report_different_users()
    {
        var reporter = TestUsers.New("r");
        var t1 = TestUsers.New("t1");
        var t2 = TestUsers.New("t2");

        (await Submit(reporter, t1)).ShouldBeOfType<SubmitReportResult.Created>();
        (await Submit(reporter, t2)).ShouldBeOfType<SubmitReportResult.Created>();

        (await State(t1)).Count.ShouldBe(1);
        (await State(t2)).Count.ShouldBe(1);
    }

    [Fact] // 3
    public async Task Four_distinct_reporters_count_4_and_no_block()
    {
        var target = TestUsers.New("target");

        for (var i = 0; i < 4; i++)
        {
            await Submit(TestUsers.New("r"), target);
        }

        (await State(target)).ShouldBe((4, 4, null, 0));
    }

    [Fact] // 4
    public async Task Fifth_distinct_reporter_creates_exactly_one_block_event_and_later_reports_do_not()
    {
        var target = TestUsers.New("target");

        var results = new List<SubmitReportResult.Created>();
        for (var i = 0; i < 7; i++)
        {
            results.Add((await Submit(TestUsers.New("r"), target)).ShouldBeOfType<SubmitReportResult.Created>());
        }

        results.Select(r => r.UniqueReportCount).ShouldBe([1, 2, 3, 4, 5, 6, 7]);
        results.Select(r => r.BlockRequested).ShouldBe([false, false, false, false, true, true, true]);

        var state = await State(target);
        state.Reports.ShouldBe(7);
        state.Count.ShouldBe(7);
        state.BlockRequestedAt.ShouldNotBeNull();
        state.Outbox.ShouldBe(1);

        // The outbox payload is the v1 contract, readable with the shared strict options.
        await using var scope = factory.NewScope();
        var message = await factory.Db(scope).OutboxMessages.AsNoTracking()
            .SingleAsync(m => EF.Functions.JsonContains(m.Payload, JsonSerializer.Serialize(new { userId = target })), Ct);
        message.PublishedAt.ShouldBeNull();
        message.Attempts.ShouldBe(0);
        var evt = JsonSerializer.Deserialize<UserBlockRequested>(message.Payload, ContractJson.Options)!;
        evt.EventId.ShouldBe(message.Id);
        evt.UserId.ShouldBe(target);
        evt.ReportCount.ShouldBe(5);
        evt.RequestedAt.ShouldBe(state.BlockRequestedAt!.Value);
    }

    [Fact] // 7
    public async Task Concurrent_reports_from_the_same_reporter_store_one_report_and_count_once()
    {
        var target = TestUsers.New("target");
        var reporter = TestUsers.New("r");

        var results = await RunConcurrently(20, i => Submit(reporter, target, $"post-{i}"));

        results.OfType<SubmitReportResult.Created>().Count().ShouldBe(1);
        results.OfType<SubmitReportResult.Duplicate>().Count().ShouldBe(19);
        (await State(target)).ShouldBe((1, 1, null, 0));
    }

    [Fact] // 8
    public async Task Concurrent_reports_from_different_reporters_are_all_counted_and_block_is_requested_once()
    {
        var target = TestUsers.New("target");

        var results = await RunConcurrently(10, _ => Submit(TestUsers.New("r"), target));

        var created = results.Select(r => r.ShouldBeOfType<SubmitReportResult.Created>()).ToList();
        created.Select(r => r.UniqueReportCount).Order().ShouldBe(Enumerable.Range(1, 10)); // no lost or repeated increments
        created.Where(r => r.UniqueReportCount >= 5).ShouldAllBe(r => r.BlockRequested);
        created.Where(r => r.UniqueReportCount < 5).ShouldAllBe(r => !r.BlockRequested);

        var state = await State(target);
        state.Reports.ShouldBe(10);
        state.Count.ShouldBe(10);
        state.Outbox.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_mix_of_duplicates_and_distinct_reporters_counts_distinct_only()
    {
        var target = TestUsers.New("target");
        var reporters = Enumerable.Range(0, 6).Select(_ => TestUsers.New("r")).ToArray();

        // Each of 6 reporters fires 4 times concurrently (24 requests).
        var results = await RunConcurrently(24, i => Submit(reporters[i % 6], target, $"post-{i}"));

        results.OfType<SubmitReportResult.Created>().Count().ShouldBe(6);
        var state = await State(target);
        state.ShouldBe((6, 6, state.BlockRequestedAt, 1));
        state.BlockRequestedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Summary_endpoint_shows_count_and_block_request_state()
    {
        var target = TestUsers.New("target");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-UserId", "support-demo");

        var empty = await client.GetFromJsonAsync<ReportSummaryResponse>($"/api/reports/users/{target}/summary", Ct);
        empty.ShouldBe(new ReportSummaryResponse(target, 0, 5, null));

        for (var i = 0; i < 5; i++)
        {
            await Submit(TestUsers.New("r"), target);
        }

        var summary = await client.GetFromJsonAsync<ReportSummaryResponse>($"/api/reports/users/{target}/summary", Ct);
        summary!.UniqueReportCount.ShouldBe(5);
        summary.BlockRequestedAt.ShouldNotBeNull();

        (await client.GetAsync("/api/reports/users/bad%20id/summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await factory.CreateClient().GetAsync($"/api/reports/users/{target}/summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------------------------------------
    // Transaction atomicity and lock behaviour (deterministic: no reliance on thread timing).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Fails the outbox INSERT, i.e. the LAST step of the transaction, to prove all-or-nothing.</summary>
    private sealed class FailOutboxInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("INSERT INTO outbox_messages", StringComparison.Ordinal)
                ? throw new InvalidOperationException("simulated failure on outbox insert")
                : ValueTask.FromResult(result);
    }

    private PostgresReportService ServiceWith(ReportingDbContext db) =>
        new(db, TimeProvider.System, Options.Create(new ReportingOptions()), NullLogger<PostgresReportService>.Instance);

    private ReportingDbContext NewDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ReportingDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptors)
            .Options);

    [Fact]
    public async Task Failure_in_the_last_step_rolls_back_report_counter_and_block_request_together()
    {
        var target = TestUsers.New("target");
        for (var i = 0; i < 4; i++)
        {
            await Submit(TestUsers.New("r"), target);
        }

        // The 5th report crosses the threshold, then the outbox insert fails.
        var fifthReporter = TestUsers.New("r");
        await using (var failingDb = NewDb(new FailOutboxInsertInterceptor()))
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                ServiceWith(failingDb).SubmitAsync(new NewReport(fifthReporter, target, "post-x", "Spam"), Ct));
        }

        // Nothing from the failed transaction survived: not the report, not the +1, not block_requested_at.
        (await State(target)).ShouldBe((4, 4, null, 0));

        // The same reporter can retry; it now crosses the threshold and the event is created once.
        var retry = (await Submit(fifthReporter, target)).ShouldBeOfType<SubmitReportResult.Created>();
        retry.UniqueReportCount.ShouldBe(5);
        var state = await State(target);
        state.ShouldBe((5, 5, state.BlockRequestedAt, 1));
        state.BlockRequestedAt.ShouldNotBeNull();
    }

    /// <summary>Holds the counter row lock (as a concurrent report transaction would) until disposed or committed.</summary>
    private async Task<(ReportingDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx)> LockCounterRow(string reported)
    {
        var db = NewDb();
        var tx = await db.Database.BeginTransactionAsync(Ct);
        var locked = await db.Database.SqlQuery<int>(
            $"SELECT unique_report_count AS \"Value\" FROM reported_user_counters WHERE reported_user_id = {reported} FOR UPDATE").ToListAsync(Ct);
        locked.ShouldHaveSingleItem();
        return (db, tx);
    }

    [Fact]
    public async Task A_report_waits_for_the_counter_lock_and_then_increments_the_committed_value()
    {
        var target = TestUsers.New("target");
        await Submit(TestUsers.New("r"), target);
        await Submit(TestUsers.New("r"), target);

        var (lockDb, lockTx) = await LockCounterRow(target);
        Task<SubmitReportResult> pending;
        await using (lockDb)
        {
            pending = Submit(TestUsers.New("r"), target);

            await Task.Delay(500, Ct);
            pending.IsCompleted.ShouldBeFalse("the report must block on the counter row lock");

            await lockTx.CommitAsync(Ct);
        }

        var created = (await pending).ShouldBeOfType<SubmitReportResult.Created>();
        created.UniqueReportCount.ShouldBe(3);
        (await State(target)).ShouldBe((3, 3, null, 0));
    }

    [Fact]
    public async Task A_cancelled_report_waiting_on_the_lock_persists_nothing()
    {
        var target = TestUsers.New("target");
        await Submit(TestUsers.New("r"), target);
        await Submit(TestUsers.New("r"), target);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (lockDb, lockTx) = await LockCounterRow(target);
        await using (lockDb)
        {
            Task<SubmitReportResult> pending;
            await using (var scope = factory.NewScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<IReportService>();
                pending = service.SubmitAsync(new NewReport(TestUsers.New("r"), target, "post-x", "Spam"), cts.Token);

                await Task.Delay(500, Ct);
                pending.IsCompleted.ShouldBeFalse();

                // Client disconnects while the request is blocked (its report row is inserted but uncommitted).
                await cts.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(() => pending);
            }

            await lockTx.RollbackAsync(Ct);
        }

        // The cancelled transaction rolled back: no report row, count unchanged.
        (await State(target)).ShouldBe((2, 2, null, 0));
    }

    /// <summary>Starts all operations at the same moment (gate) on the thread pool and awaits them all.</summary>
    private static async Task<SubmitReportResult[]> RunConcurrently(int count, Func<int, Task<SubmitReportResult>> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () => { await gate.Task; return await operation(i); }))
            .ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }
}

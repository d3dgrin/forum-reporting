using System.Text.Json;
using ForumReporting.Contracts.Events;
using ForumReporting.Contracts.Messaging;

namespace Reporting.UnitTests;

public class ContractSerializationTests
{
    [Fact]
    public void UserBlockRequested_round_trips_with_shared_options_and_keeps_the_offset()
    {
        var evt = new UserBlockRequested(Guid.NewGuid(), "target-1", 5, "Reported by 5 distinct users",
            new DateTimeOffset(2026, 11, 25, 14, 0, 0, TimeSpan.FromHours(2)));

        var json = JsonSerializer.Serialize(evt, ContractJson.Options);
        var back = JsonSerializer.Deserialize<UserBlockRequested>(json, ContractJson.Options);

        back.ShouldBe(evt);
        // Record equality compares DateTimeOffset as instants; also assert the offset survived.
        back!.RequestedAt.Offset.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public void UserBlockRequested_wire_format_is_camelCase()
    {
        var evt = new UserBlockRequested(Guid.Empty, "u", 5, "r", DateTimeOffset.UnixEpoch);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(evt, ContractJson.Options));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        names.ShouldBe(["eventId", "userId", "reportCount", "reason", "requestedAt"]);
    }

    [Theory]
    [InlineData("""{"eventId":"7d0f5f0e-0000-0000-0000-000000000001","reportCount":5,"reason":"r","requestedAt":"2026-11-25T12:00:00+00:00"}""")] // userId missing
    [InlineData("""{"eventId":"7d0f5f0e-0000-0000-0000-000000000001","userId":null,"reportCount":5,"reason":"r","requestedAt":"2026-11-25T12:00:00+00:00"}""")] // userId null
    [InlineData("""{"userId":"target-1","reportCount":5,"reason":"r","requestedAt":"2026-11-25T12:00:00+00:00"}""")] // eventId missing
    public void Malformed_payload_is_rejected(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<UserBlockRequested>(json, ContractJson.Options));

    [Fact]
    public void Shared_options_cannot_be_mutated() =>
        Should.Throw<InvalidOperationException>(() => ContractJson.Options.PropertyNamingPolicy = null);
}

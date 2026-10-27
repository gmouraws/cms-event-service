using System.Text.Json;
using CmsEventService;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CmsEventService.Tests;

public class PolicyTests
{
    private static readonly long Time = DateTimeOffset.Parse("2024-01-01T00:00:00Z").UtcTicks;
    private static Entity Current() => new() { Id = "X", Version = 2, Payload = "{\"title\":\"Example\"}", TimestampTicks = Time, Published = true, LocallyDisabled = true };
    private static CmsEvent Event(string type = "publish", long version = 2, long delta = 0, string payload = "{\"title\":\"Example\"}") => new(0, type, "X", type == "delete" ? null : version, Time + delta, type == "delete" ? null : payload);

    [Theory]
    [InlineData("publish", 1)]
    [InlineData("publish", 8)]
    [InlineData("unPublish", 5)]
    [InlineData("delete", 0)]
    public void Unknown_entities_accept_first_event(string type, long version) => Assert.Equal("applied", EventPolicy.Decide(Event(type, version), null, null).Outcome);

    [Theory]
    [InlineData("publish", 2, 0, "duplicate")]
    [InlineData("unPublish", 2, 0, "conflicting")]
    [InlineData("unPublish", 2, 1, "applied")]
    [InlineData("publish", 2, -1, "stale")]
    [InlineData("publish", 1, 100, "stale")]
    [InlineData("publish", 8, 1, "applied")]
    [InlineData("unPublish", 3, 1, "applied")]
    [InlineData("publish", 3, -1, "conflicting")]
    [InlineData("delete", 0, -1, "stale")]
    [InlineData("delete", 0, 0, "conflicting")]
    [InlineData("delete", 0, 1, "applied")]
    public void Ordering_is_explicit(string type, long version, long delta, string outcome)
        => Assert.Equal(outcome, EventPolicy.Decide(Event(type, version, delta), Current(), null).Outcome);

    [Fact]
    public void Same_version_cannot_change_payload_even_with_later_timestamp()
        => Assert.Equal("conflicting", EventPolicy.Decide(Event(delta: 10, payload: "{}"), Current(), null).Outcome);

    [Theory]
    [InlineData("publish", -1, "stale")]
    [InlineData("publish", 0, "conflicting")]
    [InlineData("publish", 1, "applied")]
    [InlineData("delete", 0, "duplicate")]
    public void Deletion_barrier_blocks_delayed_resurrection(string type, long delta, string outcome)
        => Assert.Equal(outcome, EventPolicy.Decide(Event(type, 10, delta), null, new() { Id = "X", TimestampTicks = Time }).Outcome);

    [Fact]
    public void Json_comparison_ignores_property_order_and_numeric_representation()
    {
        using var a = JsonDocument.Parse("{\"b\":[1.00,1e2],\"a\":\"<b>content</b>\"}");
        using var b = JsonDocument.Parse("{\"a\":\"<b>content</b>\",\"b\":[1,100]}");
        Assert.Equal(EventParser.Canonical(a.RootElement), EventParser.Canonical(b.RootElement));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"type\":\"publish\"}]")]
    [InlineData("[{\"type\":\"delete\",\"id\":\"X\",\"timestamp\":\"2024-01-01T00:00:00\"}]")]
    [InlineData("[{\"type\":\"delete\",\"id\":\"X\",\"id\":\"Y\",\"timestamp\":\"2024-01-01T00:00:00Z\"}]")]
    [InlineData("[{\"type\":\"delete\",\"id\":\"X\",\"extra\":1,\"timestamp\":\"2024-01-01T00:00:00Z\"}]")]
    public void Invalid_envelopes_are_rejected(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<InvalidBatchException>(() => EventParser.Parse(doc.RootElement));
    }

    [Fact]
    public async Task Read_context_rejects_saves_and_defaults_to_no_tracking()
    {
        await using var db = new ReadContext(new DbContextOptionsBuilder<ReadContext>().UseNpgsql("Host=localhost").Options);
        Assert.Equal(QueryTrackingBehavior.NoTracking, db.ChangeTracker.QueryTrackingBehavior);
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}

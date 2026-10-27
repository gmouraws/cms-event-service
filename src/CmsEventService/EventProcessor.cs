using Microsoft.EntityFrameworkCore;

namespace CmsEventService;

public sealed record BatchResult(bool Committed, IReadOnlyList<EventResult> Results);

public sealed class EventProcessor(WriterContext db, ILogger<EventProcessor> logger)
{
    public async Task<BatchResult> Process(IReadOnlyList<CmsEvent> events, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await EntityLock.Acquire(db, events.Select(e => e.Id), ct);
            var results = new List<EventResult>();
            foreach (var e in events)
            {
                var current = await db.Entities.FindAsync([e.Id], ct);
                var barrier = await db.Deletions.FindAsync([e.Id], ct);
                var result = EventPolicy.Decide(e, current, barrier);
                results.Add(result);
                if (result.Outcome != "applied") continue;
                if (e.Type == "delete")
                {
                    if (current is not null) db.Entities.Remove(current);
                    if (barrier is null) db.Deletions.Add(new() { Id = e.Id, TimestampTicks = e.TimestampTicks });
                    else barrier.TimestampTicks = e.TimestampTicks;
                }
                else
                {
                    if (current is null)
                    {
                        current = new() { Id = e.Id, Payload = e.Payload! };
                        db.Entities.Add(current);
                    }
                    current.Payload = e.Payload!;
                    current.Version = e.Version!.Value;
                    current.TimestampTicks = e.TimestampTicks;
                    current.Published = e.Type == "publish";
                }
                // Flush inside the transaction so repeated IDs see prior input events,
                // including delete/recreate. Nothing is acknowledged before commit.
                await db.SaveChangesAsync(ct);
            }
            var conflict = results.Any(r => r.Outcome == "conflicting");
            if (conflict)
            {
                await transaction.RollbackAsync(ct);
                results = results.Select(r => r.Outcome == "applied" ? r with { Outcome = "rolledBack", Reason = "Batch contains a conflict." } : r).ToList();
                db.ChangeTracker.Clear();
            }
            else await transaction.CommitAsync(ct);
            foreach (var r in results)
                logger.LogInformation("CMS event {Index} {EntityId} {EventType} {Version} outcome {Outcome}: {Reason}; committed {Committed}",
                    r.Index, events[r.Index].Id, events[r.Index].Type, events[r.Index].Version, r.Outcome, r.Reason, !conflict);
            return new(!conflict, results);
        }
        catch (Exception ex)
        {
            // Exception messages from a provider can contain submitted values. Log only its type.
            logger.LogError("CMS batch failed; transaction not acknowledged; failure type {FailureType}", ex.GetType().Name);
            throw;
        }
    }
}

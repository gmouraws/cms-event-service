using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CmsEventService;

public sealed record CmsEvent(int Index, string Type, string Id, long? Version, long TimestampTicks, string? Payload);
public sealed record EventResult(int Index, string Outcome, string Reason);
public sealed class InvalidBatchException(IReadOnlyList<EventResult> errors) : Exception("Invalid batch")
{
    public IReadOnlyList<EventResult> Errors { get; } = errors;
}

public static partial class EventParser
{
    public const int MaxRequestBytes = 1_048_576;
    public const int MaxPayloadBytes = 65_536;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    public static bool ValidId(string id) => IdPattern().IsMatch(id);

    public static IReadOnlyList<CmsEvent> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > 100)
            throw new InvalidBatchException([new(-1, "invalid", "Expected 1 to 100 events.")]);
        var events = new List<CmsEvent>();
        var errors = new List<EventResult>();
        var index = 0;
        foreach (var item in root.EnumerateArray())
        {
            try { events.Add(ParseEvent(item, index)); }
            catch (FormatException ex) { errors.Add(new(index, "invalid", ex.Message)); }
            index++;
        }
        if (errors.Count != 0) throw new InvalidBatchException(errors);
        return events;
    }

    private static CmsEvent ParseEvent(JsonElement item, int index)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Event must be an object.");
        RejectDuplicates(item);
        foreach (var prop in item.EnumerateObject())
            if (prop.Name is not ("id" or "type" or "version" or "timestamp" or "payload"))
                throw new FormatException("Unknown envelope field.");
        var type = String(item, "type");
        if (type is not ("publish" or "unPublish" or "delete")) throw new FormatException("Unsupported event type.");
        var id = String(item, "id").Trim();
        if (!ValidId(id)) throw new FormatException("Invalid ID: use 1 to 128 ASCII letters, digits, dots, underscores, colons or hyphens.");
        var timestamp = String(item, "timestamp");
        if (!Regex.IsMatch(timestamp, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            || time <= DateTimeOffset.UnixEpoch)
            throw new FormatException("Timestamp must be RFC3339 after the Unix epoch with an explicit timezone.");
        if (type == "delete")
        {
            if (item.TryGetProperty("payload", out _) || item.TryGetProperty("version", out _))
                throw new FormatException("Delete must omit payload and version.");
            return new(index, type, id, null, time.UtcTicks, null);
        }
        if (!item.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var version) || version < 1)
            throw new FormatException("Version must be a positive 64-bit integer.");
        if (!item.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new FormatException("Payload must be a JSON object.");
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaxPayloadBytes)
            throw new FormatException("Payload exceeds 64 KiB.");
        return new(index, type, id, version, time.UtcTicks, Canonical(payload));
    }

    private static string String(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException("Required string field is missing or invalid.");
        return value.GetString()!;
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new FormatException("Duplicate JSON property name.");
                if (p.Name.Contains('\0')) throw new FormatException("JSON null characters are unsupported by PostgreSQL.");
                RejectDuplicates(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicates(child);
        else if (value.ValueKind == JsonValueKind.String && value.GetString()!.Contains('\0'))
            throw new FormatException("JSON null characters are unsupported by PostgreSQL.");
        else if (value.ValueKind == JsonValueKind.Number) _ = NormalizeNumber(value.GetRawText());
    }

    public static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(p.Name);
                Write(writer, p.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in value.EnumerateArray()) Write(writer, child);
            writer.WriteEndArray();
        }
        else if (value.ValueKind == JsonValueKind.Number) writer.WriteRawValue(NormalizeNumber(value.GetRawText()));
        else value.WriteTo(writer);
    }

    private static string NormalizeNumber(string raw)
    {
        var parts = raw.ToLowerInvariant().Split('e');
        var exponent = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out exponent) || Math.Abs((long)exponent) > 100_000))
            throw new FormatException("Payload numeric exponent exceeds 1000.");
        var mantissa = parts[0];
        var negative = mantissa.StartsWith('-');
        var point = mantissa.IndexOf('.');
        if (point >= 0) exponent -= mantissa.Length - point - 1;
        var digits = mantissa.Replace(".", "").TrimStart('-').TrimStart('0');
        if (digits.Length == 0) return "0";
        var trimmed = digits.TrimEnd('0');
        exponent += digits.Length - trimmed.Length;
        if (trimmed.Length > 1000 || Math.Abs((long)exponent) > 1000)
            throw new FormatException("Payload numbers are limited to 1000 significant digits and normalized exponent magnitude 1000.");
        return (negative ? "-" : "") + trimmed + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}

public static class EventPolicy
{
    public static EventResult Decide(CmsEvent e, Entity? current, DeletionBarrier? barrier)
    {
        EventResult Result(string outcome, string reason) => new(e.Index, outcome, reason);
        if (barrier is not null && e.TimestampTicks <= barrier.TimestampTicks)
            return e.TimestampTicks < barrier.TimestampTicks ? Result("stale", "Before deletion barrier.")
                : e.Type == "delete" ? Result("duplicate", "Deletion already recorded.")
                : Result("conflicting", "Event ties a deletion timestamp.");
        if (current is null) return Result("applied", "New entity or deletion barrier.");
        if (e.Type == "delete")
            return e.TimestampTicks < current.TimestampTicks ? Result("stale", "Delete precedes latest entity event.")
                : e.TimestampTicks == current.TimestampTicks ? Result("conflicting", "Delete ties an entity event.")
                : Result("applied", "Hard deletion.");
        if (e.Version < current.Version) return Result("stale", "Older data version.");
        if (e.Version > current.Version)
            return e.TimestampTicks < current.TimestampTicks ? Result("conflicting", "Version and timestamp order disagree.")
                : Result("applied", "New data version.");
        if (e.TimestampTicks < current.TimestampTicks) return Result("stale", "Older publication transition.");
        using var existing = JsonDocument.Parse(current.Payload);
        if (e.Payload != EventParser.Canonical(existing.RootElement))
            return Result("conflicting", "Same version has different payload.");
        if (e.TimestampTicks == current.TimestampTicks)
            return current.Published == (e.Type == "publish") ? Result("duplicate", "Same event.")
                : Result("conflicting", "Indistinguishable publication ordering.");
        return Result("applied", "New publication transition.");
    }
}

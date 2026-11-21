using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NetWatch.Core.Alerts;

/// <summary>
/// Optional conditions of an event rule. Every filled-in condition must match; an empty filter matches every event
/// of the rule's event key.
/// </summary>
public sealed class EventFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    /// <summary>Regular expression matched (case-insensitive) against the message.</summary>
    public string? Pattern { get; set; }

    /// <summary>Only events of at least this severity.</summary>
    public Severity? MinSeverity { get; set; }

    /// <summary>Windows event IDs; empty = any.</summary>
    public List<int> EventIds { get; set; } = new();

    /// <summary>Substring of the source (syslog app name, event-log provider, trap name); case-insensitive.</summary>
    public string? Source { get; set; }

    /// <summary>Trap OID prefix (e.g. "1.3.6.1.6.3.1.1.5.5" for authenticationFailure).</summary>
    public string? Oid { get; set; }

    /// <summary>Event fields that must have these values (case-insensitive), e.g. {"offHours": "true"}.</summary>
    public Dictionary<string, string> FieldEquals { get; set; } = new();

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Pattern) && MinSeverity is null && EventIds.Count == 0 &&
        string.IsNullOrWhiteSpace(Source) && string.IsNullOrWhiteSpace(Oid) && FieldEquals.Count == 0;

    public static EventFilter Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new EventFilter();
        }

        try
        {
            return JsonSerializer.Deserialize<EventFilter>(json, JsonOptions) ?? new EventFilter();
        }
        catch (JsonException)
        {
            // A corrupt filter must not turn into "match everything".
            return new EventFilter { Pattern = "(?!)" };
        }
    }

    public string? ToJson() => IsEmpty ? null : JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Returns an error message if the pattern is not a valid regular expression, otherwise null.</summary>
    public string? Validate()
    {
        if (FieldEquals.Count > 20 || FieldEquals.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Length > 64 || kv.Value is null || kv.Value.Length > 200))
        {
            return "Field filters need a field name and value (max 20 entries, 64 / 200 characters).";
        }

        return ValidatePattern();
    }

    private string? ValidatePattern()
    {
        if (string.IsNullOrWhiteSpace(Pattern))
        {
            return null;
        }

        if (Pattern.Length > 500)
        {
            return "The message pattern is too long (max 500 characters).";
        }

        try
        {
            _ = new Regex(Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"Invalid message pattern: {ex.Message}";
        }
    }

    public bool Matches(EventSignal e)
    {
        if (MinSeverity is { } min && e.Severity < min)
        {
            return false;
        }

        if (EventIds.Count > 0 && (e.EventId is not { } id || !EventIds.Contains(id)))
        {
            return false;
        }

        foreach (var (field, expected) in FieldEquals)
        {
            if (e.Fields is null || !e.Fields.TryGetValue(field, out var actual) || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(Source) && (e.Source is null || !e.Source.Contains(Source.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Oid))
        {
            var prefix = Oid.Trim().TrimStart('.');
            if (e.Oid is null || !(e.Oid == prefix || e.Oid.StartsWith(prefix + ".", StringComparison.Ordinal)))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(Pattern))
        {
            var regex = RegexCache.GetOrAdd(Pattern, p =>
            {
                try
                {
                    return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    return null;
                }
            });

            try
            {
                if (regex is null || !regex.IsMatch(e.Message))
                {
                    return false;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return true;
    }
}

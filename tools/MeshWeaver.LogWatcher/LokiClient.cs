using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Observability;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.LogWatcher;

/// <summary>
/// Reads red log lines out of Loki over its HTTP query API.
///
/// <para>The query is a <c>query_range</c> over a half-open <c>[start, end)</c> window, which is
/// what lets the watcher hold an exactly-once-ish cursor: every poll asks for the slice it has not
/// read, so a missed poll is caught up rather than lost. The alternative — Loki's tail/websocket
/// stream — drops whatever arrives while the watcher is down, which is precisely when a red log is
/// most likely.</para>
///
/// <para>The HTTP round-trip runs inside <see cref="IIoPool"/>, so it is bounded and never runs on
/// the caller's scheduler (Doc/Architecture/ControlledIoPooling.md).</para>
/// </summary>
public sealed class LokiClient(HttpClient http, IIoPool pool, ILogger<LokiClient>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The LogQL selector for red lines in one namespace. <c>|~</c> is a line filter, so Loki does
    /// the matching server-side and only red lines cross the wire — the whole point of filtering
    /// here rather than shipping every INFO line to the watcher.
    /// </summary>
    public static string RedLineQuery(string ns) =>
        $"{{namespace=\"{ns}\"}} |~ `^(fail|crit):`";

    /// <summary>
    /// Reads red lines in <c>[start, end)</c> for one namespace, oldest first. Emits the batch.
    /// </summary>
    public IObservable<ImmutableList<LogLine>> Query(
        string ns, DateTimeOffset start, DateTimeOffset end, int limit)
    {
        var url = "/loki/api/v1/query_range"
                  + $"?query={Uri.EscapeDataString(RedLineQuery(ns))}"
                  + $"&start={Nanos(start)}&end={Nanos(end)}"
                  + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}"
                  + "&direction=forward";

        return pool.Invoke(ct => http.GetFromJsonAsync<LokiResponse>(url, Json, ct))
            .Select(response =>
            {
                var entries = Flatten(response).ToImmutableList();
                if (entries.Count > 0)
                    logger?.LogInformation(
                        "Loki: {Count} red line(s) in {Namespace} for [{Start:HH:mm:ss}, {End:HH:mm:ss})",
                        entries.Count, ns, start.UtcDateTime, end.UtcDateTime);
                return entries;
            });
    }

    /// <summary>
    /// Flattens Loki's stream/values shape into flat entries, oldest first. Loki returns one
    /// stream per label-set, so the ordering across streams has to be restored here — the burst
    /// grouper depends on lines arriving in emission order to attach a stack trace to its header.
    /// </summary>
    private static IEnumerable<LogLine> Flatten(LokiResponse? response) =>
        (response?.Data?.Result ?? [])
        .SelectMany(stream => (stream.Values ?? [])
            .Where(v => v.Count >= 2)
            .Select(v => new LogLine(
                FromNanos(v[0]),
                Label(stream, "namespace"),
                Label(stream, "pod"),
                v[1])))
        .OrderBy(e => e.Timestamp);

    private static string? Label(LokiStream stream, string name) =>
        stream.Stream is not null && stream.Stream.TryGetValue(name, out var value) ? value : null;

    private static string Nanos(DateTimeOffset value) =>
        (value.ToUnixTimeMilliseconds() * 1_000_000L).ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset FromNanos(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos)
            ? DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000L)
            : DateTimeOffset.UtcNow;

    // ── Loki's wire shape ────────────────────────────────────────────────────

    private sealed record LokiResponse([property: JsonPropertyName("data")] LokiData? Data);

    private sealed record LokiData([property: JsonPropertyName("result")] List<LokiStream>? Result);

    private sealed record LokiStream(
        [property: JsonPropertyName("stream")] Dictionary<string, string>? Stream,
        [property: JsonPropertyName("values")] List<List<string>>? Values);
}

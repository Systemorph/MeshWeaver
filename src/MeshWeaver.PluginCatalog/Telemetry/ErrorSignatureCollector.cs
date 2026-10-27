using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The in-process error ledger behind instance error telemetry (<c>Doc/Architecture/InstanceTelemetry</c>):
/// every <c>Error</c>/<c>Critical</c> log record this process emits is folded into ONE entry per
/// SIGNATURE — category + event id + message template + exception type + top frame — with a count
/// and first/last seen. The <see cref="ErrorSignatureLoggerProvider"/> feeds it from the logging
/// pipeline; the <see cref="InstanceErrorReporter"/> drains what is NEW or ESCALATED into a digest.
///
/// <para>Bounded: at most <see cref="Capacity"/> distinct signatures. When full, the oldest entry that
/// has ALREADY BEEN REPORTED is evicted to make room; when every entry is still unreported the new
/// signature is dropped and counted in <see cref="Dropped"/> — a flood of distinct new errors is
/// itself the finding, and the count says so. Lock-free: a logger must never block on its sink.</para>
///
/// <para>Holds no log line. The template is the <c>{OriginalFormat}</c> of the structured log call
/// (placeholders, no values); a formatted message with no template is first normalised
/// (<see cref="TelemetryRedaction.Normalize"/>), so nothing here can carry a value, a secret or a
/// person's name — see <see cref="ErrorSignatureLoggerProvider"/>.</para>
/// </summary>
public sealed class ErrorSignatureCollector
{
    /// <summary>Default <see cref="Capacity"/>.</summary>
    public const int DefaultCapacity = 200;

    private readonly ConcurrentDictionary<string, SignatureEntry> entries = new(StringComparer.Ordinal);
    private long dropped;

    /// <summary>Creates a collector holding at most <paramref name="capacity"/> signatures.</summary>
    public ErrorSignatureCollector(int capacity = DefaultCapacity)
    {
        Capacity = capacity < 1 ? 1 : capacity;
    }

    /// <summary>The most distinct signatures kept at once.</summary>
    public int Capacity { get; }

    /// <summary>Distinct signatures currently held.</summary>
    public int Count => entries.Count;

    /// <summary>Records that arrived while the ledger was full of unreported signatures and were dropped.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    /// <summary>One held signature.</summary>
    /// <param name="ReportedCount">The <see cref="Count"/> at the last ACCEPTED digest, 0 when never reported.
    /// What <see cref="ErrorSignatureCollector.Pending"/> compares against.</param>
    public sealed record SignatureEntry(
        string Signature,
        string Category,
        int EventId,
        LogLevel Level,
        string Template,
        string? ExceptionType,
        string? TopFrame,
        long Count,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen,
        long ReportedCount)
    {
        /// <summary>True when this entry owes the registry a digest line: never reported, or the
        /// count has since doubled, or grown by a hundred — whichever comes first.</summary>
        public bool IsPending =>
            ReportedCount == 0 || Count >= ReportedCount * 2 || Count >= ReportedCount + 100;

        /// <summary>The wire shape of this entry.</summary>
        public ErrorSignature ToWire() => new(
            Signature, Category, EventId, Level.ToString(), Template, ExceptionType, TopFrame, Count, FirstSeen, LastSeen);
    }

    /// <summary>
    /// The stable key of a signature: 16 hex characters of a SHA-256 over the five identity fields.
    /// The registry dedupes on it per instance, so it must be deterministic across processes and
    /// restarts — which is why it hashes the TEMPLATE and not the message.
    /// </summary>
    public static string ComputeSignature(string category, int eventId, string template, string? exceptionType, string? topFrame)
    {
        var material = string.Join('\n', category, eventId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            template, exceptionType ?? "", topFrame ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
    }

    /// <summary>Folds one error record into the ledger. Never throws, never blocks.</summary>
    public void Record(string category, int eventId, LogLevel level, string template, string? exceptionType, string? topFrame, DateTimeOffset at)
    {
        var boundedTemplate = Clip(template, ErrorDigestPayloads.MaxTemplateLength);
        var boundedCategory = Clip(category, ErrorDigestPayloads.MaxFieldLength);
        var boundedException = exceptionType is null ? null : Clip(exceptionType, ErrorDigestPayloads.MaxFieldLength);
        var boundedFrame = topFrame is null ? null : Clip(topFrame, ErrorDigestPayloads.MaxFieldLength);
        var signature = ComputeSignature(boundedCategory, eventId, boundedTemplate, boundedException, boundedFrame);

        while (true)
        {
            if (entries.TryGetValue(signature, out var existing))
            {
                var next = existing with
                {
                    Count = existing.Count + 1,
                    LastSeen = at > existing.LastSeen ? at : existing.LastSeen,
                    Level = level > existing.Level ? level : existing.Level,
                };
                if (entries.TryUpdate(signature, next, existing))
                    return;
                continue; // lost the CAS to a concurrent record — re-read and fold again
            }

            if (entries.Count >= Capacity && !EvictOneReported())
            {
                Interlocked.Increment(ref dropped);
                return;
            }

            var fresh = new SignatureEntry(signature, boundedCategory, eventId, level, boundedTemplate,
                boundedException, boundedFrame, 1, at, at, 0);
            if (entries.TryAdd(signature, fresh))
                return;
            // a concurrent record added the same signature first — fold into it
        }
    }

    /// <summary>The entries that owe a digest line, most frequent first.</summary>
    public ImmutableList<ErrorSignature> Pending() =>
        entries.Values
            .Where(entry => entry.IsPending)
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.FirstSeen)
            .Select(entry => entry.ToWire())
            .ToImmutableList();

    /// <summary>Every held entry, for diagnostics.</summary>
    public ImmutableList<SignatureEntry> Snapshot() =>
        entries.Values.OrderByDescending(entry => entry.LastSeen).ToImmutableList();

    /// <summary>
    /// Marks the given signatures as reported at the count the digest carried, so they stop being
    /// pending until they escalate. Called only after the registry ACCEPTED the digest — a refused
    /// digest leaves everything pending for the next one.
    /// </summary>
    public void MarkReported(IEnumerable<ErrorSignature> accepted)
    {
        foreach (var signature in accepted)
        {
            while (entries.TryGetValue(signature.Signature, out var existing))
            {
                var reported = Math.Max(existing.ReportedCount, signature.Count);
                if (reported == existing.ReportedCount
                    || entries.TryUpdate(signature.Signature, existing with { ReportedCount = reported }, existing))
                    break;
            }
        }
    }

    private bool EvictOneReported()
    {
        var victim = entries.Values
            .Where(entry => entry.ReportedCount > 0 && !entry.IsPending)
            .OrderBy(entry => entry.LastSeen)
            .FirstOrDefault();
        return victim is not null && entries.TryRemove(new KeyValuePair<string, SignatureEntry>(victim.Signature, victim));
    }

    private static string Clip(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

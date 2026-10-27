using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The wire contract of <c>POST /api/instances/errors</c> — a registered instance delivers a BOUNDED,
/// DEDUPLICATED digest of the error signatures it has logged since its last accepted digest, and the
/// registry turns each one into triage input (<c>Doc/Architecture/InstanceTelemetry</c>). One place
/// for both sides, like <see cref="SyncTokenPayloads"/>, so producer and consumer cannot drift.
///
/// <para>🚨 A digest carries SIGNATURES, never log lines: a category, the message TEMPLATE (the
/// <c>{OriginalFormat}</c> of the structured log call — placeholders, no values), the exception
/// type, the top frame, and counts with first/last seen. There is no field for a formatted message,
/// a stack trace or a sample, so a digest cannot leak what a log line might carry. Both sides also
/// run <see cref="TelemetryRedaction.Mask"/> over every text field, for the template that had to
/// be reconstructed from a formatted message.</para>
///
/// <para>Bounded twice: at most <see cref="MaxSignatures"/> signatures (the most frequent first) and
/// at most <see cref="MaxBodyBytes"/> on the wire (<see cref="Bound"/>); the registry refuses a body
/// above that size before it parses it.</para>
/// </summary>
public static class ErrorDigestPayloads
{
    /// <summary>The route the endpoint is mapped under.</summary>
    public const string Route = "/api/instances/errors";

    /// <summary>The most signatures one digest carries; the rest wait for the next digest.</summary>
    public const int MaxSignatures = 50;

    /// <summary>The most bytes one digest body may be. The registry refuses above it.</summary>
    public const int MaxBodyBytes = 24 * 1024;

    /// <summary>Longest message template kept in a signature.</summary>
    public const int MaxTemplateLength = 400;

    /// <summary>Longest category / exception type / frame kept in a signature.</summary>
    public const int MaxFieldLength = 200;

    /// <summary>Serializer options both sides use (Web camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The request body.
    /// </summary>
    /// <param name="Telemetry">What the sender runs — stamped on its instance record exactly as a
    /// token exchange's would be.</param>
    /// <param name="SampledAt">When the sender composed the digest (UTC).</param>
    /// <param name="Signatures">The new or escalated signatures, most frequent first.</param>
    public sealed record Request(
        InstanceTelemetry? Telemetry,
        DateTimeOffset SampledAt,
        ImmutableList<ErrorSignature> Signatures);

    /// <summary>
    /// The success response — how the registry disposed of the digest, so a sender can log it.
    /// </summary>
    /// <param name="Received">Signatures in the (bounded) body.</param>
    /// <param name="Filed">Signatures that became a NEW triage submission.</param>
    /// <param name="Updated">Signatures that updated an open submission filed earlier.</param>
    /// <param name="Ignored">Signatures the registry dropped (malformed, or a duplicate within the body).</param>
    public sealed record Response(int Received, int Filed, int Updated, int Ignored);

    /// <summary>
    /// Bounds a request to the contract: every text field clipped and masked, the signatures de-duplicated
    /// by <see cref="ErrorSignature.Signature"/>, cut to <see cref="MaxSignatures"/> most frequent first,
    /// and then shortened from the tail until the serialized body fits <see cref="MaxBodyBytes"/>.
    /// Idempotent — bounding a bounded request changes nothing.
    /// </summary>
    public static Request Bound(Request request)
    {
        var signatures = request.Signatures
            .Where(signature => !string.IsNullOrWhiteSpace(signature.Signature))
            .Select(signature => signature.Bounded())
            .GroupBy(signature => signature.Signature, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(signature => signature.Count)
            .ThenBy(signature => signature.FirstSeen)
            .Take(MaxSignatures)
            .ToImmutableList();
        var bounded = request with { Telemetry = request.Telemetry?.Bounded(), Signatures = signatures };
        while (bounded.Signatures.Count > 0 && SizeOf(bounded) > MaxBodyBytes)
            bounded = bounded with { Signatures = bounded.Signatures.RemoveAt(bounded.Signatures.Count - 1) };
        return bounded;
    }

    /// <summary>The serialized size of a request on the wire, in bytes.</summary>
    public static int SizeOf(Request request) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, Json));
}

/// <summary>
/// One deduplicated error signature — the unit a digest carries and the unit the registry files.
/// </summary>
/// <param name="Signature">Stable hash of (category, event id, template, exception type, top frame)
/// — <see cref="ErrorSignatureCollector.ComputeSignature"/>. The dedupe key on both sides.</param>
/// <param name="Category">The logger category (the type that logged).</param>
/// <param name="EventId">The log event id, 0 when none.</param>
/// <param name="Level"><c>Error</c> or <c>Critical</c>.</param>
/// <param name="Template">The message TEMPLATE with placeholders, never a formatted message.</param>
/// <param name="ExceptionType">The full type name of the exception, when one was logged.</param>
/// <param name="TopFrame">The first application frame of the exception's stack, <c>Type.Method</c>, no arguments.</param>
/// <param name="Count">Occurrences since the signature was first seen on this process.</param>
/// <param name="FirstSeen">UTC.</param>
/// <param name="LastSeen">UTC.</param>
public sealed record ErrorSignature(
    string Signature,
    string Category,
    int EventId,
    string Level,
    string Template,
    string? ExceptionType,
    string? TopFrame,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    /// <summary>The signature with every text field masked and clipped to the contract's lengths.</summary>
    public ErrorSignature Bounded() => this with
    {
        Signature = Clip(Signature, 64),
        Category = Clip(TelemetryRedaction.Mask(Category), ErrorDigestPayloads.MaxFieldLength),
        Level = Clip(Level, 16),
        Template = Clip(TelemetryRedaction.Mask(Template), ErrorDigestPayloads.MaxTemplateLength),
        ExceptionType = ExceptionType is null ? null : Clip(TelemetryRedaction.Mask(ExceptionType), ErrorDigestPayloads.MaxFieldLength),
        TopFrame = TopFrame is null ? null : Clip(TelemetryRedaction.Mask(TopFrame), ErrorDigestPayloads.MaxFieldLength),
        Count = Count < 0 ? 0 : Count,
    };

    private static string Clip(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

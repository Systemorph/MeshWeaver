using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// 🚨 <b>The converter that makes <c>[PreventLogging]</c> reach <see cref="RawJson"/> — issues
/// #3044 / #3049.</b>
///
/// <para><b>The hole.</b> <see cref="RawJson.Content"/> carries
/// <see cref="PreventLoggingAttribute"/>, and its own doc comment states the intent plainly: "this
/// is, by definition, the entire serialized message — logging it in full is just re-dumping the
/// message as a string." That attribute was INERT.
/// <see cref="LoggingTypeInfoResolver"/> strips <c>[PreventLogging]</c> members by removing
/// properties from a resolved <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/>,
/// and it can only do that for a type whose <c>Kind</c> is
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object"/>. A type claimed by
/// a custom <see cref="JsonConverter{T}"/> has <c>Kind == None</c> and NO properties at all, so the
/// resolver saw nothing to remove and <see cref="RawJsonConverter"/> went on emitting the payload
/// verbatim through <c>writer.WriteRawValue(value.Content)</c>. Marking a member of a
/// custom-converted type <c>[PreventLogging]</c> silently does nothing — which is the worst kind of
/// control, because the declaration reads as though the protection is in place.</para>
///
/// <para><b>What it cost.</b> <c>writer.WriteRawValue(string)</c> transcodes UTF-16 → UTF-8 through
/// <c>Utf8JsonWriter.TranscodeAndWriteRawValue</c>, which rents up to <b>3 bytes per char</b> from
/// the shared array pool. So every log render of a packaged delivery allocated multiples of the
/// payload — on the ERROR path, where the process is already in trouble. On 2026-09-02 that is the
/// allocation that threw <c>OutOfMemoryException</c> while a portal pod tried to report a delivery
/// failure, and the report was lost.</para>
///
/// <para><b>What replaces it.</b> The head of the payload — where the <c>$type</c> discriminator and
/// the first fields sit, i.e. everything that identifies the message and its producer — plus the
/// exact byte count, JSON-quoted so the line stays single and parseable. That is the same
/// identify-don't-dump trade <c>MessageSizeGuard</c>'s refusals already make, and it is bounded by
/// construction: the render cost of a log line no longer depends on the size of the message.</para>
///
/// <para>Registered ONLY on the logging options (<c>CreateLoggingSerializerOptions</c>), ahead of
/// the real <see cref="RawJsonConverter"/> so it wins converter resolution there. The wire options
/// are untouched — a payload must still round-trip verbatim.</para>
/// </summary>
public sealed class LoggingRawJsonConverter : JsonConverter<RawJson>
{
    /// <summary>
    /// How much of the payload the log keeps. The same bound the refusal lines quote, for the same
    /// reason: enough to recognise the message, never enough to be the message.
    /// </summary>
    public const int PreviewChars = DeliveryPayloadBounds.PayloadPreviewChars;

    /// <summary>
    /// Never used — these options exist to render log output and are never read back. Reading is a
    /// programming error rather than a data condition, so it throws instead of guessing.
    /// </summary>
    /// <param name="reader">Unused.</param>
    /// <param name="typeToConvert">Unused.</param>
    /// <param name="options">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "The logging serializer options are write-only: they redact payloads, so anything they "
            + "produce cannot round-trip. Deserialize with the hub's own JsonSerializerOptions.");

    /// <summary>
    /// Writes a bounded description of the payload — its byte count and its head — instead of the
    /// payload.
    /// </summary>
    /// <param name="writer">The writer to emit to.</param>
    /// <param name="value">The raw payload being redacted.</param>
    /// <param name="options">Unused; the shape written here is fixed.</param>
    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value?.Content))
        {
            writer.WriteNullValue();
            return;
        }

        var content = value.Content;
        writer.WriteStartObject();
        writer.WriteString("contentOmitted",
            "[PreventLogging] the raw payload is not written to logs — see LoggingRawJsonConverter");
        writer.WriteNumber("bytes", Encoding.UTF8.GetByteCount(content));
        writer.WriteString("head",
            content.Length <= PreviewChars ? content : content[..PreviewChars] + "…");
        writer.WriteEndObject();
    }
}

namespace MeshWeaver.Messaging;

/// <summary>
/// Marks a content property as SENSITIVE — personal data, health data, anything that may be read
/// through the store's access control and nowhere else. A marked value is stored, read and rendered
/// for an authorised reader exactly as an unmarked one; what changes is every rendering that leaves
/// the access rules behind: a log line, an exception text, a telemetry attribute, a hand-over to
/// another instance. There the value is replaced by <see cref="Placeholder"/>.
/// </summary>
/// <remarks>
/// <para><b>One attribute, one redaction point.</b> Every log/egress rendering of an object goes
/// through the logging serializer (<c>hub.GetLoggingSerializerOptions()</c>,
/// <c>hub.RenderForLog(value)</c>, <c>hub.RedactJson(json)</c> in
/// <c>MeshWeaver.Messaging.Serialization.SensitiveDataRedaction</c>), which replaces the marked
/// member's value recursively — in nested records, in collections, in polymorphic
/// <c>object</c>-typed content, and in untyped JSON whose <c>$type</c> names a type the process
/// knows, including a NodeType's content type compiled live from its node source. The hub's own
/// <c>JsonSerializerOptions</c>, used for persistence and the wire, are never touched: the stored
/// value stays the value.</para>
/// <para><b>Applies to.</b> A property, a field, or a positional record parameter
/// (<c>record Patient([SensitiveData] string Diagnosis)</c> — no <c>property:</c> target needed).
/// A null value stays null; any other value, of any type, renders as <see cref="Placeholder"/>.</para>
/// <para><b>What it does not protect.</b> Free text: a node's name, description, path or id, a
/// markdown body, a search query. Keep sensitive values out of those — the attribute protects
/// PROPERTIES, and only properties. See <c>Doc/Architecture/SensitiveContentInLogs</c>.</para>
/// <para><b>Relation to <see cref="PreventLoggingAttribute"/>.</b> <c>[PreventLogging]</c> OMITS a
/// member from log renderings (built for large payloads); <c>[SensitiveData]</c> keeps the member
/// visible and replaces its VALUE, so a log still shows that the field was there. Both are honoured
/// by the same logging serializer.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    Inherited = true, AllowMultiple = false)]
public sealed class SensitiveDataAttribute : Attribute
{
    /// <summary>The text a marked value is replaced with in every log or egress rendering.</summary>
    public const string Placeholder = "‹redacted›";
}

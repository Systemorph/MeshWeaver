namespace MeshWeaver.Messaging;

/// <summary>
/// A message still in its on-the-wire form — <see cref="Content"/> is the raw
/// serialized JSON of the actual message, deserialized lazily at the receiving
/// hub.
/// </summary>
/// <param name="Content">
/// The raw JSON payload. <see cref="PreventLoggingAttribute"/>: this is, by
/// definition, the entire serialized message — logging it in full is just
/// re-dumping the message as a string. Log output keeps the delivery's
/// envelope (id, sender, target) but not this blob; the deserialized message
/// is logged downstream through the normal, [PreventLogging]-filtered path.
/// </param>
public record RawJson([property: PreventLogging] string Content);

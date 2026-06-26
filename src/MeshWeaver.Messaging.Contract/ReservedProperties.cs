namespace MeshWeaver.Messaging;

/// <summary>
/// Reserved property keys used in serialized message payloads.
/// </summary>
public static class ReservedProperties
{
    /// <summary>
    /// Property key carrying the object's identity (<c>"$id"</c>).
    /// </summary>
    public const string Id = "$id";
    /// <summary>
    /// Property key carrying the object's type discriminator (<c>"$type"</c>).
    /// </summary>
    public const string Type = "$type";
}

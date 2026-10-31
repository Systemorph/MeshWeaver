namespace MeshWeaver.Messaging;

/// <summary>
/// The UNIQUE identity of one hub activation: a fresh value minted when the hub instance is
/// constructed, held in that hub's own property bag, and never shared with another instance.
///
/// <para>It is what <see cref="DisposeRequest.ForActivation"/> carries (#7324). An object hash
/// (<see cref="ShutdownNack.ActivationToken"/>) is a fine log tag, but it is not an identity: two
/// hub instances can share it, so a delayed request for an old activation could pass for its
/// successor's (#7727 review).</para>
/// </summary>
/// <param name="Value">The activation's identity — opaque, compared ordinally.</param>
public sealed record HubActivationId(string Value)
{
    /// <summary>The identity of <paramref name="hub"/>'s activation, or null for a hub that carries none.</summary>
    /// <param name="hub">The hub instance.</param>
    /// <returns>The identity, or null.</returns>
    public static string? Of(IMessageHub hub) => hub.Get<HubActivationId>()?.Value;
}

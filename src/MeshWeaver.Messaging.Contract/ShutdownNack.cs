namespace MeshWeaver.Messaging;

/// <summary>
/// The ACTIVATION IDENTITY that every transient <see cref="ErrorType.ShuttingDown"/> NACK carries,
/// and the single parser for it.
///
/// <para>🚨 Why an identity rides on the NACK at all. A consumer re-probing a <c>ShuttingDown</c>
/// address cannot otherwise tell ONE hub wedged in teardown from a RECYCLE STORM — a hundred
/// activations each dying before it can answer. Those have OPPOSITE fixes, and #2025 spent a full
/// CI cycle on exactly that ambiguity ("still recycling after 110 probes" says nothing about
/// whether it was 110 probes at one corpse or at 110 of them). The tag is stable for one
/// activation's lifetime and differs across activations, which is the whole question.</para>
///
/// <para>🚨 Why the parser is factored HERE. Two independent riders consume it —
/// <c>MeshNodeStreamExtensions.GetMeshNodeOutcome</c>'s paced re-probe loop (in
/// <c>MeshWeaver.Mesh.Contract</c>) and <c>JsonSynchronizationStream</c>'s recycle re-arm latch
/// (in <c>MeshWeaver.Data</c>) — and neither project references the other. Copying the parser into
/// both is how the MINTING sites drifted before (#2376 review: one embedded no identity at all,
/// another paired it with a per-DELIVERY id that varies on every retry against the SAME
/// activation — each defeats the counter, in opposite directions). One marker, one parser, one
/// formatter, in the contract assembly both sides already reference.</para>
/// </summary>
public static class ShutdownNack
{
    /// <summary>
    /// The literal that precedes the hex activation id in a <see cref="ErrorType.ShuttingDown"/>
    /// NACK message. Written by <see cref="FormatActivationTag"/>, read by
    /// <see cref="ExtractActivationTag"/> — never inlined at a call site.
    /// </summary>
    public const string ActivationMarker = "activation #";

    /// <summary>
    /// Renders the activation tag for <paramref name="activation"/> — the object whose identity IS
    /// the activation (the hub instance). Uses reference identity, never a value hash, so two
    /// equal-by-value hubs are still two activations.
    /// </summary>
    /// <param name="activation">The hub instance the NACK is being minted for.</param>
    /// <returns>The tag, e.g. <c>activation #017DA86C</c>.</returns>
    public static string FormatActivationTag(object activation) =>
        $"{ActivationMarker}{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(activation):X8}";

    /// <summary>
    /// Pulls the stable activation token out of a NACK message, or <c>null</c> when the message
    /// carries none.
    ///
    /// <para>🚨 The TOKEN, never the whole message text. NACK sites pair the tag with per-DELIVERY
    /// detail (an id that is unique to every retry even against the SAME activation), so comparing
    /// whole strings counts one wedged owner as a false storm. And a site that has not been taught
    /// to embed a tag contributes <c>null</c> — excluded from the count, never guessed: a rider
    /// that cannot identify the activation must fall back to counting attempts, which is what it
    /// did before this existed.</para>
    /// </summary>
    /// <param name="message">A NACK message, typically <c>DeliveryFailureException.Message</c>.</param>
    /// <returns>The hex activation id, or <c>null</c> when the message carries none.</returns>
    public static string? ExtractActivationTag(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return null;
        var start = message.IndexOf(ActivationMarker, System.StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += ActivationMarker.Length;
        var end = start;
        while (end < message.Length && System.Uri.IsHexDigit(message[end]))
            end++;
        return end > start ? message[start..end] : null;
    }
}

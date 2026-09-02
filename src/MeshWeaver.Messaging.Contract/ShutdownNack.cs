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

    /// <summary>
    /// The promise that makes an owner-side refusal RETRYABLE — "this address is coming back".
    ///
    /// <para>It is what separates a hub that is recycling from the routing layer reporting that
    /// there is nowhere to go: <c>"No node found at 'x'"</c>, <c>"No route to 'x'"</c> and
    /// <c>"Mesh/Host is shutting down, cannot route to x"</c> all promise nothing, because none of
    /// them speaks for an address that reactivates.</para>
    /// </summary>
    public const string ReactivationPromise = "t" + ReactivationTail;

    /// <summary>Both casings of <see cref="ReactivationPromise"/> off one string.</summary>
    private const string ReactivationTail = "he address may reactivate (recycle / restart)";

    /// <summary>
    /// 🚨 The BANNER every owner-side shutdown refusal opens with — <c>"Hub {address} is shutting
    /// down"</c> — and the reason it is a function rather than a literal at six call sites.
    ///
    /// <para>The banner is the only thing in a refusal that names WHO refused. Consumers use it to
    /// tell an answer from the OWNER (its intake gate, its access gate, its handler, its
    /// DataContext) from one manufactured by the ROUTING layer, which never makes this address the
    /// subject of "is shutting down" — see <see cref="IsAnsweredByOwner"/>.</para>
    /// </summary>
    /// <param name="address">The owner — an <see cref="Address"/>, or its path.</param>
    public static string Banner(object address) => $"{BannerOpen}{address}{BannerClose}";

    /// <summary>
    /// The refusal for a delivery this hub declines HERE AND NOW — it arrived (or was evaluated)
    /// after the hub left service, and was never accepted for processing.
    ///
    /// <para>🚨 THE WORDING IS CONTRACT, not prose. The mesh classifies delivery failures by their
    /// MESSAGE TEXT as well as by <see cref="ErrorType"/>, and this sentence must be matched as
    /// TRANSIENT by <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>,
    /// <c>OrleansRoutingService.ClassifyRoutedFailure</c> and
    /// <c>AreaErrorClassifier.IsTransientHubFailure</c> — so the caller RE-PROBES and lands on the
    /// fresh activation instead of taking a corpse's answer as final. It therefore carries their
    /// markers ("is shutting down", "Rejecting now") by construction. Reword it casually at a call
    /// site and #2727 comes back SILENTLY: nothing fails to compile, the delivery is still refused,
    /// and the caller simply stops retrying.</para>
    /// </summary>
    /// <param name="address">The owner refusing the delivery.</param>
    /// <param name="detail">Parenthesised evidence — run level, activation tag, what faulted. May be null.</param>
    /// <param name="what">What could not be done, e.g. <c>"cannot process GetDataRequest"</c>.</param>
    public static string RejectingNow(object address, string? detail, string what) =>
        $"{Open(address, detail)} — {what}; {ReactivationPromise}. Rejecting now.";

    /// <summary>
    /// The refusal for work this hub ACCEPTED and can no longer finish — a queued turn that came
    /// too late, machinery that can no longer be created, a gate that can never open. Same contract
    /// as <see cref="RejectingNow"/>; the tail differs only because the caller is being told to ask
    /// again rather than that its delivery was turned away at the door.
    /// </summary>
    /// <param name="address">The owner that cannot finish the work.</param>
    /// <param name="detail">Parenthesised evidence. May be null.</param>
    /// <param name="what">What could not be finished.</param>
    public static string RetryForTheAuthoritativeAnswer(object address, string? detail, string what) =>
        $"{Open(address, detail)} — {what}. T{ReactivationTail}; retry to get the authoritative answer.";

    /// <summary>
    /// 🚨 <b>Did the OWNER at <paramref name="ownerAddress"/> answer, or did the routing layer?</b>
    /// The one predicate for that question — DERIVED from <see cref="Banner"/>, never a list of the
    /// sentences somebody happened to think of.
    ///
    /// <para>Issue #3017. Enumerating owner terminals is what failed: a caller's four-shape list
    /// rejected a FIFTH owner-side terminal (the access gate's refusal) as "not from the owner",
    /// reddening a suite on a perfectly correct outcome — and its own guard passed, because a guard
    /// over an enumeration can only assert the members somebody already wrote down. A sixth existed
    /// at the time and had not been noticed either (the intake gate's <see cref="RejectingNow"/>).
    /// Every owner-side refusal opens with <see cref="Banner"/> because every one is composed here,
    /// so recognition follows the producers instead of trailing them.</para>
    ///
    /// <para>What it still REFUSES, which is what keeps it a real check: the routing layer's own
    /// failures. <c>"No node found at 'x'"</c> and <c>"No route to 'x'"</c> carry no banner;
    /// <c>"Mesh is shutting down, cannot route to x"</c> / <c>"Host is shutting down, cannot route
    /// to x"</c> make the MESH or the HOST the subject, never this address; and a DIFFERENT hub's
    /// refusal (<c>"Hub x/child is shutting down"</c>) names that hub, not this one.</para>
    ///
    /// <para>🚨 Deliberately NOT "the failure is classified <see cref="ErrorType.ShuttingDown"/>":
    /// the routing layer mints that classification too, off the very same text
    /// (<c>OrleansRoutingService.ClassifyRoutedFailure</c>), so an ErrorType test would answer this
    /// question with the routing layer's echo of it. The banner is the only evidence that
    /// identifies the speaker.</para>
    ///
    /// <para>🚨 It compares the banner's SUBJECT by PATH, not the whole rendered address. A HOSTED
    /// address renders as <c>path~host</c> (<see cref="Address.ToString"/>), so a producer holding
    /// the <see cref="Address"/> and a caller holding the path would otherwise disagree about the
    /// same hub — a false negative that reads as "the routing layer answered", which is the exact
    /// misclassification this predicate exists to prevent. The path IS the address's identity
    /// (<see cref="Address.Path"/>); the host chain says where it is activated, not who it is.</para>
    /// </summary>
    /// <param name="message">A NACK message, typically <c>DeliveryFailure.Message</c>.</param>
    /// <param name="ownerAddress">The owner the caller addressed — an <see cref="Address"/>, or its path.</param>
    /// <returns><c>true</c> when the refusal came from that owner.</returns>
    public static bool IsAnsweredByOwner(string? message, object ownerAddress)
    {
        if (string.IsNullOrEmpty(message))
            return false;
        var owner = PathOf(ownerAddress);
        if (owner.Length == 0)
            return false;
        // Every banner reads "Hub {subject} is shutting down". Walk them and compare SUBJECTS —
        // a substring test on the whole banner cannot tell "Hub a/b" from "Hub a/b/child".
        for (var i = message.IndexOf(BannerOpen, System.StringComparison.Ordinal);
             i >= 0;
             i = message.IndexOf(BannerOpen, i + BannerOpen.Length, System.StringComparison.Ordinal))
        {
            var start = i + BannerOpen.Length;
            var end = message.IndexOf(BannerClose, start, System.StringComparison.Ordinal);
            // No banner closes after this point, so none closes after any later "Hub " either.
            if (end < 0)
                return false;
            if (string.Equals(PathOf(message[start..end]), owner, System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private const string BannerOpen = "Hub ";
    private const string BannerClose = " is shutting down";

    /// <summary>
    /// The identity half of an address: its <see cref="Address.Path"/>, or — for text lifted out of
    /// a rendered banner, or a path a caller passed as a string — everything before the <c>~</c>
    /// that introduces the host chain.
    /// </summary>
    private static string PathOf(object address)
    {
        if (address is Address typed)
            return typed.Path;
        var text = address.ToString() ?? string.Empty;
        var host = text.IndexOf('~');
        return host < 0 ? text : text[..host];
    }

    /// <summary>The banner plus its parenthesised evidence, when there is any.</summary>
    private static string Open(object address, string? detail) =>
        string.IsNullOrEmpty(detail) ? Banner(address) : $"{Banner(address)} ({detail})";
}

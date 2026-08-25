namespace MeshWeaver.Messaging;

/// <summary>
/// TRANSIENT: an operation needed machinery that a hub can no longer create because that hub
/// (or an ancestor of it) has begun disposing — a recycle, a restart, a node delete. The
/// canonical case is a synchronization stream: it must host its own sub-hub, and hosted-hub
/// creation is FROZEN from the first instant of <see cref="IDisposable.Dispose"/>
/// (<c>HostedHubsCollection.CloseCreation</c>, which cascades through the whole subtree).
///
/// <para>🚨 This is NOT a terminal error. A disposing hub cannot know whether its address is
/// gone for good or about to REACTIVATE, so the honest answer to the caller is "ask again" —
/// the same contract <see cref="ErrorType.ShuttingDown"/> carries on the messaging layer
/// (<c>MessageService.NackThroughParent</c>). When this escapes a message handler,
/// <c>MessageService</c> classifies the resulting <see cref="DeliveryFailure"/> as
/// <see cref="ErrorType.ShuttingDown"/> so long-lived consumers (chiefly
/// <c>SynchronizationStream</c>'s keep-alive + change-feed resubscribe latch) ride it out and
/// rehydrate against the fresh activation instead of faulting.</para>
///
/// <para>Derives from <see cref="ObjectDisposedException"/> because that is the shape the
/// framework already uses for "this stream/hub is torn down": <c>CreateExternalClient</c>
/// threw it, <c>LayoutAreaView.SetParametersAsync</c> catches it around <c>BindStream()</c>
/// ("old circuit's hub may already be disposing"), and <c>SynchronizationStream.OnError</c>
/// classifies it as benign teardown (Debug, no stack-trace pageant). Existing handling keeps
/// working; only the classification is new.</para>
/// </summary>
public sealed class HubDisposingException : ObjectDisposedException
{
    /// <summary>The address of the hub that is disposing (or whose ancestor is).</summary>
    public Address HubAddress { get; }

    /// <summary>
    /// Creates the exception for <paramref name="hubAddress"/>. The message deliberately
    /// contains the words "is shutting down" — the same banner
    /// <c>MessageService</c>'s intake NACK uses — because the GUI's
    /// <c>AreaErrorClassifier.IsTransientHubFailure</c> and
    /// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c> match on it when the typed failure
    /// has already been flattened into a <c>DeliveryFailureException</c> message.
    /// </summary>
    /// <param name="hubAddress">Address of the disposing hub.</param>
    /// <param name="what">What could not be created, e.g. the stream reference.</param>
    public HubDisposingException(Address hubAddress, object what)
        : base(hubAddress.ToString(),
            // 🚨 DOUBLE quotes around `what`, chosen because the value cannot contain them. A
            // WorkspaceReference renders as its own POINTER, which already carries single quotes
            // around its segments — EntityReference's is `/{collection}/'{id}'` — so single-quoting
            // it produced the unreadable `cannot create '/data/'postAdvanceStatus''` (#2255).
            $"Hub {hubAddress} is shutting down — cannot create {Describe(what)}. "
            + "The address may reactivate (recycle / restart); retry to get the authoritative answer.")
    {
        HubAddress = hubAddress;
    }

    /// <summary>
    /// Renders <paramref name="what"/> for the message, delimited with DOUBLE quotes — always,
    /// with no inspection of the value.
    ///
    /// <para>The value is very often a <c>WorkspaceReference</c>, whose <c>ToString</c> IS its JSON
    /// pointer and carries its own single quotes (<c>EntityReference</c>'s is
    /// <c>/{collection}/'{id}'</c>). Single-quoting that produced the unreadable
    /// <c>cannot create '/data/'postAdvanceStatus''</c>.</para>
    ///
    /// <para>Choosing a delimiter the value cannot contain beats testing whether it "looks quoted":
    /// an apostrophe anywhere in the text proves nothing about delimiting — <c>Bob'sThing</c> has
    /// one and is not delimited, and a pointer's quotes sit in the middle rather than at the ends —
    /// so any such test is a guess that is wrong in both directions. One rule, no branch, always
    /// delimited. Pure and total: a null renders as <c>"(unspecified)"</c> rather than throwing
    /// inside an exception constructor.</para>
    /// </summary>
    private static string Describe(object? what)
    {
        var text = what?.ToString();
        return $"\"{(string.IsNullOrEmpty(text) ? "(unspecified)" : text)}\"";
    }

    /// <summary>
    /// True when <paramref name="exception"/> is — or wraps, at any depth — a
    /// <see cref="HubDisposingException"/>. Handler exceptions reach
    /// <c>MessageService</c> wrapped (e.g. <see cref="System.Reflection.TargetInvocationException"/>
    /// from <c>Workspace.SubscribeToClient</c>'s reflective dispatch), so the check must walk
    /// the chain rather than test the outermost type.
    /// </summary>
    /// <param name="exception">The exception to classify; may be null.</param>
    /// <returns><c>true</c> when the failure is a hub-disposal race.</returns>
    public static bool IsHubDisposal(Exception? exception) => IsHubDisposal(exception, depth: 0);

    // depth caps the walk: an exception graph is caller-supplied data, and AggregateException
    // fan-out makes the traversal a tree, not a chain. 16 is far beyond any real wrapping depth
    // (the deepest observed is TargetInvocationException → HubDisposingException).
    private static bool IsHubDisposal(Exception? exception, int depth)
    {
        if (depth > 16)
            return false;
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is HubDisposingException)
                return true;
            if (e is AggregateException agg
                && agg.InnerExceptions.Any(inner => IsHubDisposal(inner, depth + 1)))
                return true;
        }
        return false;
    }
}

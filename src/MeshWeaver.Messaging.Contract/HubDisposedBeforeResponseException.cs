namespace MeshWeaver.Messaging;

/// <summary>
/// TRANSIENT: a request was in flight when its ISSUING hub went down, so the response can never
/// arrive. Raised by <c>MessageHub.CancelCallbacks</c> for every pending response subject at
/// disposal.
///
/// <para>🚨 <b>Why it is a type and not just a message.</b> This is a TEARDOWN fact — the hub was
/// recycled, the silo deactivated the grain, the circuit closed — and not a fault of the work that
/// hit it. Every other teardown shape in the framework already carries that fact in its TYPE so
/// each layer classifies it identically instead of re-deriving it from a string
/// (<see cref="HubDisposingException"/>, and the disposed-container check beside it). This one did
/// not: it was a bare <see cref="ObjectDisposedException"/>, so
/// <see cref="HubDisposingException.IsHubDisposal"/> answered <c>false</c> for it and callers had
/// no way to tell "the hub went away underneath me" from a real defect. The visible cost was small
/// and the shape is not: a fire-and-forget activity-tracking write logged a full <c>fail</c>-level
/// stack for an ordinary pod recycle (#3148), and any caller wanting to tell the two apart had to
/// match on the message text this class now owns.</para>
///
/// <para>🚨 <b>The message is deliberately UNCHANGED</b> from the bare exception it replaces.
/// Several classifiers in the GUI and the stream cache match teardown on message text
/// (<c>AreaErrorClassifier.IsTransientHubFailure</c>,
/// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>), and log fingerprints group incidents by it.
/// Adding the type is additive; changing the words would not be, so it does not.</para>
///
/// <para>Derives from <see cref="ObjectDisposedException"/>, which is what this already was — so
/// every existing <c>catch (ObjectDisposedException)</c> keeps working unchanged. Only the
/// classification is new.</para>
/// </summary>
public sealed class HubDisposedBeforeResponseException : ObjectDisposedException
{
    /// <summary>The hub that was disposed while the request was outstanding.</summary>
    public Address HubAddress { get; }

    /// <summary>The request type that will never be answered (diagnostics).</summary>
    public string? RequestType { get; }

    /// <summary>The target the request was addressed to (diagnostics).</summary>
    public string? Target { get; }

    /// <summary>
    /// Creates the exception. <paramref name="objectName"/> is <c>nameof(MessageHub)</c> and the
    /// message is composed exactly as the untyped exception composed it — see the remarks on why
    /// neither may drift.
    /// </summary>
    /// <param name="objectName">The disposed object's name, as <see cref="ObjectDisposedException"/> reports it.</param>
    /// <param name="hubAddress">Address of the hub that was disposed.</param>
    /// <param name="requestType">The outstanding request's type name.</param>
    /// <param name="target">The address the request was sent to.</param>
    public HubDisposedBeforeResponseException(
        string objectName, Address hubAddress, string? requestType, string? target)
        : base(objectName,
            $"Hub {hubAddress} was disposed before the response arrived "
            + $"(request type {requestType}, target {target}).")
    {
        HubAddress = hubAddress;
        RequestType = requestType;
        Target = target;
    }

    /// <summary>
    /// True when <paramref name="exception"/> is (or wraps) this teardown. Walks the chain for the
    /// same reason <see cref="HubDisposingException.IsHubDisposal"/> does: the fault is routinely
    /// re-wrapped before a caller sees it.
    /// </summary>
    /// <param name="exception">The exception to classify; may be null.</param>
    /// <returns><c>true</c> when the issuing hub was disposed with the request outstanding.</returns>
    public static bool IsHubDisposedBeforeResponse(Exception? exception)
        => ExceptionChain.Contains<HubDisposedBeforeResponseException>(exception);
}

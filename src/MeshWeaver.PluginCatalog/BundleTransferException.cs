namespace MeshWeaver.PluginCatalog;

/// <summary>
/// WHERE a registry transfer failed — the three shapes a bounded transfer can be refused in, kept
/// apart because they want opposite fixes (#4528).
/// </summary>
public enum BundleTransferStage
{
    /// <summary>The registry accepted the connection and never began a response: no status line,
    /// no headers, zero bytes within the responsiveness budget. Accuses the REGISTRY (the request
    /// is stuck behind authentication or inside the index assembly), never the archive.</summary>
    NoResponse,

    /// <summary>Headers arrived and the body then went quiet for the whole budget. The byte count
    /// says how far it got; a stall at zero bytes is the registry, a stall near the declared length
    /// is the transport.</summary>
    StalledMidBody,

    /// <summary>The body is larger than this client accepts — declared up front, or measured while
    /// streaming when nothing was declared. Accuses the SIZE; the remedy is a smaller or resumable
    /// bundle, never a larger number.</summary>
    OverSize,
}

/// <summary>
/// A registry transfer that this client refused at a NAMED stage — see
/// <see cref="BundleTransferStage"/> — carrying the numbers a reader needs to tell the stages
/// apart without the log: what had arrived, what was declared, and how long it took to give up.
///
/// <para>🚨 Deliberately NOT a <see cref="TimeoutException"/>: a bare timeout is exactly the
/// sentence that made eighteen identical failures unexplainable ("The operation has timed out"),
/// and <see cref="BundleTransferStage.OverSize"/> is not a timeout at all.</para>
/// </summary>
public sealed class BundleTransferException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="stage">Where the transfer was refused.</param>
    /// <param name="transfer">What was being transferred, as a reader would name it
    /// (<c>Bundle index</c>, <c>Bundle for X@1.2.3</c>).</param>
    /// <param name="registryUrl">The registry the transfer was from — an instance may have several
    /// configured, and a failure that cannot be attributed to one does not say which needs fixing.</param>
    /// <param name="elapsed">How long the transfer had been running when it was refused.</param>
    /// <param name="received">Bytes of the body that had arrived.</param>
    /// <param name="declared">The <c>Content-Length</c> the registry declared, or null when it
    /// declared none.</param>
    /// <param name="bound">The budget that was exceeded: the silence budget for the two stall
    /// stages, the byte bound for <see cref="BundleTransferStage.OverSize"/>.</param>
    public BundleTransferException(
        BundleTransferStage stage, string transfer, string registryUrl, TimeSpan elapsed,
        long received, long? declared, long bound)
        : base(Describe(stage, transfer, registryUrl, elapsed, received, declared, bound))
    {
        Stage = stage;
        Transfer = transfer;
        RegistryUrl = registryUrl;
        Elapsed = elapsed;
        Received = received;
        Declared = declared;
        Bound = bound;
    }

    /// <summary>Where the transfer was refused.</summary>
    public BundleTransferStage Stage { get; }

    /// <summary>What was being transferred.</summary>
    public string Transfer { get; }

    /// <summary>The registry the transfer was from.</summary>
    public string RegistryUrl { get; }

    /// <summary>How long the transfer had been running when it was refused.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Bytes of the body that had arrived.</summary>
    public long Received { get; }

    /// <summary>The declared <c>Content-Length</c>, or null when none was declared.</summary>
    public long? Declared { get; }

    /// <summary>The budget that was exceeded — seconds of silence, or bytes.</summary>
    public long Bound { get; }

    /// <summary>The one sentence per stage. Pure, so the wording can be pinned.</summary>
    internal static string Describe(
        BundleTransferStage stage, string transfer, string registryUrl, TimeSpan elapsed,
        long received, long? declared, long bound)
        => stage switch
        {
            BundleTransferStage.NoResponse =>
                $"{transfer}: the registry at {registryUrl} did not begin a response within "
                + $"{bound} s — no status line and no headers arrived in {elapsed.TotalSeconds:0} s. "
                + "The registry is stalled, not the transfer",
            BundleTransferStage.StalledMidBody =>
                $"{transfer}: the registry at {registryUrl} sent no data for {bound} s; "
                + $"{received} of {DeclaredText(declared)} byte(s) had arrived after "
                + $"{elapsed.TotalSeconds:0} s",
            BundleTransferStage.OverSize =>
                $"{transfer}: the body from {registryUrl} is larger than the {bound} byte(s) this "
                + $"client accepts — {DeclaredText(declared)} declared, {received} received. "
                + "The remedy is a smaller or resumable bundle, never a larger bound",
            _ => $"{transfer}: refused at stage {stage}",
        };

    private static string DeclaredText(long? declared)
        => declared?.ToString() ?? "an undeclared number of";
}

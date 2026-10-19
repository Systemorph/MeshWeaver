namespace MeshWeaver.Messaging;

/// <summary>
/// The cancellation a hub hands its handler turns, and the ONE owner of the sources behind it.
///
/// <para>🚨 <b>A reader never holds a <see cref="CancellationTokenSource"/>, only a token captured
/// when that source was minted</b> (issue #6958). <see cref="MessageService"/> used to keep a
/// <c>volatile CancellationTokenSource</c> field and read <c>field.Token</c> at the top of every
/// turn, while <c>CancelExecution</c> — called OFF the action block, by a hub's disposal when its
/// bring-up never finished and by the disposal stall detector — swapped in a fresh source and then
/// cancelled and DISPOSED the old one. A turn that had read the field just before the swap then
/// called <see cref="CancellationTokenSource.Token"/> on a disposed source, which throws
/// <c>ObjectDisposedException: The CancellationTokenSource has been disposed.</c> — logged as
/// <c>Unhandled exception in delivery pipeline</c> out of <c>MessageService.RunHandler</c> on a
/// <c>content-type-registration</c> hub torn down during its own initialization.</para>
///
/// <para>Here the token is read ONCE, in the generation's constructor, while the source is
/// provably alive; a reader that snapshotted <see cref="Current"/> before a roll reads that captured
/// struct afterwards and gets the CANCELLED token — which is exactly what the roll means to tell
/// it. A token whose source has been disposed stays safe to use: <c>IsCancellationRequested</c>,
/// <c>ThrowIfCancellationRequested</c> and <c>Register</c> (which runs the callback at once on an
/// already-cancelled token) do not touch the disposed state.</para>
///
/// <para><see cref="Interlocked.Exchange{T}(ref T, T)"/> IS the claim: each generation is retired
/// by exactly one caller, so two concurrent rolls can no longer cancel or dispose the same source
/// twice — which is why there is no <c>catch (ObjectDisposedException)</c> anywhere on this path.</para>
///
/// <para>Public, like <see cref="MessageStormBreaker"/>, only so the framework's own tests can
/// drive it without an assembly-wide <c>InternalsVisibleTo</c>; applications never construct it.</para>
/// </summary>
public sealed class TurnCancellation
{
    private Generation current = new();

    /// <summary>
    /// One source and the token captured from it at construction. The token is the only thing a
    /// reader ever takes from a generation.
    /// </summary>
    public sealed class Generation
    {
        internal Generation()
        {
            Source = new CancellationTokenSource();
            Token = Source.Token;
        }

        internal CancellationTokenSource Source { get; }

        /// <summary>The token captured while the source was alive — safe to read after a roll.</summary>
        public CancellationToken Token { get; }
    }

    /// <summary>The generation handler turns observe right now.</summary>
    public Generation Current => Volatile.Read(ref current);

    /// <summary>The token a handler turn should observe right now.</summary>
    public CancellationToken Token => Current.Token;

    /// <summary>
    /// Retires the current generation — cancels it, so every handler holding its token is told to
    /// stop — and installs a fresh one for subsequent turns. Safe to call concurrently and
    /// repeatedly: each generation is claimed and retired by exactly one caller.
    /// </summary>
    /// <returns><c>true</c> when the retired generation had not been cancelled before.</returns>
    public bool CancelAndRoll()
    {
        var retired = Interlocked.Exchange(ref current, new Generation());
        var wasLive = !retired.Source.IsCancellationRequested;
        try
        {
            retired.Source.Cancel();
        }
        finally
        {
            retired.Source.Dispose();
        }
        return wasLive;
    }
}

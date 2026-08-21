using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Serializes outbound sends and re-reads each mail's CURRENT state immediately before sending —
/// the fix for the duplicate-delivery defect observed live on 2026-08-21 (every queued mail
/// arrived twice).
///
/// <para><b>Why the old claim could not work.</b> The watch query emits the live SET on every
/// change, and the change feed legitimately delivers the same <c>New</c> snapshot more than once
/// (initial snapshot + change echo — the same reason tests must never assert "exactly N change
/// events"). The old New → Sending "claim" was a plain <c>UpdateNode</c>: last-write-wins, no
/// compare — and the node it returned always echoed <c>Sending</c>, so the "claim failed" guard
/// was unreachable. Two emissions of the same queued mail therefore meant two full sends.</para>
///
/// <para><b>The shape of the fix.</b> One instance per sender; <c>Subject + Concat</c> is the
/// serialization (the canonical queue idiom — order without a lock, no semaphore), and each item
/// re-reads the mail's authoritative state through the seam before touching it: a duplicate
/// emission, a stale echo, or a re-emitted snapshot finds the mail already
/// <c>Sending</c>/<c>Sent</c>/<c>Failed</c> and is skipped. A read that does not answer within
/// its budget SKIPS with a warning — not sending is the safe direction, the mail stays
/// <c>New</c>, and the next emission retries.</para>
///
/// <para><b>What deliberately remains.</b> Two PODS overlapping during a rollout can both read
/// <c>New</c> and both send — cross-process at-least-once is the accepted trade (same as the
/// retry-after-partial-failure note on <c>ContactInquiry.SubmissionId</c>); this class removes
/// the in-process duplicates, which are the ones users actually observed.</para>
///
/// <para>Seams are delegates so the whole state machine is testable without a hub — see
/// <c>OutboundSendQueueTest</c>.</para>
/// </summary>
internal sealed class OutboundSendQueue : IDisposable
{
    /// <summary>How long the authoritative pre-send read may take. It answers from the node
    /// stream's current state, so anything but a prompt answer means the read path is unhealthy —
    /// waiting longer cannot make sending safer.</summary>
    public static readonly TimeSpan DefaultReadBudget = TimeSpan.FromSeconds(10);

    private readonly Func<string, IObservable<MeshWeaver.Mesh.Email?>> readCurrent;
    private readonly Func<MeshNode, MeshWeaver.Mesh.Email, EmailStatus, IObservable<MeshNode>> writeStatus;
    private readonly Func<MeshWeaver.Mesh.Email, IObservable<bool>> send;
    private readonly TimeSpan readBudget;
    private readonly ILogger? logger;

    private readonly Subject<(MeshNode Node, MeshWeaver.Mesh.Email Snapshot)> queue = new();
    private readonly Subject<string> processed = new();
    private readonly IDisposable subscription;

    /// <summary>Creates the queue.</summary>
    /// <param name="readCurrent">Authoritative read of the mail node's CURRENT content by path.
    /// When it answers, it emits the content (null = a node without readable e-mail content, which
    /// is skipped). An absent or unreachable node may emit NOTHING at all — the production seam is
    /// the per-node stream, which stays silent rather than emitting an absence — and the queue's
    /// read budget converts that silence into a fail-closed skip.</param>
    /// <param name="writeStatus">Writes the given status onto the node (the System-identity write
    /// lives behind this seam).</param>
    /// <param name="send">Performs the actual send; false or an error marks the mail Failed.</param>
    /// <param name="readBudget">Budget for <paramref name="readCurrent"/>; defaults to
    /// <see cref="DefaultReadBudget"/>.</param>
    /// <param name="logger">Diagnostics.</param>
    public OutboundSendQueue(
        Func<string, IObservable<MeshWeaver.Mesh.Email?>> readCurrent,
        Func<MeshNode, MeshWeaver.Mesh.Email, EmailStatus, IObservable<MeshNode>> writeStatus,
        Func<MeshWeaver.Mesh.Email, IObservable<bool>> send,
        TimeSpan? readBudget = null,
        ILogger? logger = null)
    {
        this.readCurrent = readCurrent;
        this.writeStatus = writeStatus;
        this.send = send;
        this.readBudget = readBudget ?? DefaultReadBudget;
        this.logger = logger;

        // Concat subscribes the next item only after the previous completed — the serialization.
        // Every inner observable is hardened to COMPLETE (never error), so one poisoned mail can
        // never kill the queue for the ones behind it.
        subscription = queue
            .Select(item => Observable.Defer(() => ProcessOne(item.Node, item.Snapshot))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "OutboundSendQueue: processing {Path} failed unexpectedly", item.Node.Path);
                    return Observable.Empty<Unit>();
                })
                .Concat(Observable.Defer(() =>
                {
                    processed.OnNext(item.Node.Path);
                    return Observable.Empty<Unit>();
                })))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger?.LogError(ex, "OutboundSendQueue: the queue itself faulted"));
    }

    /// <summary>Emits each item's node path when the queue has finished with it (sent, skipped,
    /// or failed) — the deterministic completion signal the tests await.</summary>
    public IObservable<string> Processed => processed;

    /// <summary>Queues one emitted mail snapshot for a serialized, re-checked send.</summary>
    public void Enqueue(MeshNode node, MeshWeaver.Mesh.Email snapshot) =>
        queue.OnNext((node, snapshot));

    private IObservable<Unit> ProcessOne(MeshNode node, MeshWeaver.Mesh.Email snapshot) =>
        readCurrent(node.Path)
            .Take(1)
            .Timeout(readBudget, Observable.Throw<MeshWeaver.Mesh.Email?>(new TimeoutException(
                $"the pre-send state read for {node.Path} did not answer within {readBudget}")))
            .SelectMany(current =>
            {
                // The gate: only a mail that CURRENTLY reads New is ours to send. Anything else —
                // a duplicate emission of an already-claimed mail, a stale echo of a finished one,
                // a node that vanished — is skipped, not an error.
                if (current is null
                    || current.Direction != EmailDirection.Outbound
                    || current.Status != EmailStatus.New)
                {
                    logger?.LogDebug(
                        "OutboundSendQueue: {Path} no longer reads as queued (status {Status}) — skipped",
                        node.Path, current?.Status.ToString() ?? "<absent>");
                    return Observable.Empty<Unit>();
                }

                return writeStatus(node, current, EmailStatus.Sending)
                    .SelectMany(_ => send(current)
                        .SelectMany(ok =>
                        {
                            logger?.LogInformation(
                                "OutboundSendQueue: {Path} → {To} sent={Sent}",
                                node.Path, current.To, ok);
                            return writeStatus(node, current,
                                ok ? EmailStatus.Sent : EmailStatus.Failed);
                        }))
                    .Select(_ => Unit.Default)
                    .Catch((Exception ex) =>
                    {
                        logger?.LogWarning(ex,
                            "OutboundSendQueue: send failed for {Path} — marking Failed", node.Path);
                        return writeStatus(node, current, EmailStatus.Failed)
                            .Select(_ => Unit.Default)
                            .Catch(Observable.Empty<Unit>());
                    });
            })
            .Catch((TimeoutException ex) =>
            {
                // Fail CLOSED: without a trustworthy current state, not sending is the safe
                // direction — the mail stays New and the next emission retries.
                logger?.LogWarning(ex,
                    "OutboundSendQueue: state read for {Path} timed out — send skipped, will retry "
                    + "on the next emission", node.Path);
                return Observable.Empty<Unit>();
            });

    public void Dispose()
    {
        subscription.Dispose();
        queue.Dispose();
        processed.Dispose();
    }
}

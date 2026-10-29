using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// One create the store never saw (#7380): the create handler answered it <c>Unavailable</c>
/// because the data store could not be reached, so nothing was written and re-issuing the same
/// request is meaningful. Recorded by the handler as a DURABLE INTENT in the
/// <see cref="PendingWriteLedger"/>; the recovery watcher (<see cref="PendingWriteRecovery"/>)
/// re-issues it ONCE when the store answers again, as <see cref="Writer"/>.
/// </summary>
/// <param name="Node">The node exactly as the refused request carried it (same id, same path).</param>
/// <param name="CreatedBy">The requester the handler resolved for the refused request.</param>
/// <param name="Writer">The AccessContext the refused request was delivered with: the identity the
/// re-issue is posted as, so it is authorised exactly as the original would have been.</param>
/// <param name="RefusedAt">When the store refused it.</param>
/// <param name="Reason">The refusal, as the handler logged it.</param>
public sealed record PendingWrite(
    MeshNode Node,
    string? CreatedBy,
    AccessContext? Writer,
    DateTimeOffset RefusedAt,
    string Reason)
{
    /// <summary>The path the write targets: its identity in the ledger.</summary>
    public string Path => Node.Path;

    /// <summary>Who the write belongs to, for the report: the requester, else the writer's id.</summary>
    public string WriterName => CreatedBy ?? Writer?.ObjectId ?? "(unknown writer)";
}

/// <summary>What became of one <see cref="PendingWrite"/> when it was re-issued.</summary>
/// <param name="Write">The write.</param>
/// <param name="Verdict">One of the <see cref="PendingWriteVerdict"/> values.</param>
/// <param name="Detail">The store's or the handler's answer, when it was not a landing.</param>
public sealed record PendingWriteOutcome(PendingWrite Write, string Verdict, string? Detail);

/// <summary>The open set of verdicts a re-issued <see cref="PendingWrite"/> reaches.</summary>
public static class PendingWriteVerdict
{
    /// <summary>The re-issue landed: the node now exists, created as the original writer.</summary>
    public const string Landed = "Landed";

    /// <summary>The path already holds a node: another writer (often the caller itself) created it
    /// while the intent was pending. Nothing was written.</summary>
    public const string AlreadyPresent = "AlreadyPresent";

    /// <summary>The re-issue was refused again (unavailable again, or judged). Reported and DROPPED:
    /// a pending write is re-issued once and never retried for ever.</summary>
    public const string Dropped = "Dropped";
}

/// <summary>
/// The durable record of creates the store never saw (#7380), and the ONE place the recovery watcher
/// reads them from.
///
/// <para><b>Why a record, not a retry.</b> The create handler is told the store was unreachable after
/// the bounded connect retry (<c>TransientStorageFaults.RetryTransientConnect</c>) is spent. Retrying
/// there, or on a timer, aims more load at the resource that is down; waiting inside the caller's
/// subscription loses the write when the caller goes away. So the refused write is RECORDED here and
/// re-issued once, by <see cref="PendingWriteRecovery"/>, on the platform's evidence that the store
/// answers again (<see cref="StoreReachability.StoreAnswersAgain"/>).</para>
///
/// <para><b>Durable.</b> The record cannot go to the store that just refused it. With
/// <see cref="JournalDirectoryKey"/> configured, each intent is also a file in that directory (on a
/// volume that is not the database), written before the intent counts as recorded and removed once
/// it is settled; a process that starts finds the files and re-issues them on the first evidence. Without the
/// key, an intent lives for the process: it survives the caller and the activation that refused it,
/// not a restart.</para>
///
/// <para>One intent per PATH: a second refusal of the same path replaces the first, so a path is
/// re-issued once whatever how often its writer was refused. Mesh-scoped singleton; instance state
/// only.</para>
/// </summary>
public sealed class PendingWriteLedger : IDisposable
{
    /// <summary>The configuration key naming the journal directory (optional).</summary>
    public const string JournalDirectoryKey = "Mesh:PendingWrites:JournalDirectory";

    private const string JournalExtension = ".pending-write.json";
    private readonly IIoPool pool;
    private readonly ILogger? logger;
    private readonly Subject<PendingWriteOutcome> outcomes = new();
    private ImmutableDictionary<string, PendingWrite> pending =
        ImmutableDictionary.Create<string, PendingWrite>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the ledger.</summary>
    /// <param name="journalDirectory">The journal directory, or null for a process-lifetime ledger.</param>
    /// <param name="pool">The file-system I/O pool the journal is written through.</param>
    /// <param name="logger">Where recordings are logged.</param>
    public PendingWriteLedger(string? journalDirectory, IIoPool pool, ILogger<PendingWriteLedger>? logger = null)
    {
        JournalDirectory = string.IsNullOrWhiteSpace(journalDirectory) ? null : journalDirectory.Trim();
        this.pool = pool;
        this.logger = logger;
    }

    /// <summary>The journal directory, or null when intents live for the process only.</summary>
    public string? JournalDirectory { get; }

    /// <summary>The intents not yet re-issued.</summary>
    public ImmutableList<PendingWrite> Pending => pending.Values.ToImmutableList();

    /// <summary>Every settled re-issue, as it is settled — the report stream.</summary>
    public IObservable<PendingWriteOutcome> Outcomes => outcomes;

    /// <summary>
    /// Records <paramref name="write"/>. Cold: the intent counts once the journal file (when there is
    /// a journal) is written, and the observable then emits once and completes.
    /// </summary>
    /// <param name="write">The refused create.</param>
    /// <param name="options">Serializer options that can write the node's content.</param>
    public IObservable<Unit> Record(PendingWrite write, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var persisted = JournalDirectory is { } directory
                ? pool.InvokeBlocking(ct =>
                {
                    Directory.CreateDirectory(directory);
                    var json = JsonSerializer.Serialize(write, options);
                    var file = FileOf(directory, write.Path);
                    var temporary = file + ".tmp";
                    ct.ThrowIfCancellationRequested();
                    File.WriteAllText(temporary, json);
                    ct.ThrowIfCancellationRequested();
                    File.Move(temporary, file, overwrite: true);
                    return Unit.Default;
                })
                : Observable.Return(Unit.Default);
            return persisted.Do(_ =>
            {
                ImmutableInterlocked.AddOrUpdate(ref pending, write.Path, write, (_, _) => write);
                logger?.LogWarning(
                    "[PendingWrite] RECORDED {Path} by {Writer}: the store refused the create as unavailable; it is "
                    + "re-issued once, as the same writer, when the store answers again{Journal}",
                    write.Path, write.WriterName,
                    JournalDirectory is null ? " (process-lifetime record)" : $" (journal {JournalDirectory})");
            });
        });

    /// <summary>
    /// Reads the journal into the ledger — what a starting process does, so intents recorded before a
    /// restart are re-issued. Emits how many intents were loaded. A file that cannot be read is
    /// reported and left in place; it is never silently skipped as if it were not there.
    /// </summary>
    /// <param name="options">Serializer options that can read the node's content.</param>
    public IObservable<int> LoadJournal(JsonSerializerOptions options) =>
        JournalDirectory is not { } directory
            ? Observable.Return(0)
            : pool.InvokeBlocking(ct =>
            {
                if (!Directory.Exists(directory))
                    return 0;
                var loaded = 0;
                foreach (var file in Directory.EnumerateFiles(directory, "*" + JournalExtension))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var write = JsonSerializer.Deserialize<PendingWrite>(File.ReadAllText(file), options);
                        if (write?.Node is null)
                            throw new JsonException("the journal entry names no node");
                        ImmutableInterlocked.TryAdd(ref pending, write.Path, write);
                        loaded++;
                    }
                    catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
                    {
                        logger?.LogError(exception,
                            "[PendingWrite] journal entry {File} could not be read, so the write it records cannot be "
                            + "re-issued; it is left in place for an operator", file);
                    }
                }
                return loaded;
            });

    /// <summary>
    /// Takes every pending intent out of the ledger, atomically: what one recovery pass re-issues.
    /// An intent taken here is never re-issued by another pass.
    /// </summary>
    public ImmutableList<PendingWrite> TakeAll()
    {
        var taken = Interlocked.Exchange(ref pending,
            ImmutableDictionary.Create<string, PendingWrite>(StringComparer.OrdinalIgnoreCase));
        return taken.Values.ToImmutableList();
    }

    /// <summary>
    /// Settles a re-issued intent: removes its journal file and reports the outcome (logged, and on
    /// <see cref="Outcomes"/>). Cold.
    /// </summary>
    /// <param name="outcome">What became of the re-issue.</param>
    public IObservable<PendingWriteOutcome> Settle(PendingWriteOutcome outcome) =>
        (JournalDirectory is { } directory
            ? pool.InvokeBlocking(ct =>
            {
                var file = FileOf(directory, outcome.Write.Path);
                ct.ThrowIfCancellationRequested();
                if (File.Exists(file))
                    File.Delete(file);
                return Unit.Default;
            })
            : Observable.Return(Unit.Default))
        .Select(_ =>
        {
            var write = outcome.Write;
            if (outcome.Verdict == PendingWriteVerdict.Dropped)
                logger?.LogWarning(
                    "[PendingWrite] DROPPED {Path} by {Writer} (refused as unavailable at {RefusedAt:O}): the re-issue "
                    + "was refused again — {Detail}. A pending write is re-issued once and never retried for ever; "
                    + "the writer must repeat it.",
                    write.Path, write.WriterName, write.RefusedAt, outcome.Detail);
            else
                logger?.LogInformation(
                    "[PendingWrite] {Verdict} {Path} by {Writer} (refused as unavailable at {RefusedAt:O}){Detail}",
                    outcome.Verdict, write.Path, write.WriterName, write.RefusedAt,
                    outcome.Detail is null ? "" : $" — {outcome.Detail}");
            outcomes.OnNext(outcome);
            return outcome;
        });

    private static string FileOf(string directory, string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));
        return System.IO.Path.Combine(directory, hash[..32] + JournalExtension);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        outcomes.OnCompleted();
        outcomes.Dispose();
    }
}

/// <summary>
/// The recovery watcher of #7380: when the store answers again, each pending create is re-issued
/// ONCE, as the writer that was refused.
///
/// <para><b>The trigger is evidence, not a clock.</b> <see cref="StoreReachability.StoreAnswersAgain"/>:
/// a declared change-feed gap (the store's notification connection lost and resumed, which a database
/// restart produces) or a commit the store announced. While the store stays away neither arrives and
/// nothing is re-issued. On a host with no cross-process feed nothing is ever re-issued, and the
/// intents stay pending and visible.</para>
///
/// <para><b>Once, then a verdict.</b> Each piece of evidence takes every pending intent out of the
/// ledger (<see cref="PendingWriteLedger.TakeAll"/>) and re-issues it as a
/// <see cref="CreateNodeRequest"/> marked <see cref="CreateNodeRequest.ReissuedAfterUnavailable"/>, so
/// the handler never records it a second time. It lands (<see cref="PendingWriteVerdict.Landed"/>),
/// finds the path already written (<see cref="PendingWriteVerdict.AlreadyPresent"/>), or is refused
/// again for any reason and is reported by name and dropped (<see cref="PendingWriteVerdict.Dropped"/>).
/// It is never retried for ever.</para>
///
/// <para><b>As the writer.</b> The request carries the refused request's <c>CreatedBy</c> and is posted
/// with its <see cref="AccessContext"/>, so the owner authorises it exactly as it would have
/// authorised the original.</para>
/// </summary>
public static class PendingWriteRecovery
{
    /// <summary>How many pending creates one recovery pass re-issues at a time.</summary>
    public const int ReissueConcurrency = 4;

    /// <summary>
    /// Arms the watcher on the mesh hub, after loading the journal. A host with no
    /// <see cref="PendingWriteLedger"/> registered arms nothing.
    /// </summary>
    /// <param name="meshHub">The mesh hub (lives for the process).</param>
    public static void Arm(IMessageHub meshHub)
    {
        var ledger = meshHub.ServiceProvider.GetService<PendingWriteLedger>();
        if (ledger is null)
            return;
        var feed = meshHub.ServiceProvider.GetService<IMeshInvalidationFeed>();
        var logger = meshHub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PendingWriteRecovery));
        var subscriptions = new CompositeDisposable
        {
            ledger.LoadJournal(meshHub.JsonSerializerOptions).Subscribe(
                loaded =>
                {
                    if (loaded > 0)
                        logger?.LogWarning(
                            "[PendingWrite] {Count} create(s) refused before this process started are pending; each is "
                            + "re-issued once when the store answers again", loaded);
                },
                ex => logger?.LogError(ex, "[PendingWrite] the journal {Directory} could not be read", ledger.JournalDirectory)),
            feed.StoreAnswersAgain(partition: null)
                .Select(_ => ledger.TakeAll())
                .Where(batch => !batch.IsEmpty)
                // Bounded: a store that just came back is not handed every refused create at once.
                .Select(batch => batch
                    .Select(write => Reissue(meshHub, write).SelectMany(ledger.Settle))
                    .MergeBounded(ReissueConcurrency))
                .Concat()
                .Subscribe(
                    _ => { },
                    ex => logger?.LogError(ex, "[PendingWrite] the recovery watcher stopped; pending creates are no "
                                               + "longer re-issued on this process")),
        };
        meshHub.RegisterForDisposal(subscriptions);
    }

    /// <summary>
    /// Re-issues one pending create and classifies the answer. Never faults: a fault is a
    /// <see cref="PendingWriteVerdict.Dropped"/> outcome naming it.
    /// </summary>
    /// <param name="meshHub">The mesh hub.</param>
    /// <param name="write">The pending create.</param>
    public static IObservable<PendingWriteOutcome> Reissue(IMessageHub meshHub, PendingWrite write)
    {
        var request = new CreateNodeRequest(write.Node)
        {
            CreatedBy = write.CreatedBy,
            ReissuedAfterUnavailable = true,
        };
        var target = meshHub.NodeOperationTarget();
        return Observable.Defer(() => meshHub.NodeOperationIssuingHub()
                .Observe(request, o =>
                {
                    o = o.WithTarget(target);
                    return write.Writer is { } writer ? o.WithAccessContext(writer) : o;
                }))
            .Take(1)
            .Select(delivery => delivery.Message switch
            {
                { Success: true } => new PendingWriteOutcome(write, PendingWriteVerdict.Landed, null),
                { RejectionReason: NodeCreationRejectionReason.NodeAlreadyExists } response =>
                    new PendingWriteOutcome(write, PendingWriteVerdict.AlreadyPresent, response.Error),
                var response => new PendingWriteOutcome(write, PendingWriteVerdict.Dropped,
                    $"{response.RejectionReason}: {response.Error}"),
            })
            .Catch((Exception ex) => Observable.Return(
                new PendingWriteOutcome(write, PendingWriteVerdict.Dropped, $"{ex.GetType().Name}: {ex.Message}")));
    }
}

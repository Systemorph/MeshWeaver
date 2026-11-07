using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>What an install-record write intends — open string constants
/// (<c>Doc/Architecture/OpenVocabulariesAsStringConstants</c>).</summary>
public static class PackageRecordWriteIntent
{
    /// <summary>Create the record when absent, re-stamp it when present — the default install of a
    /// package the instance does not hold yet. An unattended re-stamp is refused while the package is
    /// being retired.</summary>
    public const string CreateAllowed = "CreateAllowed";

    /// <summary>Re-stamp an EXISTING record only — never re-create a record that is gone (the repair,
    /// the unattended update lane). Refused while the package is being retired.</summary>
    public const string Restamp = "Restamp";

    /// <summary>A person installed it from the catalog — creates or re-stamps, and CLEARS a retirement
    /// that has not yet started removing the record.</summary>
    public const string Person = "Person";
}

/// <summary>The kinds of fact a writer states on an install record — open string constants.</summary>
public static class PackageRecordFactKind
{
    /// <summary>An uninstall asks to hold the package's retirement (<see cref="PackageManifest.RetiringBy"/>).</summary>
    public const string Begin = "Begin";

    /// <summary>The holder is about to remove the record — from here a person's install is refused too.</summary>
    public const string Remove = "Remove";

    /// <summary>The holder stopped (refused or failed) — the retirement is released.</summary>
    public const string End = "End";

    /// <summary>A writer's new record content, applied or refused by the owner.</summary>
    public const string Stamp = "Stamp";

    /// <summary>A person installed the package from the catalog — clears a retirement that is not yet
    /// removing the record, even when the install wrote nothing (an up-to-date package skips the
    /// record write entirely).</summary>
    public const string PersonClear = "PersonClear";
}

/// <summary>Who is retiring a package, set and cleared only by the install record's OWN hub.</summary>
/// <param name="Request">The uninstall request holding it.</param>
/// <param name="Reason">Why.</param>
/// <param name="Since">When it was granted.</param>
/// <param name="Removing">The holder announced it is removing the record now.</param>
public sealed record PackageRetirement(string Request, string? Reason, DateTimeOffset Since, bool Removing = false);

/// <summary>A fact a writer states, unconditionally, under its own key on an install record.</summary>
public sealed record PackageRecordFact
{
    /// <summary>A <see cref="PackageRecordFactKind"/> value.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The uninstall request a retirement fact speaks for.</summary>
    public string? Request { get; init; }

    /// <summary>Why — carried onto <see cref="PackageRetirement.Reason"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>The new record content of a <see cref="PackageRecordFactKind.Stamp"/>.</summary>
    public PackageManifest? Stamp { get; init; }

    /// <summary>The <see cref="PackageRecordWriteIntent"/> of a stamp.</summary>
    public string? Intent { get; init; }

    /// <summary>When it was stated.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>The owner's answer to one fact, under the fact's key, for its writer to read and remove.</summary>
/// <param name="Applied">Whether the owner applied it.</param>
/// <param name="Detail">What the owner did, or why it refused.</param>
/// <param name="At">When the owner decided.</param>
public sealed record PackageRecordFactOutcome(bool Applied, string Detail, DateTimeOffset At);

/// <summary>An install-record write the record's own hub refused — a refusal, never a silent success.</summary>
/// <param name="path">The install record.</param>
/// <param name="reason">The owner's words.</param>
public sealed class PackageRecordWriteRefusedException(string path, string reason)
    : InvalidOperationException($"the install record {path} refused the write: {reason}")
{
    /// <summary>The install record.</summary>
    public string Path { get; } = path;

    /// <summary>The owner's words.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// 🚨 <b>The install record decides who may write it</b> (MeshWeaver.Plugins#5055,
/// <c>Doc/Architecture/ConditionalWritesAcrossHubs</c>).
///
/// <para>Every writer that is not the record's own hub runs its <c>stream.Update</c> lambda on its
/// MIRROR, so a condition such as "is this package being retired?" evaluated there is a snapshot that
/// can be stale — and a lambda that refuses by returning the node unchanged posts nothing and reports
/// success. So a writer never decides: it states a FACT, unconditionally and under its own key
/// (<see cref="PackageManifest.RecordFacts"/>), and the record's OWN hub folds every pending fact
/// against current state (<see cref="Fold"/>, serialised there), answering each under the same key
/// (<see cref="PackageManifest.FactOutcomes"/>). The writer reads its answer and removes it.</para>
///
/// <para><b>Retirement</b> (<see cref="PackageManifest.RetiringBy"/>): an uninstall states
/// <see cref="PackageRecordFactKind.Begin"/> and proceeds only once the owner granted it; it states
/// <see cref="PackageRecordFactKind.Remove"/> before it deletes the record and
/// <see cref="PackageRecordFactKind.End"/> when it stops without deleting. While a retirement is held
/// the owner REFUSES every unattended stamp; a person's stamp clears a retirement that has not reached
/// <see cref="PackageRetirement.Removing"/> and is applied.</para>
///
/// <para><b>Re-creation</b> is closed one level down: a stamp is a <c>stream.Update</c>, and a delete
/// wins over an update that commits after it (<c>UpdateNeverResurrectsADeletedNodeTest</c>); an absent
/// record is created only by <see cref="PackageRecordWriteIntent.CreateAllowed"/> or
/// <see cref="PackageRecordWriteIntent.Person"/>, never by a re-stamp.</para>
/// </summary>
public static class InstallRecordGate
{
    /// <summary>How long a writer waits for the owner's answer before it re-reads the record: absent
    /// ⇒ the record was removed meanwhile (refused), present ⇒ a fault naming the silent owner.</summary>
    public static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(60);

    /// <summary>Answers older than this are pruned by the owner — a writer that died never removes its own.</summary>
    public static readonly TimeSpan OutcomeRetention = TimeSpan.FromHours(1);

    // ── Owner side ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Folds every pending fact on <paramref name="node"/> against its CURRENT state — run only by the
    /// record's own hub, inside its serialised <c>Update</c>. Pure. Facts are consumed whether applied
    /// or refused, each answered under its key; old answers are pruned.
    /// </summary>
    /// <param name="node">The install record as the owner holds it.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="now">The decision time.</param>
    public static MeshNode Fold(MeshNode node, JsonSerializerOptions options, DateTimeOffset now)
    {
        var record = node.ContentAs<PackageManifest>(options);
        if (record is null)
            return node;
        var facts = record.RecordFacts ?? ImmutableDictionary<string, PackageRecordFact>.Empty;
        var outcomes = (record.FactOutcomes ?? ImmutableDictionary<string, PackageRecordFactOutcome>.Empty)
            .Where(kv => now - kv.Value.At <= OutcomeRetention)
            .ToImmutableDictionary(StringComparer.Ordinal);
        // Requests that RELEASED their retirement, kept for the retention window so a Begin of the
        // same request that arrives late (its writer gave up on the answer and released) can never
        // re-grant it.
        var ended = (record.EndedRetirements ?? ImmutableDictionary<string, DateTimeOffset>.Empty)
            .Where(kv => now - kv.Value <= OutcomeRetention)
            .ToImmutableDictionary(StringComparer.Ordinal);
        if (facts.IsEmpty && outcomes.Count == (record.FactOutcomes?.Count ?? 0)
                          && ended.Count == (record.EndedRetirements?.Count ?? 0))
            return node;

        var retiring = record.RetiringBy;
        var content = record;
        foreach (var (key, fact) in facts.OrderBy(kv => kv.Value.At).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            PackageRecordFactOutcome Answer(bool applied, string detail) => new(applied, detail, now);
            switch (fact.Kind)
            {
                case PackageRecordFactKind.Begin when fact.Request is { Length: > 0 } endedRequest && ended.ContainsKey(endedRequest):
                    outcomes = outcomes.SetItem(key, Answer(false, $"refused: {endedRequest} already released this retirement — a late Begin never re-grants it"));
                    break;
                case PackageRecordFactKind.Begin when retiring is null && fact.Request is { Length: > 0 } request:
                    retiring = new PackageRetirement(request, fact.Reason, now);
                    outcomes = outcomes.SetItem(key, Answer(true, $"retirement held by {request}"));
                    break;
                case PackageRecordFactKind.Begin when retiring is not null && string.Equals(retiring.Request, fact.Request, StringComparison.Ordinal):
                    outcomes = outcomes.SetItem(key, Answer(true, $"retirement already held by {retiring.Request}"));
                    break;
                case PackageRecordFactKind.Begin:
                    outcomes = outcomes.SetItem(key, Answer(false, retiring is null
                        ? "refused: a retirement names no request"
                        : $"refused: {retiring.Request} already holds this package's retirement"));
                    break;
                case PackageRecordFactKind.Remove when retiring is not null && string.Equals(retiring.Request, fact.Request, StringComparison.Ordinal):
                    retiring = retiring with { Removing = true };
                    outcomes = outcomes.SetItem(key, Answer(true, "removing the record"));
                    break;
                case PackageRecordFactKind.Remove:
                    outcomes = outcomes.SetItem(key, Answer(false,
                        $"refused: {fact.Request} no longer holds this package's retirement (a person installed it, or it was never granted)"));
                    break;
                case PackageRecordFactKind.End:
                    var held = retiring is not null && string.Equals(retiring.Request, fact.Request, StringComparison.Ordinal);
                    if (held)
                        retiring = null;
                    if (fact.Request is { Length: > 0 } released)
                        ended = ended.SetItem(released, now);
                    outcomes = outcomes.SetItem(key, Answer(true, held ? "retirement released" : "nothing to release"));
                    break;
                case PackageRecordFactKind.PersonClear when retiring is { Removing: true }:
                    outcomes = outcomes.SetItem(key, Answer(false, $"refused: {retiring.Request} is removing this install record now"));
                    break;
                case PackageRecordFactKind.PersonClear:
                    var clearedBy = retiring?.Request;
                    retiring = null;
                    outcomes = outcomes.SetItem(key, Answer(true, clearedBy is null ? "nothing to clear" : $"cleared the retirement held by {clearedBy}"));
                    break;
                case PackageRecordFactKind.Stamp when !IsKnownIntent(fact.Intent):
                    outcomes = outcomes.SetItem(key, Answer(false, $"refused: '{fact.Intent}' is not a write intent this owner knows"));
                    break;
                case PackageRecordFactKind.Stamp when fact.Stamp is null:
                    outcomes = outcomes.SetItem(key, Answer(false, "refused: the stamp carries no content"));
                    break;
                case PackageRecordFactKind.Stamp when retiring is not null
                                                    && !(fact.Intent == PackageRecordWriteIntent.Person && !retiring.Removing):
                    outcomes = outcomes.SetItem(key, Answer(false, retiring.Removing
                        ? $"refused: {retiring.Request} is removing this install record now"
                        : $"refused: the package is being retired by {retiring.Request}"));
                    break;
                case PackageRecordFactKind.Stamp:
                    var cleared = retiring?.Request;
                    retiring = null;
                    content = fact.Stamp;
                    outcomes = outcomes.SetItem(key, Answer(true, cleared is null
                        ? "applied"
                        : $"applied — a person's install cleared the retirement held by {cleared}"));
                    break;
                default:
                    outcomes = outcomes.SetItem(key, Answer(false, $"refused: unknown fact kind '{fact.Kind}'"));
                    break;
            }
        }

        return node with
        {
            Content = content with
            {
                RetiringBy = retiring,
                EndedRetirements = ended.IsEmpty ? null : ended,
                RecordFacts = null,
                FactOutcomes = outcomes.IsEmpty ? null : outcomes,
                WriteIntent = null,
            },
        };
    }

    /// <summary>Arms the owner fold on an install record's OWN hub — on its init turn, coupled to its
    /// lifetime. A static method group, so repeat registrations collapse.</summary>
    /// <param name="hub">The install record's own hub.</param>
    public static IObservable<Unit> Arm(IMessageHub hub) => Observable.Defer(() =>
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstallRecordGate));
        var workspace = hub.GetWorkspace();
        hub.RegisterForDisposal(ActivityControlPlaneExtensions.SubscribeHubWatcher(
            hub,
            () => workspace.GetMeshNodeStream()
                .Select(node => node?.ContentAs<PackageManifest>(hub.JsonSerializerOptions)?.RecordFacts is { Count: > 0 } facts
                    ? string.Join("|", facts.Keys.Order(StringComparer.Ordinal))
                    : "")
                // DistinctUntilChanged BEFORE the filter: a key folded away and stated again must wake us.
                .DistinctUntilChanged()
                .Where(keys => keys.Length > 0),
            _ => workspace.GetMeshNodeStream()
                .Update(node => Fold(node, hub.JsonSerializerOptions, DateTimeOffset.UtcNow))
                .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "[InstallRecordGate] folding the facts on {Address} failed", hub.Address)),
            logger,
            "install-record gate"));
        return Observable.Return(Unit.Default);
    });

    // ── Writer side ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes an install record through the gate: absent ⇒ created when <paramref name="intent"/>
    /// allows it (a create that collides falls back to a stamp), refused for a re-stamp; present ⇒ a
    /// stamp the owner applies or refuses. Emits the record as written; faults with
    /// <see cref="PackageRecordWriteRefusedException"/> when refused. Runs as System. Cold.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="record">The record node to write (path, name, node type, content).</param>
    /// <param name="intent">A <see cref="PackageRecordWriteIntent"/> value.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<MeshNode> Write(IMessageHub hub, MeshNode record, string intent, ILogger? logger)
    {
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var content = record.ContentAs<PackageManifest>(hub.JsonSerializerOptions)
                      ?? throw new ArgumentException($"{record.Path} carries no install record content", nameof(record));
        var clean = content with { RetiringBy = null, RecordFacts = null, FactOutcomes = null, WriteIntent = null };
        return storage.Read(record.Path, hub.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .SelectMany(existing =>
            {
                // Every intent is resolved by name; an unclaimed value is refused, never given the
                // default's semantics (open vocabularies: unknown stays unknown).
                if (!IsKnownIntent(intent))
                    return Observable.Throw<MeshNode>(new PackageRecordWriteRefusedException(record.Path,
                        $"'{intent}' is not a write intent this gate knows — nothing was written"));
                if (existing is not null)
                    return Stamp(hub, record.Path, clean, intent, logger);
                if (intent == PackageRecordWriteIntent.Restamp)
                    return Observable.Throw<MeshNode>(new PackageRecordWriteRefusedException(record.Path,
                        "there is no install record — a re-stamp never re-creates a removed record"));
                return access.RunAsSystem(() => hub.NodeOperationIssuingHub()
                        .Observe<CreateNodeResponse>(new CreateNodeRequest(record with { Content = clean })))
                    .FirstAsync()
                    .Select(d => d.Message)
                    .SelectMany(response => response.Success
                        ? Observable.Return(response.Node!)
                        // Lost a create race: the record exists now, so it is a stamp after all.
                        : response.RejectionReason == NodeCreationRejectionReason.NodeAlreadyExists
                            ? Stamp(hub, record.Path, clean, intent, logger)
                            : Observable.Throw<MeshNode>(new InvalidOperationException(
                                $"Recording install at '{record.Path}' failed: {response.Error}")));
            });
    }

    /// <summary>Whether <paramref name="intent"/> is one of the <see cref="PackageRecordWriteIntent"/> values. Pure.</summary>
    /// <param name="intent">The intent.</param>
    public static bool IsKnownIntent(string? intent) =>
        intent is PackageRecordWriteIntent.CreateAllowed or PackageRecordWriteIntent.Restamp or PackageRecordWriteIntent.Person;

    /// <summary>
    /// A person's catalog install of <paramref name="packageId"/> CLEARS a retirement that is not yet
    /// removing the record — stated even when the install wrote nothing (an up-to-date package never
    /// reaches the record write). Absent record: nothing to clear. Faults with
    /// <see cref="PackageRecordWriteRefusedException"/> when the record is being removed right now.
    /// </summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="packageId">The package.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<Unit> ClearForPerson(IMessageHub hub, string packageId, ILogger? logger)
    {
        var path = $"{PackageInstaller.InstalledPartition}/{packageId}";
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        return storage.Read(path, hub.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .SelectMany(existing => existing is null
                ? Observable.Return(Unit.Default)
                : Submit(hub, path, $"personclear-{Guid.NewGuid():N}",
                        new PackageRecordFact { Kind = PackageRecordFactKind.PersonClear, At = DateTimeOffset.UtcNow }, logger)
                    .SelectMany(outcome => outcome.Applied
                        ? Observable.Return(Unit.Default)
                        : Observable.Throw<Unit>(new PackageRecordWriteRefusedException(path, outcome.Detail))));
    }

    private static IObservable<MeshNode> Stamp(IMessageHub hub, string path, PackageManifest content, string intent, ILogger? logger)
    {
        var key = $"stamp-{Guid.NewGuid():N}";
        return Submit(hub, path, key, new PackageRecordFact
            {
                Kind = PackageRecordFactKind.Stamp,
                Stamp = content,
                Intent = intent,
                At = DateTimeOffset.UtcNow,
            }, logger)
            .SelectMany(outcome => outcome.Applied
                ? ReadOwn(hub, path)
                : Observable.Throw<MeshNode>(new PackageRecordWriteRefusedException(path, outcome.Detail)));
    }

    /// <summary>States an uninstall's <see cref="PackageRecordFactKind.Begin"/> and answers whether
    /// the owner granted the retirement (null) or why not.</summary>
    /// <param name="hub">Any surviving hub of the mesh.</param>
    /// <param name="packageId">The package.</param>
    /// <param name="request">The uninstall request path.</param>
    /// <param name="reason">Why.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<string?> BeginRetirement(IMessageHub hub, string packageId, string request, string? reason, ILogger? logger) =>
        Retirement(hub, packageId, request, PackageRecordFactKind.Begin, reason, logger);

    /// <summary>States <see cref="PackageRecordFactKind.Remove"/> before the record is deleted; null when
    /// the owner confirmed the retirement is still held, else why not.</summary>
    public static IObservable<string?> MarkRemoving(IMessageHub hub, string packageId, string request, ILogger? logger) =>
        Retirement(hub, packageId, request, PackageRecordFactKind.Remove, null, logger);

    /// <summary>States <see cref="PackageRecordFactKind.End"/> — the uninstall stopped without deleting.</summary>
    public static IObservable<string?> EndRetirement(IMessageHub hub, string packageId, string request, ILogger? logger) =>
        Retirement(hub, packageId, request, PackageRecordFactKind.End, null, logger);

    private static IObservable<string?> Retirement(IMessageHub hub, string packageId, string request, string kind, string? reason, ILogger? logger)
    {
        var path = $"{PackageInstaller.InstalledPartition}/{packageId}";
        // A FRESH correlation key per submission (#7964 review): a persisted answer under a reused key
        // could be read as this submission's grant. The request is the stable holder identity the owner
        // is idempotent on; the key only correlates this one answer.
        var key = $"{kind.ToLowerInvariant()}-{Guid.NewGuid():N}";
        return Submit(hub, path, key, new PackageRecordFact { Kind = kind, Request = request, Reason = reason, At = DateTimeOffset.UtcNow }, logger)
            .Select(outcome => outcome.Applied ? null : outcome.Detail);
    }

    /// <summary>
    /// States one fact under its key on the record (an unconditional write: the patch always carries
    /// it), waits for the owner's answer under the same key, then removes the answer. When no answer
    /// arrives within <see cref="AnswerBudget"/> the record is re-read: absent ⇒ refused (removed
    /// meanwhile — a stated fact never re-creates it); present ⇒ a fault naming the silent owner.
    /// </summary>
    internal static IObservable<PackageRecordFactOutcome> Submit(
        IMessageHub hub, string path, string key, PackageRecordFact fact, ILogger? logger) =>
        Submit(hub, path, key, fact, logger, removeAnswer: null);

    /// <summary><see cref="Submit(IMessageHub, string, string, PackageRecordFact, ILogger?)"/> with a
    /// test seam for the answer removal — the durability barrier — so its failure can be pinned.</summary>
    internal static IObservable<PackageRecordFactOutcome> Submit(
        IMessageHub hub, string path, string key, PackageRecordFact fact, ILogger? logger,
        Func<IObservable<Unit>>? removeAnswer)
    {
        var storageForRemoval = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        // 🚨 A delete of THIS record racing the submission does not always reach the timeout: the
        // stream cache ends active readers with a delete exception, a fresh read gets a routing
        // NotFound, and the stating Update itself can fail on a deleted node (#7964 review). Each is
        // judged by the one fact that matters — is the record gone? — and becomes the typed refusal;
        // with the record still there the fault is unrelated and propagates unchanged.
        return SubmitCore(hub, path, key, fact, logger, removeAnswer)
            .Catch((Exception ex) => storageForRemoval.Read(path, hub.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
                .Catch((Exception _) => Observable.Throw<MeshNode?>(ex))
                .SelectMany(node => node is null
                    ? Observable.Return(new PackageRecordFactOutcome(false,
                        "the install record was removed before its owner answered", DateTimeOffset.UtcNow))
                    : Observable.Throw<PackageRecordFactOutcome>(ex)));
    }

    private static IObservable<PackageRecordFactOutcome> SubmitCore(
        IMessageHub hub, string path, string key, PackageRecordFact fact, ILogger? logger,
        Func<IObservable<Unit>>? removeAnswer)
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var storage = hub.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var options = hub.JsonSerializerOptions;
        // 🚨 Every write is issued from the mesh's off-router execution hub, NEVER from the record's own
        // hub even when that is the caller (#7964 review): an own-hub stream.Update acks the in-memory
        // commit, a cross-hub one acks the owner's DURABLE write — and the durability barrier below
        // depends on the second.
        var issuer = hub.NodeOperationExecutionHub() ?? hub;
        var answer = access.RunAsSystem(() => issuer.GetMeshNodeStream(path))
            .Select(node => node.ContentAs<PackageManifest>(options)?.FactOutcomes?.GetValueOrDefault(key))
            .Where(outcome => outcome is not null)
            .Select(outcome => outcome!)
            .Take(1)
            .Timeout(AnswerBudget)
            .Catch((TimeoutException _) => storage.Read(path, options).Take(1).DefaultIfEmpty(null)
                .SelectMany(node => node is null
                    ? Observable.Return(new PackageRecordFactOutcome(false,
                        "the install record was removed before its owner answered", DateTimeOffset.UtcNow))
                    : Observable.Throw<PackageRecordFactOutcome>(new TimeoutException(
                        $"the install record {path} did not answer the {fact.Kind} '{key}' within {AnswerBudget}"))));
        IObservable<Unit> RemoveAnswer() => removeAnswer?.Invoke()
            ?? access.RunAsSystem(() => issuer.GetMeshNodeStream(path)
                    .Update<PackageManifest>(current => current with
                    {
                        // Remove the key, never null the map: a null would delete every other
                        // writer's answer that arrived meanwhile.
                        FactOutcomes = current.FactOutcomes?.Remove(key),
                    }))
                .Take(1)
                .Select(_ => Unit.Default);
        return access.RunAsSystem(() => issuer.GetMeshNodeStream(path)
                .Update<PackageManifest>(current => current with
                {
                    RecordFacts = (current.RecordFacts ?? ImmutableDictionary<string, PackageRecordFact>.Empty).SetItem(key, fact),
                }))
            .Take(1)
            .SelectMany(_ => answer)
            // The answer is the writer's to remove — under its own key, unconditionally — and the
            // removal is AWAITED before the outcome is emitted: a cross-hub stream.Update acks off the
            // owner's DURABLE write of the node as it now stands, which carries the owner's fold. So
            // when a write through this gate completes, the stamped record is in storage — the
            // read-after-write the full-replace CreateOrUpdateNodeRequest used to give.
            // 🚨 For an APPLIED outcome that barrier is the contract, so a failed removal FAULTS the
            // write (#7964 review): returning "applied" without it would let a caller read storage
            // and find the previous stamp. A REFUSED outcome promised no write, so it is still
            // returned as the refusal it is; the owner prunes the stray answer.
            .SelectMany(outcome =>
            {
                logger?.LogDebug("[InstallRecordGate] {Path} answered {Kind} '{Key}': {Detail}", path, fact.Kind, key, outcome.Detail);
                return RemoveAnswer()
                    .Select(_ => outcome)
                    .Catch((Exception ex) =>
                    {
                        if (outcome.Applied)
                            return Observable.Throw<PackageRecordFactOutcome>(new InvalidOperationException(
                                $"the install record {path} applied the {fact.Kind} '{key}' but its durability barrier failed: {ex.Message}", ex));
                        logger?.LogWarning(ex, "[InstallRecordGate] removing the refused answer '{Key}' on {Path} failed", key, path);
                        return Observable.Return(outcome);
                    });
            });
    }

    private static IObservable<MeshNode> ReadOwn(IMessageHub hub, string path)
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1).Timeout(AnswerBudget));
    }
}

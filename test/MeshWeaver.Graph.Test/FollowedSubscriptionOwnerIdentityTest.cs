using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A FOLLOWED direct subscriber never becomes the identity of the owner hub's own work
/// (MeshWeaver#4306, follow-up 2; triage <c>rbuergi/Feedback/declared-read-follow-identity-leak-into-owner-hub</c>).
///
/// <para><see cref="MeshWeaver.Hosting.Security.DeclaredReadSubscriptionFollow"/> decides a direct
/// subscriber's read ON the owner per-node hub, AS that subscriber, for every revision it serves. A
/// stream the node-stream cache opened was once followed the same way, and the owner's own control
/// plane then composed its next read as the user whose write had opened that cache stream
/// (measured in MeshWeaver.Plugins' <c>OperationRequestOnControlTest</c>). The cache is no longer
/// followed; this pins the remaining question for a GENUINE direct subscriber: when another user
/// writes the node, does the owner's watcher react — and compose its next step — as the writer (or
/// as the platform), or does the follow's evaluation hand it the SUBSCRIBER's identity?</para>
///
/// <para>The owner's watcher is the shape every control-plane watcher has (<c>ModuleReloadExecutor.Arm</c>):
/// the hub's own node stream once the hub is Started, each revision a step run behind the previous
/// one (<c>Select(step).Concat()</c>). It records the ambient <see cref="AccessService.Context"/> when
/// the revision reaches it AND when its step for that revision is composed — the latter is subscribed
/// on whatever thread completed the previous step, which is where a leaked identity would surface.
/// A second watcher reads the same node back through the node-stream mirror, unsealed.</para>
///
/// <para>🚨 <b>What this guard has NOT been shown to catch.</b> Two seeded leaks were run against it
/// and neither turned it red: an <c>AccessService.SetContext(subscriber)</c> latched inside the
/// follow's verdict (the owner hub resets the ambient identity per delivery, and the watcher has
/// already reacted earlier in the same turn), and re-enabling the follow of the mirror's own stream
/// while that stream was opened by X (the Plugins shape — it did not reproduce in core). So it pins
/// the observable outcome for a direct subscriber; it is not a reproduction of the Plugins leak.</para>
/// </summary>
public class FollowedSubscriptionOwnerIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "w";
    private const string WatchedType = "Kiosk/Watched";

    /// <summary>X — the link's subject, read ONLY through the type's declared point read: the followed subscriber.</summary>
    private static readonly AccessContext Xavier = new() { ObjectId = "xavier", Name = "xavier" };

    /// <summary>Y — an Editor of the partition: the writer the owner's watcher must react as.</summary>
    private static readonly AccessContext Yvonne = new() { ObjectId = "yvonne", Name = "yvonne" };

    private static readonly PointReadDeclaration Declaration = new()
    {
        SubjectProperty = "subject",
        Namespace = Partition,
        RevokedProperty = "revokedAt",
        ExpiresProperty = "expiresAt",
        Areas = [""],
    };

    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .WithMeshType(typeof(TicketContent), nameof(TicketContent))
        .ConfigureServices(services => services.AddSingleton<OwnerWatcherLog>())
        .AddMeshNodes(new MeshNode("Watched", "Kiosk")
        {
            NodeType = "NodeType",
            Name = WatchedType,
            Content = new NodeTypeDefinition { PointRead = Declaration },
            HubConfiguration = config => config
                .WithContentType<TicketContent>()
                .AddDefaultLayoutAreas()
                .WithInitialization(ArmWatcher),
        });

    /// <summary>
    /// The owner's watcher reacts to — and composes its step for — each write as its WRITER (Y, or
    /// System) or as the platform, never as X, whose DIRECT subscription to the node the declared
    /// read follows for every one of those revisions.
    ///
    /// <para>The follow's evaluation is proven to have run, not assumed: the same delivery shape
    /// yields a <see cref="MeshWeaver.Data.Serialization.ClientReadFollow"/>; X's subscription
    /// receives the System write's revision, which a followed subscription forwards only after the
    /// verdict decided AS X on the owner hub admitted it; and Y's write — no longer platform-issued,
    /// so the declaration no longer admits X — ends X's subscription with the follow's refusal, which
    /// only that evaluation produces.</para>
    ///
    /// <para>CONTROL (no follow): a second link of the same type, the same two writes, nobody
    /// subscribed.</para>
    /// </summary>
    [HubFact]
    public async Task AFollowedDirectSubscriber_NeverBecomesTheOwnerWatchersIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await SeedTopLevel(new MeshNode(Partition) { NodeType = "Markdown", Name = Partition });
        await access.RunAsSystem(() => NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Yvonne.ObjectId, "Editor", Partition)))
            .Should().Emit(cancellationToken: ct);
        var recorded = Mesh.ServiceProvider.GetRequiredService<OwnerWatcherLog>().Observations.Replay();
        using var recording = recorded.Connect();
        using var held = new System.Reactive.Disposables.CompositeDisposable();
        var control = await IssueArmed("control-link", recorded, null, held, ct);
        // X opens the followed link first, through the node-stream mirror: the mirror's stream of it
        // — the one the owner's own mirror read shares — is then opened in X's context, the shape in
        // which a followed MIRROR stream once leaked its opener into the owner's control plane.
        var followed = await IssueArmed("followed-link", recorded, Xavier, held, ct);

        // ── CONTROL: nobody subscribed — a System write, then a write by Y ──
        var controlBySystem = Marker(2);
        var controlByY = Marker(3);
        await WriteAs(null, control, controlBySystem, ct);
        await WriteAs(Yvonne, control, controlByY, ct);
        await StepFor(recorded, control, controlBySystem, ct);
        await StepFor(recorded, control, controlByY, ct);

        // ── A GENUINE direct subscription by X, followed by the declared read ──
        var owner = Mesh.GetHostedHub(new Address(followed), HostedHubCreation.Never)
                    ?? throw new InvalidOperationException($"the per-node hub of '{followed}' is not active");
        var probe = new MessageDelivery<SubscribeRequest>(
            new Address("client", "xavier"), owner.Address, new SubscribeRequest("follow-probe", new MeshNodeReference()), Mesh.JsonSerializerOptions)
        {
            AccessContext = Xavier,
        };
        Assert.NotNull(MeshWeaver.Hosting.Security.DeclaredReadSubscriptionFollow.Follow(owner, probe));

        var client = GetClient(config => ConfigureClient(config).AddLayoutClient());
        client.ServiceProvider.GetRequiredService<AccessService>().SetHostIdentity(Xavier);
        var subscription = ((Workspace)client.GetWorkspace())
            .GetRemoteStreamUnchecked<MeshNode, MeshNodeReference>(new Address(followed), new MeshNodeReference())
            .Select(change => change.Value)
            .Where(node => node is not null)
            .Materialize()
            .Replay();
        using var open = subscription.Connect();
        await subscription.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind == NotificationKind.OnNext && n.Value?.Path == followed,
                "PRECONDITION: X's direct subscription is admitted through the declaration and serves the node", cancellationToken: ct);

        // A System write: the follow decides the revision AS X and forwards it.
        var followedBySystem = Marker(2);
        await WriteAs(null, followed, followedBySystem, ct);
        var forwarded = await subscription.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext || ExpiresAtOf(n.Value) == followedBySystem,
                "the followed subscription forwards the System revision once its verdict, decided as X, admits it", cancellationToken: ct);
        Assert.Equal(NotificationKind.OnNext, forwarded.Kind);
        await StepFor(recorded, followed, followedBySystem, ct);

        // A write by Y: the follow decides the revision AS X and refuses it (no longer platform-issued).
        var followedByY = Marker(3);
        await WriteAs(Yvonne, followed, followedByY, ct);
        var ended = await subscription.Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnNext || ExpiresAtOf(n.Value) == followedByY,
                "the follow, deciding Y's revision as X, ends the subscription", cancellationToken: ct);
        Assert.Equal(NotificationKind.OnError, ended.Kind);
        Assert.Contains("Access denied", ended.Exception!.Message);
        await StepFor(recorded, followed, followedByY, ct);

        // Every write also reached the owner's read of itself through the mirror (a positive signal
        // before the never-X assertion below covers those observations too).
        await MirrorFor(recorded, control, controlBySystem, ct);
        await MirrorFor(recorded, control, controlByY, ct);
        await MirrorFor(recorded, followed, followedBySystem, ct);
        await MirrorFor(recorded, followed, followedByY, ct);
        Output.WriteLine($"CONTROL (no follow), System write: {Describe(recorded, control, controlBySystem)}");
        Output.WriteLine($"CONTROL (no follow), Y write:      {Describe(recorded, control, controlByY)}");
        Output.WriteLine($"FOLLOWED, System write: {Describe(recorded, followed, followedBySystem)}");
        Output.WriteLine($"FOLLOWED, Y write:      {Describe(recorded, followed, followedByY)}");

        AssertRanAsWriterOrPlatform(recorded, control, controlBySystem, WellKnownUsers.System, "CONTROL: the System write");
        AssertRanAsWriterOrPlatform(recorded, control, controlByY, Yvonne.ObjectId, "CONTROL: Y's write");
        AssertRanAsWriterOrPlatform(recorded, followed, followedBySystem, WellKnownUsers.System, "the System write, X's subscription followed");
        AssertRanAsWriterOrPlatform(recorded, followed, followedByY, Yvonne.ObjectId, "Y's write, X's subscription followed");
        // Nothing the owner's watcher EVER did — any revision, any phase — ran as the subscriber.
        Assert.DoesNotContain(ObservationsSoFar(recorded, followed),
            o => string.Equals(o.ObjectId, Xavier.ObjectId, StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────────── the owner's watcher ─────────────────────────────

    /// <summary>
    /// The control-plane watcher shape: once Started, every revision of the hub's own node is a step
    /// run behind the previous one. Records the ambient identity on arrival and when the step composes.
    /// </summary>
    private static IObservable<Unit> ArmWatcher(IMessageHub hub)
    {
        var log = hub.ServiceProvider.GetRequiredService<OwnerWatcherLog>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var path = string.Join("/", hub.Address.Segments);
        string? ExpiresAt(MeshNode node) => node.ContentAs<TicketContent>(hub.JsonSerializerOptions)?.ExpiresAt;
        void Record(string kind, MeshNode node)
        {
            var ambient = access.Context;
            log.Record(new WatcherObservation(path, node.Version, ExpiresAt(node), kind, ambient?.ObjectId, ambient?.IsHub == true));
        }
        hub.RegisterForDisposal(hub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started)
            .SelectMany(_ => hub.GetWorkspace().GetMeshNodeStream())
            .Do(node => Record(WatcherObservation.Arrived, node))
            // The step is composed when Concat subscribes it — on the thread that completed the
            // previous step, whose context is what a leaked identity would ride in on.
            .Select(node => Observable.Defer(() =>
            {
                Record(WatcherObservation.Step, node);
                return Observable.Timer(TimeSpan.FromMilliseconds(5)).Select(_ => Unit.Default);
            }))
            .Concat()
            .Subscribe(_ => { }, _ => { }));
        // The same node read back THROUGH the process-wide node-stream mirror, as a control plane
        // reads what it reacts to. Deliberately UNSEALED (no RunAs): a sealed read restores its own
        // subscriber's identity around every notification and would mask whatever flow the mirror
        // delivered the revision on — which is where a subscriber's identity would ride in.
        hub.RegisterForDisposal(hub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Started)
            .Take(1)
            .Where(level => level == MessageHubRunLevel.Started)
            .SelectMany(_ => hub.GetMeshNodeStream(path))
            .Subscribe(node => Record(WatcherObservation.MirrorArrived, node), _ => { }));
        return Observable.Return(Unit.Default);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>Sets <paramref name="expiresAt"/> on the node as <paramref name="writer"/>; null writes as System.</summary>
    private async Task WriteAs(AccessContext? writer, string path, string expiresAt, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<MeshNode> Write() => Mesh.GetMeshNodeStream(path).Update(node =>
            node with { Content = node.ContentAs<TicketContent>(Mesh.JsonSerializerOptions)! with { ExpiresAt = expiresAt } });
        await (writer is null ? access.RunAsSystem(Write) : access.RunAs(writer, Write))
            .Should().Emit(cancellationToken: ct);
    }

    /// <summary>
    /// A link X reads only through the declaration, issued by System, its hub active and its watcher
    /// armed. Activated by <paramref name="activator"/>'s read through the node-stream mirror, held
    /// open in <paramref name="held"/>; null activates as System and holds nothing.
    /// </summary>
    private async Task<string> IssueArmed(
        string id, IObservable<WatcherObservation> recorded, AccessContext? activator,
        System.Reactive.Disposables.CompositeDisposable held, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var link = (await access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode(id, Partition)
        {
            NodeType = WatchedType,
            Name = id,
            Content = new TicketContent(Xavier.ObjectId, Marker(1), null),
        })).Should().Emit(cancellationToken: ct))!.Path;
        if (activator is null)
        {
            // As System: activates the per-node hub (and so arms its watcher) without asking anyone's read.
            await access.RunAsSystem(() => Mesh.GetMeshNodeStream(link)).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        }
        else
        {
            // The activator's own read through the mirror is the FIRST read of the node, so the
            // mirror's stream of it is opened in the activator's context, and stays open.
            var read = access.RunAs(activator, () => Mesh.GetMeshNodeStream(link)).Replay(1);
            held.Add(read.Connect());
            await read.Take(1).Should().Within(TestTimeouts.Convergence)
                .Emit($"PRECONDITION: {activator.ObjectId} reads the link through the declaration", ct);
        }
        await recorded.Should().Within(TestTimeouts.Convergence)
            .Match(o => o.Path == link, $"the watcher of '{link}' is armed and has seen the node", cancellationToken: ct);
        return link;
    }

    /// <summary>A distinct expiry, days ahead, that marks one write.</summary>
    private static string Marker(int days) => DateTimeOffset.UtcNow.Date.AddDays(days).ToString("O");

    /// <summary>
    /// Every observation of the revision marked <paramref name="expiresAt"/> ran as its writer
    /// <paramref name="writer"/>, as System, or as a hub credential — never as anyone else. A step is
    /// composed on the flow that completed the PREVIOUS step, so it may legitimately carry the
    /// previous revision's writer (System here); what it may never carry is a third identity.
    /// </summary>
    private static void AssertRanAsWriterOrPlatform(
        IConnectableObservable<WatcherObservation> recorded, string path, string expiresAt, string writer, string what)
    {
        var observed = ObservationsSoFar(recorded, path).Where(o => o.ExpiresAt == expiresAt).ToImmutableList();
        Assert.NotEmpty(observed);
        Assert.All(observed, o => Assert.True(
            o.IsHub
            || string.Equals(o.ObjectId, writer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(o.ObjectId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase),
            $"{what}: the watcher's '{o.Kind}' ran as '{o.ObjectId ?? "(none)"}', expected '{writer}', System or a hub credential — never the subscriber"));
    }

    private static Task<WatcherObservation> StepFor(IObservable<WatcherObservation> recorded, string path, string expiresAt, CancellationToken ct)
        => recorded.Should().Within(TestTimeouts.Convergence)
            .Match(o => o.Path == path && o.ExpiresAt == expiresAt && o.Kind == WatcherObservation.Step,
                $"the owner's watcher composes its step for the revision expiring {expiresAt}", cancellationToken: ct);

    private static Task<WatcherObservation> MirrorFor(IObservable<WatcherObservation> recorded, string path, string expiresAt, CancellationToken ct)
        => recorded.Should().Within(TestTimeouts.Convergence)
            .Match(o => o.Path == path && o.ExpiresAt == expiresAt && o.Kind == WatcherObservation.MirrorArrived,
                $"the owner's mirror read of itself sees the revision expiring {expiresAt}", cancellationToken: ct);

    private static ImmutableList<WatcherObservation> ObservationsSoFar(IConnectableObservable<WatcherObservation> recorded, string path)
    {
        // A ReplaySubject replays synchronously on Subscribe; nothing is awaited here.
        var builder = ImmutableList.CreateBuilder<WatcherObservation>();
        using (recorded.Where(o => o.Path == path).Subscribe(builder.Add)) { }
        return builder.ToImmutable();
    }

    private static string Describe(IConnectableObservable<WatcherObservation> recorded, string path, string expiresAt)
        => string.Join("; ", ObservationsSoFar(recorded, path)
            .Where(o => o.ExpiresAt == expiresAt)
            .Select(o => $"{o.Kind}@v{o.Version} as '{o.ObjectId ?? "(none)"}'{(o.IsHub ? " (hub)" : "")}"));

    private string? ExpiresAtOf(MeshNode? node) => node?.ContentAs<TicketContent>(Mesh.JsonSerializerOptions)?.ExpiresAt;
}

/// <summary>One thing the owner's watcher did, and the ambient identity it did it under.</summary>
/// <param name="Path">The node the watcher belongs to.</param>
/// <param name="Version">The revision's version.</param>
/// <param name="ExpiresAt">The revision's marker (each write sets a distinct one).</param>
/// <param name="Kind"><see cref="Arrived"/> or <see cref="Step"/>.</param>
/// <param name="ObjectId">The ambient <see cref="AccessService.Context"/>'s object id, or null.</param>
/// <param name="IsHub">Whether the ambient context is a hub credential.</param>
public sealed record WatcherObservation(string Path, long Version, string? ExpiresAt, string Kind, string? ObjectId, bool IsHub)
{
    /// <summary>The revision reached the watcher.</summary>
    public const string Arrived = "arrived";

    /// <summary>The watcher composed its step for the revision.</summary>
    public const string Step = "step";

    /// <summary>The revision reached the watcher's read of its own node through the node-stream mirror.</summary>
    public const string MirrorArrived = "mirror-arrived";
}

/// <summary>What every owner watcher of the test mesh recorded — a mesh-scoped singleton, never static.</summary>
public sealed class OwnerWatcherLog : IDisposable
{
    private readonly ReplaySubject<WatcherObservation> observations = new();

    /// <summary>Every observation so far, then live.</summary>
    public IObservable<WatcherObservation> Observations => observations;

    /// <summary>Records one observation.</summary>
    /// <param name="observation">What the watcher did, and as whom.</param>
    public void Record(WatcherObservation observation) => observations.OnNext(observation);

    /// <inheritdoc />
    public void Dispose() => observations.Dispose();
}

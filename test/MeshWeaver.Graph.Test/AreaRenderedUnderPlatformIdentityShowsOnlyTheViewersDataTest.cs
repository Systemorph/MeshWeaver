using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A layout area shows its VIEWER only what the viewer may read — even when the thread that
/// opened or re-rendered it carried the platform identity.</b>
///
/// <para>The shape this guards (MeshWeaver.Education journey run 39600717306): a course was
/// installed on demand while the person was on its page, and the page rendered on a turn whose
/// ambient <see cref="AccessContext"/> was <c>system-security</c>. #7036 fixed the EDIT that turn
/// mis-attributed. This class answers the READ side: does a <c>GetMeshNodeStream</c> /
/// <c>IMeshService.Query</c> / <c>hub.GetQuery</c> issued while the area renders run as the
/// platform and so show the person data they cannot read?</para>
///
/// <para>Every area here issues all three reads against <see cref="SecretPath"/> — stored, with no
/// grant for the viewer — and renders what each answered. Two families of cases:</para>
/// <list type="bullet">
///   <item><b>Opened</b> under an ambient identity (<c>platform</c> — the client hub opens the area
///         while the platform identity is ambient, as a Blazor render run on a platform hub's turn
///         does; <c>viewer</c> — the control). The client carries the portal hub's post step
///         (<c>PortalApplication.DefaultPortalConfig</c>), so the subscribe is the circuit user's.</item>
///   <item><b>Re-rendered</b> by an emission the platform produced: the area waits for the course
///         and only then reads — the on-demand install as the OWNER's side sees it. The install is a
///         write the platform makes under <c>system-security</c>, so the emission that releases
///         the reads is produced on the platform's turn.</item>
/// </list>
/// <para>Measured before the fix: the OPENED cases were already safe (the subscribe carries the
/// circuit user, and the render restores it), but every RE-RENDERED case showed the secret through
/// all three probes — <c>IMeshService.Query</c>, <c>hub.GetQuery</c> and the own-node
/// <c>GetMeshNodeStream</c> each delivered their emission under the WRITER's identity, so the view's
/// next read ran as <c>system-security</c>. They now deliver under their reader's identity
/// (Doc/Architecture/AccessContextPropagation → "Emissions arrive as the reader").</para>
/// <para>Two controls keep each verdict honest: a reader who DOES hold Read sees the secret through
/// every probe, and an area subscribed with no circuit stamp (so AS the platform) shows it too —
/// the probes see exactly what the render's identity may read.</para>
/// </summary>
public class AreaRenderedUnderPlatformIdentityShowsOnlyTheViewersDataTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Viewer = "leak-viewer";
    private const string Keeper = "vault-keeper";
    private const string Space = "LeakSpace";
    private const string Page = Space + "/Page";
    private const string Vault = "LeakVault";
    private const string SecretName = "TOP-SECRET-7c1e";
    private const string SecretPath = Vault + "/Secret";
    private const string CourseId = "Course";
    private const string InstalledName = "Installed Page";

    /// <summary>The area that issues the three reads as it first renders.</summary>
    private const string PeekArea = "PeekAtTheSecret";

    /// <summary>Prefix of the areas that read only once the platform has installed the course; the suffix names the trigger.</summary>
    private const string AfterInstallArea = "PeekAfterInstall-";

    private const string WaitingText = "waiting for the install";
    private const string Absent = "absent";
    private const string Denied = "denied";

    /// <summary>
    /// The <c>subject</c> trigger, fired by the test inside <c>ImpersonateAsSystem</c>. A plain
    /// subject, never a replaying one — a replay would hand the value over on the RENDER's own
    /// subscribe (under the viewer) and the case would test nothing; it is fired only once the area
    /// shows its placeholder, i.e. once the view listens.
    /// </summary>
    private readonly Subject<Unit> platformTurn = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout =>
                new[] { "query", "synced", "node", "subject" }.Aggregate(
                    layout.WithView(PeekArea, (LayoutAreaHost host, RenderingContext _) =>
                        Probe(host).Select(reading => (UiControl?)Controls.Markdown(reading))),
                    (definition, trigger) => definition.WithView(AfterInstallArea + trigger,
                        (LayoutAreaHost host, RenderingContext _) => Installed(host, trigger)
                            .Take(1)
                            // The reads are issued HERE — inside the emission the platform's
                            // install produced, on whatever identity that turn carries.
                            .SelectMany(_ => Probe(host))
                            .Select(reading => (UiControl?)Controls.Markdown(reading))
                            .StartWith((UiControl?)Controls.Markdown(WaitingText))))))
            .AddMeshNodes(
                Person(Viewer, "Leak Viewer"),
                AssignmentNodeFactory.UserRole(Viewer, "Admin", Viewer),
                Person(Keeper, "Vault Keeper"),
                AssignmentNodeFactory.UserRole(Keeper, "Admin", Keeper),
                new MeshNode(Space) { Name = "Leak Space", NodeType = "Space" },
                new MeshNode("Page", Space) { Name = "A Page" },
                // Both read the space and its page; only the keeper reads the vault.
                AssignmentNodeFactory.UserRole(Viewer, "Viewer", Space),
                AssignmentNodeFactory.UserRole(Keeper, "Viewer", Space),
                new MeshNode(Vault) { Name = "Leak Vault", NodeType = "Space" },
                AssignmentNodeFactory.UserRole(Keeper, "Viewer", Vault));

    private static MeshNode Person(string id, string name) => new(id)
    {
        NodeType = UserNodeType.NodeType,
        Name = name,
        State = MeshNodeState.Active,
        Content = new User { FullName = name, Email = $"{id}@meshweaver.io" },
    };

    /// <summary>
    /// The emission that tells the page the course is installed, through each kind of source a view
    /// waits on: a mesh query, a synced query, the live node stream — each of which delivers under
    /// its reader's identity — and a plain subject, which the view itself carries.
    /// </summary>
    private IObservable<Unit> Installed(LayoutAreaHost host, string trigger) => trigger switch
    {
        "query" => host.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{Space} scope:children"))
            .Where(change => change.Items.Any(n => n.Id == CourseId))
            .Select(_ => Unit.Default),
        "synced" => host.Hub.GetQuery($"install-watch-{host.Hub.Address}", $"namespace:{Space} scope:children")
            .Where(nodes => nodes.Any(n => n.Id == CourseId))
            .Select(_ => Unit.Default),
        "node" => host.Workspace.GetMeshNodeStream(Page)
            .Where(node => node?.Name == InstalledName)
            .Select(_ => Unit.Default),
        // A source the framework does not own carries no reader: the VIEW restores its viewer, the
        // one obligation left to a view author (LayoutAreaHost.ViewerContext).
        "subject" => platformTurn.CarryAccessContext(host.Hub.ServiceProvider, host.ViewerContext),
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
    };

    /// <summary>
    /// The three reads a view issues, each reduced to what it would SHOW: the secret's name, or
    /// <see cref="Absent"/> / <see cref="Denied"/>. Called inside the render, so each read resolves
    /// its identity wherever the render runs.
    /// </summary>
    private static IObservable<string> Probe(LayoutAreaHost host)
    {
        var point = host.Workspace.GetMeshNodeStream(SecretPath)
            .Select(node => node?.Name ?? Absent)
            .Take(1)
            .Catch((UnauthorizedAccessException _) => Observable.Return(Denied));
        var query = host.Hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{SecretPath}"))
            .Take(1)
            .Select(change => Names(change.Items));
        var synced = host.Hub.GetQuery($"leak-probe-{host.Hub.Address}", $"path:{SecretPath}")
            .Take(1)
            .Select(Names);
        return point.Zip(query, synced, (p, q, s) => $"point={p};query={q};synced={s}");
    }

    private static string Names(IEnumerable<MeshNode> nodes)
    {
        var names = nodes.Select(n => n.Name).ToArray();
        return names.Length == 0 ? Absent : string.Join(",", names);
    }

    private static readonly string NothingShown = $"point={Denied};query={Absent};synced={Absent}";
    private static readonly string SecretShown = $"point={SecretName};query={SecretName};synced={SecretName}";

    /// <summary>
    /// Stores the secret the way content arrives at runtime: written by the platform into the store.
    /// Deliberately NOT an <c>AddMeshNodes</c> seed — seeds are served by the static-node query
    /// provider, which applies no per-user filter, so a seeded secret reads through
    /// <c>IMeshService.Query</c> for every identity, the control included. That is a separate
    /// question from this one.
    /// </summary>
    private async Task StoreTheSecret(CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (access.ImpersonateAsSystem())
            await mesh.CreateNode(new MeshNode("Secret", Vault) { Name = SecretName, NodeType = "Markdown" })
                .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>The portal hub's post step: a post carrying no identity is stamped with the circuit user.</summary>
    private static MessageHubConfiguration AsCircuitOf(MessageHubConfiguration configuration, AccessContext circuitUser)
        => configuration
            .AddLayoutClient()
            .AddPostPipeline(pipeline => pipeline.AddPipeline((delivery, next) =>
                next(delivery.AccessContext is null ? delivery.SetAccessContext(circuitUser) : delivery)));

    private static AccessContext Login(string id) => new() { ObjectId = id, Name = id, Email = $"{id}@meshweaver.io" };

    /// <summary>
    /// Opens <paramref name="area"/> on the page for <paramref name="person"/> with the given ambient
    /// identity on the opening thread, runs <paramref name="install"/> once the view is listening,
    /// and returns the reading the area settled on.
    /// </summary>
    private async Task<string> ReadingOf(string person, string area, string openedUnder, CancellationToken ct,
        Action? install = null, bool withPortalPostStep = true)
    {
        await StoreTheSecret(ct);
        var user = Login(person);
        // The process's standing identity is the KEEPER, who may read the secret: a read that fell back
        // to it instead of running as the viewer would show the secret, so a fallback cannot pass.
        TestUsers.DevLogin(Mesh, Login(Keeper));
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var client = withPortalPostStep
            ? GetClient(c => AsCircuitOf(base.ConfigureClient(c), user))
            : GetClient(c => base.ConfigureClient(c).AddLayoutClient());
        var reference = new LayoutAreaReference(area);

        ISynchronizationStream<JsonElement> stream;
        if (openedUnder == "platform")
        {
            // A render run on the caller's execution context, where the caller is a platform hub turn.
            using (access.ImpersonateAsSystem())
                stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(Page), reference);
        }
        else
        {
            using (access.SwitchAccessContext(user))
                stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(Page), reference);
        }

        var texts = stream.GetControlStream(area)
            .OfType<MarkdownControl>()
            .Select(control => control.Markdown?.ToString() ?? "")
            .Replay();
        using var connection = texts.Connect();

        if (install is not null)
        {
            // Only once the view LISTENS for the install — its placeholder is on screen — does the
            // platform install; otherwise the trigger could land before anyone waits for it.
            await texts.FirstAsync(text => text == WaitingText).Timeout(TestTimeouts.Convergence).Await(ct);
            install();
        }

        return await texts.FirstAsync(text => text.StartsWith("point=", StringComparison.Ordinal))
            .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// The person never sees the secret, whichever identity was ambient on the thread that opened
    /// the area: every read the render issued answered for the person.
    /// </summary>
    /// <param name="openedUnder"><c>platform</c> — the defect's shape; <c>viewer</c> — the control.</param>
    [Theory(Timeout = 60000)]
    [InlineData("platform")]
    [InlineData("viewer")]
    public async Task AnAreaOpenedUnderAnyAmbientIdentity_ShowsTheViewerNothingTheyCannotRead(string openedUnder)
    {
        var reading = await ReadingOf(Viewer, PeekArea, openedUnder, TestContext.Current.CancellationToken);
        Output.WriteLine($"[opened under {openedUnder}] {reading}");

        reading.Should().Be(NothingShown,
            "the area renders for the person who subscribed it, who holds no grant on " + SecretPath
            + " — a read run as the platform identity would show its name");
    }

    /// <summary>
    /// The on-demand install as the owner's side sees it: the area issues its reads only inside the
    /// emission the platform's install produced, under <c>system-security</c>. Whatever source the
    /// view waited on, the reads it releases are the VIEWER's.
    /// </summary>
    /// <param name="trigger">The source the view waits on: <c>query</c> (<c>IMeshService.Query</c>),
    /// <c>synced</c> (<c>hub.GetQuery</c>), <c>node</c> (<c>GetMeshNodeStream</c>, the page's own
    /// node), <c>subject</c> (a plain subject the platform fires on its own turn, carried by the view
    /// with <c>host.ViewerContext</c>). Before the fix the first three showed the secret.</param>
    [Theory(Timeout = 60000)]
    [InlineData("query")]
    [InlineData("synced")]
    [InlineData("node")]
    [InlineData("subject")]
    public async Task ReadsReleasedByAPlatformInstall_RunAsTheViewer(string trigger)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var reading = await ReadingOf(Viewer, AfterInstallArea + trigger, "viewer", TestContext.Current.CancellationToken,
            install: () =>
            {
                using (access.ImpersonateAsSystem())
                {
                    if (trigger == "subject")
                        platformTurn.OnNext(Unit.Default);
                    else if (trigger == "node")
                        Mesh.GetWorkspace().GetMeshNodeStream(Page).Update(node => node with { Name = InstalledName })
                            .Subscribe(_ => { }, ex => Output.WriteLine($"install failed: {ex}"));
                    else
                        mesh.CreateNode(new MeshNode(CourseId, Space) { Name = "The Course", NodeType = "Markdown" })
                            .Subscribe(_ => { }, ex => Output.WriteLine($"install failed: {ex}"));
                }
            });
        Output.WriteLine($"[install via {trigger}] {reading}");

        reading.Should().Be(NothingShown,
            "the install ran as the platform, but the reads it released belong to the area's viewer, "
            + "who holds no grant on " + SecretPath);
    }

    /// <summary>
    /// Instrument check: with the portal hub's post step REMOVED, an area opened under the platform
    /// identity is subscribed AS the platform, and the probes then show the secret. So a probe that
    /// answers "nothing" is the gate's verdict for the render's identity, never a blind probe.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task WithoutTheCircuitStamp_AnAreaOpenedUnderThePlatformIdentity_RendersAsThePlatform()
    {
        var reading = await ReadingOf(Viewer, PeekArea, "platform", TestContext.Current.CancellationToken,
            withPortalPostStep: false);
        Output.WriteLine($"[no circuit stamp] {reading}");

        reading.Should().Be(SecretShown,
            "without the circuit stamp the subscribe carries the ambient platform identity, and the area renders for it");
    }

    /// <summary>
    /// Positive control: a reader who DOES hold Read on the secret sees its name through every probe.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AReaderWithAGrant_SeesTheSecretThroughEveryProbe()
    {
        var reading = await ReadingOf(Keeper, PeekArea, "viewer", TestContext.Current.CancellationToken);
        Output.WriteLine($"[keeper] {reading}");

        reading.Should().Be(SecretShown, "the keeper holds Viewer on " + Vault);
    }
}

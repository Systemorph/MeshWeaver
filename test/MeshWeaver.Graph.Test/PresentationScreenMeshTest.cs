using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Presentation mode (issue #1803) against a REAL mesh: the marking is one user's, it takes effect
/// the moment it is written, and the node it names stays exactly as reachable, as searchable and as
/// permitted as it was.
///
/// <para>The pure tests pin what the screen MEANS. This one pins the two claims that only a live
/// mesh can answer — that the preference actually rides the viewer's own profile (so one viewer's
/// screen can never become another's), and that marking a node changes NOTHING about reading it. The
/// second is the one that matters most: the moment this feature can gate a read it is a second
/// access-control system that can disagree with the real one.</para>
/// </summary>
public class PresentationScreenMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "Acme";
    private const string Child = "Acme/Q3Renewal";
    private const string Other = "Northwind";
    private const string Alice = "alice";
    private const string Bob = "bob";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Space) { Name = "Acme", NodeType = "Space" },
                new MeshNode("Q3Renewal") { Namespace = Space, Name = "Q3 Renewal", NodeType = "Markdown" },
                new MeshNode(Other) { Name = "Northwind", NodeType = "Space" });

    private IMessageHub Hub => Mesh.ServiceProvider.GetRequiredService<IMessageHub>();

    private async Task CreateUser(string id, User content)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (access.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(id)
            {
                NodeType = "User",
                Name = id,
                Content = content,
            }).Should().Emit();
    }

    private IObservable<PresentationScreen> ScreenOf(string viewer)
        => PresentationScreenExtensions.ScreenOf(Hub, viewer);

    [Fact(Timeout = 60000)]
    public async Task AMarkedSpaceIsHiddenForTheMarkerOnly()
    {
        await CreateUser(Alice, new User
        {
            Email = "alice@acme.com",
            PresentationMode = true,
            HiddenPaths = [Space],
        });
        // Bob is presenting too, with his own marks. Acme is not one of them.
        await CreateUser(Bob, new User
        {
            Email = "bob@acme.com",
            PresentationMode = true,
            HiddenPaths = [Other],
        });

        var aliceScreen = await ScreenOf(Alice)
            .Where(s => s.Active && s.MarkedPaths.Contains(Space))
            .FirstAsync().Timeout(30.Seconds());
        var bobScreen = await ScreenOf(Bob)
            .Where(s => s.Active && s.MarkedPaths.Contains(Other))
            .FirstAsync().Timeout(30.Seconds());

        Output.WriteLine($"alice: active={aliceScreen.Active} marks=[{string.Join(",", aliceScreen.MarkedPaths)}]");
        Output.WriteLine($"bob:   active={bobScreen.Active} marks=[{string.Join(",", bobScreen.MarkedPaths)}]");

        // The positive: the marked space and everything under it leave alice's tile surfaces.
        aliceScreen.Hides(Space).Should().BeTrue();
        aliceScreen.Hides(Child).Should().BeTrue();
        aliceScreen.Hides(Other).Should().BeFalse();

        // 🚨 The negative, which is the point: bob's portal is untouched. A marking is a preference
        // on the marker's own profile and there is no path by which it can reach another viewer.
        bobScreen.Hides(Space).Should().BeFalse();
        bobScreen.Hides(Child).Should().BeFalse();
    }

    [Fact(Timeout = 60000)]
    public async Task MarkingGrantsNothingAndDeniesNothing()
    {
        await CreateUser(Alice, new User { Email = "alice@acme.com" });

        var before = await Hub.GetEffectivePermissions(Space, Alice).FirstAsync().Timeout(30.Seconds());

        // Mark it, with the mode ON — the strongest form of the setting.
        await Hub.GetMeshNodeStream(Alice)
            .Update(node => node with
            {
                Content = new User
                {
                    Email = "alice@acme.com",
                    PresentationMode = true,
                    HiddenPaths = [Space],
                }
            })
            .Should().Emit();

        var screen = await ScreenOf(Alice)
            .Where(s => s.Active && s.MarkedPaths.Contains(Space))
            .FirstAsync().Timeout(30.Seconds());
        screen.Hides(Space).Should().BeTrue("the screen is up — this is the state under test");

        // 1. PERMITTED — unchanged, to the bit. The screen is not in the permission fold at all.
        var after = await Hub.GetEffectivePermissions(Space, Alice).FirstAsync().Timeout(30.Seconds());
        Output.WriteLine($"effective permissions on {Space}: before={before} after={after}");
        after.Should().Be(before, "a display preference must not move a single permission bit");

        // 2. REACHABLE — direct navigation still resolves the node and its child.
        var direct = await Hub.GetMeshNodeStream(Space)
            .Where(n => n is not null).FirstAsync().Timeout(30.Seconds());
        direct.Path.Should().Be(Space);
        var child = await Hub.GetMeshNodeStream(Child)
            .Where(n => n is not null).FirstAsync().Timeout(30.Seconds());
        child.Path.Should().Be(Child);

        // 3. SEARCHABLE — the mesh query engine still returns it for this very viewer. The screen
        //    filters what a surface PAINTS; it never narrows a query and never touches the index.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var found = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{Space}").ForViewer(Alice))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync().Timeout(30.Seconds());
        found.Items.Should().Contain(n => n.Path == Space,
            "hiding a tile must not remove the node from the viewer's own search");
    }

    [Fact(Timeout = 60000)]
    public async Task TheToggleTakesEffectWithoutAReload()
    {
        // The first acceptance criterion: flipping the mode re-renders every bound surface. The
        // preference is therefore read LIVE off the profile, not snapshotted onto the AccessContext
        // when the circuit opened — a snapshot would still say "off" at exactly the moment someone
        // starts sharing their screen.
        await CreateUser(Alice, new User { Email = "alice@acme.com", HiddenPaths = [Space] });

        var marked = await ScreenOf(Alice)
            .Where(s => s.MarkedPaths.Contains(Space))
            .FirstAsync().Timeout(30.Seconds());
        marked.Active.Should().BeFalse();
        marked.Hides(Space).Should().BeFalse("a mark on its own hides nothing");

        var options = Hub.JsonSerializerOptions;
        await Hub.GetMeshNodeStream(Alice)
            .Update(node => node with
            {
                Content = PresentationPreference.SetMode(node.ContentAs<User>(options), true)
            })
            .Should().Emit();

        // The SAME subscription source now reports the screen up — no reload, no new circuit.
        var live = await ScreenOf(Alice)
            .Where(s => s.Active)
            .FirstAsync().Timeout(30.Seconds());
        live.Hides(Space).Should().BeTrue();
        live.Hides(Child).Should().BeTrue();

        // …and turning it off is the complete undo, with the marks still in place.
        await Hub.GetMeshNodeStream(Alice)
            .Update(node => node with
            {
                Content = PresentationPreference.SetMode(node.ContentAs<User>(options), false)
            })
            .Should().Emit();

        var off = await ScreenOf(Alice)
            .Where(s => !s.Active)
            .FirstAsync().Timeout(30.Seconds());
        off.Hides(Space).Should().BeFalse();
        off.MarkedPaths.Should().Contain(Space, "the mark survives, so the next presentation needs no setup");
    }

    [Fact(Timeout = 60000)]
    public async Task AnAnonymousViewerHasNoScreen_AndCostsNoProfileRead()
    {
        var anonymous = await PresentationScreenExtensions
            .ScreenOf(Hub, WellKnownUsers.Anonymous).FirstAsync().Timeout(10.Seconds());
        anonymous.Should().BeSameAs(PresentationScreen.Off);

        var none = await PresentationScreenExtensions
            .ScreenOf(Hub, null).FirstAsync().Timeout(10.Seconds());
        none.Should().BeSameAs(PresentationScreen.Off);
    }
}

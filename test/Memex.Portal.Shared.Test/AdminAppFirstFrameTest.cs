using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The Admin app as a reader that takes ONE frame sees it — an MCP <c>get @Admin/area/Settings</c>
/// (<c>MeshOperations.RenderArea</c> takes the first materialised frame and closes), a first paint. Measured on a live
/// instance: that frame listed only the compiled Admin-app tabs — the six seeded ones (Invitations,
/// Privacy, Published to the web, Updates, Control lane, Inbox) were missing from the nav while the
/// same seeds passed their gates in <c>$Menu:NodeSettings</c> — and the Overview's installed-plugin
/// section said "No plugins are installed on this instance" on an instance running dozens.
///
/// <para>Both were a first frame painted from a SEED rather than an answer. The seeded tabs' lane
/// opened its admin verdict with a synthetic <c>false</c>, so the page painted before the verdict
/// the rest of the page already waited for; the plugin section opened its install-registry query
/// with an empty list and rendered that as "none installed". A person watching the page saw the
/// tabs and the grid arrive a moment later; anything that reads one frame — the MCP read, a
/// screenshot, a server-side first paint — saw the seed as the answer.</para>
///
/// <para>The platform admin's grant is created at RUNTIME, as it is on an instance (Admin/_Access
/// rows written by the setup claim or another admin) — a static grant would answer the verdict on
/// the evaluator's synchronous fast path and hide the ordering this pins.</para>
/// </summary>
public class AdminAppFirstFrameTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PlatformAdmin = "adminframe-admin";
    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            .AddPlatformSettingsTabContributions()
            .AddMeshNodes(new MeshNode(PlatformAdmin)
            {
                NodeType = UserNodeType.NodeType,
                Name = PlatformAdmin,
                State = MeshNodeState.Active,
                Content = new User { FullName = PlatformAdmin, Email = $"{PlatformAdmin}@meshweaver.io" },
            })
            .ConfigureDefaultNodeHub(config => config
                .AddPlatformSettingsTabAreas()
                .AddAdminAppOverviewTab())
            // Both instances the defect was measured on close themselves to logged-out callers:
            // there, a read that lost its viewer is ANONYMOUS and holds nothing — not even the
            // world-readable install records.
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AnonymousAccess.DenyAnonymousConfigKey] = "true",
                }).Build()));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>Grants the platform admin at runtime and installs one package, as System.</summary>
    private async Task Arrange()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await Access.RunAsSystem(() => mesh.CreateNode(
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminAppNodeType.Path)))
            .FirstAsync().Timeout(Budget).Await(ct);
        var manifest = new PackageManifest
        {
            Id = "FirstFramePlugin", Name = "First frame plugin", Version = "1.0.0",
            Kind = PackageKind.Content, TargetPartition = "FirstFramePlugin", SourceFolder = "FirstFramePlugin",
        };
        await PackageInstaller.Install(Mesh, manifest,
                [new PackageFile("FirstFramePlugin/Doc.md", "# First frame plugin")], "HEAD")
            .FirstAsync().Timeout(Budget).Await(ct);
    }

    /// <summary>
    /// Every frame of the Admin app's settings page as the platform admin sees it, the identity
    /// carried the way a multi-user server carries it: on the REQUEST only. The test host's
    /// process-wide identity (<c>TestUsers.DevLogin</c>) is cleared first, because a portal never has
    /// one — there, every render continuation that runs off the viewer's delivery finds no identity
    /// at all, and a test host that falls back to one hides exactly that.
    /// </summary>
    private IObservable<JsonElement> Frames()
    {
        var viewer = new AccessContext
            { ObjectId = PlatformAdmin, Name = PlatformAdmin, Email = $"{PlatformAdmin}@meshweaver.io" };
        var client = GetClient();
        var access = client.ServiceProvider.GetRequiredService<AccessService>();
        Access.ClearHostIdentity();
        access.ClearHostIdentity();
        // The subscribe request is stamped with the identity ambient at stream creation.
        using (access.SwitchAccessContext(viewer))
            return client.GetWorkspace()
                .GetRemoteStream<JsonElement, LayoutAreaReference>(
                    new Address(AdminAppNodeType.Path), new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea))
                .Select(change => change.Value);
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static bool IsType(JsonElement control, string type)
        => control.TryGetProperty("$type", out var t) && t.GetString() == type;

    /// <summary>Every NavLink title in the frame, each with the title of the NavGroup it sits in.</summary>
    private static (string Tab, string? Section)[] NavTabs(JsonElement frame)
    {
        if (!frame.TryGetProperty("areas", out var areas))
            return [];
        var groups = areas.EnumerateObject()
            .Where(a => IsType(a.Value, "NavGroupControl"))
            .Select(a => (Id: JsonSerializer.Deserialize<string>(a.Name)!, Title: a.Value.GetProperty("title").GetString()))
            .ToArray();
        return areas.EnumerateObject()
            .Where(a => IsType(a.Value, "NavLinkControl"))
            .Select(a =>
            {
                var id = JsonSerializer.Deserialize<string>(a.Name)!;
                var section = groups.FirstOrDefault(g => id.StartsWith(g.Id + "/", StringComparison.Ordinal)).Title;
                return (a.Value.GetProperty("title").GetString()!, section);
            })
            .ToArray();
    }

    /// <summary>
    /// The seeded instance tabs are in the Admin app's nav the FIRST time the nav renders, each in
    /// the section its seed names: Invitations, Privacy and Published to the web under People &amp;
    /// sign-in; Updates, Control lane and Inbox under Operations. "First time the nav renders" is
    /// the first frame carrying the compiled Administrators tab — a reader that takes that frame
    /// (an MCP read, a first paint) must not find the seeded tabs missing.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task TheFirstRenderedNav_CarriesTheSeededInstanceTabs_InTheirSections()
    {
        await Arrange();
        var tabs = await Frames()
            .Select(NavTabs)
            .Where(t => t.Any(x => x.Tab == "Administrators"))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Output.WriteLine(string.Join("\n", tabs.Select(t => $"{t.Section} / {t.Tab}")));

        tabs.Should().Contain(("Invitations", AdminAppNodeType.PeopleGroup));
        tabs.Should().Contain(("Privacy", AdminAppNodeType.PeopleGroup));
        tabs.Should().Contain(("Published to the web", AdminAppNodeType.PeopleGroup));
        tabs.Should().Contain(("Updates", AdminAppNodeType.OperationsGroup));
        tabs.Should().Contain(("Control lane", AdminAppNodeType.OperationsGroup));
        tabs.Should().Contain(("Inbox", AdminAppNodeType.OperationsGroup));
    }

    /// <summary>
    /// The Overview's installed-plugin section, the first time it says anything, lists what IS
    /// installed — never the "No plugins are installed" line painted before the registry answered.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task TheInstalledPluginSection_FirstSaysWhatIsInstalled()
    {
        await Arrange();
        var names = await Frames()
            .Select(frame => (Text: frame.GetRawText(), Frame: frame))
            .Where(f => f.Text.Contains("No plugins are installed on this instance", StringComparison.Ordinal)
                        || f.Text.Contains("aboutPlugins", StringComparison.Ordinal))
            .Select(f => f.Frame.GetProperty("data").EnumerateObject()
                .Where(d => JsonSerializer.Deserialize<string>(d.Name) == "aboutPlugins")
                .SelectMany(d => d.Value.EnumerateArray())
                .Select(row => row.GetProperty("name").GetString())
                .ToArray())
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

        names.Should().Contain("First frame plugin",
            "a package is installed — the section's first word is the registry's answer, never an empty seed");
    }
}

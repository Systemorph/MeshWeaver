using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A module bundle is picked from the running platform's OWN line (policy
/// <c>major-lines-run-separately</c>, MeshWeaver.Plugins#5550).
///
/// <para><b>The incident.</b> A line-3 registry shelved a line-4 AI build (4.0.1, floor 4.0.13296)
/// as the head of AI, next to the line-3 build. Its index lists the module newest-first, and the
/// client took the first entry: a line-3 control (3.1.13396) was offered 4.0.1, refused it at
/// landing, and was never offered the line-3 AI — while its image needed a newer one to get ready.
/// Module versions say nothing about the line (line 3 ships AI 1.52.0, line 4 ships 4.0.1), so the
/// line is read off what the entry states about its PLATFORM: its framework identity (a
/// compatibility key) and its declared floor.</para>
///
/// <para><b>Pinned:</b> a line-3 process plans 1.52.0 from <c>[4.0.1, 1.52.0]</c>; a line-4 process
/// plans 4.0.1 from the same index; a line-3 process offered only 4.0.1 plans nothing, downloads
/// nothing and names the line as the reason; and — the negative control — the unsplit pick, which
/// is what the client did before, takes 4.0.1 on line 3.</para>
/// </summary>
public abstract class AModuleBundleIsPickedFromItsOwnPlatformLineTest(ITestOutputHelper output, string running)
    : MonolithMeshTestBase(output)
{
    internal const string Plugin = "AI";
    internal const string Module = "MeshWeaver.AI";
    internal const string Line4Version = "4.0.1";
    internal const string Line3Version = "1.52.0";

    private readonly string moduleRoot =
        Path.Combine(Path.GetTempPath(), "mw-line-" + Guid.NewGuid().ToString("N")[..8]);

    internal FakeRegistry Registry { get; } = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton(new RunningPlatformVersionOverride(running))
                .AddSingleton(_ => new ModuleLandingService(baseDirectory: moduleRoot))
                .AddSingleton<IHttpClientFactory>(new FakeFactory(Registry)));

    /// <summary>Plans the AI module's adoption the way every landing does — no bytes travel for a
    /// plan, so the test reads exactly the pick.</summary>
    internal Task<PluginBundleClient.ModuleAdoptionPlan> Plan(CancellationToken ct) =>
        new PluginBundleClient(Mesh, FakeRegistry.Url, "mwi_test")
            .PlanModuleAdoption(Plugin, Module, unattended: false, policyDecline: null,
                Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>())
            .Should().Within(TestTimeouts.Convergence).Emit("the plan answers once", ct);

    /// <summary>The index as the line-3 registry served it in the incident: the line-4 head first,
    /// stating its floor and its compatibility key, then the line-3 generation.</summary>
    internal static string BothLines =>
        Index(Entry(Line4Version, "4.0.13296", "c004e001"), Entry(Line3Version, "3.1.12509", "c003e001"));

    internal static string OnlyLine4 => Index(Entry(Line4Version, "4.0.13296", "c004e001"));

    private static string Entry(string version, string floor, string identity) =>
        "{\"plugin\":\"" + Plugin + "\",\"version\":\"" + version + "\",\"url\":\"" + FakeRegistry.Url
        + PluginBundleClient.RoutePrefix + "/" + Plugin + "/" + version + "\",\"module\":\"" + Module
        + "\",\"minMeshVersion\":\"" + floor + "\",\"frameworkMvid\":\"" + identity + "\"}";

    private static string Index(params string[] entries) =>
        "{\"frameworkMvid\":\"c003e001\",\"bundles\":[" + string.Join(",", entries) + "]}";

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(moduleRoot))
                Directory.Delete(moduleRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory no later run reads.
        }
    }

    /// <summary>The bundle routes: the index as set, and every download counted (and refused —
    /// a plan never downloads).</summary>
    internal sealed class FakeRegistry : HttpMessageHandler
    {
        public const string Url = "https://registry.test";
        private int downloads;

        public string IndexJson { get; set; } = "{\"bundles\":[]}";

        public int Downloads => Volatile.Read(ref downloads);

        private string? indexQuery;

        /// <summary>The query string of the last index request — the line the client stated.</summary>
        public string? IndexQuery => Volatile.Read(ref indexQuery);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri && uri.AbsolutePath.EndsWith("/index.json", StringComparison.Ordinal))
            {
                Volatile.Write(ref indexQuery, uri.Query);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(IndexJson, Encoding.UTF8,
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
                });
            }
            Interlocked.Increment(ref downloads);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>The line-3 process of the incident (3.1.13396).</summary>
public class AModuleBundleIsPickedFromItsOwnPlatformLine_OnLine3Test(ITestOutputHelper output)
    : AModuleBundleIsPickedFromItsOwnPlatformLineTest(output, "3.1.13396")
{
    [Fact(Timeout = 300_000)]
    public async Task ALine3ProcessPlansTheLine3Build_NotTheNewerLine4Head()
    {
        var ct = TestContext.Current.CancellationToken;
        Registry.IndexJson = BothLines;

        var plan = await Plan(ct);

        plan.Decided.ServedVersion.Should().Be(Line3Version,
            "the line-4 head (floor 4.0.13296, identity c004e001) is another line's build; the newest "
            + "build of THIS line is 1.52.0");
        plan.Lands.Should().BeTrue("AI was never landed here, so the line-3 build lands");
        plan.Bundle.Should().NotBeNull("a landing plan carries the bundle it lands");
        plan.Bundle?.Version.Should().Be(Line3Version, "the bundle that lands is the one that was planned");
        Registry.IndexQuery.Should().Contain("line=3",
            "the client states its line, so a registry with one shelf per line answers with this line's builds only");
    }

    [Fact(Timeout = 300_000)]
    public async Task OnlyAnotherLinesBuild_LandsNothing_AndNamesTheLine()
    {
        var ct = TestContext.Current.CancellationToken;
        Registry.IndexJson = OnlyLine4;

        var plan = await Plan(ct);

        plan.Lands.Should().BeFalse("a build of another line is never landed");
        plan.Decided.ServedVersion.Should().BeNull("nothing of this line is served");
        var verdict = plan.Decided.Verdict;
        verdict.Should().NotBeNull("a plan that lands nothing says why");
        verdict?.Action.Should().Be(ModuleUpdateAction.SkipNoBundle);
        verdict?.Reason.Should().Contain("another platform line")
            .And.Contain("AI 4.0.1 (line 4")
            .And.Contain("this process runs line 3",
                "the landing log must name the line as the reason, not 'lists no bundle'");
        Registry.Downloads.Should().Be(0, "nothing is downloaded only to be refused at landing");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the pick as the client made it before the split (the index's first module
    /// entry, unsplit), over the same index: it is the line-4 head, which is the incident. Asserted
    /// through the one helper with no running line, so the control shares the parse with the fix.
    /// </summary>
    [Fact]
    public void NegativeControl_TheUnsplitPickTakesTheLine4Head()
    {
        var index = System.Text.Json.JsonSerializer.Deserialize<PluginBundleClient.BundleIndex>(BothLines,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("the test index must deserialize");

        var unsplit = BundleLine.Split(index, runningLine: null).Kept
            .First(b => b.Plugin == Plugin && b.Module != null);
        var split = BundleLine.Split(index, runningLine: 3).Kept
            .First(b => b.Plugin == Plugin && b.Module != null);

        unsplit.Version.Should().Be(Line4Version, "without the line, the newest-first index hands line 3 the line-4 head");
        split.Version.Should().Be(Line3Version);
    }
}

/// <summary>A line-4 process (4.0.13398) reading the same index.</summary>
public class AModuleBundleIsPickedFromItsOwnPlatformLine_OnLine4Test(ITestOutputHelper output)
    : AModuleBundleIsPickedFromItsOwnPlatformLineTest(output, "4.0.13398")
{
    [Fact(Timeout = 300_000)]
    public async Task ALine4ProcessPlansTheLine4Build()
    {
        var ct = TestContext.Current.CancellationToken;
        Registry.IndexJson = BothLines;

        var plan = await Plan(ct);

        plan.Decided.ServedVersion.Should().Be(Line4Version);
        plan.Lands.Should().BeTrue();
        Registry.IndexQuery.Should().Contain("line=4", "the client states its line");
    }

    [Fact(Timeout = 300_000)]
    public async Task ALine4ProcessNeverTakesTheLine3Build()
    {
        var ct = TestContext.Current.CancellationToken;
        Registry.IndexJson = "{\"frameworkMvid\":\"c003e001\",\"bundles\":[{\"plugin\":\"AI\",\"version\":\"1.52.0\","
                             + "\"url\":\"https://registry.test/x\",\"module\":\"MeshWeaver.AI\",\"minMeshVersion\":\"3.1.12509\"}]}";

        var plan = await Plan(ct);

        plan.Lands.Should().BeFalse("a line-3 build is never landed on line 4, newer or not");
        plan.Decided.Verdict.Should().NotBeNull("a plan that lands nothing says why");
        plan.Decided.Verdict?.Reason.Should().Contain("this process runs line 4");
    }
}

/// <summary>
/// 🚨 The module-set proposal judges only the install records of ITS OWN line. Install records are
/// mesh-wide, the landed tree is per line: a line-3 Chat record (floor 3.1.12509, requires
/// <c>AI@^1.51.0</c>) judged against the line-4 tree's AI 4.0.1 refused every line-4 proposal.
/// </summary>
public class ADependencyFloorIsJudgedOnItsOwnPlatformLineTest
{
    private static ModuleActivationList AiLanded(string version) => new()
    {
        Entries = [new ModuleActivationEntry { Name = "MeshWeaver.AI", Version = version, PackagePath = "Plugins/AI" }],
    };

    private static PackageManifest Chat(string? floor) => new()
    {
        Id = "Chat",
        Name = "Chat",
        Kind = PackageKind.NodeRepo,
        TargetPartition = "Chat",
        SourceFolder = "Chat",
        Version = "1.9.0",
        ReleasedVersion = "1.9.0",
        Module = "MeshWeaver.Blazor.Chat",
        MinMeshVersion = floor,
        Requires = ["AI@^1.51.0"],
    };

    [Fact]
    public void ALine3RecordIsNotJudgedAgainstTheLine4Tree()
        => ModuleDependencyFloor.UnmetOnLine([Chat("3.1.12509")], AiLanded("4.0.1"), line: 4)
            .Should().BeEmpty("the line-3 record describes a Chat build the line-4 tree does not load");

    [Fact]
    public void ALine3RecordIsStillJudgedOnLine3()
        => ModuleDependencyFloor.UnmetOnLine([Chat("3.1.12509")], AiLanded("1.43.14"), line: 3)
            .Should().ContainSingle("control: on its own line the floor still refuses an AI below it");

    [Fact]
    public void ARecordStatingNoFloorIsJudgedOnEveryLine()
        => ModuleDependencyFloor.UnmetOnLine([Chat(null)], AiLanded("4.0.1"), line: 4)
            .Should().ContainSingle("no stated line is no evidence of another line — the check is kept");
}

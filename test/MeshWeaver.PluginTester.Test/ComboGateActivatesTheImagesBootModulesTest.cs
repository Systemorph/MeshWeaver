using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeshWeaver.ComboVerifier;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The combo gate activates the candidate IMAGE's own boot modules (MeshWeaver#4413).
///
/// <para><b>The defect, measured.</b> Every candidate offered to a customer estate's control
/// instance (3.1.13396 through 3.1.13514, combo-verify run 44149403398, slot 4) carried a Red
/// verdict on one module: <c>Deployments</c> — <c>install: NodeType(s) not registered:
/// Hosting/Deployment</c>. The instance itself reads its Deployments records fine. The control
/// image registers the Hosting NodeTypes in CODE, through the boot module
/// <c>MeshWeaver.Fleet.Control.dll</c> it asks for with <c>Modules__Assemblies__100</c> in its
/// container environment; the gate host is that image's <c>/app</c>, but the tester composes its
/// own <c>Modules:Assemblies</c> in memory, so the module was on disk in the gate and never
/// activated. A Red about the CONTENT whose cause was the INSTRUMENT.</para>
///
/// <para>The docker CLI is replaced by <see cref="DockerImageGate.DockerOverride"/>. The guard below
/// fails on the unfixed gate: neither container is handed the image's boot module.</para>
/// </summary>
public sealed class ComboGateActivatesTheImagesBootModulesTest : IDisposable
{
    private const string Image = "meshweaver.azurecr.io/memex-control:3.1.13514";
    private const string FleetControl = "MeshWeaver.Fleet.Control";

    private readonly string scratch = Directory.CreateTempSubdirectory("combo-boot-").FullName;
    private readonly IoPoolRegistry pools = new();

    private string WorkRoot => Path.Combine(scratch, "slot-4");
    private string HostDir => Path.Combine(scratch, "host");

    /// <summary>A gate host shaped like the control image's /app: the baseline appsettings (with
    /// the comments the real file carries) and the control module under <c>modules/</c>.</summary>
    public ComboGateActivatesTheImagesBootModulesTest()
    {
        Directory.CreateDirectory(WorkRoot);
        Directory.CreateDirectory(Path.Combine(HostDir, "modules", FleetControl));
        File.WriteAllText(Path.Combine(HostDir, "modules", FleetControl, FleetControl + ".dll"), "");
        File.WriteAllText(Path.Combine(HostDir, "MeshWeaver.Hosting.Instance.dll"), "");
        File.WriteAllText(Path.Combine(HostDir, "appsettings.json"),
            """
            {
              "Modules": {
                // boot-loaded provider packs
                "Assemblies": [
                  "MeshWeaver.Hosting.Instance.dll",
                ]
              }
            }
            """);
    }

    /// <summary>Releases the pools and deletes the case's scratch tree.</summary>
    public void Dispose()
    {
        pools.Dispose();
        Directory.Delete(scratch, recursive: true);
    }

    /// <summary>🚨 THE REGRESSION GUARD: the module the image's environment asks for reaches BOTH
    /// the bake and the gate as a <c>--module</c> inside the mounted host, after the baseline the
    /// image's appsettings declares — the order the portal activates them in.</summary>
    [Fact]
    public void TheImagesEnvironmentDeclaredBootModule_ReachesTheBakeAndTheGate()
    {
        var docker = new FakeDocker(
            ["PATH=/usr/bin", $"Modules__Assemblies__100={FleetControl}.dll", "Mesh__ClosedTypeSet=true"]);
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Null(run.Error);
        Assert.Equal(2, docker.Containers.Count);
        foreach (var container in docker.Containers)
            Assert.Equal(
                new[] { $"/host/modules/{FleetControl}/{FleetControl}.dll" },
                ModuleArgs(container));
    }

    /// <summary>🚨 THE REGRESSION GUARD for combo-verify never verifying (#4413): a boot module whose
    /// assembly sits at the platform host's app root is the host's own build, so it is named in
    /// <see cref="ImageBootModuleSet.ShippedByHost"/> and never handed over as <c>--module</c> —
    /// composing it fails the bake FATAL and turns every verdict NotVerifiable.</summary>
    [Fact]
    public void Resolve_NeverComposesWhatThePlatformHostAlreadyShips()
    {
        var set = ImageBootModules.Resolve(HostDir,
            ["MeshWeaver.Hosting.Instance.dll", $"{FleetControl}.dll"], [], "/host");

        Assert.Equal(new[] { $"/host/modules/{FleetControl}/{FleetControl}.dll" }, set.Paths.ToArray());
        Assert.Equal(new[] { "MeshWeaver.Hosting.Instance.dll" }, set.ShippedByHost.ToArray());
        Assert.Contains("closure", set.Describe());
    }

    /// <summary>An entry present BOTH under <c>modules/{name}/</c> and at the host root is composed
    /// from <c>modules/</c> — the portal's own precedence — and not named as shipped by the host.</summary>
    [Fact]
    public void Resolve_PrefersTheModulesFolderOverTheHostRoot()
    {
        File.WriteAllText(Path.Combine(HostDir, FleetControl + ".dll"), "");
        var set = ImageBootModules.Resolve(HostDir, [$"{FleetControl}.dll"], [], "/host");

        Assert.Equal(new[] { $"/host/modules/{FleetControl}/{FleetControl}.dll" }, set.Paths.ToArray());
        Assert.Empty(set.ShippedByHost);
    }

    /// <summary>An image whose environment cannot be read is an INSTRUMENT failure: no container
    /// runs, and the run says why instead of gating without the image's modules.</summary>
    [Fact]
    public void AnUnreadableImageEnvironment_RunsNoGate()
    {
        var docker = new FakeDocker(null);
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Empty(docker.Containers);
        Assert.Null(run.ExitCode);
        Assert.Contains("could not be read", run.Error);
    }

    /// <summary>An environment inspect that TIMED OUT is an instrument failure named as a timeout —
    /// never read as an image with no environment, which would run the gate without the image's
    /// boot modules (the exact shape of MeshWeaver#4413).</summary>
    [Fact]
    public void ATimedOutEnvironmentInspect_RunsNoGate_AndSaysItTimedOut()
    {
        var docker = new FakeDocker([$"Modules__Assemblies__100={FleetControl}.dll"]) { InspectTimesOut = true };
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Empty(docker.Containers);
        Assert.Null(run.ExitCode);
        Assert.Contains("timed out", run.Error);
    }

    /// <summary>Two variables that differ only in case (distinct on Linux, both legal in
    /// <c>Config.Env</c>) resolve last-wins, the way <see cref="ImageBootModules.Declared"/> layers
    /// the module list — and never throw, which would crash the gate instead of answering.</summary>
    [Fact]
    public void HostEnvironment_ToleratesANameSetTwiceModuloCase()
    {
        Assert.Equal("Staging", ImageBootModules.HostEnvironment(
            ["ASPNETCORE_ENVIRONMENT=Production", "aspnetcore_environment=Staging"]));

        var docker = new FakeDocker(
            ["ASPNETCORE_ENVIRONMENT=Production", "aspnetcore_environment=Staging",
             $"Modules__Assemblies__100={FleetControl}.dll"]);
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Null(run.Error);
        Assert.Equal(2, docker.Containers.Count);
    }

    /// <summary>Configuration merges an array BY INDEX: a far index ADDS, index 0 REPLACES, and an
    /// environment-specific appsettings layers between the base file and the environment.</summary>
    [Fact]
    public void Declared_LayersLikeThePortalsConfiguration()
    {
        var declared = ImageBootModules.Declared(
            """{ "Modules": { "Assemblies": [ "A.dll", "B.dll", "C.dll" ] } }""",
            """{ "modules": { "assemblies": [ "A2.dll" ] } }""",
            ["MODULES__ASSEMBLIES__1=B3.dll", "Modules__Assemblies__100=Z.dll", "Modules__Required__0=X.dll",
             "Modules__Assemblies__7=   "]);

        Assert.Equal(new[] { "A2.dll", "B3.dll", "C.dll", "Z.dll" }, declared.ToArray());
    }

    /// <summary>The environment-specific file is the one the host would pick.</summary>
    [Theory]
    [InlineData(new[] { "ASPNETCORE_ENVIRONMENT=Staging", "DOTNET_ENVIRONMENT=Dev" }, "Staging")]
    [InlineData(new[] { "DOTNET_ENVIRONMENT=Dev" }, "Dev")]
    [InlineData(new string[0], "Production")]
    public void HostEnvironment_IsTheOneTheHostBuilderSelects(string[] env, string expected) =>
        Assert.Equal(expected, ImageBootModules.HostEnvironment(env));

    /// <summary>A boot module the combo also LANDS is taken from the landed copy only (landed wins,
    /// as in a running portal); one the image does not carry is NAMED, never passed.</summary>
    [Fact]
    public void Resolve_LetsTheLandedCopyWin_AndNamesWhatTheImageLacks()
    {
        var set = ImageBootModules.Resolve(HostDir,
            [$"{FleetControl}.dll", "MeshWeaver.AI.OpenAI.dll", "MeshWeaver.Missing.dll"],
            ["MeshWeaver.AI.OpenAI"], "/host");

        Assert.Equal(new[] { $"/host/modules/{FleetControl}/{FleetControl}.dll" }, set.Paths.ToArray());
        Assert.Equal(new[] { "MeshWeaver.AI.OpenAI.dll" }, set.ShadowedByLanded.ToArray());
        Assert.Equal(new[] { "MeshWeaver.Missing.dll" }, set.Absent.ToArray());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private DockerImageGate Gate(FakeDocker docker) =>
        new(pools, TimeSpan.FromMinutes(25), TextWriter.Null, DockerImageGate.FleetPlatform, HostDir)
        {
            DockerOverride = docker.Call,
        };

    private static string[] ModuleArgs(IReadOnlyList<string> args) =>
        [.. Enumerable.Range(0, args.Count - 1).Where(i => args[i] == "--module").Select(i => args[i + 1])];

    /// <summary>Answers the platform inspect as present, the environment inspect with
    /// <paramref name="environment"/> (null = the inspect fails), and records every container;
    /// the compile container seals a bake with one bundle.</summary>
    private sealed class FakeDocker(string[]? environment)
    {
        public List<string[]> Containers { get; } = [];

        /// <summary>When set, the environment inspect answers the way RunDocker reports a budget
        /// overrun: exit -1, no output, TimedOut.</summary>
        public bool InspectTimesOut { get; init; }

        public DockerImageGate.DockerResult Call(
            IReadOnlyList<string> args, CancellationToken ct, TimeSpan budget)
        {
            if (args[0] == "image")
            {
                if (args.Contains("{{.Os}}/{{.Architecture}}"))
                    return new(0, DockerImageGate.FleetPlatform, TimedOut: false);
                if (args.Contains("{{json .Config.Env}}"))
                    return InspectTimesOut
                        ? new(-1, "", TimedOut: true)
                        : environment is null
                        ? new(1, "Error: No such image", TimedOut: false)
                        : new(0, System.Text.Json.JsonSerializer.Serialize(environment), TimedOut: false);
                return new(0, "sha256:x", TimedOut: false);
            }
            if (args[0] != "run")
                throw new InvalidOperationException($"unexpected docker call: {string.Join(' ', args)}");

            Containers.Add([.. args]);
            if (!args.Contains("compile"))
                return new(0, "gate ran", TimedOut: false);
            var bake = Enumerable.Range(0, args.Count - 1)
                .Where(i => args[i] == "-v").Select(i => args[i + 1])
                .Single(m => m.EndsWith(":/bake", StringComparison.Ordinal))[..^":/bake".Length];
            File.WriteAllText(Path.Combine(bake, DockerImageGate.FrameworkMvidFile), "c003e001\n");
            File.WriteAllText(Path.Combine(bake, "Hosting.zip"), "bundle");
            return new(0, "baked", TimedOut: false);
        }
    }
}

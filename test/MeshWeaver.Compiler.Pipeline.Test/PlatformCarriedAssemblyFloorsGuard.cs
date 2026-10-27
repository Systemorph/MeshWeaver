using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A third-party assembly the platform carries is a CONTRACT at a version, whether or not
/// core calls it.</b> Roslyn's Workspaces depends on <c>Humanizer.Core</c> 2.14.1, so the portal
/// image always ships a <c>Humanizer.dll</c> — and that copy is the one every module binds: a
/// module's own bundled copy never loads while the platform carries the name, and .NET binds a
/// reference only to an EQUAL or HIGHER version. MeshWeaver.Plugins modules compile against
/// Humanizer 3.x (<c>MeshWeaver.Graph.Views</c> calls <c>DateTime.Humanize</c>).
///
/// <para><b>The incident.</b> #7262 dropped core's only direct <c>Humanizer.Core</c> reference
/// ("nothing in that project calls it") and the image's copy fell back to Roslyn's 2.14. Every
/// install of <c>MeshWeaver.Graph.Views</c> was then refused at boot —
/// <c>requires 'Humanizer 3.0.0.0 (this platform carries 2.14.0.0)'</c> — the module contributed
/// nothing, and the Education gate went red on an unchanged commit because no course's exercise
/// page rendered.</para>
///
/// <para><b>What this measures</b>: the platform closure as this test host gets it through
/// <c>MeshWeaver.Compiler.Pipeline</c> (the project that brings Roslyn in, and carries the lift),
/// read with the SAME surface and link check the module landing uses
/// (<see cref="ModulePlatformSurface"/>, <see cref="ModulePlatformLink"/>). The negative control
/// re-reads that surface with only Humanizer's version lowered to Roslyn's, and the identical
/// module bytes are refused with the production sentence — so the floor is what decides it.</para>
/// </summary>
public class PlatformCarriedAssemblyFloorsGuard : IDisposable
{
    private const string Humanizer = "Humanizer";

    /// <summary>The lowest version of Humanizer the platform may carry: modules are built against
    /// Humanizer.Core 3.x, whose assembly version is 3.0.0.0.</summary>
    private static readonly Version HumanizerFloor = new(3, 0, 0, 0);

    /// <summary>Roslyn's own transitive — what the platform carries when nothing lifts it.</summary>
    private static readonly Version RoslynsHumanizer = new(2, 14, 0, 0);

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-floor-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the per-test root.</summary>
    public PlatformCarriedAssemblyFloorsGuard() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
        catch (UnauthorizedAccessException) { /* same */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The platform carries Humanizer at or above the floor, and a module that calls it — compiled
    /// against the platform's own copy, as every module lane compiles — links.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void ThePlatformCarriesHumanizerAtOrAboveTheFloorModulesAreBuiltAgainst()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var surface = ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory);

        Assert.True(surface.Carries(Humanizer),
            "the platform closure carries no Humanizer at all — Roslyn's Workspaces depends on it, so "
            + "this test is no longer reading the platform closure; fix the test's input before trusting it");
        var carried = surface.IdentityOf(Humanizer)?.Version;
        Assert.NotNull(carried);
        Assert.True(carried >= HumanizerFloor,
            $"the platform carries Humanizer {carried}, below the floor {HumanizerFloor} that "
            + "MeshWeaver.Plugins modules are built against: every such module is refused at load "
            + "('requires Humanizer 3.0.0.0 (this platform carries …)'). Keep the Humanizer.Core "
            + "reference in MeshWeaver.Compiler.Pipeline.");

        var module = HumanizingModule();
        var verdict = ModulePlatformLink.Check(module, surface);
        Assert.True(verdict.MayLoad, verdict.Report());
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — the same module bytes against the same platform surface, with ONLY
    /// Humanizer's version lowered to Roslyn's 2.14 (the image #7262 shipped): refused as a binding
    /// conflict, naming exactly what production logged. The unmodified round-trip of the same
    /// document links, so the serialisation is not what refuses it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void AModuleBuiltAgainstHumanizer3IsRefusedByAPlatformCarryingRoslynsHumanizer()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var document = JsonNode.Parse(
            ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory).ToJson())!.AsObject();
        var module = HumanizingModule();

        var asCarried = ModulePlatformLink.Check(module, ModulePlatformSurface.FromJson(document.ToJsonString()));
        Assert.True(asCarried.MayLoad, asCarried.Report());

        var humanizer = document["identities"]?[Humanizer]?.AsObject();
        Assert.NotNull(humanizer);
        humanizer["version"] = RoslynsHumanizer.ToString(4);
        var downgraded = ModulePlatformLink.Check(module, ModulePlatformSurface.FromJson(document.ToJsonString()));

        Assert.Equal(ModuleLinkState.BindingConflict, downgraded.State);
        Assert.Contains(
            $"{Humanizer} {HumanizerFloor.ToString(4)} (this platform carries {RoslynsHumanizer.ToString(4)})",
            downgraded.Report(), StringComparison.Ordinal);
    }

    /// <summary>Compiles a module that calls <c>DateTime.Humanize</c> — the call
    /// MeshWeaver.Graph.Views makes — against the platform's reference set, and writes it alone
    /// into a module folder.</summary>
    private string HumanizingModule()
    {
        const string name = "MeshWeaver.Test.HumanizingModule";
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText("""
                using Humanizer;
                public static class Ago
                {
                    public static string Of(System.DateTime when) => when.Humanize();
                }
                """)],
            CompileReferences.Default,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}

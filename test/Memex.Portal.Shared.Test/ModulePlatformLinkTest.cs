using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A module is adopted on what it CAN LOAD, never on a version string (#3538, #3518).</b>
///
/// <para><b>The incident these reproduce.</b> memex-cloud ran a core of 2026-09-03 and adopted
/// <c>DefaultViews</c>' <c>MeshWeaver.Graph.Views</c>, whose bytes were compiled on 09-06 against a
/// <c>MeshWeaver.Mesh.Contract</c> carrying <c>CodeOutputCurrency</c> — a type added on 09-04. The
/// only gate was the module's declared <c>minMeshVersion: 3.0.0-rc8</c> floor, which the running
/// <c>3.0.0-rc9.ci.7693</c> satisfied, so the bytes landed and loaded. They installed CLEANLY —
/// the module's <c>MeshNodeProviderAttribute</c> touches none of the missing surface, so #2234's
/// per-module install isolation saw nothing — and then EVERY render of every code cell threw
/// <c>TypeLoadException: Could not load type 'MeshWeaver.Mesh.CodeOutputCurrency'</c>, for every
/// user, until a human read a pod log.</para>
///
/// <para><b>Why the declared floor can never answer it.</b> A floor is a CLAIM about API
/// compatibility that an author writes by hand. The module's real requirement is not a version at
/// all — it is the SET OF TYPES its bytes are linked against, and that set is stated exactly, by
/// the compiler, in the assembly's own metadata. So it is MEASURED against the platform copies
/// this process would actually bind to (<see cref="ModulePlatformLink"/>), metadata only, with no
/// <c>Assembly.Load</c> and no type loading.</para>
///
/// <para><b>What makes these able to FAIL.</b> Every one compiles REAL assemblies with Roslyn and
/// runs the REAL gate. The landing tests compile a module against a stand-in
/// <c>MeshWeaver.Mesh.Contract</c> that carries a type the platform running this test does NOT
/// have — the memex-cloud shape verbatim, same assembly simple name, missing type — and drive the
/// REAL <see cref="ModuleLandingService"/>. Neutralising the gate (making
/// <c>ModuleLinkVerdict.MayLoad</c> true for every state) flips
/// <see cref="AModuleBuiltAgainstANewerPlatform_IsRefusedAtLanding_NamingTheMissingType"/>,
/// <see cref="UnreadableBytes_AreRefused_NotWavedThroughAsUnknown"/> and
/// <see cref="AnUnloadableModule_IsParked_AndTheOthersStillInstall"/>. Removing the positive
/// controls' counterpart — <see cref="AModuleBuiltAgainstThisPlatform_LandsAsBefore"/> and
/// <see cref="ALinkableModule_ReportsANonZeroDenominator"/> — is what stops the gate from passing
/// by refusing everything, or by checking nothing.</para>
/// </summary>
public class ModulePlatformLinkTest : IDisposable
{
    /// <summary>The platform assembly the stand-in impersonates: a REAL one this process has
    /// loaded, so the test measures against the copy a module would actually bind to.</summary>
    private const string ContractAssembly = "MeshWeaver.Mesh.Contract";

    /// <summary>A type no build of <see cref="ContractAssembly"/> has ever carried — the stand-in
    /// for <c>CodeOutputCurrency</c> as it looked from a platform three days behind.</summary>
    private const string FutureType = "MeshWeaver.Mesh.CodeOutputCurrencyFromTheFuture";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-linkprobe-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    /// <summary>Creates the per-test deployment root and the REAL landing service over it.</summary>
    public ModulePlatformLinkTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    // ───────────────────────────────────────────────────── the floor is the BUILD platform

    /// <summary>
    /// 🚨 <b>THE repro of #3538, at the landing.</b> A module compiled against a
    /// <c>MeshWeaver.Mesh.Contract</c> that has a type this platform's copy does not is REFUSED,
    /// and the refusal NAMES the type — the sentence that was missing from the memex-cloud
    /// incident, where the only evidence was a <c>TypeLoadException</c> per render.
    ///
    /// <para>The declared floor is deliberately left absent, so the ONLY thing that can refuse
    /// this is the measured one. Before the fix, an absent floor was "no constraint" and these
    /// bytes landed.</para>
    /// </summary>
    [Fact]
    public async Task AModuleBuiltAgainstANewerPlatform_IsRefusedAtLanding_NamingTheMissingType()
    {
        var module = ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.FutureViewPack");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await landing.LandModule(
                    "MeshWeaver.Test.FutureViewPack",
                    [("MeshWeaver.Test.FutureViewPack.dll", module)],
                    minMeshVersion: null)
                .Timeout(TestTimeouts.Convergence).Await());

        Assert.Contains(FutureType, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(ContractAssembly, refusal.Message, StringComparison.Ordinal);
        // 🚨 And nothing landed: the previous generation is what keeps serving. A refusal that
        // still wrote the bytes would leave the next boot to load them.
        Assert.Empty(ModuleActivationSidecar.Read(root).Entries);
    }

    /// <summary>
    /// The positive control, and the reason the gate above is a gate rather than a wall: a module
    /// compiled against THIS platform's real contract lands exactly as it always did.
    /// </summary>
    [Fact]
    public async Task AModuleBuiltAgainstThisPlatform_LandsAsBefore()
    {
        var module = ModuleBuiltAgainstThisPlatform("MeshWeaver.Test.CurrentViewPack");

        await landing.LandModule(
                "MeshWeaver.Test.CurrentViewPack",
                [("MeshWeaver.Test.CurrentViewPack.dll", module)])
            .Timeout(TestTimeouts.Convergence).Await();

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("MeshWeaver.Test.CurrentViewPack", entry.Name);
        Assert.True(entry.Enabled);
    }

    /// <summary>
    /// 🚨 The SHELF path is unchanged in kind: a registry stocking a module for OTHER platforms
    /// must still carry bytes IT cannot load. They land and serve; this process's boot refuses
    /// them, which is exactly what the floor already did (2026-08-22's three-way deadlock).
    /// </summary>
    [Fact]
    public async Task AnUnloadableModule_IsShelvedRatherThanRefused_OnThePublishPath()
    {
        var module = ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.ShelvedViewPack");

        var outcome = await landing.ShelveModule(
                "MeshWeaver.Test.ShelvedViewPack",
                [("MeshWeaver.Test.ShelvedViewPack.dll", module)])
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.True(outcome.Held);
        Assert.Contains(FutureType, outcome.HoldReason!, StringComparison.Ordinal);
        // The bytes ARE on the shelf — consumers fetch them and apply this same measurement
        // against THEIR platform.
        Assert.Single(ModuleActivationSidecar.Read(root).Entries);
    }

    /// <summary>
    /// 🚨 <b>The third state, and it fails CLOSED.</b> Bytes that are not a readable managed
    /// assembly answer <see cref="ModuleLinkState.Indeterminate"/> — "I could not determine
    /// whether this loads" — and that is NEVER folded into "it loads". A gate whose unknown reads
    /// as a pass is a gate that cannot fail.
    /// </summary>
    [Fact]
    public async Task UnreadableBytes_AreRefused_NotWavedThroughAsUnknown()
    {
        var verdict = ModulePlatformLink.Check(
            [0x4D, 0x5A, 0x90], "MeshWeaver.Test.Garbage",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MeshWeaver.Test.Garbage" },
            ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory));

        Assert.Equal(ModuleLinkState.Indeterminate, verdict.State);
        Assert.False(verdict.MayLoad);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await landing.LandModule(
                    "MeshWeaver.Test.Garbage", [("MeshWeaver.Test.Garbage.dll", [0x4D, 0x5A, 0x90])])
                .Timeout(TestTimeouts.Convergence).Await());
        Assert.Contains("could NOT be determined", refusal.Message, StringComparison.Ordinal);
    }

    // ───────────────────────────────────────────────────── the probe itself, and its denominator

    /// <summary>
    /// 🚨 <b>The denominator, printed.</b> A clean verdict over ZERO checked references is
    /// indistinguishable from a check that never ran — the sweep-with-no-denominator shape this
    /// codebase keeps meeting. A linkable module must therefore report that it actually resolved
    /// references against real platform assemblies.
    /// </summary>
    [Fact]
    public void ALinkableModule_ReportsANonZeroDenominator()
    {
        var path = Write("MeshWeaver.Test.CountedViewPack",
            ModuleBuiltAgainstThisPlatform("MeshWeaver.Test.CountedViewPack"));

        var verdict = ModulePlatformLink.Check(
            path, ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory));

        Assert.Equal(ModuleLinkState.Linkable, verdict.State);
        Assert.True(verdict.CheckedTypeReferences > 0,
            "a Linkable verdict over zero checked references is a check that never ran: "
            + verdict.Report());
        Assert.Contains(ContractAssembly, verdict.CheckedAssemblies);
    }

    /// <summary>
    /// The coarse-grained half of the same defect: a module referencing a whole PLATFORM assembly
    /// this deployment does not carry. Refused, because that is a certain load failure — while a
    /// non-platform assembly it does not carry is a private dependency, NAMED as unchecked rather
    /// than guessed about.
    /// </summary>
    [Fact]
    public void AWholePlatformAssemblyThisDeploymentLacks_IsRefused()
    {
        var path = Write("MeshWeaver.Test.OrphanViewPack",
            ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.OrphanViewPack"));

        // A surface with NO probe directory and NO loaded assemblies carries nothing at all.
        var verdict = ModulePlatformLink.Check(
            path, ModulePlatformSurface.Of(Array.Empty<System.Reflection.Assembly>()));

        Assert.Equal(ModuleLinkState.Unlinkable, verdict.State);
        Assert.Contains(verdict.MissingTypes,
            m => m.Contains("no such platform assembly", StringComparison.Ordinal));
        // System.* is not the platform's, so it is reported as UNCHECKED rather than missing.
        Assert.Contains(verdict.UncheckedAssemblies,
            a => a.StartsWith("System.", StringComparison.Ordinal));
    }

    // ───────────────────────────────────────────────────── the generation is parked, not the portal

    /// <summary>
    /// 🚨 <b>THE repro of #3518's blast radius.</b> An unloadable module costs THAT module and
    /// nothing else: it is never loaded, it is recorded as
    /// <see cref="IncompatibleModule"/> naming the type it wanted, the module beside it installs
    /// normally, and <c>InstallAssemblies</c> does not throw. That is the difference between "one
    /// view pack is absent" and "every render on the portal throws".
    /// </summary>
    [Fact]
    public void AnUnloadableModule_IsParked_AndTheOthersStillInstall()
    {
        var bad = Write("MeshWeaver.Test.ParkedViewPack",
            ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.ParkedViewPack"));
        // A real, loadable assembly standing in for the module that must keep working.
        var goodName = typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.GetName().Name!;
        var good = Write(goodName,
            File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location));

        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "test"));

        // Does not throw: a module that cannot load is a reason to lose THAT module.
        builder.InstallAssemblies(bad, good);

        var provider = services.BuildServiceProvider();
        var parked = Assert.Single(provider.GetServices<IncompatibleModule>());
        Assert.Equal("MeshWeaver.Test.ParkedViewPack", parked.Name);
        Assert.Contains(FutureType, parked.Report(), StringComparison.Ordinal);
        Assert.Contains("CONTRIBUTING NOTHING", parked.Report(), StringComparison.Ordinal);

        Assert.Contains(provider.GetServices<InstalledModuleAssembly>(),
            m => string.Equals(m.Assembly.GetName().Name, goodName, StringComparison.Ordinal));
    }

    /// <summary>
    /// A parked module reads as QUARANTINED on every surface, never as PENDING. "Restart required"
    /// would be a promise no restart can keep — a restart re-runs the same measurement on the same
    /// bytes and refuses again — which is the same false-prompt rule a held entry and a missing
    /// DLL already follow.
    /// </summary>
    [Fact]
    public async Task AParkedModule_ReadsAsQuarantined_NeverAsRestartRequired()
    {
        await landing.LandModule(
                "MeshWeaver.Test.CurrentViewPack",
                [("MeshWeaver.Test.CurrentViewPack.dll",
                    ModuleBuiltAgainstThisPlatform("MeshWeaver.Test.CurrentViewPack"))])
            .Timeout(TestTimeouts.Convergence).Await();

        var report = new PendingModuleActivations(root)
        {
            QuarantinedModules = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase, "MeshWeaver.Test.CurrentViewPack"),
        }.Read(
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            loadedModuleGenerations: ImmutableDictionary<string, string>.Empty);

        Assert.False(report.HasPending);
        Assert.True(report.HasQuarantined);
        Assert.Equal("MeshWeaver.Test.CurrentViewPack", Assert.Single(report.Quarantined).Name);
        Assert.Contains("REFUSED against this platform build", report.Describe(),
            StringComparison.Ordinal);
        // A blank package path matches nothing — never a wildcard that would put another
        // package's quarantine on this card.
        Assert.False(report.IsQuarantinedForPackage(null));
    }

    /// <summary>
    /// 🚨 A REQUIRED module refused because it needs another platform must NOT stall a rollout.
    /// That is the declared floor's rule, and it exists because no rollout of this deployment can
    /// conjure the platform the module was built for — in the rollback direction, stalling would
    /// hold the very roll that resolves it (the 2026-08-22 three-way deadlock, one lane over).
    ///
    /// <para>An IMAGE-shipped module that cannot link stays <c>Incompatible</c> and DOES stall:
    /// that is a build defect the previous generation does not share.</para>
    /// </summary>
    [Fact]
    public void ARequiredStoreModuleRefusedForItsPlatform_DoesNotStallARollout()
    {
        var refused = IncompatibleModule.FromLinkRefusal(
            "/data/modules/MeshWeaver.Test.ParkedViewPack@1/MeshWeaver.Test.ParkedViewPack.dll",
            ModulePlatformLink.Check(
                ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.ParkedViewPack"),
                "MeshWeaver.Test.ParkedViewPack",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "MeshWeaver.Test.ParkedViewPack" },
                ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory)));

        Assert.True(refused.RefusedBeforeLoad);

        // 🚨 The entries carry '.dll'. `Classify` derives the module name with
        // Path.GetFileNameWithoutExtension, which on a suffix-less DOTTED assembly name strips the
        // last segment ("MeshWeaver.Test.ParkedViewPack" → "MeshWeaver.Test") — so a suffix-less
        // entry matches no module and every verdict here would be ExpectedLater by accident. The
        // Name assertions below are what make that failure visible instead of green.
        var storeDelivered = RequiredModuleStatus.Classify(
            requiredEntries: ["MeshWeaver.Test.ParkedViewPack.dll"],
            baselineEntries: [],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvesFromDeployment: _ => false,
            activation: null,
            landedDllExists: _ => false,
            platformGate: _ => null,
            incompatibleModules: [refused]);
        Assert.Empty(RequiredModuleStatus.Incompatible(storeDelivered));
        var expected = Assert.Single(RequiredModuleStatus.ExpectedLater(storeDelivered));
        Assert.Equal("MeshWeaver.Test.ParkedViewPack", expected.Name);
        // And it says WHY — the link refusal, not a generic "not installed".
        Assert.Contains(FutureType, expected.Reason, StringComparison.Ordinal);

        // The image's OWN pack, unlinkable against the image it shipped in, still stalls.
        var imageShipped = RequiredModuleStatus.Classify(
            requiredEntries: ["MeshWeaver.Test.ParkedViewPack.dll"],
            baselineEntries: ["MeshWeaver.Test.ParkedViewPack.dll"],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvesFromDeployment: _ => false,
            activation: null,
            landedDllExists: _ => false,
            platformGate: _ => null,
            incompatibleModules: [refused]);
        var stalls = Assert.Single(RequiredModuleStatus.Incompatible(imageShipped));
        Assert.Equal("MeshWeaver.Test.ParkedViewPack", stalls.Name);
    }

    // ───────────────────────────────────────────────────────────── harness

    /// <summary>
    /// A module compiled against a STAND-IN <c>MeshWeaver.Mesh.Contract</c> that carries
    /// <see cref="FutureType"/> — the assembly a platform three days ahead would have shipped. The
    /// bytes therefore reference a type by the same assembly simple name this process has loaded,
    /// and that copy does not have it: memex-cloud's exact shape, and one no version comparison
    /// can see.
    /// </summary>
    private static byte[] ModuleBuiltAgainstAFuturePlatform(string moduleName)
    {
        var future = Emit(ContractAssembly, """
            namespace MeshWeaver.Mesh;
            /// <summary>The type a newer platform has and this one does not.</summary>
            public enum CodeOutputCurrencyFromTheFuture { Unknown, Current, Stale }
            """, excludeReference: ContractAssembly);

        return Emit(moduleName, """
            using MeshWeaver.Mesh;
            public static class CodeViews
            {
                public static string BuildContent(CodeOutputCurrencyFromTheFuture currency)
                    => currency.ToString();
            }
            """,
            excludeReference: ContractAssembly,
            extra: MetadataReference.CreateFromImage(future));
    }

    /// <summary>A module compiled against the REAL contract this process is running, using a type
    /// it genuinely has — the control that proves the gate does not simply refuse everything.</summary>
    private static byte[] ModuleBuiltAgainstThisPlatform(string moduleName) =>
        Emit(moduleName, """
            using MeshWeaver.Mesh;
            public static class CodeViews
            {
                public static string BuildContent(MeshNode node) => node.Id;
            }
            """);

    /// <summary>Compiles one source file into an assembly named <paramref name="assemblyName"/>,
    /// against this process's own reference set.</summary>
    /// <param name="assemblyName">The emitted assembly's simple name.</param>
    /// <param name="source">The C# to compile.</param>
    /// <param name="excludeReference">A reference assembly to LEAVE OUT — how a stand-in of the
    /// same simple name is substituted for the platform's own.</param>
    /// <param name="extra">References to add, e.g. the stand-in.</param>
    private static byte[] Emit(
        string assemblyName, string source, string? excludeReference = null,
        MetadataReference? extra = null)
    {
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Where(p => excludeReference is null
                        || !string.Equals(Path.GetFileNameWithoutExtension(p), excludeReference,
                            StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        if (extra is not null)
            platform.Add(extra);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }

    /// <summary>Writes an assembly into its OWN directory under the test root, so its "siblings"
    /// are exactly its own closure — the shape a landed generation directory has.</summary>
    private string Write(string assemblyName, byte[] bytes)
    {
        var directory = Path.Combine(root, "emitted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, assemblyName + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The link probe compares assembly VERSIONS, asymmetrically (#4083).</b>
///
/// <para><b>The incident these reproduce.</b> On 2026-09-11 core #4012 bumped YamlDotNet
/// 16.3.0 → 18.1.0. <c>MeshWeaver.AI</c> references YamlDotNet directly and versionless, was built
/// on an image carrying 18, and landed through the registry on portal pods whose image shipped 16.
/// The probe said <c>Linkable</c> — every type name the module uses exists in both majors — and
/// every new pod crash-looped at hub construction on
/// <c>FileLoadException 0x80131040</c>: the platform's 16.3.0.0 copy is what the loader binds, and
/// .NET never binds a reference to a LOWER version than it asks for.</para>
///
/// <para><b>Two causes, both pinned here.</b> (a) the bundle shipped its own YamlDotNet, so the
/// reference was excluded from the denominator under the "built together" rule — a premise that is
/// false exactly when the platform carries the same simple name, because the platform's copy wins;
/// (b) no version was ever compared, and <c>ModuleLinkState</c> had no state to put a
/// <c>FileLoadException</c> verdict in.</para>
///
/// <para><b>The rule is asymmetric on purpose.</b> A reference HIGHER than the platform's copy is
/// refused by the loader and is therefore a hard verdict (<see cref="ModuleLinkState.BindingConflict"/>);
/// a reference LOWER than the platform's copy rolls forward and is an ADVISORY — patch drift is
/// ordinary, and a gate that reds on it is switched off within the week. A public key token
/// mismatch under the same name is hard whatever the versions. A surface that records no version
/// is UNKNOWN, and unknown is an advisory, never a verdict.</para>
///
/// <para><b>What makes these able to FAIL.</b> Every one compiles REAL assemblies with Roslyn —
/// two stand-in <c>YamlDotNet</c> builds with identical type names and different manifest versions,
/// exactly the 09-11 shape — and drives the REAL probe, the REAL surface document and the REAL
/// landing service. Restoring the closure short-circuit flips
/// <see cref="TheIncidentShape_AModuleBoundToYamlDotNet18_OnAPlatformCarrying16_CannotBind"/>;
/// dropping the version comparison flips every hard verdict below; making the comparison symmetric
/// flips <see cref="AReferenceBelowThePlatformsVersion_IsLinkable_WithAnAdvisory"/>.</para>
/// </summary>
public class ModuleLinkVersionTest : IDisposable
{
    /// <summary>The third-party assembly of the incident. The stand-ins carry a type the real one
    /// has too, so on the RUNNING process the type walk passes and only the version can refuse.</summary>
    private const string Yaml = "YamlDotNet";
    private const string YamlType = "YamlDotNet.Serialization.Deserializer";
    private const string Identity = "s4083surface00000000000000000000";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-4083-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    public ModuleLinkVersionTest()
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

    // ───────────────────────────────────────────────── the incident, against a file surface

    /// <summary>
    /// 🚨 <b>THE repro of 2026-09-11.</b> A module compiled against YamlDotNet 18.1.0.0, shipping
    /// its own copy (so <c>YamlDotNet</c> is in its closure), checked against a platform whose
    /// YamlDotNet is 16.3.0.0 with IDENTICAL type names. Type-name existence says fine; the loader
    /// says <c>FileLoadException</c>. The verdict must be the hard one, name the assembly and both
    /// versions, and <c>MayLoad</c> must be false — both against the file surface (the running
    /// direction) and against the published document read back (the roll-gate direction).
    /// </summary>
    [Fact]
    public void TheIncidentShape_AModuleBoundToYamlDotNet18_OnAPlatformCarrying16_CannotBind()
    {
        var platform = ModulePlatformSurface.OfFiles([Write(Yaml, YamlStandIn("16.3.0.0"))]);
        var module = ModuleAgainst(YamlStandIn("18.1.0.0"), "MeshWeaver.Test.AiPack");
        var closure = Closure("MeshWeaver.Test.AiPack", Yaml);

        var onFiles = ModulePlatformLink.Check(module, "MeshWeaver.Test.AiPack", closure, platform);
        var onDocument = ModulePlatformLink.Check(module, "MeshWeaver.Test.AiPack", closure,
            ModulePlatformSurface.FromJson(platform.ToJson(Identity)));

        foreach (var verdict in new[] { onFiles, onDocument })
        {
            Assert.Equal(ModuleLinkState.BindingConflict, verdict.State);
            Assert.False(verdict.MayLoad);
            var conflict = Assert.Single(verdict.BindingConflicts);
            Assert.StartsWith(Yaml + " 18.1.0.0", conflict, StringComparison.Ordinal);
            Assert.Contains("16.3.0.0", conflict, StringComparison.Ordinal);
            Assert.Contains("FileLoadException", verdict.Report(), StringComparison.Ordinal);
            Assert.Empty(verdict.MissingTypes);
            Assert.True(verdict.ComparedAssemblyReferences > 0, verdict.Report());
        }
        // The document reaches the same verdict as the files: the roll gate and the boot probe
        // cannot disagree about this.
        Assert.Equal(onFiles.BindingConflicts, onDocument.BindingConflicts);
    }

    /// <summary>
    /// The other direction of the same skew: a module bound to 16.3.0.0 on a platform carrying
    /// 18.1.0.0 binds — the loader rolls forward — and the drift is on the verdict as an ADVISORY.
    /// Never a refusal: patch drift is ordinary, and this is the arm a symmetric comparison gets
    /// wrong.
    /// </summary>
    [Fact]
    public void AReferenceBelowThePlatformsVersion_IsLinkable_WithAnAdvisory()
    {
        var platform = ModulePlatformSurface.OfFiles([Write(Yaml, YamlStandIn("18.1.0.0"))]);
        var module = ModuleAgainst(YamlStandIn("16.3.0.0"), "MeshWeaver.Test.OlderAiPack");

        var verdict = ModulePlatformLink.Check(
            module, "MeshWeaver.Test.OlderAiPack", Closure("MeshWeaver.Test.OlderAiPack", Yaml), platform);

        Assert.Equal(ModuleLinkState.Linkable, verdict.State);
        Assert.True(verdict.MayLoad);
        Assert.Empty(verdict.BindingConflicts);
        var advisory = Assert.Single(verdict.Advisories);
        Assert.StartsWith(Yaml + ":", advisory, StringComparison.Ordinal);
        Assert.Contains("16.3.0.0", advisory, StringComparison.Ordinal);
        Assert.Contains("18.1.0.0", advisory, StringComparison.Ordinal);
        Assert.Contains("rolls forward", advisory, StringComparison.Ordinal);
        Assert.Contains("Advisory", verdict.Report(), StringComparison.Ordinal);
    }

    /// <summary>Equal versions: as today — Linkable, nothing to report.</summary>
    [Fact]
    public void AnEqualVersion_IsLinkable_WithNothingToReport()
    {
        var platform = ModulePlatformSurface.OfFiles([Write(Yaml, YamlStandIn("16.3.0.0"))]);
        var module = ModuleAgainst(YamlStandIn("16.3.0.0"), "MeshWeaver.Test.SameAiPack");

        var verdict = ModulePlatformLink.Check(
            module, "MeshWeaver.Test.SameAiPack", Closure("MeshWeaver.Test.SameAiPack", Yaml), platform);

        Assert.Equal(ModuleLinkState.Linkable, verdict.State);
        Assert.Empty(verdict.BindingConflicts);
        Assert.Empty(verdict.Advisories);
        Assert.Equal(1, verdict.ComparedAssemblyReferences);
    }

    /// <summary>
    /// The same simple name under a DIFFERENT public key token is a different assembly to the
    /// loader, whatever the versions say — hard, in both version directions.
    /// </summary>
    [Fact]
    public void APublicKeyTokenMismatch_IsABindingConflict_WhateverTheVersions()
    {
        var keyA = typeof(object).Assembly.GetName().GetPublicKey()!;
        var keyB = typeof(Compilation).Assembly.GetName().GetPublicKey()!;
        Assert.True(keyA.Length > 0 && keyB.Length > 0 && !keyA.SequenceEqual(keyB),
            "precondition: two distinct real public keys to sign the stand-ins with");

        var platform = ModulePlatformSurface.OfFiles([Write(Yaml, YamlStandIn("16.3.0.0", keyA))]);
        var module = ModuleAgainst(YamlStandIn("16.3.0.0", keyB), "MeshWeaver.Test.KeyedAiPack");

        var verdict = ModulePlatformLink.Check(
            module, "MeshWeaver.Test.KeyedAiPack", Closure("MeshWeaver.Test.KeyedAiPack", Yaml), platform);

        Assert.Equal(ModuleLinkState.BindingConflict, verdict.State);
        var conflict = Assert.Single(verdict.BindingConflicts);
        Assert.Contains("public key token", conflict, StringComparison.Ordinal);
        Assert.Contains(Yaml, conflict, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 A surface that records NO versions — the document every bake wrote before #4083 — cannot
    /// answer a version question, and "unknown" is an ADVISORY, never a hard verdict: the roll gate
    /// would otherwise hold every roll on a publication that simply predates the section. Even the
    /// incident shape is Linkable-with-advisory against such a document.
    /// </summary>
    [Fact]
    public void ASurfaceWithoutVersions_IsAdvisoryOnly_NeverHard()
    {
        var withVersions = ModulePlatformSurface.OfFiles([Write(Yaml, YamlStandIn("16.3.0.0"))]).ToJson(Identity);
        var document = JsonNode.Parse(withVersions)!.AsObject();
        Assert.True(document.Remove("identities"), "precondition: the document carried identities to strip");
        var legacy = ModulePlatformSurface.FromJson(document.ToJsonString());
        Assert.Null(legacy.IdentityOf(Yaml));
        Assert.True(legacy.Carries(Yaml));

        var module = ModuleAgainst(YamlStandIn("18.1.0.0"), "MeshWeaver.Test.LegacyAiPack");
        var verdict = ModulePlatformLink.Check(
            module, "MeshWeaver.Test.LegacyAiPack", Closure("MeshWeaver.Test.LegacyAiPack", Yaml), legacy);

        Assert.Equal(ModuleLinkState.Linkable, verdict.State);
        Assert.Empty(verdict.BindingConflicts);
        var advisory = Assert.Single(verdict.Advisories);
        Assert.Contains("records no version", advisory, StringComparison.Ordinal);
        Assert.Contains("18.1.0.0", advisory, StringComparison.Ordinal);
    }

    // ───────────────────────────────────────────── the closure rule, and its one exception

    /// <summary>
    /// 🚨 Cause (a): a copy travelling in the module's own bundle no longer hides a reference the
    /// platform carries. The SAME module bytes, the same surface — with <c>YamlDotNet</c> in the
    /// closure (the bundle ships it) and without — reach the same hard verdict, because the
    /// platform's copy is what loads either way.
    /// </summary>
    [Fact]
    public void TheBundlesOwnCopy_DoesNotHideAPlatformCarriedAssembly()
    {
        var platform = ModulePlatformSurface.OfFiles([Write(Yaml, YamlStandIn("16.3.0.0"))]);
        var module = ModuleAgainst(YamlStandIn("18.1.0.0"), "MeshWeaver.Test.ShippingAiPack");

        var shipping = ModulePlatformLink.Check(module, "MeshWeaver.Test.ShippingAiPack",
            Closure("MeshWeaver.Test.ShippingAiPack", Yaml), platform);
        var consuming = ModulePlatformLink.Check(module, "MeshWeaver.Test.ShippingAiPack",
            Closure("MeshWeaver.Test.ShippingAiPack"), platform);

        Assert.True(platform.IsPlatformBound(Yaml));
        Assert.Equal(ModuleLinkState.BindingConflict, shipping.State);
        Assert.Equal(ModuleLinkState.BindingConflict, consuming.State);
        Assert.Equal(consuming.BindingConflicts, shipping.BindingConflicts);
    }

    /// <summary>
    /// The exception that keeps the rule honest: a sibling the surface carries ONLY because a
    /// superseded generation of this same module landed it earlier is NOT platform-bound — the
    /// next boot binds the sibling this bundle brings, not the one it replaces. So a module
    /// re-landing its own siblings, with a new type on the new sibling, stays Linkable. The
    /// contrast (the sibling NOT in the closure — some other module's, which the loader would
    /// resolve from the landed directory) is measured and refused, which shows the predicate is
    /// what does the work.
    /// </summary>
    [Fact]
    public void AClosureSiblingCarriedOnlyByALandedDirectory_IsStillTheModulesOwnBusiness()
    {
        const string sibling = "MeshWeaver.Test.OwnSibling";
        var older = Emit(sibling, "16.0.0.0", "namespace MeshWeaver.Test; public static class OldApi { public static int A => 1; }");
        var newer = Emit(sibling, "16.0.0.0", "namespace MeshWeaver.Test; public static class OldApi { public static int A => 1; } public static class NewApi { public static int B => 2; }");
        var landedDirectory = Path.GetDirectoryName(Write(sibling, older))!;
        var module = Emit("MeshWeaver.Test.ReLandingPack", "1.0.0.0",
            "public static class Views { public static int Show() => MeshWeaver.Test.NewApi.B; }",
            MetadataReference.CreateFromImage(newer));
        var surface = ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory, landedDirectory);
        Assert.True(surface.Carries(sibling), "precondition: the landed directory is on the surface");
        Assert.False(surface.IsPlatformBound(sibling), "a landed directory's copy is not what the platform binds");

        var reLanding = ModulePlatformLink.Check(module, "MeshWeaver.Test.ReLandingPack",
            Closure("MeshWeaver.Test.ReLandingPack", sibling), surface);
        var foreign = ModulePlatformLink.Check(module, "MeshWeaver.Test.ReLandingPack",
            Closure("MeshWeaver.Test.ReLandingPack"), surface);

        Assert.Equal(ModuleLinkState.Linkable, reLanding.State);
        Assert.Equal(ModuleLinkState.Unlinkable, foreign.State);
        Assert.Contains(foreign.MissingTypes, m => m.StartsWith("MeshWeaver.Test.NewApi", StringComparison.Ordinal));
    }

    // ────────────────────────────────── both directions of the incident, on the real paths

    /// <summary>
    /// Direction (ii), the landing path, on the RUNNING process: this test process carries the
    /// real YamlDotNet in its application directory; a module bound to a stand-in at 99.0.0.0 —
    /// shipping that stand-in beside it, as the 09-11 bundle did — is REFUSED at landing, before
    /// any byte touches the disk, with a sentence that names the assembly and the exception.
    /// </summary>
    [Fact]
    public async Task AModuleBoundAboveTheRunningPlatformsVersion_IsRefusedAtLanding()
    {
        var running = ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory);
        Assert.True(running.Carries(Yaml), "precondition: this process's application directory carries YamlDotNet");
        Assert.True(running.IsPlatformBound(Yaml));
        var carried = running.IdentityOf(Yaml);
        Assert.NotNull(carried?.Version);
        var above = new Version(carried!.Version!.Major + 1, 0, 0, 0).ToString(4);

        const string name = "MeshWeaver.Test.LandingAiPack";
        var standIn = YamlStandIn(above);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await landing.LandModule(name, [(name + ".dll", ModuleAgainst(standIn, name)), (Yaml + ".dll", standIn)])
                .Timeout(TestTimeouts.Convergence).Await());

        Assert.Contains(Yaml + " " + above, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("FileLoadException", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(ModuleActivationSidecar.Read(root).Entries);
    }

    /// <summary>The positive control on the same path: a module bound BELOW the running copy's
    /// version lands — the loader rolls forward, and the drift is a report, not a refusal.</summary>
    [Fact]
    public async Task AModuleBoundBelowTheRunningPlatformsVersion_Lands()
    {
        var running = ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory);
        Assert.True(running.Carries(Yaml), "precondition: this process's application directory carries YamlDotNet");

        const string name = "MeshWeaver.Test.RollingForwardAiPack";
        var standIn = YamlStandIn("1.0.0.0");
        await landing.LandModule(name, [(name + ".dll", ModuleAgainst(standIn, name)), (Yaml + ".dll", standIn)])
            .Timeout(TestTimeouts.Convergence).Await();

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal(name, entry.Name);
        Assert.True(entry.Enabled);
    }

    /// <summary>
    /// The boot probe's shape (<c>Check(path, surface)</c>, the closure being the DLLs beside the
    /// entry): the module's own YamlDotNet copy sits beside it on disk and the running process
    /// carries the real one — the verdict is the hard one, and it is what
    /// <c>MeshBuilder.TryLoad</c> parks the generation on.
    /// </summary>
    [Fact]
    public void ALandedGenerationBoundAboveTheRunningPlatformsVersion_IsParkedAtBoot()
    {
        var running = ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory);
        Assert.True(running.Carries(Yaml), "precondition: this process's application directory carries YamlDotNet");
        var above = new Version(running.IdentityOf(Yaml)!.Version!.Major + 1, 0, 0, 0).ToString(4);

        const string name = "MeshWeaver.Test.BootAiPack";
        var standIn = YamlStandIn(above);
        var entry = Write(name, ModuleAgainst(standIn, name));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(entry)!, Yaml + ".dll"), standIn);

        var verdict = ModulePlatformLink.Check(entry, running);

        Assert.Equal(ModuleLinkState.BindingConflict, verdict.State);
        Assert.False(verdict.MayLoad);
        var parked = IncompatibleModule.FromLinkRefusal(entry, verdict);
        Assert.Contains(Yaml + " " + above, parked.Report(), StringComparison.Ordinal);
        Assert.True(parked.RefusedBeforeLoad);
    }

    // ──────────────────────────────────────────── the loader itself, as the rule's control

    /// <summary>
    /// 🚨 The rule is the LOADER's, measured — not a policy. An UNSIGNED dependency at 1.0.0.0
    /// already loaded in a context, and a consumer whose bytes ask for 9.0.0.0: the loaded copy is
    /// REFUSED — the first call that touches it throws, never returns the copy's answer. The
    /// spelling of the refusal is the context's: in the Default context, where the copy is the
    /// TPA's, it is <see cref="FileLoadException"/> <c>0x80131040</c> (the 09-11 pod logs); in a
    /// custom context the refused candidate is skipped, nothing else is found, and it is
    /// <see cref="FileNotFoundException"/> naming the 9.0.0.0 request. The other way round
    /// (9.0.0.0 loaded, 1.0.0.0 asked for) binds and runs. The probe's asymmetry is exactly this,
    /// and a strong name is not what triggers it — which is why the comparison applies to every
    /// carried assembly and not only to signed ones.
    /// </summary>
    [Fact]
    public void TheLoaderRefusesAHigherReference_AndRollsForwardALowerOne_UnsignedIncluded()
    {
        const string dep = "MeshWeaver.Test.VersionedDep";
        var v1 = Emit(dep, "1.0.0.0", "namespace MeshWeaver.Test; public static class Api { public static int Answer() => 42; }");
        var v9 = Emit(dep, "9.0.0.0", "namespace MeshWeaver.Test; public static class Api { public static int Answer() => 42; }");
        var askingFor9 = Emit("MeshWeaver.Test.AskingFor9", "1.0.0.0",
            "public static class View { public static int Render() => MeshWeaver.Test.Api.Answer(); }",
            MetadataReference.CreateFromImage(v9));
        var askingFor1 = Emit("MeshWeaver.Test.AskingFor1", "1.0.0.0",
            "public static class View { public static int Render() => MeshWeaver.Test.Api.Answer(); }",
            MetadataReference.CreateFromImage(v1));

        var refused = Invoke(v1, askingFor9);
        var rolledForward = Invoke(v9, askingFor1);

        var refusal = Assert.IsAssignableFrom<Exception>(refused);
        Assert.True(refusal is FileLoadException or FileNotFoundException,
            $"the loader must REFUSE the 1.0.0.0 copy for a 9.0.0.0 reference, not answer from it: {refusal}");
        Assert.Contains("9.0.0.0", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(42, rolledForward);

        // And the probe agrees with the loader on both, from the same bytes.
        var above = ModulePlatformLink.Check(askingFor9, "MeshWeaver.Test.AskingFor9",
            Closure("MeshWeaver.Test.AskingFor9", dep), ModulePlatformSurface.OfFiles([Write(dep, v1)]));
        var below = ModulePlatformLink.Check(askingFor1, "MeshWeaver.Test.AskingFor1",
            Closure("MeshWeaver.Test.AskingFor1", dep), ModulePlatformSurface.OfFiles([Write(dep, v9)]));
        Assert.Equal(ModuleLinkState.BindingConflict, above.State);
        Assert.Equal(ModuleLinkState.Linkable, below.State);
        Assert.Single(below.Advisories);
    }

    /// <summary>Loads <paramref name="dependency"/> into a fresh collectible context, then
    /// <paramref name="consumer"/>, and invokes <c>View.Render()</c>: the value it returns, or the
    /// exception the loader threw (unwrapped from reflection's envelope).</summary>
    private static object? Invoke(byte[] dependency, byte[] consumer)
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext(
            "loader-control-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var dependencyStream = new MemoryStream(dependency);
            context.LoadFromStream(dependencyStream);
            using var consumerStream = new MemoryStream(consumer);
            var render = context.LoadFromStream(consumerStream).GetType("View")!.GetMethod("Render")!;
            try
            {
                return render.Invoke(null, null);
            }
            catch (Exception exception)
            {
                while (exception is System.Reflection.TargetInvocationException { InnerException: { } inner })
                    exception = inner;
                return exception;
            }
        }
        finally { context.Unload(); }
    }

    // ───────────────────────────────────────────────────────────────────────── harness

    private static HashSet<string> Closure(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>A stand-in <c>YamlDotNet</c> carrying <see cref="YamlType"/> — a type name the
    /// real one has too — at the given manifest version, optionally signed with a public key
    /// (public-sign: the key goes into the manifest, no private half needed).</summary>
    private static byte[] YamlStandIn(string version, byte[]? publicKey = null) =>
        Emit(Yaml, version, """
            namespace YamlDotNet.Serialization;
            public sealed class Deserializer { public T Deserialize<T>(string yaml) => default!; }
            """, publicKey: publicKey);

    /// <summary>A module compiled against the given stand-in, using <see cref="YamlType"/>.</summary>
    private static byte[] ModuleAgainst(byte[] yamlStandIn, string moduleName) =>
        Emit(moduleName, "1.0.0.0", $$"""
            public static class SkillMarkdown
            {
                public static object Parse(string yaml) => new {{YamlType}}().Deserialize<object>(yaml);
            }
            """, MetadataReference.CreateFromImage(yamlStandIn), excludeReference: Yaml);

    /// <summary>Compiles one source file against this process's reference set, stamping the
    /// manifest version and (optionally) a public key.</summary>
    private static byte[] Emit(
        string assemblyName, string version, string source,
        MetadataReference? extra = null, string? excludeReference = null, byte[]? publicKey = null)
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

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        if (publicKey is not null)
            options = options.WithCryptoPublicKey([.. publicKey]).WithPublicSign(true);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [
                CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]"),
                CSharpSyntaxTree.ParseText(source),
            ],
            platform,
            options);

        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }

    /// <summary>Writes an assembly into its OWN directory under the test root.</summary>
    private string Write(string assemblyName, byte[] bytes)
    {
        var directory = Path.Combine(root, "emitted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, assemblyName + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

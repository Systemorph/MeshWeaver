using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the one decision that makes adopting a prebuilt assembly safe: <b>an assembly is adopted
/// only when it was built against THIS framework's content.</b>
///
/// <para><b>Why this is guarded and not merely documented.</b> <c>FrameworkVersion</c> is
/// MeshWeaver.Graph's MVID — a content identity — and the assembly-store key carries its first
/// eight characters. So seeding bytes built against a different framework writes them under the
/// LIVE framework's tag, where <c>TryGetAssemblyPath</c> reports them as a usable build and
/// <c>HasUsableBuild</c> stops the compile that was needed. The mismatch then surfaces as a
/// <c>TypeLoadException</c> inside a collectible ALC at activation: no compile error, no overlay,
/// nothing to grep. A too-permissive gate here does not degrade to "recompiles more than
/// necessary" — it degrades to a portal that will not come up, for a reason nothing reports.</para>
///
/// <para>Declining, by contrast, costs one compile — exactly what happens today.</para>
/// </summary>
public class PrebuiltAssemblySeederGateTest
{
    [Fact]
    public void AbsentIdentityDeclines()
    {
        // A producer that predates MVID recording emits no identity. "It came from our CI" is not
        // evidence of ABI compatibility, so absence must decline rather than default to trust.
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason(null));
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason(string.Empty));
    }

    [Fact]
    public void DifferentIdentityDeclines()
    {
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason("00000000000000000000000000000000"));
    }

    [Fact]
    public void MatchingIdentityIsAdopted()
    {
        // The live value, so the test cannot pass by matching a hard-coded constant that has since
        // moved — this repo's whole framework identity changes whenever Graph's content does.
        Assert.Null(PrebuiltAssemblySeeder.DeclineReason(NodeTypeCompilationHelpers.FrameworkVersion));
    }

    [Fact]
    public void ComparisonIsExactNotCaseInsensitiveOrPrefix()
    {
        var live = NodeTypeCompilationHelpers.FrameworkVersion;

        // The store's FrameworkTag is FrameworkVersion[..8]. A prefix match here would adopt any
        // assembly whose framework merely shares that tag — which is precisely the collision the
        // full MVID exists to rule out.
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason(live[..8]));
        Assert.NotNull(PrebuiltAssemblySeeder.DeclineReason(live.ToUpperInvariant()));
    }

    // ── IsAlreadyAdopted: the "need we adopt at all" half ────────────────────────────────────────
    //
    // DeclineReason answers "must we NOT adopt"; IsAlreadyAdopted answers "would adopting change
    // anything". Without the second, every boot re-adopts every bundle entry it holds — and
    // adoption is not bookkeeping: it activates the type's per-node hub, re-uploads the bytes and
    // writes the node. On memex-cloud 2026-08-17 that was 43 activations / 43 uploads / 43 writes,
    // 13.5 s of a 101 s boot, to establish that nothing had changed.
    //
    // "Already built" delegates to NodeTypeBakeStatus.Classify — the one definition of that in the
    // framework — so the seeder skips PRECISELY what the bake would call Baked. A too-permissive
    // answer here does NOT degrade to "a bit stale": it silently skips an adoption that was needed.

    private static NodeTypeDefinition Adopted() => new()
    {
        CompilationStatus = CompilationStatus.Ok,
        LastCompiledVersion = 7,
        CompiledFrameworkVersion = PrebuiltAssemblySeeder.LiveFrameworkMvid,
        LatestAssemblyCollection = "nodetype-cache",
        LatestAssemblyPath = "Some/Type/v7-abc.dll",
    };

    [Fact]
    public void AnAdoptedRecordWhoseBytesAreOnTheStoreNeedsNoReAdoption() =>
        Assert.True(PrebuiltAssemblySeeder.IsAlreadyAdopted(Adopted(), storeHasBytes: true, null));

    [Fact]
    public void AClearedStoreReAdopts() =>
        // 🚨 The BytesMissing trap: the record is pristine and the bytes are gone (a cleared,
        // remounted or stale-restored assembly volume). Skipping on the record's word alone would
        // leave the type permanently unbuilt — the decision must stay level-triggered on the store.
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(Adopted(), storeHasBytes: false, null));

    [Fact]
    public void AnotherFrameworksStampReAdoptsWhenTheStoreHasNothing() =>
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            Adopted() with { CompiledFrameworkVersion = "deadbeefdeadbeefdeadbeefdeadbeef" },
            storeHasBytes: false, null));

    [Fact]
    public void ARecordSittingAtErrorReAdopts() =>
        // PreviouslyBroken beats every other state in Classify, bytes or no bytes.
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            Adopted() with { CompilationStatus = CompilationStatus.Error },
            storeHasBytes: true, null));

    [Fact]
    public void ARecordWithNoAssemblyCoordinatesReAdopts()
    {
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            Adopted() with { LatestAssemblyPath = null }, storeHasBytes: true, null));
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            Adopted() with { LatestAssemblyCollection = null }, storeHasBytes: true, null));
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            Adopted() with { LastCompiledVersion = null }, storeHasBytes: true, null));
    }

    [Fact]
    public void ADifferentDependencyRecordReAdopts()
    {
        // The bundle would REPLACE CompiledDependencies, so this is not a no-op — a rebuilt module
        // closure has to land even though the framework identity and the bytes are untouched
        // (#1707 slice 2). This is the one conjunct IsAlreadyAdopted adds on top of Classify.
        var stamped = Adopted() with
        {
            CompiledDependencies = System.Collections.Immutable.ImmutableSortedDictionary
                .CreateRange(StringComparer.Ordinal,
                    [new KeyValuePair<string, string>("Custom.Module", "mvid:one")]),
        };

        Assert.True(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            stamped, true, new Dictionary<string, string> { ["Custom.Module"] = "mvid:one" }));
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            stamped, true, new Dictionary<string, string> { ["Custom.Module"] = "mvid:two" }));
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            stamped, true, new Dictionary<string, string>
            {
                ["Custom.Module"] = "mvid:one",
                ["Other.Module"] = "mvid:three",
            }));
        // A record the bundle would ADD where none is stamped is still a change.
        Assert.False(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            Adopted(), true, new Dictionary<string, string> { ["Custom.Module"] = "mvid:one" }));
    }

    [Fact]
    public void ALegacyBundleWithNoDependencyRecordStampsNothingAndSoChangesNothing() =>
        // Seed leaves any prior stamp untouched for a legacy bundle, so a stamped record is not
        // evidence that re-seeding would differ.
        Assert.True(PrebuiltAssemblySeeder.IsAlreadyAdopted(
            Adopted() with
            {
                CompiledDependencies = System.Collections.Immutable.ImmutableSortedDictionary
                    .CreateRange(StringComparer.Ordinal,
                        [new KeyValuePair<string, string>("Custom.Module", "mvid:one")]),
            },
            storeHasBytes: true, null));

    [Fact]
    public void DeclineReasonNamesBothIdentities()
    {
        // The reason is the only breadcrumb when a package silently keeps recompiling, so it has to
        // carry what was built against AND what is live — "declined" alone sends the next person
        // looking at the compiler.
        var reason = PrebuiltAssemblySeeder.DeclineReason("deadbeefdeadbeefdeadbeefdeadbeef");

        Assert.NotNull(reason);
        Assert.Contains("deadbeefdeadbeefdeadbeefdeadbeef", reason);
        Assert.Contains(NodeTypeCompilationHelpers.FrameworkVersion, reason);
    }
}

#pragma warning disable CS1591

using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the per-type DEPENDENCY RECORD (#1707 slice 2): the pure record computation over an
/// emitted assembly's references, the resolver precedence (module MVID over platform surface),
/// the adopt-time validation, and — the point of the slice — that <c>HasUsableBuild</c> /
/// <c>HasStaleFrameworkBuild</c> / the bake probe's <c>Classify</c> judge a record-stamped build
/// by ITS OWN dependencies instead of by the instance-wide modules fingerprint, so a module
/// update invalidates only its dependents and instance composition stops keying anything.
/// </summary>
public class CompiledDependencyRecordTest
{
    private const string Toolchain = "mvid:toolchain-1";

    private static Func<string, string?> Resolver(params (string Name, string Id)[] pairs)
        => name => pairs.FirstOrDefault(p => p.Name == name).Id;

    // ---- the record computation ----------------------------------------------------------------

    [Fact]
    public void Compute_FiltersOutOfScope_DedupesAndIncludesTheToolchainKey()
    {
        var record = CompiledDependencies.Compute(
            ["System.Runtime", "MeshWeaver.Layout", "MeshWeaver.Layout", "Custom.Module", null],
            Resolver(("MeshWeaver.Layout", "ref:aaa"), ("Custom.Module", "mvid:bbb")),
            Toolchain);

        record.Keys.Should().Equal(
            CompiledDependencies.ToolchainKey, "Custom.Module", "MeshWeaver.Layout");
        record[CompiledDependencies.ToolchainKey].Should().Be(Toolchain);
        record["MeshWeaver.Layout"].Should().Be("ref:aaa");
        record["Custom.Module"].Should().Be("mvid:bbb");
    }

    [Fact]
    public void FindMismatch_IsNullWhenEveryEntryStillResolvesTheSameId()
    {
        var record = CompiledDependencies.Compute(
            ["MeshWeaver.Layout"], Resolver(("MeshWeaver.Layout", "ref:aaa")), Toolchain);

        CompiledDependencies.FindMismatch(
                record, Resolver(("MeshWeaver.Layout", "ref:aaa")), Toolchain)
            .Should().BeNull();
    }

    [Fact]
    public void FindMismatch_NamesTheDriftedDependency()
    {
        var record = CompiledDependencies.Compute(
            ["MeshWeaver.Layout"], Resolver(("MeshWeaver.Layout", "ref:aaa")), Toolchain);

        CompiledDependencies.FindMismatch(
                record, Resolver(("MeshWeaver.Layout", "ref:CHANGED")), Toolchain)
            .Should().Contain("MeshWeaver.Layout").And.Contain("ref:aaa").And.Contain("ref:CHANGED");
    }

    [Fact]
    public void FindMismatch_AnEntryTheEnvironmentCannotClassifyIsAMismatch()
    {
        // The build binds a module this deployment does not have — not provably compatible.
        var record = CompiledDependencies.Compute(
            ["Custom.Module"], Resolver(("Custom.Module", "mvid:bbb")), Toolchain);

        CompiledDependencies.FindMismatch(record, Resolver(), Toolchain)
            .Should().Contain("Custom.Module").And.Contain(CompiledDependencies.AbsentId);
    }

    [Fact]
    public void FindMismatch_AToolchainChangeInvalidatesEveryRecord()
    {
        var record = CompiledDependencies.Compute([], Resolver(), Toolchain);

        CompiledDependencies.FindMismatch(record, Resolver(), "mvid:toolchain-2")
            .Should().Contain(CompiledDependencies.ToolchainKey);
    }

    [Fact]
    public void FindMismatch_ARecordWithoutTheToolchainKeyIsNeverTrusted()
    {
        // FindMismatch compares only PRESENT entries, so a record lacking the reserved toolchain
        // entry would validate forever across toolchain changes — fail-open. It must decline
        // (Copilot finding on #1719); Compute always writes the key, so only an empty or
        // hand-assembled record can hit this.
        var handAssembled = System.Collections.Immutable.ImmutableSortedDictionary<string, string>
            .Empty.Add("MeshWeaver.Layout", "ref:aaa");

        CompiledDependencies.FindMismatch(
                handAssembled, Resolver(("MeshWeaver.Layout", "ref:aaa")), Toolchain)
            .Should().Contain(CompiledDependencies.ToolchainKey,
                "a record that cannot invalidate on toolchain changes must not validate at all");
    }

    // ---- the resolver ---------------------------------------------------------------------------

    [Fact]
    public void IdResolver_ModuleMvidWinsOverPlatformSurface_AndSchemesNeverCollide()
    {
        var resolver = CompiledDependencies.CreateIdResolver(
            surfaceByName: new Dictionary<string, string> { ["MeshWeaver.Layout"] = "aaa" },
            moduleMvidByName: new Dictionary<string, string> { ["MeshWeaver.Layout"] = "aaa" },
            implMvidOf: _ => null);

        // Same raw id, but the module resolution wins and the scheme prefix keeps mvid:aaa from
        // ever comparing equal to ref:aaa.
        resolver("MeshWeaver.Layout").Should().Be(CompiledDependencies.MvidScheme + "aaa");
    }

    [Fact]
    public void IdResolver_PlatformFallsBackToMvid_ThenAbsent_AndNonMeshWeaverIsOutOfScope()
    {
        var resolver = CompiledDependencies.CreateIdResolver(
            surfaceByName: new Dictionary<string, string>(),
            moduleMvidByName: new Dictionary<string, string>(),
            implMvidOf: name => name == "MeshWeaver.Data" ? "ddd" : null);

        resolver("MeshWeaver.Data").Should().Be(CompiledDependencies.MvidScheme + "ddd");
        resolver("MeshWeaver.Layout").Should().Be(CompiledDependencies.AbsentId);
        resolver("System.Runtime").Should().BeNull();
    }

    // ---- the adopt-time judges ------------------------------------------------------------------

    private static NodeTypeDefinition UsableDef(
        System.Collections.Immutable.ImmutableSortedDictionary<string, string>? record,
        string? modulesHash = null)
        => new()
        {
            LatestAssemblyCollection = "local",
            LatestAssemblyPath = "x/v1-abc.dll",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            CompiledModulesHash = modulesHash,
            CompiledDependencies = record,
            LastCompiledVersion = 1,
        };

    private static readonly MeshNode Node = new("T", "Test");

    [Fact]
    public void HasUsableBuild_TheRecordDecides_AndTheInstanceFingerprintStopsMattering()
    {
        var record = CompiledDependencies.Compute(
            ["Custom.Module"], Resolver(("Custom.Module", "mvid:v1")), Toolchain);
        // The instance fingerprint DIFFERS — under the legacy rule that alone invalidated. With a
        // record present the build's own dependencies decide.
        var guards = new NodeTypeCompilationHelpers.BuildGuards(
            ModulesHash: "some-other-composition",
            DependencyIdOf: Resolver(("Custom.Module", "mvid:v1")),
            ToolchainId: Toolchain);

        NodeTypeCompilationHelpers.HasUsableBuild(Node, UsableDef(record, "stamped-hash"), guards)
            .Should().BeTrue("every dependency the build binds still resolves identically");

        var drifted = guards with { DependencyIdOf = Resolver(("Custom.Module", "mvid:v2")) };
        NodeTypeCompilationHelpers.HasUsableBuild(Node, UsableDef(record, "stamped-hash"), drifted)
            .Should().BeFalse("the module this type binds was updated");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(UsableDef(record, "stamped-hash"), drifted)
            .Should().BeTrue("the stale twin must re-drive the compile for the same condition");
    }

    [Fact]
    public void HasUsableBuild_LegacyModulesHashRule_GovernsNullRecordStamps()
    {
        var guards = new NodeTypeCompilationHelpers.BuildGuards(
            ModulesHash: "live-hash",
            DependencyIdOf: Resolver(),
            ToolchainId: Toolchain);

        NodeTypeCompilationHelpers.HasUsableBuild(Node, UsableDef(null, "live-hash"), guards)
            .Should().BeTrue();
        NodeTypeCompilationHelpers.HasUsableBuild(Node, UsableDef(null, "old-hash"), guards)
            .Should().BeFalse("a null record keeps the pre-#1707 whole-set rule");
        NodeTypeCompilationHelpers.HasUsableBuild(Node, UsableDef(null, null), guards)
            .Should().BeTrue("a null stamp is grandfathered, exactly as before");
    }

    [Fact]
    public void Classify_DependencyStale_BeatsTheBytesWinRule()
    {
        var record = CompiledDependencies.Compute(
            ["Custom.Module"], Resolver(("Custom.Module", "mvid:v1")), Toolchain);
        var def = UsableDef(record);

        // The store HAS bytes under the live framework tag — but the record says those bytes bind
        // a module build this environment no longer runs. Bytes must NOT win: the store key
        // cannot see the record.
        NodeTypeBakeStatus.Classify(
                def, storeHasBytes: true,
                NodeTypeCompilationHelpers.FrameworkVersion,
                Resolver(("Custom.Module", "mvid:v2")), Toolchain)
            .Should().Be(BakeState.DependencyStale);

        // A matching record keeps the bytes-win premise intact.
        NodeTypeBakeStatus.Classify(
                def, storeHasBytes: true,
                NodeTypeCompilationHelpers.FrameworkVersion,
                Resolver(("Custom.Module", "mvid:v1")), Toolchain)
            .Should().Be(BakeState.Baked);

        // No resolver (legacy caller) — record ignored, prior behavior unchanged.
        NodeTypeBakeStatus.Classify(
                def, storeHasBytes: true, NodeTypeCompilationHelpers.FrameworkVersion)
            .Should().Be(BakeState.Baked);
    }
}

#pragma warning disable CS1591

using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 MeshWeaver#6831 (review finding on #6838) — the module version a registry states for the bytes
/// a record names is recorded WITH those bytes' identity, so the two transitions that used to relabel
/// a compiled build cannot:
/// <list type="bullet">
///   <item>adopt 1.0.1 → compile 1.1.1 here → source moves to 1.1.2 with no bundle, so the delivery
///     hold settles the record <c>StaleAdopted</c> without touching the bytes. Read off the
///     provenance, the 1.1.1 bytes were labelled with the surviving <c>AdoptedModuleVersion</c>,
///     1.0.1.</item>
///   <item>compile 1.1.1 → an <c>@@</c>-include-only edit ships as 1.1.2. <c>IsDirty</c> does not
///     see includes, so read off <c>CurrentModuleVersion</c> the 1.1.1 bytes were labelled 1.1.2.</item>
/// </list>
/// Real <see cref="NodeTypeCompilationHelpers.ApplyCompileSuccess"/> over a real assembly file (its
/// MVID is read from disk) and the real <see cref="BuildDeliveryHold.Settle(NodeTypeDefinition, bool, string)"/>.
/// </summary>
public class BuiltModuleVersionFollowsTheBytesTest
{
    private const string AdoptedVersion = "1.0.1";
    private const string CompiledVersion = "1.1.1";
    private const string NextVersion = "1.1.2";
    private const string AdoptedMvid = "00000000-0000-0000-0000-0000000000a1";

    private static readonly ImmutableDictionary<string, long> CompiledSet =
        ImmutableDictionary<string, long>.Empty.Add("T/Source/A.cs", 2);

    /// <summary>A record whose current build is an adoption of the 1.0.1 bundle, with the source
    /// it holds already at 1.1.1 (so the next compile builds 1.1.1).</summary>
    private static NodeTypeDefinition AdoptedAt101() => new()
    {
        Configuration = "config => config",
        BuildProvenance = BuildProvenance.StaleAdopted,
        AdoptedModuleVersion = AdoptedVersion,
        LatestAssemblyMvid = AdoptedMvid,
        BuiltModuleVersion = AdoptedVersion,
        BuiltModuleVersionMvid = AdoptedMvid,
        CurrentModuleVersion = CompiledVersion,
        CurrentSourceVersions = CompiledSet,
    };

    private static NodeCompilationResult CompiledHere(ImmutableDictionary<string, long> sources) => new(
        AssemblyLocation: typeof(BuildDeliveryHold).Assembly.Location,
        NodeTypeConfigurations: [],
        CompiledSources: sources,
        Collection: "local",
        ContentPath: "T/v3-built.dll",
        Version: 3);

    private static NodeTypeDefinition CompileAt111()
        => NodeTypeCompilationHelpers.ApplyCompileSuccess(
            AdoptedAt101(), CompiledHere(CompiledSet), currentNodeVersion: 3, activityPath: null, releasePath: null,
            compiledInput: AdoptedAt101());

    [Fact]
    public void ACompile_LabelsItsOwnBytesWithTheVersionItCompiled()
    {
        var compiled = CompileAt111();

        Assert.NotEqual(AdoptedMvid, compiled.LatestAssemblyMvid);
        Assert.Equal(compiled.LatestAssemblyMvid, compiled.BuiltModuleVersionMvid);
        Assert.Equal(CompiledVersion, PrebuiltAssemblySeeder.ModuleVersionOfBuild(compiled));
    }

    [Fact]
    public void ADeliveryHoldOverACompiledBuild_KeepsTheCompiledVersion()
    {
        var compiled = CompileAt111();
        var moved = compiled with
        {
            CurrentModuleVersion = NextVersion,
            CurrentSourceVersions = CompiledSet.SetItem("T/Source/A.cs", 3),
        };

        var held = BuildDeliveryHold.Settle(moved, hasUsableBuild: true, "awaiting the 1.1.2 bundle");

        Assert.NotNull(held);
        Assert.True(BuildDeliveryHold.IsHeld(held), "the record must be in a delivery hold for this case to say anything");
        Assert.NotEqual(BuildProvenance.Compiled, held!.BuildProvenance);
        Assert.Equal(AdoptedVersion, held.AdoptedModuleVersion);
        Assert.Equal(CompiledVersion, PrebuiltAssemblySeeder.ModuleVersionOfBuild(held));
    }

    [Fact]
    public void AnIncludeOnlyEdit_DoesNotRelabelTheCompiledBytes()
    {
        var compiled = CompileAt111();
        // The include moved, the type's own source set did not: IsDirty stays false.
        var includeOnly = compiled with { CurrentModuleVersion = NextVersion };

        Assert.False(includeOnly.IsDirty);
        Assert.Equal(CompiledVersion, PrebuiltAssemblySeeder.ModuleVersionOfBuild(includeOnly));
    }

    [Fact]
    public void ACompileWhoseSnapshotMovedUnderIt_RecordsTheVersionAsUnknown()
    {
        var movedDuringCompile = AdoptedAt101() with
        {
            CurrentModuleVersion = NextVersion,
            CurrentSourceVersions = CompiledSet.SetItem("T/Source/A.cs", 3),
        };

        var compiled = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            movedDuringCompile, CompiledHere(CompiledSet), currentNodeVersion: 3, activityPath: null, releasePath: null,
            compiledInput: AdoptedAt101());

        Assert.Null(PrebuiltAssemblySeeder.ModuleVersionOfBuild(compiled));
    }

    /// <summary>
    /// Review thread 4287471990: a configuration- or include-only update to 1.1.2 lands WHILE the
    /// 1.1.1 compile runs. The source-version dictionaries exclude both, so the compiled set still
    /// equals the live one and the type is not dirty — and reading the version off the LIVE record
    /// at the stamp labelled the 1.1.1 bytes 1.1.2. The compile read one side of the update or the
    /// other and nothing records which, so the version is unknown.
    /// </summary>
    [Theory]
    [InlineData("configuration")]
    [InlineData("include")]
    public void AnUpdateLandingDuringTheCompile_IsNeverAttachedToTheBytes(string moved)
    {
        var input = AdoptedAt101() with
        {
            CurrentSourceFingerprint = "fp-111",
            CurrentSourceIncludes = ["T/Shared/Inc.cs"],
        };
        var live = moved == "configuration"
            ? input with { CurrentModuleVersion = NextVersion, Configuration = "config => config.WithType<int>()" }
            : input with { CurrentModuleVersion = NextVersion, CurrentSourceFingerprint = "fp-112" };
        Assert.False((live with { CompiledSources = CompiledSet }).IsDirty,
            "the case only exists when the source dictionaries cannot see the update");

        var compiled = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            live, CompiledHere(CompiledSet), currentNodeVersion: 3, activityPath: null, releasePath: null,
            compiledInput: input);

        Assert.Equal(compiled.LatestAssemblyMvid, compiled.BuiltModuleVersionMvid);
        Assert.NotEqual(NextVersion, PrebuiltAssemblySeeder.ModuleVersionOfBuild(compiled));
        Assert.Null(PrebuiltAssemblySeeder.ModuleVersionOfBuild(compiled));
    }

    /// <summary>The control for the theory above: with no input moving during the compile, the
    /// compiled input's version IS the bytes' version.</summary>
    [Fact]
    public void ACompileNothingMovedUnder_StatesTheInputsVersion()
    {
        var input = AdoptedAt101() with { CurrentSourceFingerprint = "fp-111" };

        var compiled = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            input, CompiledHere(CompiledSet), currentNodeVersion: 3, activityPath: null, releasePath: null,
            compiledInput: input);

        Assert.Equal(CompiledVersion, PrebuiltAssemblySeeder.ModuleVersionOfBuild(compiled));
    }

    /// <summary>
    /// Review thread 4291278201: a record ADOPTED at 1.0.1 before the version was keyed to the bytes
    /// (no <c>BuiltModuleVersionMvid</c>) is compiled here by the contract handler's store-miss or
    /// foreign-identity branch. That writer names the new bytes and keeps the adopted provenance and
    /// <c>AdoptedModuleVersion</c>, so the historical fallback advertised the locally compiled bytes
    /// as 1.0.1. It must replace the adoption's claim — with unknown, since it cannot establish one.
    /// </summary>
    [Fact]
    public void ALocalCompileAfterAnOldAdoption_NoLongerAdvertisesTheAdoptedVersion()
    {
        var historical = new NodeTypeDefinition
        {
            Configuration = "config => config",
            BuildProvenance = BuildProvenance.AdoptedVerified,
            AdoptedModuleVersion = AdoptedVersion,
            LatestAssemblyMvid = AdoptedMvid,
            CurrentModuleVersion = CompiledVersion,
            CurrentSourceVersions = CompiledSet,
            LastCompiledVersion = 2,
        };
        Assert.Equal(AdoptedVersion, PrebuiltAssemblySeeder.ModuleVersionOfBuild(historical));

        var localCompile = new MeshWeaver.Mesh.GetCompilationPathResponse(
                Success: true,
                AssemblyLocation: typeof(BuildDeliveryHold).Assembly.Location,
                Collection: "local",
                Version: "3",
                Error: null,
                HubConfiguration: null)
            { ContentPath = "T/v3-built.dll" };
        var stamped = NodeTypeContractHandler.ApplyResolvedSuccess(
            historical, localCompile, freshCompile: true,
            new MeshWeaver.Mesh.MeshNode("T", "P") { Content = historical });

        Assert.NotEqual(AdoptedMvid, stamped.LatestAssemblyMvid);
        Assert.Equal(BuildProvenance.AdoptedVerified, stamped.BuildProvenance);
        Assert.Null(PrebuiltAssemblySeeder.ModuleVersionOfBuild(stamped));
    }

    /// <summary>The control: the same writer HYDRATING the bytes the record already names keeps the
    /// version pair (and so stays the #2895 no-op).</summary>
    [Fact]
    public void AHydrateOfTheNamedBytes_KeepsTheirVersion()
    {
        var compiled = CompileAt111();
        var hydrate = new MeshWeaver.Mesh.GetCompilationPathResponse(
                Success: true,
                AssemblyLocation: typeof(BuildDeliveryHold).Assembly.Location,
                Collection: "local",
                Version: "3",
                Error: null,
                HubConfiguration: null)
            { ContentPath = "T/v3-built.dll" };

        var stamped = NodeTypeContractHandler.ApplyResolvedSuccess(
            compiled, hydrate, freshCompile: false, new MeshWeaver.Mesh.MeshNode("T", "P") { Content = compiled });

        Assert.Equal(CompiledVersion, PrebuiltAssemblySeeder.ModuleVersionOfBuild(stamped));
    }

    [Fact]
    public void NewBytesNamedWithoutTheirVersion_ReadAsUnknown_NeverAsTheOldBytesVersion()
    {
        var renamed = AdoptedAt101() with { LatestAssemblyMvid = "00000000-0000-0000-0000-0000000000b2" };

        Assert.Null(PrebuiltAssemblySeeder.ModuleVersionOfBuild(renamed));
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A module refused by plan tier says so on the instance (#4097).</b>
///
/// <para><b>The hour these pin — 2026-09-12, <c>build.meshweaver.cloud</c>.</b> A fresh instance
/// on the free plan showed <c>canPatch=False</c> ("list MeshWeaver.SelfUpdate.Aks under
/// Modules:Assemblies"), <c>required_modules: Degraded — install the package from the registry</c>,
/// and a package card that simply did not exist. The cause was one line on the REGISTRY: the
/// <c>Hosting</c> package (<c>preInstalled: true</c>, <c>tier: enterprise</c>) was refused by
/// <c>PluginGrant.Allows → PlanTierRanks.CoversInstance</c>, and by design that refusal was
/// indistinguishable from "no such bundle" on the wire. One cause, three consequences, nothing on
/// the instance naming it.</para>
///
/// <para><b>The two halves.</b> The REGISTRY side returns a TYPED refusal for a package it itself
/// declares in the instance's default set from a source the grant reaches — and stays silent,
/// exactly as before, for a package outside the granted sources (the enumeration defence). The
/// CONSUMER side records the verdict on the activation record and says the one sentence in the
/// #4091 vocabulary on every surface that used to name the consequence.</para>
/// </summary>
public class PlanTierRefusalTest : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The Store's ladder as the registry reads it: free 0 · personal 10 · pro 20 ·
    /// dedicated 25 (all-access) · enterprise 30.</summary>
    private static readonly PlanTierRanks Ladder = new(
        ImmutableDictionary<string, int>.Empty
            .Add("free", 0).Add("personal", 10).Add("pro", 20).Add("dedicated", 25).Add("enterprise", 30),
        ImmutableHashSet.Create("dedicated"));

    private const string Platform = "Plugins";
    private const string Paid = "Reinsurance";
    private const string Patcher = "MeshWeaver.SelfUpdate.Aks";

    private static PluginGrantEntry Entry(string source, string package = PluginGrantEntry.AllPackages, string? cap = null) =>
        new() { Source = source, PackageId = package, Tier = cap };

    private static AuthenticatedInstance Instance(string? plan, params PluginGrantEntry[] entries) =>
        new(new MeshWeaverInstance { InstanceId = "build", KeyHash = "hash", Plan = plan },
            new PluginGrant { InstanceId = "build", Entries = entries })
        { Ranks = Ladder };

    /// <summary>The Hosting package as MeshWeaver.Plugins' <c>Hosting/index.json</c> declares it.</summary>
    private static readonly PackageManifest Hosting = new()
    {
        Id = "Hosting", Name = "Hosting", Module = Patcher, PreInstalled = true, Tier = "enterprise",
    };

    private static readonly PackageManifest Store = new() { Id = "Store", Name = "Store", PreInstalled = true };

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-tierrefusal-" + Guid.NewGuid().ToString("N"));

    public PlanTierRefusalTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    // ───────────────────────────────────────────── the registry side: the grant's verdict

    /// <summary>
    /// The incident's exact shape: a free instance holding <c>Plugins/*</c>, and the enterprise
    /// <c>Hosting</c> package the registry declares in its default set. Allowed? No. Refused BY
    /// PLAN, and said so — with the plan the decision was taken at.
    /// </summary>
    [Fact]
    public void AFreeInstance_IsRefusedTheEnterprisePreInstall_ByPlan_AndToldSo()
    {
        var caller = Instance("free", Entry(Platform));

        Assert.False(caller.Allows(Platform, Hosting.Id, Hosting.Tier, Now));

        var refusal = caller.TierRefusal(Platform, Hosting.Id, Hosting.Tier, Hosting.Module, Now);
        Assert.NotNull(refusal);
        Assert.Equal("Hosting", refusal!.PackageId);
        Assert.Equal(Patcher, refusal.Module);
        Assert.Equal("enterprise", refusal.RequiredTier);
        Assert.Equal("free", refusal.InstancePlan);
        Assert.Equal(
            "⛔ Not installed on this instance: Hosting needs plan tier enterprise, this instance is on free.",
            refusal.Describe());
    }

    /// <summary>A record that predates the plan field is a FREE instance, and the sentence says
    /// <c>free</c>, never "(none)".</summary>
    [Fact]
    public void ABlankPlan_IsRefusedAtTheBaseline_AndTheSentenceNamesFree()
    {
        var refusal = Instance(null, Entry(Platform)).TierRefusal(Platform, Hosting.Id, Hosting.Tier, Hosting.Module, Now);
        Assert.Equal("free", refusal!.InstancePlan);
    }

    /// <summary>
    /// 🚨 THE ENUMERATION DEFENCE, unchanged. The same enterprise package from a source the
    /// instance holds NO entry for is absence — null, exactly like a package that does not exist.
    /// A registered instance must not learn what a registry carries by reading its refusals.
    /// </summary>
    [Fact]
    public void AnUngrantedSource_YieldsAbsence_NotARefusal()
    {
        var caller = Instance("free", Entry(Platform));

        Assert.Null(caller.TierRefusal(Paid, "UWDeepfield", "enterprise", null, Now));
        // And the grant itself agrees, before the token scope even enters.
        Assert.Null(caller.Grant.TierRefusal(Paid, "UWDeepfield", "enterprise", Ladder, "free", Now));
    }

    /// <summary>An expired entry reaches nothing: absence, not a refusal it could not decide.</summary>
    [Fact]
    public void AnExpiredEntry_YieldsAbsence()
    {
        var expired = new PluginGrantEntry { Source = Platform, PackageId = PluginGrantEntry.AllPackages, ExpiresAt = Now.AddDays(-1) };
        Assert.Null(Instance("free", expired).TierRefusal(Platform, Hosting.Id, Hosting.Tier, Hosting.Module, Now));
    }

    /// <summary>A revoked grant licenses nothing and explains nothing — the kill switch is silence.</summary>
    [Fact]
    public void ARevokedGrant_YieldsAbsence()
    {
        var caller = new AuthenticatedInstance(
            new MeshWeaverInstance { InstanceId = "build", KeyHash = "hash", Plan = "free" },
            new PluginGrant { InstanceId = "build", Entries = [Entry(Platform)], IsRevoked = true })
        { Ranks = Ladder };
        Assert.Null(caller.TierRefusal(Platform, Hosting.Id, Hosting.Tier, Hosting.Module, Now));
    }

    /// <summary>A plan that covers the package is ALLOWED — no refusal to report.</summary>
    [Theory]
    [InlineData("enterprise")]
    [InlineData("dedicated")]
    public void ACoveringPlan_IsAllowed_AndNothingIsRefused(string plan)
    {
        var caller = Instance(plan, Entry(Platform));
        Assert.True(caller.Allows(Platform, Hosting.Id, Hosting.Tier, Now));
        Assert.Null(caller.TierRefusal(Platform, Hosting.Id, Hosting.Tier, Hosting.Module, Now));
    }

    /// <summary>A baseline package (no tier) is covered by every plan, so a free instance is never
    /// told it is refused the Store.</summary>
    [Fact]
    public void ABaselinePackage_IsNeverRefused()
    {
        Assert.Null(Instance("free", Entry(Platform)).TierRefusal(Platform, Store.Id, Store.Tier, null, Now));
    }

    /// <summary>
    /// 🚨 The sentence names the plan that ACTUALLY refused. A pro instance whose only entry for
    /// the source is capped at free (<c>Plugins/*@free</c>) is decided at free — saying "this
    /// instance is on pro" would send the operator to a plan that is not the problem.
    /// </summary>
    [Fact]
    public void ACappedEntry_ReportsTheNarrowedPlan()
    {
        var refusal = Instance("pro", Entry(Platform, cap: "free"))
            .TierRefusal(Platform, Hosting.Id, Hosting.Tier, Hosting.Module, Now);
        Assert.Equal("free", refusal!.InstancePlan);
    }

    /// <summary>A token can only NARROW: a package outside the presented token's scope is absent
    /// to that token, whatever the durable grant says.</summary>
    [Fact]
    public void ATokenScopeExcludingThePackage_YieldsAbsence()
    {
        var caller = Instance("free", Entry(Platform)) with
        {
            TokenScope = new SyncAccessTokenClaims("build", "hash", ["Plugins/Store"], Now.AddHours(1)),
        };
        Assert.Null(caller.TierRefusal(Platform, Hosting.Id, Hosting.Tier, Hosting.Module, Now));
    }

    // ───────────────────────────────────────────── the registry side: the listing

    /// <summary>
    /// The listing's refusal set, computed exactly as <c>/api/plugins</c> computes it: the
    /// pre-installed package the plan refuses from the GRANTED source is named; the same package
    /// from an UNGRANTED source is not; a granted package is not; a refused package that is NOT
    /// pre-installed is not either — the default set is what the registry chose to tell the
    /// instance about, and nothing else is.
    /// </summary>
    [Fact]
    public void TheListing_NamesOnlyTheDefaultSetRefusals_FromGrantedSources()
    {
        var caller = Instance("free", Entry(Platform));
        var platform = new ConfiguredPackageSource(new NullSource(), "HEAD", Platform);
        var paid = new ConfiguredPackageSource(new NullSource(), "HEAD", Paid);
        var optionalEnterprise = Hosting with { Id = "Optional", Module = null, PreInstalled = false };

        var refused = PluginRegistryEndpoints.TierRefusals(caller,
        [
            (platform, (IReadOnlyList<PackageManifest>)[Store, Hosting, optionalEnterprise]),
            (paid, (IReadOnlyList<PackageManifest>)[Hosting with { Id = "PaidHosting" }]),
        ]);

        var only = Assert.Single(refused);
        Assert.Equal("Hosting", only.PackageId);
        Assert.Equal(Patcher, only.Module);
    }

    /// <summary>The legacy anonymous caller sees everything and is refused nothing.</summary>
    [Fact]
    public void TheAnonymousCaller_HasNoRefusals()
    {
        var platform = new ConfiguredPackageSource(new NullSource(), "HEAD", Platform);
        Assert.Empty(PluginRegistryEndpoints.TierRefusals(null, [(platform, (IReadOnlyList<PackageManifest>)[Hosting])]));
    }

    /// <summary>
    /// The wire is ADDITIVE both ways: the refusal travels beside <c>packages</c> and round-trips
    /// typed; a listing from a registry that predates #4097 (no <c>refused</c> member) parses to
    /// an empty refusal set, never to an error.
    /// </summary>
    [Fact]
    public void ThePayload_CarriesTheRefusalBesideThePackages_AndAnOldRegistryParsesToNone()
    {
        var refusal = new PlanTierRefusal("Hosting", Patcher, "enterprise", "free");
        var json = PluginRegistryPayloads.List([Store], [refusal]);
        Assert.Contains("\"refused\"", json);

        var listing = PluginRegistryPayloads.ParseList(json);
        Assert.Equal("Store", Assert.Single(listing.Packages).Id);
        Assert.Equal(refusal, Assert.Single(listing.Refused));

        var legacy = PluginRegistryPayloads.ParseList("{\"packages\":[{\"id\":\"Store\"}]}");
        Assert.Single(legacy.Packages);
        Assert.Empty(legacy.Refused);

        // No refusals ⇒ the member is not written at all — byte-identical to before #4097.
        Assert.DoesNotContain("refused", PluginRegistryPayloads.List([Store]));
    }

    // ───────────────────────────────────────────── the consumer side

    /// <summary>
    /// Surface 1 — <c>/health</c> <c>required_modules</c>. With the refusal on the activation
    /// record (which is what the host's health check reads), the required patcher module is still
    /// ExpectedLater — no rollout can change a plan — but the reason names the package, the tier
    /// and the plan instead of "install the package from the registry".
    /// </summary>
    [Fact]
    public void TheRequiredModuleProbe_NamesTheTier_InsteadOfTellingTheInstanceToInstall()
    {
        var activation = new ModuleActivationList
        {
            TierRefusals = [new PlanTierRefusal("Hosting", Patcher, "enterprise", "free")],
        };

        var verdicts = RequiredModuleStatus.Classify(
            requiredEntries: [Patcher + ".dll"],
            baselineEntries: [],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvesFromDeployment: _ => false,
            activation: activation,
            landedDllExists: _ => false,
            platformGate: _ => null,
            incompatibleModules: []);

        var expected = Assert.Single(RequiredModuleStatus.ExpectedLater(verdicts));
        Assert.Equal(Patcher, expected.Name);
        Assert.Contains(
            "⛔ Not installed on this instance: Hosting needs plan tier enterprise, this instance is on free.",
            expected.Reason);
        Assert.DoesNotContain("install the package from the registry", expected.Reason);
        Assert.Empty(RequiredModuleStatus.Absent(verdicts));
    }

    /// <summary>The control arm: without a recorded refusal the sentence is the one it always was.</summary>
    [Fact]
    public void TheRequiredModuleProbe_WithoutARefusal_StillSaysInstallFromTheRegistry()
    {
        var verdicts = RequiredModuleStatus.Classify(
            requiredEntries: [Patcher + ".dll"],
            baselineEntries: [],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvesFromDeployment: _ => false,
            activation: new ModuleActivationList(),
            landedDllExists: _ => false,
            platformGate: _ => null,
            incompatibleModules: []);

        Assert.Contains("install the package from the registry", Assert.Single(verdicts).Reason);
    }

    /// <summary>
    /// The record between the default install and the probes: the markers are made to MATCH the
    /// registry's answer of the pass — written, read back onto the activation list (the SAME read
    /// the host's health check performs), and cleared by a later pass in which the registry no
    /// longer refuses (a plan upgrade). A content-only refusal leaves no marker.
    /// </summary>
    [Fact]
    public void TheMarkers_FollowTheRegistrysAnswer_AndReadBackOntoTheActivationList()
    {
        var hosting = new PlanTierRefusal("Hosting", Patcher, "enterprise", "free");
        var contentOnly = new PlanTierRefusal("Course", null, "pro", "free");

        Assert.Equal([Patcher], ModuleActivationSidecar.SyncTierRefusals(root, [hosting, contentOnly]));
        Assert.True(File.Exists(ModuleActivationSidecar.TierRefusedMarkerPath(root, Patcher)));

        var read = ModuleActivationSidecar.Read(root);
        Assert.Equal(hosting, Assert.Single(read.TierRefusals));
        Assert.Equal(hosting, read.TierRefusalFor(Patcher));
        Assert.Equal(hosting, read.TierRefusalFor(Patcher.ToUpperInvariant()));
        Assert.Null(read.TierRefusalFor("MeshWeaver.AI"));

        // The activation report — what the package card and the activation health line read.
        var report = new PendingModuleActivations(root).Read(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.True(report.HasTierRefused);
        Assert.Contains("Hosting needs plan tier enterprise, this instance is on free", report.Describe());

        // The plan was raised: the next pass answers no refusals, and the marker goes.
        Assert.Empty(ModuleActivationSidecar.SyncTierRefusals(root, []));
        Assert.False(File.Exists(ModuleActivationSidecar.TierRefusedMarkerPath(root, Patcher)));
        Assert.Empty(ModuleActivationSidecar.Read(root).TierRefusals);
    }

    /// <summary>The marker is a sibling of the #4083 refusal marker and must not be mistaken for
    /// an activation ENTRY by the bulk write or the entry enumeration.</summary>
    [Fact]
    public void TheMarker_IsNotAnEntry_AndSurvivesABulkWrite()
    {
        ModuleActivationSidecar.SyncTierRefusals(root, [new PlanTierRefusal("Hosting", Patcher, "enterprise", "free")]);
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.AI", Directory = "MeshWeaver.AI/1" }],
        });

        var read = ModuleActivationSidecar.Read(root);
        Assert.Equal("MeshWeaver.AI", Assert.Single(read.Entries).Name);
        Assert.Single(read.TierRefusals);
    }

    /// <summary>
    /// Surface 3 — the self-updater's startup line. With the patcher's package refused by plan
    /// the qualifier after <c>canPatch=False</c> names the tier; without one it keeps pointing at
    /// the two ways a Kubernetes install gets its patcher.
    /// </summary>
    [Fact]
    public void TheSelfUpdaterStartupLine_NamesTheTier_WhenThePatchersPackageWasRefused()
    {
        ModuleActivationSidecar.SyncTierRefusals(root, [new PlanTierRefusal("Hosting", Patcher, "enterprise", "free")]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ModuleRoot.ConfigKey] = root })
            .Build();

        var refusal = UnavailableUpdateMechanics.PatcherTierRefusal(configuration);
        Assert.NotNull(refusal);
        var line = UnavailableUpdateMechanics.DescribeCannotPatch(refusal);
        Assert.Contains("Hosting needs plan tier enterprise, this instance is on free", line);
        Assert.DoesNotContain("Modules:Assemblies", line);

        ModuleActivationSidecar.SyncTierRefusals(root, []);
        Assert.Null(UnavailableUpdateMechanics.PatcherTierRefusal(configuration));
        Assert.Contains("Modules:Assemblies", UnavailableUpdateMechanics.DescribeCannotPatch(null));
    }

    /// <summary>Surface 2 — the card's row: the refusal folds into a manifest that is pre-installed,
    /// carries the module and the tier, and is never mistaken for an installable entry.</summary>
    [Fact]
    public void ARefusalFoldsIntoAManifestRow_ThatIsRefused_NotInstallable()
    {
        var row = PackageManifest.FromRefusal(new PlanTierRefusal("Hosting", Patcher, "enterprise", "free"), Platform);
        Assert.True(row.IsRefused);
        Assert.True(row.PreInstalled);
        Assert.Equal("Hosting", row.Id);
        Assert.Equal(Patcher, row.Module);
        Assert.Equal("enterprise", row.Tier);
        Assert.Equal(Platform, row.Source);
        Assert.False(Hosting.IsRefused);
    }

    /// <summary>A source that lists nothing — the endpoints' refusal computation never lists.</summary>
    private sealed class NullSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            System.Reactive.Linq.Observable.Return((IReadOnlyList<PackageManifest>)[]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            System.Reactive.Linq.Observable.Return((IReadOnlyList<PackageFile>)[]);
    }
}

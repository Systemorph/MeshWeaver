using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 An adopted assembly must be distinguishable from a compiled one, and a bundle that states
/// which sources it was built from must be REFUSED when those are not the sources this mesh holds
/// (#2813).
///
/// <para><b>What went wrong.</b> A GitSync <c>update</c> pulled new source for <c>Crm/Migration</c>
/// and then adopted a prebuilt assembly instead of compiling what it had just pulled. The node
/// reported <c>Succeeded</c>; the next run executed the OLD code and destroyed live client data —
/// four documents lost their bodies, one of them a 7,000-word commercial offer, and one
/// unrecoverable because its only version in history was the one the stale code wrote.</para>
///
/// <para><b>Why every check said it was fine.</b> The two signals an operator is taught to trust —
/// <see cref="CompilationStatus.Ok"/> and <c>CompiledSources == CurrentSourceVersions</c> (i.e.
/// <c>IsDirty == false</c>) — both read clean, because <b>the adoption writes the second one
/// itself</b>: <c>PrebuiltAssemblySeeder.Seed</c> asks the owner (via
/// <c>RequestedSourceStampAt</c>) to stamp <c>CompiledSources</c> from its own live snapshot. The
/// staleness detector was not broken. It was answering a question the adoption had already
/// answered for it. Only forcing a compile moved the MVID.</para>
///
/// <para><b>The three rows, and why the middle one is the only hard fail.</b> A bundle that carries
/// no fingerprint is of <i>unknown</i> provenance, not <i>proven stale</i>; refusing those would
/// break every bundle published to date and the node-repo CI gates that depend on prebuilt
/// fetches. Only a fingerprint that is PRESENT and DISAGREES is a lie, and only that is refused.
/// The requirement for the other two is visibility, not refusal.</para>
/// </summary>
public class AdoptedBuildProvenanceTest
{
    private static readonly ImmutableDictionary<string, long> LiveSources =
        ImmutableDictionary.CreateRange(new Dictionary<string, long>
        {
            ["Crm/Migration/Source/code"] = 638_000_000_000_000_000,
        });

    /// <summary>An adoption exactly as <c>PrebuiltAssemblySeeder.Seed</c> leaves it: the request is
    /// standing, and the producer's fingerprint (or its absence) is stamped.</summary>
    private static NodeTypeDefinition PendingAdoption(
        string? producerFingerprint, string? liveFingerprint) =>
        new()
        {
            CompilationStatus = CompilationStatus.Ok,
            RequestedSourceStampAt = System.DateTimeOffset.UtcNow,
            AdoptedSourceFingerprint = producerFingerprint,
            CurrentSourceFingerprint = liveFingerprint,
            CurrentSourceVersions = LiveSources,
            // Seed has ALREADY stamped the adopted build's coordinates by the time the owner judges
            // it — which is why "refuse" has to decide whether those bytes keep serving.
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "adopted/Crm.Migration.dll",
            LatestAssemblyMvid = "ed21acce00000000",
            // 🚨 …and the type had COMPILED here before, so it carries a CompiledSources snapshot
            // that MATCHES the live one. Seed does not clear it. Without this line the fixture
            // models only a never-compiled type, CompiledSources is null for free, and the refusal
            // row's "the staleness question stays open" assertion passes without exercising
            // anything — which is exactly how it read green while the defect was live.
            CompiledSources = LiveSources,
        };

    /// <summary>
    /// 🚨 THE DATA-LOSS ROW. The bundle names sources that are NOT the ones this mesh holds, so
    /// the bytes are last week's code over today's data. The stamp is withheld — that write is the
    /// lie, because it is what makes <c>IsDirty</c> false by construction — and a real compile of
    /// the live source is driven instead.
    /// </summary>
    [Fact]
    public void AFingerprintThatDisagrees_IsRefused_AndDrivesARealCompile()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            PendingAdoption(producerFingerprint: "aaaaaaaaaaaaaaaa",
                            liveFingerprint: "bbbbbbbbbbbbbbbb"),
            LiveSources, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused,
            "the bundle states which sources it was built from and they are not the live ones — "
            + "that is the case this whole mechanism exists to catch");
        result.CompiledSources.Should().BeNull(
            "🚨 the PRIOR snapshot has to be CLEARED, not merely left unstamped: this type compiled "
            + "here before, so it arrives carrying a CompiledSources that MATCHES the live sources "
            + "(Seed never clears it). Leaving it reads IsDirty=false beside "
            + "BuildProvenance=AdoptionRefused — 'my compiled sources are current' about bytes that "
            + "were explicitly rejected, which is the same unearned claim one step along");
        result.IsDirty.Should().BeTrue(
            "the staleness question must be left visibly OPEN, not answered by the adoption");
        result.CompilationStatus.Should().Be(CompilationStatus.Pending,
            "refusing is not enough — the live source has to actually get compiled");
        result.RequestedSourceStampAt.Should().BeNull(
            "the request is consumed on every path, so it can never re-fire");

        // 🚨 …and the rejected bytes STOP SERVING. Seed had already stamped the adopted build's
        // coordinates; leaving them is what let proven-stale code keep executing while the node
        // merely said so. A fresh compile is dispatched above, so the gap is seconds.
        result.LatestAssemblyPath.Should().BeNull();
        result.LatestAssemblyCollection.Should().BeNull(
            "\"marked and still serving\" is the state that let an armed control-plane node fire "
            + "pre-fix code unattended — on a mesh that CAN rebuild, stale bytes must stop running");
        result.LatestAssemblyMvid.Should().BeNull(
            "leaving the served-build identity would name bytes nothing serves");
    }

    /// <summary>
    /// 🚨 THE CONDITIONAL, and the branch that must never be decided by assuming a flag's value.
    /// On a <c>Modules:RequirePrebuilt</c> mesh the local compile the refusal dispatches is refused
    /// BY DESIGN, so clearing the coordinates would leave the type with no assembly at all,
    /// INDEFINITELY — an outage with no recovery path, self-inflicted by the guard. Marked-stale
    /// beats dead when there is no path to a fresh build, and the caller escalates to
    /// <c>Critical</c> because only a human rebaking fixes it.
    ///
    /// <para>The flag is measured absent on memex and memex-cloud (and #2194 item 3 records the
    /// same) — that is TWO instances, and says nothing about pearl, atioz, local installs, or any
    /// external instance the registry serves. This branch exists because configuration lives on AKS
    /// in places this repo has never heard of.</para>
    /// </summary>
    [Fact]
    public void OnAMeshThatCannotCompile_TheRefusedBytesKeepServing_RatherThanLeavingNothing()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            PendingAdoption(producerFingerprint: "aaaaaaaaaaaaaaaa",
                            liveFingerprint: "bbbbbbbbbbbbbbbb"),
            LiveSources, canCompileLocally: false);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused,
            "the verdict is the same — what changes is only whether the rejected bytes keep serving");
        result.CompiledSources.Should().BeNull(
            "the staleness question stays open on BOTH branches — and it matters most here, where "
            + "the compile that would refresh it is refused by design, so this record is the one "
            + "the node is LEFT in until a human rebakes");
        result.IsDirty.Should().BeTrue(
            "a refused adoption must never read as 'sources current', least of all on the mesh "
            + "that cannot produce fresh ones");

        result.LatestAssemblyPath.Should().Be("adopted/Crm.Migration.dll");
        result.LatestAssemblyCollection.Should().Be("assemblies",
            "RequirePrebuilt refuses the local compile that would replace these bytes, so clearing "
            + "them leaves the type with NO assembly at all, indefinitely — worse than marked-stale "
            + "when there is no path to a fresh build");
    }

    /// <summary>
    /// Matching fingerprint: the bytes and the source have been compared and they agree. Stamps
    /// exactly as before, so an adoption still sticks and the release watcher's "satisfied by the
    /// existing current build" branch still absorbs.
    /// </summary>
    [Fact]
    public void AMatchingFingerprint_AdoptsAndRecordsThatItWasVerified()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            PendingAdoption(producerFingerprint: "cafebabecafebabe",
                            liveFingerprint: "cafebabecafebabe"),
            LiveSources, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified);
        result.IsDirty.Should().BeFalse(
            "a verified adoption must still satisfy the release watcher's !IsDirty branch — "
            + "otherwise every install recompiles what it just adopted");
        result.RequestedSourceStampAt.Should().BeNull();
    }

    /// <summary>
    /// 🚨 THE ROW THAT MUST NOT BECOME A REFUSAL. A legacy bundle records no fingerprint, so its
    /// provenance is UNKNOWN — and it still stamps.
    ///
    /// <para>Withholding the stamp here would make every legacy-bundle type <c>IsDirty</c> on
    /// arrival, which stops <c>InstallReleaseRequestWatcher</c>'s "satisfied by the existing
    /// current build" branch (it requires <c>!IsDirty</c>) from ever absorbing — so every install
    /// recompiles everything, the 43 activations / 13.5 s of boot the prebuilt lane exists to
    /// remove. On a <c>Modules:RequirePrebuilt</c> mesh it is worse: a local compile is refused by
    /// design, so not stamping would PARK every legacy-bundle type. The requirement is
    /// <b>visibility, not refusal</b> — so the stamp stays and the provenance says it was never
    /// earned.</para>
    /// </summary>
    [Fact]
    public void ALegacyBundle_StillAdoptsAndStamps_ButIsNeverRecordedAsVerified()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            PendingAdoption(producerFingerprint: null, liveFingerprint: "cafebabecafebabe"),
            LiveSources, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptedUnverified,
            "no fingerprint means UNKNOWN provenance, which is not the same fact as proven stale "
            + "— and it must never read as Verified");
        result.IsDirty.Should().BeFalse(
            "🚨 the stamp is KEPT deliberately: withholding it makes every legacy-bundle type "
            + "dirty on arrival, the release watcher's !IsDirty absorb branch stops firing, and on "
            + "a RequirePrebuilt mesh — where a local compile is refused by design — every "
            + "legacy-bundle type PARKS. That is the outage refusing unproven bundles was "
            + "rejected to avoid, arriving through a different door");
        result.RequestedSourceStampAt.Should().BeNull();
    }

    /// <summary>
    /// The owner has an adopted fingerprint but has not published its own yet — the window before
    /// the sources watcher writes both in one update. Nothing has been COMPARED, so this is
    /// unknown, not a mismatch. Refusing on an absence is the <c>INCONCLUSIVE</c> lesson from the
    /// emit canary (#890): a probe must not answer its scariest branch on its own inability to run.
    /// </summary>
    [Fact]
    public void AnAdoptedFingerprintWithNoLiveOneYet_IsUnknown_NeverARefusal()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            PendingAdoption(producerFingerprint: "cafebabecafebabe", liveFingerprint: null),
            LiveSources, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptedUnverified);
        result.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "an absence is not a disagreement — driving a compile here would recompile every "
            + "adoption that lands before its owner has published a fingerprint");
    }

    /// <summary>
    /// 🚨 The reporter-side assertion the incident actually needed, stated as the thing that was
    /// NOT stateable before: two nodes that are byte-identical on every signal an operator reads
    /// must still be distinguishable. If this ever fails, the provenance field has stopped
    /// carrying the only information that separates them.
    /// </summary>
    [Fact]
    public void AVerifiedAndAnUnverifiedAdoption_AgreeOnEveryOldSignal_AndDifferOnlyInProvenance()
    {
        var verified = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            PendingAdoption("cafebabecafebabe", "cafebabecafebabe"), LiveSources,
            canCompileLocally: true);
        var unverified = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            PendingAdoption(null, "cafebabecafebabe"), LiveSources, canCompileLocally: true);

        // Every signal that existed before this change reads the same on both — which is exactly
        // why the stale one was invisible.
        verified.CompilationStatus.Should().Be(unverified.CompilationStatus);
        verified.IsDirty.Should().Be(unverified.IsDirty);
        verified.CompiledSources!.Count.Should().Be(unverified.CompiledSources!.Count);
        verified.CompiledSources["Crm/Migration/Source/code"].Should()
            .Be(unverified.CompiledSources["Crm/Migration/Source/code"]);

        verified.BuildProvenance.Should().NotBe(unverified.BuildProvenance,
            "provenance is the ONLY thing that separates a checked adoption from an unchecked "
            + "one — an operator, and a control plane, must be able to see it before arming "
            + "anything against the node");
    }

    /// <summary>
    /// Non-vacuity on the default: a locally-compiled record — one that never adopted, and every
    /// record written before this field existed — reads as <see cref="BuildProvenance.Compiled"/>.
    /// If the zero value were an adopted one, every historical node would claim a provenance it
    /// never had.
    /// </summary>
    [Fact]
    public void ARecordThatNeverAdopted_ReadsAsCompiled()
        => new NodeTypeDefinition().BuildProvenance.Should().Be(BuildProvenance.Compiled);
}

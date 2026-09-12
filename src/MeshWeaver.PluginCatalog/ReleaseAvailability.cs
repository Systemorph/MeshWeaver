using System.Collections.Immutable;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 THE predicate behind both release gates (#1754 deployment, #1755 build): <i>given a target
/// release, and given a set of packages, is every one of them AVAILABLE for that release?</i>
///
/// <para>It is stated once, here, and consumed three ways — the portals' self-update poll, CD's
/// own post-promote assertion, and the CI build gate — because a rule that only one caller
/// honours is not a rule. Everything about it is PURE: the caller supplies the target and an
/// <see cref="ReleaseArtifacts">observation</see> of what the artifact stores actually hold, and
/// this class decides. That is what lets an in-process service, an HTTP endpoint and a CI step
/// reach the identical verdict without three copies of the reasoning.</para>
///
/// <para>🚨 <b>What holds a roll, since #3651 (maintainer rule of 2026-09-07,
/// <c>Doc/Architecture/ModuleAdoptionPolicy</c>): a module that provably cannot LOAD on the target
/// — and nothing declared.</b> Two lanes, judged differently on purpose:</para>
/// <list type="number">
/// <item><description><b>Content package</b> — a published, SEALED bake under the TARGET release's
/// framework identity (<c>prebuilt-bundles/&lt;identity&gt;/&lt;source&gt;/</c>, with its
/// <c>_complete</c> sentinel written strictly last). Absent ⇒ the instance Roslyn-compiles that
/// content at boot. 🚨 That is a COST, reported as <see cref="UpdatabilityVerdict.BootCompiles"/>
/// ("would recompile at boot: education, crm") — never a hold: the compile is the same code path
/// every pull request of that content already proves green, and on 2026-09-07 holding on it would
/// have kept memex-cloud on 8009 a second day because the satellites had not baked for the new
/// identity yet. <see cref="ReleaseGatePolicy.RequirePrebuilt"/> (the instance's
/// <c>Modules:RequirePrebuilt</c>) is the opt-in STRICT mode in which a missing bake still holds —
/// there a boot compile is refused by the seeder, so the roll would park. The identity match is
/// EXACT — the strict-MVID rule of <c>PrebuiltAssemblySeeder.DeclineReason</c> — and it is
/// expressed here as "the bundle is sealed under the target's identity", never re-derived. A
/// sealed set that is INCONSISTENT (#3175) stays a hold: a torn publication is refused whole.</description></item>
/// <item><description><b>Compiled module</b> — MEASURED. A module with a build published for the
/// target identity (the identity's sealed module set carries it) will be adopted and needs no
/// check. A module with no such build keeps its landed generation across the roll, so that
/// generation's bytes are linked against the TARGET's type surface
/// (<see cref="ModulePlatformLink.Check(string, ModulePlatformSurface)"/> over the
/// <see cref="ReleaseArtifacts.PlatformSurface"/> the publication carries): <c>Unlinkable</c> is
/// <see cref="PackageAvailabilityKind.ModuleUnloadable"/> — THE hold, naming the module and the
/// missing type; <c>Linkable</c> clears; <c>Indeterminate</c> (no surface published, unreadable
/// bytes) is REPORTED on <see cref="UpdatabilityVerdict.Advisories"/> and is neither clearance nor
/// a hold — the boot-time probe, the keep-the-previous-generation fallback and the readiness stall
/// are the safety net. 🚨 NOT the declared <c>MinMeshVersion</c> floor: that was a BLOCKER until
/// #3648 and on 2026-09-07 it declined all 11 candidate releases on memex-cloud ("77 plugins
/// required … every one declined") because the comparator ranks <c>ci &lt; rc &lt; clean</c> —
/// while every candidate would have loaded. The floor is worded onto
/// <see cref="UpdatabilityVerdict.Advisories"/> and decides nothing.</description></item>
/// </list>
///
/// <para>🚨 <b>It fails SAFE where it cannot see the STORE.</b> "Cannot determine" is NOT "clear to
/// proceed". When the target's framework identity cannot be resolved, or the artifact store could
/// not be read, every package answers <see cref="PackageAvailabilityKind.Indeterminate"/> and the
/// verdict is NOT updatable — with a reason that says so in those words. An availability failure
/// is never dressed up as a compatibility verdict: <see cref="PackageAvailabilityKind.Indeterminate"/>
/// and <see cref="PackageAvailabilityKind.ModuleUnloadable"/> are different answers to different
/// questions, and a caller that cannot tell them apart cannot tell an outage from an incompatible
/// release. The one Indeterminate that does NOT hold is the LINK check's — a missing
/// <c>platform-surface.json</c> is a publication that predates #3651, and holding every roll on it
/// would freeze the fleet exactly as the floors did; it is reported instead.</para>
///
/// <para>Everything here is PURE: the caller supplies the target, the packages, and an
/// <see cref="ReleaseArtifacts">observation</see> that already carries the link measurements
/// (<see cref="ModuleLinkObservation.Measure"/> reads the landed bytes; this class never does IO),
/// so an in-process service, an HTTP endpoint and a CI step reach the identical verdict.</para>
/// </summary>
public static class ReleaseAvailability
{
    /// <summary>
    /// Is <paramref name="target"/> a release every one of <paramref name="packages"/> can survive,
    /// given <paramref name="artifacts"/>? Pure, total, and never throws — the answer for an
    /// unreadable observation is a NOT-updatable verdict whose reasons name the unreadability.
    /// </summary>
    /// <param name="target">The candidate release: its version, and the framework identity its
    /// image resolves (null/blank ⇒ indeterminate).</param>
    /// <param name="packages">What must survive the roll — an environment's installed set for the
    /// deployment gate, a repo's declared upstreams for the build gate.</param>
    /// <param name="artifacts">What the artifact stores were observed to hold for that target.</param>
    public static UpdatabilityVerdict IsUpdatable(
        ReleaseTarget target,
        IEnumerable<RequiredPackage> packages,
        ReleaseArtifacts artifacts)
        => IsUpdatable(target, packages, artifacts, ReleaseGatePolicy.Default);

    /// <summary>
    /// As the three-argument overload, under an explicit <paramref name="policy"/> — the instance's
    /// <c>Modules:RequirePrebuilt</c> decides whether a missing content bake is the cost it is
    /// everywhere else (reported, the roll proceeds) or the hold a strict instance opts into. 🚨 An
    /// overload, not an optional parameter: the three-argument signature is binary API for every
    /// host compiled against the previous platform.
    /// </summary>
    /// <param name="target">The candidate release.</param>
    /// <param name="packages">What must survive the roll.</param>
    /// <param name="artifacts">What the artifact stores were observed to hold for that target,
    /// including the link measurements of every landed module
    /// (<see cref="ModuleLinkObservation.Measure"/>).</param>
    /// <param name="policy">The instance's gate policy.</param>
    public static UpdatabilityVerdict IsUpdatable(
        ReleaseTarget target,
        IEnumerable<RequiredPackage> packages,
        ReleaseArtifacts artifacts,
        ReleaseGatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var required = packages?.ToImmutableArray() ?? [];

        // The observation itself is unusable: say THAT, once, about every package. Answering
        // "content bake missing" here would report an outage as an incompatibility — the exact
        // conflation #1754 forbids.
        var blocked = IndeterminateReason(target, artifacts);
        if (blocked is not null)
            return new UpdatabilityVerdict(
                false,
                [.. required.Select(p => new PackageAvailability(
                    p.Name, PackageAvailabilityKind.Indeterminate, blocked))],
                blocked);

        var evaluated = required
            .Select(p => Evaluate(p, target, artifacts, policy))
            .ToImmutableArray();
        var verdicts = evaluated.Select(e => e.Availability).ToImmutableArray();

        var blockers = verdicts.Where(v => !v.IsAvailable).ToImmutableArray();
        var bootCompiles = verdicts
            .Where(v => v is { Kind: PackageAvailabilityKind.ContentBakeMissing, IsAdvisory: true })
            .Select(v => v.Package)
            .ToImmutableArray();
        return new UpdatabilityVerdict(
            blockers.Length == 0,
            verdicts,
            blockers.Length == 0
                ? null
                : string.Join("; ", blockers.Select(b => $"{b.Package}: {b.Reason}")))
        {
            Advisories =
            [
                .. bootCompiles.IsEmpty
                    ? []
                    : new[] { BootCompileAdvisory(bootCompiles, target) },
                .. evaluated.Select(e => e.LinkAdvisory).OfType<string>(),
                .. required.Select(p => FloorAdvisory(p, target)).OfType<string>(),
            ],
            BootCompiles = bootCompiles,
        };
    }

    /// <summary>
    /// The one line that names every package the instance would Roslyn-compile at boot on the
    /// target — "would recompile at boot: education, crm". A cost the operator reads, never a
    /// reason in <see cref="UpdatabilityVerdict.IsUpdatable"/> (outside
    /// <see cref="ReleaseGatePolicy.RequirePrebuilt"/>).
    /// </summary>
    private static string BootCompileAdvisory(ImmutableArray<string> packages, ReleaseTarget target) =>
        $"would recompile at boot on {Describe(target)} (no sealed content bake for framework "
        + $"identity {target.FrameworkIdentity}): {string.Join(", ", packages)}";

    /// <summary>
    /// The declared-floor ADVISORY for one package against the target (#3648): the sentence naming
    /// both versions when the module's <see cref="RequiredPackage.MinMeshVersion"/> ranks above
    /// <see cref="ReleaseTarget.Version"/>, or null. Reported on
    /// <see cref="UpdatabilityVerdict.Advisories"/>, logged by the callers, and by design absent
    /// from <see cref="UpdatabilityVerdict.IsUpdatable"/> and <see cref="UpdatabilityVerdict.Blockers"/>.
    /// </summary>
    private static string? FloorAdvisory(RequiredPackage package, ReleaseTarget target) =>
        ModulePlatformFloor.DeclineReason(package.MinMeshVersion, target.Version) is { } reason
            ? $"{package.Name}: {reason}"
            : null;

    /// <summary>
    /// Why the whole observation is unusable, or null when it can be reasoned about. Kept separate
    /// from the per-package rules so "we could not look" can never be mistaken for "we looked and
    /// it is not there".
    /// </summary>
    private static string? IndeterminateReason(ReleaseTarget target, ReleaseArtifacts artifacts)
    {
        if (artifacts.ReadFailure is { Length: > 0 } failure)
            return $"the artifact catalogue for release {Describe(target)} could not be read "
                   + $"({failure}) — cannot determine availability, which is not clearance to proceed";
        if (string.IsNullOrWhiteSpace(target.Version))
            return "no target release version was given — cannot determine availability, which is "
                   + "not clearance to proceed";
        if (string.IsNullOrWhiteSpace(target.FrameworkIdentity))
            return $"the framework identity of release {target.Version} is not resolvable — no "
                   + "content bake has been published for it, so nothing can be shown adoptable; "
                   + "cannot determine availability, which is not clearance to proceed";
        return null;
    }

    /// <summary>One package's answer plus the link ADVISORY it may carry — the sentence for a link
    /// check that could not be made, which rides beside the verdict rather than inside it.</summary>
    private readonly record struct Evaluation(PackageAvailability Availability, string? LinkAdvisory);

    private static Evaluation Evaluate(
        RequiredPackage package, ReleaseTarget target, ReleaseArtifacts artifacts, ReleaseGatePolicy policy)
    {
        // 🚨 #3648 — no floor step. "Modules first: a floor that EXCEEDS the target is a definite
        // incompatibility" stood here and answered ModuleFloorExceedsTarget; it was not definite,
        // it was a string order (ci < rc < clean), and it held every production portal on
        // 2026-09-07. The floor is reported as an advisory by IsUpdatable; this evaluation asks
        // what the artifact stores can answer — is the bake there, is the sealed set consistent —
        // and what the landed bytes can answer: would the module LOAD on the target.

        // 🚨 THE MODULE LANE FIRST (#3651): an unloadable module is the one hold on this lane, and
        // it must not be shadowed by a content advisory on the same package (a MIXED package —
        // content plus a compiled module — is the MeshWeaver.SocialMedia shape).
        var (unloadable, linkAdvisory) = ModuleLane(package, target, artifacts);
        if (unloadable is not null)
            return new Evaluation(unloadable, null);

        if (package.HasContent && !artifacts.SealedBundles.Contains(package.BundleName))
            // 🚨 ADVISORY, not a hold, since #3651: a boot compile is a cost the operator reads
            // ("would recompile at boot: …"), and the same compile every PR of that content already
            // proved green. Only a Modules:RequirePrebuilt instance — where the seeder REFUSES the
            // compile and the type would park — keeps it as the hold it used to be everywhere.
            return new Evaluation(
                new PackageAvailability(
                    package.Name,
                    PackageAvailabilityKind.ContentBakeMissing,
                    $"no sealed content bake for framework identity {target.FrameworkIdentity} — the "
                    + $"bundle '{package.BundleName}' is not published for release {target.Version}, so "
                    + "this instance would recompile it at boot"
                    + (policy.RequirePrebuilt
                        ? $" — and {PrebuiltAssemblySeeder.RequirePrebuiltConfigKey} refuses a boot "
                          + "compile on this instance, so the roll is held"
                        : " (a cost, not a hold — #3651)"))
                {
                    IsAdvisory = !policy.RequirePrebuilt,
                },
                linkAdvisory);

        // 🚨 PRESENT is not CONSISTENT (#3175). A sealed bundle is adoptable only if the module
        // bytes its NodeTypes were built against are the module bytes sealed for the SAME identity.
        if (package.HasContent && SealedSetProblem(package, target, artifacts) is { } inconsistent)
            return new Evaluation(inconsistent, linkAdvisory);

        return new Evaluation(
            new PackageAvailability(package.Name, PackageAvailabilityKind.Available, null),
            linkAdvisory);
    }

    /// <summary>
    /// 🚨 <b>The measured module gate (#3651).</b> For a package that ships a compiled module:
    /// <list type="bullet">
    /// <item><description>a build published for the target identity (the identity's sealed module
    /// set DECLARES the module) will be adopted at the roll — nothing to check here; the sealed-set
    /// consistency rule already judged those bytes;</description></item>
    /// <item><description>no such build, and nothing landed on this instance — nothing will be
    /// loaded, nothing to hold on;</description></item>
    /// <item><description>no such build, and a landed generation — that generation keeps running
    /// across the roll, so its bytes were linked against the target's surface
    /// (<see cref="ReleaseArtifacts.ModuleLinks"/>): <c>Unlinkable</c> HOLDS as
    /// <see cref="PackageAvailabilityKind.ModuleUnloadable"/>, naming the module and the missing
    /// types, and so does <c>BindingConflict</c> (#4083), naming the assembly versions the target
    /// cannot bind; <c>Linkable</c> clears, its roll-forward version drift on the advisories;
    /// <c>Indeterminate</c> — or no measurement at all, which is
    /// what a publication without <c>platform-surface.json</c> yields — is REPORTED as an advisory
    /// and decides nothing.</description></item>
    /// </list>
    /// Returns the hold, or the advisory, or neither.
    /// </summary>
    private static (PackageAvailability? Unloadable, string? Advisory) ModuleLane(
        RequiredPackage package, ReleaseTarget target, ReleaseArtifacts artifacts)
    {
        if (string.IsNullOrWhiteSpace(package.ModuleName))
            return (null, null);
        if (artifacts.Modules?.MvidByModule.ContainsKey(package.ModuleName) == true)
            return (null, null);
        if (string.IsNullOrWhiteSpace(package.LandedModulePath))
            // 🚨 NOTHING LANDED AND THE TARGET DOES NOT CARRY IT — an install record naming a
            // package NO PUBLISHER PRODUCES (#3706). This used to return silence, and silence is
            // the wrong answer twice over: the gate correctly does not hold (no roll of this
            // deployment can conjure a build nobody publishes — holding would be the eternal wait
            // #3706 was filed about), but nothing told the operator that the record is stale
            // either, so the only way to learn it was to read a frozen `heldReason` and reach the
            // wrong conclusion. That is exactly what happened: memex's Agent / Skill / PlatformUI
            // records outlived the packages (the AI engine serves those now — MeshWeaver.Plugins
            // 7afbd745, deliberately), and the hold quoted against them had been computed once,
            // 36 hours earlier, by a code path that no longer decides anything.
            //
            // Named, never a hold — which is #3706's option 3 stated as behaviour.
            //
            // 🚨 Only when the set was actually READ. A null or refused SealedModuleSet means the
            // module was not looked for, and "we did not look" must never be worded as "it does
            // not exist" — the same conflation #1754 forbids one severity up. Unmeasured stays
            // silent here and is reported by the paths that own it.
            return artifacts.Modules is { Refusal: null } observed
                    && !observed.MvidByModule.ContainsKey(package.ModuleName)
                ? (null,
                    $"{package.Name}: the install record names module {package.ModuleName}, which "
                    + $"the module set sealed for framework identity {target.FrameworkIdentity} "
                    + "does not carry, and no generation of it is landed on this instance — so no "
                    + "publisher produces it and no roll can obtain it. Reported, never a hold: "
                    + "holding would wait for ever. If the package is genuinely retired, remove "
                    + "its install record; if it moved, the record must name its new home")
                : (null, null);

        if (!artifacts.ModuleLinks.TryGetValue(package.Name, out var link))
            return (null,
                $"{package.Name}: whether its landed module {package.ModuleName} loads on "
                + $"{Describe(target)} could not be determined — "
                + (artifacts.PlatformSurfaceDetail
                   ?? "the landed generation was not measured against the target's surface")
                + ". Reported, not a hold: the boot-time link probe decides, and a generation that "
                + "does not load is kept out while the previous one keeps serving");

        return link.State switch
        {
            // Linkable clears — and carries the version drift that rolls FORWARD as an advisory
            // (#4083): on record, deciding nothing.
            ModuleLinkState.Linkable => (null,
                link.Advisories.IsDefaultOrEmpty
                    ? null
                    : $"{package.Name}: its landed module {package.ModuleName} links on "
                      + $"{Describe(target)} with assembly version skew the loader rolls forward "
                      + "— reported, never a hold: " + string.Join("; ", link.Advisories)),
            // 🚨 The FileLoadException shape (#4083): the landed generation references an assembly
            // the target carries at a LOWER version (or under another public key token) than the
            // module's bytes were bound to. The target's copy is what loads there, and .NET never
            // binds a reference to a lower version — a definite incompatibility, the same hold as
            // Unlinkable. The 2026-09-11 shape: MeshWeaver.AI bound to YamlDotNet 18.1.0.0 on an
            // image carrying 16.3.0.0 crash-looped every new pod at hub construction.
            ModuleLinkState.BindingConflict => (
                new PackageAvailability(
                    package.Name,
                    PackageAvailabilityKind.ModuleUnloadable,
                    $"its landed module {package.ModuleName} cannot load on {Describe(target)} "
                    + $"(framework identity {target.FrameworkIdentity}): no build of it is published "
                    + "for that identity, and the landed generation references "
                    + string.Join(", ", link.BindingConflicts)
                    + " — the target's copy is what the loader binds, and a reference to a higher "
                    + "version than the platform carries throws FileLoadException the first time "
                    + "any code path touches the assembly. The roll is held until a build of the "
                    + "module for this platform is published, or the module is uninstalled"),
                null),
            ModuleLinkState.Unlinkable => (
                new PackageAvailability(
                    package.Name,
                    PackageAvailabilityKind.ModuleUnloadable,
                    $"its landed module {package.ModuleName} cannot load on {Describe(target)} "
                    + $"(framework identity {target.FrameworkIdentity}): no build of it is published "
                    + "for that identity, and the landed generation references "
                    + string.Join(", ", link.MissingTypes)
                    + ", which the target does not carry — loading it there throws "
                    + "TypeLoadException at the first render that touches it. The roll is held until "
                    + "a build of the module for this platform is published, or the module is "
                    + "uninstalled"),
                null),
            _ => (null,
                $"{package.Name}: whether its landed module {package.ModuleName} loads on "
                + $"{Describe(target)} could not be determined ({link.Detail}). Reported, not a "
                + "hold: the boot-time link probe decides, and a generation that does not load is "
                + "kept out while the previous one keeps serving"),
        };
    }

    /// <summary>
    /// 🚨 <b>The set, not the file (#3175).</b> memex-cloud rolled to ci.7621 on a verdict that
    /// checked PRESENCE — every installed package had a sealed bundle under the target's identity —
    /// and the portal then DECLINED SocialMedia at adoption: its NodeTypes recorded
    /// <c>MeshWeaver.Markdown.Collaboration</c> at one MVID, the module sealed for the same identity
    /// carried another. Two producers of one module for one identity; a set that was complete and
    /// mutually inconsistent; a half-broken portal the gate exists to prevent. Same morning, every
    /// satellite gate declined four map galleries whose records named <c>MeshWeaver.Maps</c> at an
    /// MVID nothing composed. This is the assertion the maintainer asked for — <i>"a clear
    /// confirmation that all plugins deployed to an instance are available for the correct platform
    /// version; if not ⇒ nothing goes"</i>.
    ///
    /// <para>The rule: every <c>mvid:</c> entry in a bundle's dependency records that names a module
    /// the identity's sealed module set carries must equal the MVID sealed for it, and no assembly
    /// name may appear in the set at two MVIDs. A module the sealed set does NOT carry is one the
    /// instance lands from the registry outside any publication — this gate cannot see those bytes
    /// and does not pretend to. Records that cannot be checked because the module set is unreadable
    /// answer <see cref="PackageAvailabilityKind.Indeterminate"/> — a HOLD, never an incompatibility
    /// verdict, and never a pass: an unsealed or torn module set is exactly the case that let ci.7621
    /// through.</para>
    ///
    /// <para>🚨 <b>"No name at two MVIDs" counts RIDING copies, not just declared ones (#3221).</b> A
    /// module-owned <c>MeshWeaver.*</c> sibling rides every bundle that references it, so one name
    /// commonly reaches a mesh from many bundles at once — measured on MeshWeaver.Plugins
    /// <c>main</c> 2026-09-03, 19 of 37 module bundles carry a copy of an assembly some other package
    /// declares as its module. That is the sanctioned shape and it is NOT refused
    /// (<see href="../ModuleOwnedSiblingsRide">Module-Owned Siblings Ride</see>): excluding declared
    /// modules from the closure would invert the package graph — <c>AI</c> requires only <c>Store</c>
    /// yet would need <c>Essentials</c>' module, while <c>Essentials</c> requires <c>AI</c> — and a
    /// solo install would fault on its first touch. What IS refused is the copies DISAGREEING: the
    /// loader binds <c>MeshWeaver.*</c> by a strictly synchronised <c>AssemblyVersion</c> and keeps
    /// whichever copy it saw first, so two builds under one name make the live MVID a coin toss and
    /// decline every NodeType that recorded the other. Byte equality per (assembly name, framework
    /// identity) is the invariant; this is where it is asserted rather than assumed from a uniform
    /// CD wave.</para>
    /// </summary>
    private static PackageAvailability? SealedSetProblem(
        RequiredPackage package, ReleaseTarget target, ReleaseArtifacts artifacts)
    {
        foreach (var record in artifacts.DependencyRecords
                     .Where(r => string.Equals(r.Bundle, package.BundleName, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var (name, id) in record.Dependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                // 🚨 THE MODULE LANE, not one spelling of it (#3934). A module entry is now a
                // FLOOR (min:<version>) wherever the module states a version, and falls back to
                // mvid: only where it does not — so a predicate that matched mvid: alone would
                // have stopped seeing most module entries the day producers started stating
                // versions, and this gate would have gone quietly green over a torn publication.
                // That is the one failure mode a roll gate may not have.
                if (name.StartsWith('!') || !CompiledDependencies.IsModuleLaneId(id))
                    continue;

                var set = artifacts.Modules;
                if (set is null || set.Refusal is not null)
                    return new PackageAvailability(
                        package.Name,
                        PackageAvailabilityKind.Indeterminate,
                        $"'{package.BundleName}' ({record.NodePath}) was built against module {name} {id}, "
                        + $"but the module set sealed for framework identity {target.FrameworkIdentity} "
                        + $"could not be read ({set?.Refusal ?? "it was not observed"}) — cannot determine "
                        + "whether the bundle and the module are one build, which is not clearance to proceed");

                if (set.Conflicts.FirstOrDefault(c => c.StartsWith($"module {name}:", StringComparison.Ordinal))
                    is { } conflict)
                    return new PackageAvailability(
                        package.Name,
                        PackageAvailabilityKind.SealedSetInconsistent,
                        $"'{package.BundleName}' ({record.NodePath}) binds module {name}, which the set "
                        + $"sealed for framework identity {target.FrameworkIdentity} carries as TWO "
                        + $"DIFFERENT builds ({conflict}) — the loader keeps whichever it sees first, so "
                        + "the other's NodeTypes are declined at adoption; the set is inconsistent and "
                        + "nothing rolls");

                // 🚨 THE ONE CHECK THE FLOOR RETIRES, and only for a floor-shaped id (#3934):
                // "the bundle and the module are two BUILDS" stopped being a refusal the moment a
                // record stopped naming a build. An instance no longer declines that at adoption,
                // so a gate holding a roll for it would be refusing on a fact nothing downstream
                // acts on. The two checks above — the set could not be read, and the set carries
                // one name at two builds — are untouched and still fire for every module entry,
                // floor or pin: a torn publication is refused whole, exactly as
                // Doc/Architecture/ModuleAdoptionPolicy requires.
                if (id.StartsWith(CompiledDependencies.MvidScheme, StringComparison.Ordinal)
                    && set.MvidByModule.TryGetValue(name, out var sealedMvid)
                    && !string.Equals(sealedMvid, id, StringComparison.Ordinal))
                    return new PackageAvailability(
                        package.Name,
                        PackageAvailabilityKind.SealedSetInconsistent,
                        $"'{package.BundleName}' ({record.NodePath}) was built against module {name} {id}, "
                        + $"but the module set sealed for framework identity {target.FrameworkIdentity} "
                        + $"carries {sealedMvid} — the bundle and the module it binds are two builds, and "
                        + "the instance would decline it at adoption (dependency record mismatch)");
            }
        }
        return null;
    }

    private static string Describe(ReleaseTarget target) =>
        string.IsNullOrWhiteSpace(target.Version) ? "<unspecified>" : target.Version;
}

/// <summary>
/// The candidate release a gate is asked about: the platform version being rolled to, and the
/// framework build identity the image resolves. The identity gates content bakes; the version
/// names the release in every reason and words the declared-floor advisory (#3648) — neither
/// substitutes for the other.
/// </summary>
/// <param name="Version">The platform version tag, e.g. <c>3.0.0-ci.4049</c>, or a clean
/// <c>3.0.0</c> for a promoted release.</param>
/// <param name="FrameworkIdentity">The framework build identity (<c>s&lt;hash&gt;</c> /
/// <c>g&lt;sha&gt;</c>) that release's image resolves, or null when it could not be resolved.</param>
public sealed record ReleaseTarget(string? Version, string? FrameworkIdentity);

/// <summary>
/// One thing that must survive the roll. The deployment gate builds these from an environment's
/// install records; the build gate builds them from a repo's declared upstreams.
/// </summary>
/// <param name="Name">How the package is named to a human in the refusal.</param>
/// <param name="BundleName">The bake bundle's base name — the package id the bake writes as
/// <c>&lt;id&gt;.zip</c> and lists in the <c>_complete</c> sentinel.</param>
/// <param name="MinMeshVersion">The compiled module's declared platform floor, or null for a
/// content-only package. 🚨 ADVISORY since #3648: it is passed through as declared and worded onto
/// <see cref="UpdatabilityVerdict.Advisories"/> when the target does not satisfy it; it never
/// decides <see cref="UpdatabilityVerdict.IsUpdatable"/>. (It used to be passed only when the
/// running platform satisfied it — the "regression check" reading — and even so every <c>rc</c>
/// and <c>3.0.0</c> floor blocked every <c>ci</c> target on 2026-09-07.)</param>
/// <param name="HasContent">Whether the package ships NodeType content that must be baked. False
/// for a module-only package, which this gate then has nothing to hold on — its loadability is
/// measured at landing and at boot, not here.</param>
public sealed record RequiredPackage(
    string Name, string BundleName, string? MinMeshVersion = null, bool HasContent = true)
{
    /// <summary>
    /// The compiled module this package ships, by assembly simple name (the install record's
    /// <c>module</c> field), or null for a content-only package. Init-only rather than positional
    /// (#3651): the constructor is binary API for hosts compiled against the previous platform.
    /// </summary>
    public string? ModuleName { get; init; }

    /// <summary>
    /// The entry DLL of the module's ACTIVE landed generation on this instance
    /// (<c>modules/&lt;name&gt;@&lt;gen&gt;/&lt;name&gt;.dll</c>, through the one resolution rule
    /// <c>ModuleActivationBoot.LandedDllPath</c>), or null when nothing is landed. This is what
    /// keeps running across a roll to a target that publishes no build of the module — so it is
    /// what <see cref="ModuleLinkObservation.Measure"/> links against the target's surface.
    /// </summary>
    public string? LandedModulePath { get; init; }
}

/// <summary>
/// The instance's gate policy (#3651): what a missing content bake MEANS on this deployment.
/// </summary>
/// <param name="RequirePrebuilt">The instance's <c>Modules:RequirePrebuilt</c> — the opt-in strict
/// mode in which the seeder refuses a boot compile and parks the type, so a missing bake is a
/// hold rather than a cost. Off everywhere by default (<c>PrebuiltAssemblySeeder.RequirePrebuilt</c>
/// reads it; absent means off).</param>
public sealed record ReleaseGatePolicy(bool RequirePrebuilt)
{
    /// <summary>The fleet default: a missing bake is reported and the roll proceeds.</summary>
    public static ReleaseGatePolicy Default { get; } = new(RequirePrebuilt: false);
}

/// <summary>
/// What the artifact stores were OBSERVED to hold for one target release. Deliberately a value:
/// the observation is made by whoever can reach the store (a mounted bundle root, the registry
/// index, a CI storage probe) and the rules above never do IO, so every caller reasons identically.
/// </summary>
/// <param name="SealedBundles">Bundle base names published under the target's framework identity in
/// a SEALED source directory — a <c>_complete</c> sentinel present and every bundle it lists
/// actually there. An unsealed or torn directory contributes nothing, exactly as the boot seeder
/// treats it.</param>
/// <param name="ReadFailure">Why the observation could not be made, or null when it was made.
/// Non-null forces every verdict to <see cref="PackageAvailabilityKind.Indeterminate"/>.</param>
public sealed record ReleaseArtifacts(
    ImmutableHashSet<string> SealedBundles,
    string? ReadFailure = null)
{
    /// <summary>
    /// The module bundles sealed for the target's identity across every complete source — simple
    /// name → the <c>mvid:</c> id the dependency records spell — or null when the observation did
    /// not read them. Null is NOT "consistent": a record naming a module then answers
    /// <see cref="PackageAvailabilityKind.Indeterminate"/> (#3175).
    /// </summary>
    public SealedModuleSet? Modules { get; init; }

    /// <summary>The per-NodeType dependency records of every sealed bundle — what each bundle's
    /// assemblies were BUILT against — keyed by bundle id. Empty when the observation carried
    /// none, in which case there is nothing to check and presence decides.</summary>
    public ImmutableArray<BundleDependencyRecord> DependencyRecords { get; init; } = [];

    /// <summary>
    /// 🚨 The TARGET platform's type surface (#3651) — <c>platform-surface.json</c>, written by the
    /// bake inside the target image and published beside <c>_complete</c>, read back through
    /// <see cref="ModulePlatformSurface.FromJson"/>. What a landed module's bytes are linked
    /// against to answer "would it load there" without the target running anywhere. Null when no
    /// sealed source under the identity carries one, or none parses — then
    /// <see cref="PlatformSurfaceDetail"/> says which, and every link check is Indeterminate:
    /// reported, never a hold and never clearance.
    /// </summary>
    public ModulePlatformSurface? PlatformSurface { get; init; }

    /// <summary>Why <see cref="PlatformSurface"/> is null, in one sentence — a publication that
    /// predates #3651, or a document that did not parse. Null when the surface was read.</summary>
    public string? PlatformSurfaceDetail { get; init; }

    /// <summary>
    /// The link measurements (#3651) — package name → the verdict of linking that package's
    /// LANDED module generation against <see cref="PlatformSurface"/>, produced by
    /// <see cref="ModuleLinkObservation.Measure"/> so this value stays pure. A package with no
    /// entry was not measured (no surface, no landed module, or the caller skipped the step); the
    /// rule reads that as Indeterminate for the link check only.
    /// </summary>
    public ImmutableDictionary<string, ModuleLinkVerdict> ModuleLinks { get; init; } =
        ImmutableDictionary<string, ModuleLinkVerdict>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>An observation that failed — the fail-safe constructor.</summary>
    public static ReleaseArtifacts Unreadable(string reason) =>
        new(ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase), reason);

    /// <summary>An observation of the given sealed bundle names, case-insensitively matched (the
    /// bake writes file names, and the stores this runs against are not all case-sensitive).</summary>
    public static ReleaseArtifacts Of(IEnumerable<string> sealedBundles) =>
        new(
            ImmutableHashSet.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                sealedBundles.Select(StripBundleExtension)),
            null);

    /// <summary>The sentinel lists file names (<c>Store.zip</c>); packages are named by id
    /// (<c>Store</c>). One place converts, so no caller has to remember which side it holds.</summary>
    private static string StripBundleExtension(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

/// <summary>Why one package can or cannot survive the target release.</summary>
public enum PackageAvailabilityKind
{
    /// <summary>The package has a usable artifact for the target release.</summary>
    Available,

    /// <summary>No sealed content bake exists under the target's framework identity, so the
    /// instance would Roslyn-compile this package's NodeTypes at boot. 🚨 ADVISORY since #3651 —
    /// <see cref="PackageAvailability.IsAdvisory"/> is true and the package counts as available —
    /// except under <see cref="ReleaseGatePolicy.RequirePrebuilt"/>, where the boot compile the
    /// seeder would refuse makes it the hold it used to be everywhere.</summary>
    ContentBakeMissing,

    /// <summary>
    /// 🚨 RETIRED by #3648 — nothing produces this any more. It meant "the compiled module declares
    /// a platform floor the target release does not satisfy" and it BLOCKED the roll; on
    /// 2026-09-07 that string comparison declined every candidate release on every production
    /// portal. The declared floor is now reported on <see cref="UpdatabilityVerdict.Advisories"/>.
    /// The member stays so every later member keeps its ordinal (a verdict serialized by an older
    /// build still deserializes) and a caller compiled against it keeps compiling.
    /// </summary>
    ModuleFloorExceedsTarget,

    /// <summary>🚨 Availability could NOT be determined. Never "clear to proceed", and never to be
    /// reported as an incompatibility: this is the answer when the catalogue is unreachable or the
    /// target release has no resolvable framework identity.</summary>
    Indeterminate,

    /// <summary>
    /// The COMBO gate ran this module's content inside the candidate image and it did not survive
    /// — it failed to install, to compile, to render, or its Tests area went red
    /// (<see cref="ComboVerdictKind.Red"/>). A definite incompatibility, like
    /// <see cref="SealedSetInconsistent"/> and unlike <see cref="Indeterminate"/>: the gate
    /// looked, and the answer is about the release rather than about our ability to see it.
    ///
    /// <para>🚨 Appended, never inserted: every member before it keeps its ordinal, so a verdict
    /// serialized by an older build still deserializes correctly.</para>
    /// </summary>
    ComboVerificationFailed,

    /// <summary>
    /// 🚨 The bundle IS sealed under the target's identity, and it is still not adoptable: its
    /// NodeTypes were built against a module build the SAME identity's sealed module set does not
    /// carry (or two sources sealed two builds of one module). The instance would decline every
    /// such NodeType at adoption — "dependency record mismatch" — which is what memex-cloud did on
    /// ci.7621 after a presence-only verdict let it roll (#3175). A definite inconsistency of the
    /// published SET, not an absence and not an unreadability. Appended, never inserted.
    /// </summary>
    SealedSetInconsistent,

    /// <summary>
    /// 🚨 <b>THE hold on the module lane (#3651).</b> The package's landed module generation —
    /// which keeps running across the roll because no build of it is published for the target
    /// identity — references a type the target's platform surface does not carry
    /// (<see cref="ModuleLinkState.Unlinkable"/>), or an assembly the target carries at a LOWER
    /// version or under another public key token than the bytes were bound to
    /// (<see cref="ModuleLinkState.BindingConflict"/>, #4083). Loading it there throws
    /// <c>TypeLoadException</c> at the first render that touches it (#3538), or
    /// <c>FileLoadException</c> the first time the assembly is touched. MEASURED on the bytes,
    /// never declared; a definite incompatibility, like <see cref="SealedSetInconsistent"/>. The
    /// reason names the module and the missing types. Appended, never inserted.
    /// </summary>
    ModuleUnloadable,
}

/// <summary>
/// The module bundles one framework identity's sealed publications composed, read across every
/// complete source: what a consumer pinned to that identity composes, and therefore what every
/// dependency record sealed for it must name (#3175).
/// </summary>
/// <param name="MvidByModule">Module simple name → its id in the dependency-record spelling
/// (<c>mvid:&lt;32 hex&gt;</c>), for every module exactly one bundle DECLARED. A riding copy never
/// defines an entry here (#3221): the declared module is what an instance registers as an
/// <c>InstalledModuleAssembly</c>, so it alone is what a dependency record can be judged against.</param>
/// <param name="Conflicts">One line per assembly name the identity's sealed set carries at TWO
/// DIFFERENT builds, each starting <c>module &lt;name&gt;:</c> and naming both producers — source,
/// module bundle, id, and whether that copy was the bundle's declared module or a sibling riding
/// beside it (#3221). A conflict is an inconsistency of the set itself, whatever any bundle
/// recorded, and the two producers may be one source: a module-owned <c>MeshWeaver.*</c> sibling
/// rides every bundle that references it, so one name commonly reaches a mesh from many bundles at
/// once and they must all be one build.</param>
/// <param name="Refusal">Why the set could not be read completely (a source sealed before module
/// sealing existed, a torn module index, an unreadable module bundle), or null when it was. Non-null
/// makes every record that names a module <see cref="PackageAvailabilityKind.Indeterminate"/>.</param>
public sealed record SealedModuleSet(
    ImmutableDictionary<string, string> MvidByModule,
    ImmutableArray<string> Conflicts,
    string? Refusal);

/// <summary>One NodeType's dependency record inside one sealed bundle — the producer's
/// <c>(referenced assembly → surface id)</c> pairs, exactly as the boot seeder validates them.</summary>
/// <param name="Bundle">The bundle id (file name without <c>.zip</c>).</param>
/// <param name="NodePath">The NodeType the assembly implements.</param>
/// <param name="Dependencies">The record: module names carry <c>mvid:</c> ids, platform assemblies
/// <c>ref:</c> hashes, and the reserved <c>!</c>-prefixed keys carry the toolchain and content key.</param>
public sealed record BundleDependencyRecord(
    string Bundle,
    string NodePath,
    ImmutableDictionary<string, string> Dependencies);

/// <summary>One package's answer.</summary>
/// <param name="Package">The package, as named to a human.</param>
/// <param name="Kind">The verdict.</param>
/// <param name="Reason">Why, in one sentence — null only when available.</param>
public sealed record PackageAvailability(string Package, PackageAvailabilityKind Kind, string? Reason)
{
    /// <summary>
    /// 🚨 True when <see cref="Kind"/> is a COST the operator reads rather than a hold (#3651):
    /// a <see cref="PackageAvailabilityKind.ContentBakeMissing"/> outside
    /// <see cref="ReleaseGatePolicy.RequirePrebuilt"/>. The kind and the reason stay exactly what
    /// they were, so the Updates tab and the API can still say "would recompile at boot"; only
    /// whether it decides the roll changes. Init-only: the positional constructor is binary API.
    /// </summary>
    public bool IsAdvisory { get; init; }

    /// <summary>Whether this package clears the gate — available, or an advisory that names a
    /// cost and decides nothing.</summary>
    public bool IsAvailable => Kind == PackageAvailabilityKind.Available || IsAdvisory;
}

/// <summary>
/// The gate's answer: whether the roll may proceed, every package's reason, and a one-line summary
/// for the refusal a human reads. <see cref="IsUpdatable"/> false with a null
/// <see cref="HoldReason"/> is impossible by construction — a refusal always says why.
/// </summary>
/// <param name="IsUpdatable">True only when every package is available.</param>
/// <param name="Packages">Every package's verdict, including the available ones (a gate that
/// reports only failures cannot show that it looked at anything).</param>
/// <param name="HoldReason">The joined reasons, or null when updatable.</param>
/// <param name="NotEnforcedReason">Set only when the gate does not APPLY to this deployment at all
/// — see <see cref="NotEnforced"/>. Never a way to pass a gate that does apply.</param>
public sealed record UpdatabilityVerdict(
    bool IsUpdatable,
    ImmutableArray<PackageAvailability> Packages,
    string? HoldReason,
    string? NotEnforcedReason = null)
{
    /// <summary>
    /// 🚨 The gate does not APPLY here — the deployment consumes no CI bakes at all (no
    /// <c>PreWarm:PrebuiltBundleRoot</c>), so it already compiles its content at every boot and
    /// holding the update could only freeze it forever, which is the outage this gate exists to
    /// avoid rather than to cause.
    ///
    /// <para>This is deliberately NOT the same thing as passing. It is updatable with a stated
    /// reason the caller must LOG and SURFACE, so "nothing is gating this environment" is visible
    /// rather than inferred from a green tick. It is the one applicability answer; every other
    /// unknown is <see cref="PackageAvailabilityKind.Indeterminate"/>, which HOLDS.</para>
    /// </summary>
    public static UpdatabilityVerdict NotEnforced(string reason) =>
        new(true, [], null, reason);

    /// <summary>
    /// 🚨 The gate could not RUN — it is not wired into this host at all.
    ///
    /// <para>This is a HOLD, and it is deliberately not <see cref="NotEnforced"/>. That answer
    /// exists for the one stated applicability exemption (a deployment consuming no CI bakes);
    /// reusing it for a wiring failure is the trap this repo has been bitten by repeatedly — an
    /// <c>if: vars.X != ''</c> that skips green, a health check reporting Healthy while the bake
    /// is <c>NotStarted</c>. A gate that cannot run must never look like a gate that passed, and
    /// "the service is not registered" is the purest possible case of cannot-run: there is no
    /// verdict at all, only the absence of one.</para>
    ///
    /// <para>It reports as <see cref="IsIndeterminate"/> — an availability failure to FIX, never
    /// a compatibility verdict about the release — so the surfaces that already distinguish the
    /// two do so here without changing.</para>
    /// </summary>
    public static UpdatabilityVerdict Unavailable(string reason) =>
        new(false,
            [new PackageAvailability("(gate)", PackageAvailabilityKind.Indeterminate, reason)],
            reason);

    /// <summary>The packages that block the roll.</summary>
    public IEnumerable<PackageAvailability> Blockers => Packages.Where(p => !p.IsAvailable);

    /// <summary>
    /// 🚨 What the verdict SAYS without deciding on it — logged by every caller, recorded on the
    /// policy node and shown on the Updates tab beside <see cref="HoldReason"/>; by construction
    /// never a reason in <see cref="IsUpdatable"/> or a member of <see cref="Blockers"/>. Three
    /// kinds of line, in this order:
    /// <list type="bullet">
    /// <item><description>the boot-compile cost (#3651): "would recompile at boot on X: education,
    /// crm" — the packages in <see cref="BootCompiles"/>, one line;</description></item>
    /// <item><description>a link check that could NOT be made (#3651): the target published no
    /// <c>platform-surface.json</c>, or the landed bytes were unreadable — reported, neither
    /// clearance nor a hold, because the boot-time probe and the keep-the-previous-generation
    /// fallback are the safety net;</description></item>
    /// <item><description>a declared <c>minMeshVersion</c> floor the target does not rank above
    /// (#3648), naming both versions — a version string was the wrong instrument (every
    /// production portal held on 2026-09-07).</description></item>
    /// </list>
    /// Empty when there is nothing to say. An init-only property, not a positional parameter:
    /// replacing a public record's constructor signature is what
    /// <see cref="MissingMethodException"/>-aborts a host compiled against the previous platform.
    /// </summary>
    public ImmutableArray<string> Advisories { get; init; } = [];

    /// <summary>
    /// The packages the instance would Roslyn-compile at boot on the target (#3651) — every
    /// <see cref="PackageAvailabilityKind.ContentBakeMissing"/> that is an advisory — by name, so
    /// a surface can list them without parsing the sentence. Empty when every content-bearing
    /// package has a sealed bake for the target, or the hold is on something else.
    /// </summary>
    public ImmutableArray<string> BootCompiles { get; init; } = [];

    /// <summary>
    /// True when the hold is an "I could not look" rather than "I looked and it is incompatible".
    /// Callers surface this differently on purpose: an unreachable catalogue is an availability
    /// incident to fix, an incompatible package is a release to re-bake.
    /// </summary>
    public bool IsIndeterminate =>
        Packages.Any(p => p.Kind == PackageAvailabilityKind.Indeterminate);
}

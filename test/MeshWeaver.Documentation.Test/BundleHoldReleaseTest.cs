#pragma warning disable CS1591
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// What RELEASES a bundle-keyed hold (MeshWeaver#3845 hole 4) — the predicate a publication
/// announcement runs BEFORE anything is re-fetched.
///
/// <para>🚨 Every case here is built in the state a held source is actually in: sitting on the
/// OLDER commit, with a FINAL verdict recorded at the sealed one. Without that state the reconciler
/// re-imports anyway ("the seal releases the sync") and every assertion below would pass for a
/// reason that has nothing to do with the release — which is exactly what the first version of
/// these tests did, in MeshWeaver.Hosting.Test, where <c>SourceFingerprint</c> is not visible.</para>
///
/// <para>The two findings this file exists for (review on #4605): a hold taken by SHARING must
/// never be its own release trigger — its bundle can be on the shelf while the type it shares a
/// source with is still waiting, so releasing on it would re-import, re-hold the identical set and
/// repeat that on every later publication — and an entry written BEFORE that flag existed (#4595)
/// can say NEITHER, so it gets its own answer rather than borrowing one.</para>
/// </summary>
public class BundleHoldReleaseTest
{
    private static readonly RepoIdentity Crm = new("Systemorph", "MeshWeaver.Crm");
    private const string Identity = "sb43f9287dbd6922a7937bd24be103937";
    private const string OtherIdentity = "sffffffffffffffffffffffffffffffff";
    private const string SealedCommit = "76cdb553bd256f6cbc4b27caea5f139505787790";
    private const string OlderCommit = "d9e2768cb0000000000000000000000000000000";
    private const string Root = "Crm/Widget";
    private const string Sharer = "Crm/Report";
    private const string RootWanted = "aaaa000000000000";
    private const string SharerWanted = "bbbb000000000000";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static SealedSource Sealed() =>
        new("crm", "Systemorph/MeshWeaver.Crm", SealedCommit, true, null);

    /// <summary>🚨 The state a held source is IN: behind the seal, and already carrying a final
    /// verdict at the sealed commit — so the settled shortcut is live and the ONLY thing that can
    /// produce a re-import is the release predicate.</summary>
    private static GitHubSyncConfig Settled(GitHubSyncConfig config)
        => config with
        {
            LastAttemptedCommitSha = SealedCommit,
            LastAttemptWasFinal = true,
            LastSyncOutcome = GitHubSyncService.HeldOutcome,
            LastAttemptedConfigFingerprint = GitHubSyncService.SourceFingerprint(config),
        };

    private static GitHubSyncConfig Held(params BundleHeldNodeType[] held)
        => Settled(new GitHubSyncConfig
        {
            RepositoryUrl = "https://github.com/Systemorph/MeshWeaver.Crm",
            Branch = "main",
            LastSyncCommitSha = OlderCommit,
            BundleHeldNodeTypes = [..held],
        });

    /// <summary>A config as #4595 WROTE it: the entries carry no <c>heldBySharing</c> key at all,
    /// because the field did not exist. DESERIALIZED, never hand-constructed — what the absent key
    /// becomes is the thing under test.</summary>
    private static GitHubSyncConfig LegacyHeld(params (string Path, string Wanted)[] held)
    {
        var entries = string.Join(",", held.Select(h =>
            $$"""{"path":"{{h.Path}}","heldFingerprint":"0000000000000000","wantedFingerprint":"{{h.Wanted}}","identity":"{{Identity}}","reason":"held before the flag existed"}"""));
        var json = $$"""
            {
              "repositoryUrl": "https://github.com/Systemorph/MeshWeaver.Crm",
              "branch": "main",
              "lastSyncCommitSha": "{{OlderCommit}}",
              "bundleHeldNodeTypes": [{{entries}}]
            }
            """;
        var config = JsonSerializer.Deserialize<GitHubSyncConfig>(json, Options)!;
        config.BundleHeldNodeTypes.Should().NotBeNull();
        config.BundleHeldNodeTypes!.Should().AllSatisfy(h => h.HeldBySharing.Should().BeNull(
            "a record written before the field existed says NEITHER — read as false a legacy sharer "
            + "becomes a release trigger, read as true a legacy independent hold can never release"));
        return Settled(config);
    }

    private static BundleHeldNodeType Entry(string path, string wanted, bool? bySharing,
        string identity = Identity)
        => new(path, "0000000000000000", wanted, identity,
            bySharing == true ? "shares a held source" : "no bundle names this type")
        {
            HeldBySharing = bySharing,
        };

    private static PrebuiltBundleInventory Shelf(params (string Path, string Fingerprint)[] carried)
        => new(
            carried
                .GroupBy(c => c.Path, StringComparer.Ordinal)
                .ToImmutableDictionary(
                    g => g.Key,
                    g => g.Select(c => c.Fingerprint).ToImmutableHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal),
            carried.Length,
            SealedReadOutcome.Read);

    private static SealedSyncReconcile.Plan Decide(GitHubSyncConfig config,
        PrebuiltBundleInventory inventory)
        => SealedSyncReconcile.DecideWithInventory(
            Sealed(), Crm, "Crm", config, [Sealed()], Identity, [], inventory);

    [Fact]
    public void TheHeldStateIsNotMovedByTheSealAlone_SoNothingButAReleaseCanReImport()
    {
        // The negative control for every fact below: with an empty shelf the source stays put, so a
        // re-import in any other case IS the release firing rather than the seal.
        var plan = Decide(Held(Entry(Root, RootWanted, bySharing: false)), Shelf());

        plan.Action.Should().Be(SealedSyncReconcile.Action.None,
            "if the seal alone re-imported, every case below would pass for a reason that has nothing "
            + "to do with the release");
        (plan.Settled || plan.SteadyState).Should().BeTrue(
            "and it is never RECORDED as a hold — since policy module-sync-per-manifest-hash the seal "
            + "does not move a source that has a commit at all");
    }

    [Fact]
    public void AReleaseReImportsAtTheCommitWhoseSourcesWereHeld_NeverAtTheSeal()
    {
        var plan = Decide(Held(Entry(Root, RootWanted, bySharing: false)) with
            {
                LastAttemptedCommitSha = "feedfacefeedfacefeedfacefeedfacefeedface",
            },
            Shelf((Root, RootWanted)));

        plan.Action.Should().Be(SealedSyncReconcile.Action.ReconcileAtSealedCommit);
        plan.Commit.Should().Be("feedfacefeedfacefeedfacefeedfacefeedface",
            "the held sources are those of the attempted commit; the seal may be BEHIND the source");
    }

    [Fact]
    public void AnIndependentHold_ReleasesWhenItsBundleArrives()
    {
        var plan = Decide(Held(Entry(Root, RootWanted, bySharing: false)),
            Shelf((Root, RootWanted)));

        plan.Action.Should().Be(SealedSyncReconcile.Action.ReconcileAtSealedCommit);
        plan.Reason.Should().Contain(RootWanted);
    }

    [Fact]
    public void ASharersHold_IsNeverATrigger_EvenWhenItsOwnBundleIsOnTheShelf()
        => Decide(
                Held(Entry(Sharer, SharerWanted, bySharing: true),
                    Entry(Root, RootWanted, bySharing: false)),
                Shelf((Sharer, SharerWanted)))
            .Action.Should().Be(SealedSyncReconcile.Action.None,
                "the root is still missing, so the re-import would re-hold the identical set — and do "
                + "it again on every later publication. A sharer is re-judged by the import the "
                + "root's own release dispatches");

    [Fact]
    public void ALegacyHold_WhoseOwnBundleArrived_IsNotATriggerWhileAnotherHeldTypeWaits()
        => Decide(LegacyHeld((Root, RootWanted), (Sharer, SharerWanted)),
                Shelf((Root, RootWanted)))
            .Action.Should().Be(SealedSyncReconcile.Action.None,
                "whether the landed one was held on its own merit or as a SHARER is exactly what the "
                + "record cannot say, so releasing on it risks the futile loop");

    [Fact]
    public void ALegacyHold_IsATrigger_OnceTheShelfCarriesTheWholeHeldSet()
    {
        var plan = Decide(LegacyHeld((Root, RootWanted), (Sharer, SharerWanted)),
            Shelf((Root, RootWanted), (Sharer, SharerWanted)));

        plan.Action.Should().Be(SealedSyncReconcile.Action.ReconcileAtSealedCommit,
            "whatever the two entries were, this re-import clears BOTH — it cannot be futile, and it "
            + "rewrites the list WITH the flag, which ends the unknown state after one conclusion");
        plan.Reason.Should().Contain("every held type");
    }

    [Fact]
    public void AnUnreadableShelf_ReleasesNothing()
        => Decide(Held(Entry(Root, RootWanted, bySharing: false)),
                new PrebuiltBundleInventory(
                    ImmutableDictionary<string, ImmutableHashSet<string>>.Empty, 0,
                    SealedReadOutcome.Unreadable))
            .Action.Should().Be(SealedSyncReconcile.Action.None,
                "a re-attempt taken from a measurement that was not made is not a release — here "
                + "cannot-tell IS do-nothing, because doing nothing preserves the hold");

    [Fact]
    public void AHoldJudgedUnderAnotherIdentity_IsRetaken_WhateverTheShelfSays()
    {
        var plan = Decide(
            Held(Entry(Root, RootWanted, bySharing: true, identity: OtherIdentity)), Shelf());

        plan.Action.Should().Be(SealedSyncReconcile.Action.ReconcileAtSealedCommit,
            "the instance has rolled, so the judgement is about bytes it no longer runs — void, not "
            + "merely old, and that holds for a sharer's entry too");
        plan.Reason.Should().Contain("no longer runs");
    }
}

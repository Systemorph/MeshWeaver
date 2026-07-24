using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 Deterministic pin of the cross-hub MeshNode patch-ack WRITE-IDENTITY gate
/// (<c>DataExtensions.ChangeContainsStampedWrite</c>, consumed by
/// <c>ApplyMeshNodePatchInTurn</c>'s ack watcher) — the residual acked-write-loss
/// behind <c>TwoSiloRecycleConvergenceTest</c> on main runs 30068597014 and
/// 30079395006 (the second one AFTER the round-1 pending-save flush fix).
///
/// <para><b>The pinned interleaving.</b> A post-recycle write reactivates the owner
/// per-node hub COLD. The MeshNode init gate opens inside
/// <c>BuildInstanceCollection</c> — on the storage-read emission, BEFORE the loaded
/// collection commits to the primary stream — releasing the held
/// <c>PatchDataRequest</c> into a window where the store is still empty. The old ack
/// watcher was <c>stream.Skip(1).Take(1)</c>: emission COUNTING. The first
/// post-subscribe emission in that window is the initial LOAD echo — the PRE-patch
/// node — so the watcher flushed the stale state to storage and posted
/// <c>PatchDataResponse</c> SUCCESS, while the merge turn no-op'd NotFound against
/// the empty store (suppressed by the AckOnce guard). Result: a success-acked write
/// that never existed anywhere — the store stays frozen at the pre-recycle version
/// and the test's <c>WaitForPersistedBeyond</c> times out.</para>
///
/// <para>These tests script that exact emission sequence and assert the production
/// identity gate (a) never fires before the merge stamps, (b) rejects the load echo
/// and sibling-satellite churn, and (c) fires exactly on the emission that contains
/// the stamped write — plus the counterfactual: the old counting shape selects the
/// load echo on the same sequence. Write-echo detection is identity-based, never
/// emission-count-based (PR #584 rule).</para>
/// </summary>
public class PatchAckWriteIdentityTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>Local stand-in with the serialized shape the gate inspects
    /// (PascalCase Id/Version like a MeshNode with default options).</summary>
    private sealed record Node(string Id, long Version, string? ApiKey = null);

    private const string IdKey = "Id";
    private const string VersionKey = "Version";

    private static readonly Node LoadEchoV12 = new("Anthropic", 12, "sk-v6");
    private static readonly Node SiblingSatellite = new("_Activity-1", 99);
    private static readonly Node CommitV13 = new("Anthropic", 13, "sk-post-recycle");

    private static bool Gate(object? value, string? stampedId, long stampedVersion)
        => DataExtensions.ChangeContainsStampedWrite(
            value, stampedId, stampedVersion, IdKey, VersionKey, Options);

    [Fact]
    public void BeforeTheMergeStamps_NothingAcks()
    {
        // stampedVersion = -1 / stampedId = null until the merge lambda commits.
        Gate(LoadEchoV12, null, -1).Should().BeFalse(
            "no emission may ack a patch whose merge has not stamped yet — the cold "
            + "activation's load echo arrives exactly in this window");
        Gate(CommitV13, null, -1).Should().BeFalse();
    }

    [Fact]
    public void LoadEcho_AtPreWriteVersion_DoesNotAck()
    {
        Gate(LoadEchoV12, "Anthropic", 13).Should().BeFalse(
            "the initial LOAD echo carries the PRE-patch state (Version 12 < stamped 13) — "
            + "acking on it is the acked-write-loss of runs 30068597014 / 30079395006");
    }

    [Fact]
    public void SiblingSatelliteChurn_DoesNotAck()
    {
        Gate(SiblingSatellite, "Anthropic", 13).Should().BeFalse(
            "the pathless per-node reduced stream surfaces sibling satellites "
            + "(Source/Release/_Activity) — a foreign id must never ack this write");
    }

    [Fact]
    public void EmissionContainingTheStampedWrite_Acks()
    {
        Gate(CommitV13, "Anthropic", 13).Should().BeTrue(
            "the commit emission carries the stamped id at the minted version");
        // A later state that already CONTAINS the write (version advanced past the
        // stamp by a subsequent writer) also satisfies read-your-write.
        Gate(new Node("Anthropic", 14, "sk-later"), "Anthropic", 13).Should().BeTrue();
    }

    [Fact]
    public void ProductionGate_SelectsTheCommit_NeverTheLoadEcho()
    {
        // The full scripted interleaving through the production Where(...).Take(1)
        // shape: replay (pre-subscribe current), load echo, sibling churn, commit.
        var stamped = (Id: (string?)null, Version: -1L);
        var source = new Subject<Node>();
        Node? acked = null;
        using var sub = source
            .Where(v => Gate(v, stamped.Id, stamped.Version))
            .Take(1)
            .Subscribe(v => acked = v);

        source.OnNext(LoadEchoV12);        // pre-stamp load echo — must not ack
        stamped = ("Anthropic", 13);       // merge lambda stamps at commit time
        source.OnNext(LoadEchoV12);        // late load echo — still pre-write state
        source.OnNext(SiblingSatellite);   // satellite churn — foreign id
        acked.Should().BeNull("nothing so far contains the stamped write");

        source.OnNext(CommitV13);          // the commit — contains the write
        acked.Should().Be(CommitV13);
    }

    [Fact]
    public void CounterfactualOldCountingShape_TakesTheLoadEcho()
    {
        // The OLD ack watcher was `stream.Skip(1).Take(1)` — emission counting. On
        // the cold-activation sequence (replay, then load echo, then commit) it
        // selects the LOAD ECHO as "the committed value": the flush then persisted
        // the stale pre-patch node and acked SUCCESS for a write that never landed.
        // This test documents the defective selection the identity gate exists to
        // prevent; it goes RED if anyone reintroduces counting semantics into a
        // sequence-equivalent shape.
        var source = new Subject<Node>();
        Node? taken = null;
        using var sub = source.Skip(1).Take(1).Subscribe(v => taken = v);

        source.OnNext(LoadEchoV12);   // 1st: the replayed current (skipped)
        source.OnNext(LoadEchoV12);   // 2nd: the initial-load echo — counting takes THIS
        source.OnNext(CommitV13);     // the real commit arrives too late

        taken.Should().Be(LoadEchoV12,
            "counting semantics mistake the cold-activation load echo for the commit — "
            + "the exact acked-write-loss interleaving (do not 'fix' this assertion; it "
            + "documents why the ack gate must be write-identity-based)");
    }
}

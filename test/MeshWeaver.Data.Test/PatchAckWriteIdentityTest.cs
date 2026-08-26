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

    /// <summary>Probe payload whose converter COUNTS every time the node is turned into a
    /// document — the instrument for the O(1) pin below. Instance state on a per-test options
    /// object, never static.</summary>
    private sealed class CountingPayload
    {
        public string Text { get; init; } = "payload";
    }

    private sealed class CountingPayloadConverter : System.Text.Json.Serialization.JsonConverter<CountingPayload>
    {
        public int Writes { get; private set; }

        public override CountingPayload Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // A converter MUST leave the reader positioned on the last token of the value it
            // consumed; returning without advancing would strand the reader mid-value and throw
            // on the next read. This converter exists to count WRITES, so the value itself is
            // discarded — but it is still consumed properly, so the type stays deserializable if
            // a later test round-trips it.
            reader.Skip();
            return new();
        }

        public override void Write(
            Utf8JsonWriter writer, CountingPayload value, JsonSerializerOptions options)
        {
            Writes++;
            writer.WriteStartObject();
            writer.WriteString("text", value.Text);
            writer.WriteEndObject();
        }
    }

    private sealed record PayloadNode(string Id, long Version, CountingPayload Payload);

    private static JsonSerializerOptions CountingOptions(
        CountingPayloadConverter counter, JsonNamingPolicy? namingPolicy = null)
    {
        var options = new JsonSerializerOptions
        {
            // Explicit so the gate's contract lookup is available on the FIRST call — a bare
            // options object has no resolver until something serializes through it.
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy = namingPolicy
        };
        options.Converters.Add(counter);
        return options;
    }

    /// <summary>
    /// 🚨 The gate must answer from the node's two identity scalars WITHOUT materialising the
    /// node — issue #2339. It is evaluated once per still-pending patch on EVERY emission of the
    /// owner's reduced stream, so a burst of K concurrent cross-hub writes runs it K(K+1)/2
    /// times; building the whole document each time made that O(K² · nodeSize) and, because a
    /// slower emission leaves MORE patches pending, self-amplifying — the owner applied 288
    /// writes in 1.5 s while its subscribers saw a ~2.7 s wall with no frames at all.
    /// <para>Deterministic, not a timing assertion: the payload converter fires exactly when a
    /// document is built, so the count IS the observation. The explicit serialisation first is
    /// the control — it proves the instrument can fire, so the zero after it means something.</para>
    /// </summary>
    [Fact]
    public void TheGate_AnswersWithoutMaterialisingTheNode()
    {
        var counter = new CountingPayloadConverter();
        var options = CountingOptions(counter);
        var node = new PayloadNode("Anthropic", 13, new CountingPayload());

        // Control: the instrument fires when the document really is built.
        var document = JsonSerializer.Serialize(node, node.GetType(), options);
        counter.Writes.Should().Be(1, "the converter must be reached by a genuine serialisation");
        // ...and the probe type really does round-trip, so the converter's Read consumes its
        // value rather than stranding the reader mid-document.
        JsonSerializer.Deserialize<PayloadNode>(document, options)!.Id.Should().Be("Anthropic");

        for (var i = 0; i < 50; i++)
        {
            GateWith(node, "Anthropic", 13, options).Should().BeTrue();
            GateWith(node, "Anthropic", 14, options).Should().BeFalse();
            GateWith(node, "Other", 13, options).Should().BeFalse();
        }

        counter.Writes.Should().Be(1,
            "150 gate evaluations must not build a single document — the ack watcher runs this "
            + "predicate once per pending patch per emission, so any per-call serialisation is "
            + "quadratic in the burst and self-amplifying (#2339)");
    }

    /// <summary>The contract read must resolve the SAME identity the document path resolves,
    /// including under a naming policy — the keys it matches are the effective JSON names.</summary>
    [Fact]
    public void TheGate_ResolvesTheSameIdentity_UnderACamelCasePolicy()
    {
        var counter = new CountingPayloadConverter();
        var options = CountingOptions(counter, JsonNamingPolicy.CamelCase);
        var node = new PayloadNode("Anthropic", 13, new CountingPayload());

        DataExtensions.ChangeContainsStampedWrite(node, "Anthropic", 13, "id", "version", options)
            .Should().BeTrue("camelCase is the naming policy the mesh hub serializes MeshNode with");
        DataExtensions.ChangeContainsStampedWrite(node, "Anthropic", 14, "id", "version", options)
            .Should().BeFalse();
        counter.Writes.Should().Be(0);
    }

    /// <summary>A version the serializer OMITS (0 under WhenWritingDefault) must read as 0 —
    /// the same default the document path applies when the key is absent.</summary>
    [Fact]
    public void NeverStampedVersion_ReadsAsZero_AndCannotAck()
    {
        var counter = new CountingPayloadConverter();
        var options = CountingOptions(counter);
        var node = new PayloadNode("Anthropic", 0, new CountingPayload());

        GateWith(node, "Anthropic", 1, options).Should().BeFalse(
            "a node still at Version 0 cannot contain a write stamped at 1");
        GateWith(node, "Anthropic", 0, options).Should().BeTrue(
            "stampedVersion 0 is satisfied by version 0 — the >= rule, unchanged");
    }

    private static bool GateWith(object? value, string? stampedId, long stampedVersion, JsonSerializerOptions options)
        => DataExtensions.ChangeContainsStampedWrite(
            value, stampedId, stampedVersion, IdKey, VersionKey, options);

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

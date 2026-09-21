#pragma warning disable CS1591

using System.Text.Json.Nodes;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The other half of #2469: its fix removed a false POSITIVE and introduced a false NEGATIVE.
///
/// <para>Confirmation compares the caller's submitted leaves against the live node. But the caller's
/// RAW json and the STORED json are not the same document — the owner persists what the serializer
/// writes. Every value the serializer normalizes away or rewrites therefore becomes an expectation
/// that can never be satisfied, so a write that LANDED is reported as
/// <i>"did not land within the confirmation window"</i>, and the message tells the caller to retry.</para>
///
/// <para><b>Measured 2026-09-21 on a live approval node</b> (memex.systemorph.com,
/// <c>rbuergi/ClientToCounterpartyMigration/_Approval/…</c>): a patch carrying
/// <c>"status": "Pending"</c> — <c>ApprovalStatus</c>'s ZERO member, which the serializer omits —
/// was reported as not landed, three times. The third attempt had ALSO changed <c>purpose</c>, and
/// that change was in the store at version 4 while the caller was being told the write failed.
/// A control patch of a node-level scalar on the same node, and a wholesale Update, both confirmed
/// — Update because it round-trips the node through the serializer before capturing its
/// expectation, which is exactly the step Patch was missing.</para>
///
/// <para>These are unit tests on the projection because that is where the defect lives: an
/// end-to-end reproduction needs a value the serializer drops AND a real owner, and the interesting
/// part — which expectation is built — is decided before any write is posted.</para>
/// </summary>
public class PatchExpectationIsNormalizedTest
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// 🚨 THE production case. The caller sends a default-valued enum; the serializer omits it. The
    /// expectation must not demand a VALUE that will never be written — but it must still demand
    /// the key's ABSENCE, or the check stops being a check at all.
    /// </summary>
    [Fact]
    public void ADefaultValuedEnumTheSerializerOmits_IsExpectedToBeAbsent()
    {
        var delta = Obj("""{"content":{"purpose":"Migrate 8 roots","status":"Pending"}}""");
        // What the serializer actually writes: no `status` (zero member omitted).
        var stored = Obj("""{"content":{"purpose":"Migrate 8 roots","approver":"rbuergi"}}""");

        var expected = MeshOperations.ProjectTouched(stored, delta);

        // Expected as an explicit null — FieldsLandedIn reads that as "missing or null".
        Assert.True(expected["content"]!.AsObject().ContainsKey("status"));
        Assert.Null(expected["content"]!["status"]);
        Assert.True(MeshOperations.FieldsLandedIn(stored, expected));
    }

    /// <summary>
    /// 🚨 The other side of the SAME case, and the one that could have falsified the fix: dropping
    /// a serializer-omitted key from the expectation does not merely fail to detect a refusal — it
    /// makes a patch that touched ONLY such a field report success unconditionally, because the
    /// expectation it leaves behind (<c>{"content":{}}</c>) is satisfied by the first emission
    /// whatever the node holds. That is #2469's false POSITIVE coming back, which is strictly
    /// worse than the false negative this class removes: a write that lies about having happened
    /// never gets retried.
    /// </summary>
    [Fact]
    public void ARefusedWriteOfADefaultValue_StillFailsConfirmation()
    {
        var delta = Obj("""{"content":{"status":"Pending"}}""");
        var stored = Obj("""{"content":{"approver":"rbuergi"}}""");
        // The owner refused the write; the live node still carries the value it had.
        var refused = Obj("""{"content":{"status":"Approved","approver":"rbuergi"}}""");

        var expected = MeshOperations.ProjectTouched(stored, delta);

        Assert.False(MeshOperations.FieldsLandedIn(refused, expected));
        // …and the write that DID land still confirms, so this is not just a stricter check.
        Assert.True(MeshOperations.FieldsLandedIn(stored, expected));
    }

    /// <summary>The raw-value comparison this replaces could never pass — the negative control.</summary>
    [Fact]
    public void TheRawCallerDelta_CouldNeverBeSatisfied()
    {
        var delta = Obj("""{"content":{"purpose":"Migrate 8 roots","status":"Pending"}}""");
        var stored = Obj("""{"content":{"purpose":"Migrate 8 roots","approver":"rbuergi"}}""");

        Assert.False(MeshOperations.FieldsLandedIn(stored, delta),
            "if this passed, the old behaviour was fine and there was nothing to fix");
    }

    /// <summary>A key that IS written stays expected — the fix must not weaken the check.</summary>
    [Fact]
    public void AFieldThatSurvivesSerialization_IsStillExpected()
    {
        var delta = Obj("""{"content":{"purpose":"new text","status":"Pending"}}""");
        var stored = Obj("""{"content":{"purpose":"new text"}}""");

        var expected = MeshOperations.ProjectTouched(stored, delta);

        Assert.Equal("new text", expected["content"]!["purpose"]!.GetValue<string>());
        // …and it genuinely fails when that field did NOT land.
        var notLanded = Obj("""{"content":{"purpose":"OLD text"}}""");
        Assert.False(MeshOperations.FieldsLandedIn(notLanded, expected));
    }

    /// <summary>
    /// 🚨 #2469's guarantee must survive: a concurrent writer touching a DIFFERENT field neither
    /// satisfies nor fails the check, because only the caller's own key paths are ever projected.
    /// </summary>
    [Fact]
    public void AConcurrentWritersUnrelatedField_NeitherSatisfiesNorFails()
    {
        var delta = Obj("""{"content":{"policy":"None"}}""");
        var stored = Obj("""{"content":{"policy":"None","checkedAt":"2026-09-21T05:00:00Z"}}""");

        var expected = MeshOperations.ProjectTouched(stored, delta);

        Assert.False(expected["content"]!.AsObject().ContainsKey("checkedAt"));
        // The poller moved on again — still confirmed, because only `policy` was ever asked about.
        var pollerMovedOn = Obj("""{"content":{"policy":"None","checkedAt":"2026-09-21T06:00:00Z"}}""");
        Assert.True(MeshOperations.FieldsLandedIn(pollerMovedOn, expected));
        // But the caller's OWN field failing to land is still caught.
        var refused = Obj("""{"content":{"policy":"Automatic","checkedAt":"2026-09-21T06:00:00Z"}}""");
        Assert.False(MeshOperations.FieldsLandedIn(refused, expected));
    }

    /// <summary>A value the serializer REWRITES is compared in its stored form, not as typed.</summary>
    [Fact]
    public void ARewrittenValue_IsComparedInItsStoredForm()
    {
        var delta = Obj("""{"content":{"dueDate":"2026-09-28T00:00:00+00:00"}}""");
        var stored = Obj("""{"content":{"dueDate":"2026-09-28T00:00:00Z"}}""");

        var expected = MeshOperations.ProjectTouched(stored, delta);

        Assert.Equal("2026-09-28T00:00:00Z", expected["content"]!["dueDate"]!.GetValue<string>());
        Assert.True(MeshOperations.FieldsLandedIn(stored, expected));
    }

    /// <summary>
    /// A default-valued number or bool projects by the same rule as the enum — expected ABSENT,
    /// satisfied when it landed and failed when the old value is still there.
    /// </summary>
    [Theory]
    [InlineData("""{"content":{"name":"x","order":0}}""", "order", """{"content":{"name":"x","order":7}}""")]
    [InlineData("""{"content":{"name":"x","enabled":false}}""", "enabled", """{"content":{"name":"x","enabled":true}}""")]
    public void ADefaultValuedScalarTheSerializerOmits_IsExpectedToBeAbsent(
        string deltaJson, string omitted, string refusedJson)
    {
        var stored = Obj("""{"content":{"name":"x"}}""");

        var expected = MeshOperations.ProjectTouched(stored, Obj(deltaJson));

        Assert.True(expected["content"]!.AsObject().ContainsKey(omitted));
        Assert.Null(expected["content"]![omitted]);
        Assert.True(MeshOperations.FieldsLandedIn(stored, expected));

        // The refused write: the live node kept the non-default value the caller was replacing.
        Assert.False(MeshOperations.FieldsLandedIn(Obj(refusedJson), expected));
    }

    /// <summary>Node-level fields project the same way — the patch that DID confirm in production.</summary>
    [Fact]
    public void NodeLevelScalars_ProjectUnchanged()
    {
        var delta = Obj("""{"name":"Approval: … (pending your decision)"}""");
        var stored = Obj("""{"name":"Approval: … (pending your decision)","version":2}""");

        var expected = MeshOperations.ProjectTouched(stored, delta);

        Assert.Single(expected);
        Assert.True(MeshOperations.FieldsLandedIn(stored, expected));
    }

    /// <summary>
    /// An explicit null (RFC 7396 "remove") is a real instruction, and the shape the store actually
    /// produces is the key GONE — not a key carrying a JSON null. Pinned in that shape deliberately:
    /// written the other way this test passes under a projection that drops absent keys, and so
    /// proves nothing about the case it is named for.
    /// </summary>
    [Fact]
    public void AnExplicitNullRemoval_ExpectsTheKeyToBeGone()
    {
        var delta = Obj("""{"content":{"icon":null}}""");
        // After the merge the key is gone, so the serializer writes no `icon` at all.
        var stored = Obj("""{"content":{"name":"x"}}""");

        var expected = MeshOperations.ProjectTouched(stored, delta);

        Assert.True(expected["content"]!.AsObject().ContainsKey("icon"));
        Assert.Null(expected["content"]!["icon"]);
        Assert.True(MeshOperations.FieldsLandedIn(stored, expected));
        // A live key explicitly set to null is "removed" too — RFC 7396 makes them the same state.
        Assert.True(MeshOperations.FieldsLandedIn(Obj("""{"content":{"name":"x","icon":null}}"""), expected));
        // But a removal the owner REFUSED is caught.
        Assert.False(MeshOperations.FieldsLandedIn(Obj("""{"content":{"name":"x","icon":"Folder"}}"""), expected));
    }
}

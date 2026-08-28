#pragma warning disable CS1591

using System.Text.Json.Nodes;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #2469: "MCP patch reports success and does not land: Admin/UpdatePolicy refuses an admin write
/// silently, and the version-transition suffix is the only tell."
///
/// <para><b>The mechanism, read directly out of the write path (MeshNodeStreamExtensions.cs):</b>
/// <c>UpdateRemote</c> posts the caller's patch to the owning per-node hub and waits up to
/// <c>UpdateResponseWaitBound</c> (2s) for its verdict. On a BUSY owner — exactly what
/// <c>Admin/UpdatePolicy</c> is, ticked by the self-update poller every few seconds — that ack can
/// arrive late. When it does, <c>UpdateRemote</c> emits the OPTIMISTIC (unconfirmed) snapshot and
/// moves on; the real, LATE verdict — success or a genuine <c>Conflict</c> refusal — is only ever
/// logged server-side (<c>LATE_ACK</c> / <c>LATE_NACK_TERMINAL</c>), never propagated back to a
/// caller who already got an answer. <c>EditContent</c> was fixed for exactly this shape in #1716
/// ("Gate the success string on the edit provably landing... instead of lying 'Edited:'"), but
/// <c>Patch</c>/<c>Update</c> never got the same treatment — they confirmed via
/// <c>WaitForReadYourWrites</c>, which only checks that SOME version bump happened on the node.
/// That is not sufficient: a concurrent writer touching a DIFFERENT field (the poller's
/// <c>checkedAt</c>/<c>latestAvailableTag</c>) bumps the version too, so the confirmation is
/// satisfied whether or not the caller's OWN field ever changed.</para>
///
/// <para><b>Why this is a unit test on the internal check, not an end-to-end timing race.</b> The
/// obvious integration reproduction — race a second writer against the operator's patch inside a
/// <c>MonolithMeshTestBase</c> mesh — was attempted and does not reproduce reliably: MeshNode
/// cross-hub writes to one path share ONE <c>IMeshNodeStreamCache</c>-owned write queue
/// (silo-scoped, not per-caller), so a second write to the SAME path cannot even be POSTED until
/// the first one's slot releases (on its ack, or the queue's own <c>QueueAdvanceBound</c>, 5s) —
/// which is precisely the ordering guarantee that makes <c>stream.Update</c> safe. That guarantee
/// also means a single-silo test can never get a second writer's change live BEFORE the first
/// write's own confirmation check runs, so the exact production race (genuinely independent
/// replicas / a real Postgres flush pushing the ack past 2s) is not reproducible fast and
/// deterministically without a multi-silo cluster. The defect is not in doubt — it is read
/// directly off the code above — so this test pins it precisely at the level it actually lives:
/// the confirmation PREDICATE <c>Patch</c>/<c>Update</c> use to decide "did MY field land".
/// </para>
///
/// <para>Fixture: the exact shape a busy <c>Admin/UpdatePolicy</c> produces — an operator wants
/// <c>policy: "None"</c>, but at CONFIRMATION TIME the live node shows the poller's
/// <c>checkedAt</c> has moved on (proving a real write occurred) while <c>policy</c> is still
/// whatever it was before. The OLD check (<c>live.Version &gt; versionBefore</c>, inlined here
/// exactly as <c>WaitForReadYourWrites</c> implements it) says "confirmed" — SetsPolicyAwayFrom
/// wrongly. The fix, <see cref="MeshOperations.FieldsLandedIn"/>, does not.</para>
/// </summary>
public class PatchLandedWriteCheckTest
{
    private const long VersionBefore = 1492;

    /// <summary>What Patch captures as the caller's own intent before content gets deep-merged —
    /// see MeshOperations.Patch's <c>expectedFields</c> snapshot.</summary>
    private static JsonObject ExpectedFields => JsonNode.Parse("""{"content":{"policy":"None"}}""")!.AsObject();

    /// <summary>A live node where a DIFFERENT writer's field (checkedAt) advanced — proving SOME
    /// write landed — while the caller's OWN field (policy) never changed.</summary>
    private static JsonObject LiveWithOnlyThePollerHavingWritten => JsonNode.Parse("""
        {
          "version": 1493,
          "content": {
            "requireCiGreen": true,
            "latestAvailableTag": "3.0.0-rc8.ci.6223",
            "checkedAt": "2026-08-28T09:57:45.3029952+00:00"
          }
        }
        """)!.AsObject();

    /// <summary>A live node where the caller's OWN field genuinely landed too.</summary>
    private static JsonObject LiveWithTheCallersOwnFieldLanded => JsonNode.Parse("""
        {
          "version": 1494,
          "content": {
            "policy": "None",
            "requireCiGreen": true,
            "latestAvailableTag": "3.0.0-rc8.ci.6223",
            "checkedAt": "2026-08-28T09:57:45.3029952+00:00"
          }
        }
        """)!.AsObject();

    /// <summary>Inlines the OLD confirmation predicate exactly as <c>WaitForReadYourWrites</c>
    /// implements it (MeshOperations.cs: <c>n.Version > versionBefore</c>) — the version-only check
    /// this issue is about.</summary>
    private static bool OldVersionOnlyCheckConfirms(JsonObject live, long versionBefore) =>
        live["version"]!.GetValue<long>() > versionBefore;

    [Fact]
    public void OldCheck_ConfirmsOnAnUnrelatedWritersVersionBump_EvenThoughTheCallersFieldNeverLanded()
    {
        var live = LiveWithOnlyThePollerHavingWritten;

        // This is the #2469 bug, pinned directly: the OLD criterion — the only one Patch/Update
        // used before this fix — says "confirmed" purely because the POLLER'S write bumped the
        // version. Nothing here required "policy" to be "None".
        OldVersionOnlyCheckConfirms(live, VersionBefore).Should().BeTrue(
            "a version-only check cannot tell the caller's own write apart from an unrelated one — "
            + "this is exactly why Patch could answer 'Patched: ... (v1492 -> v1493)' while policy "
            + "was never set");
    }

    [Fact]
    public void FieldsLandedIn_RefusesToConfirm_WhenTheCallersOwnFieldNeverLanded()
    {
        var live = LiveWithOnlyThePollerHavingWritten;

        // The fix: the SAME live node the old check wrongly confirmed on is correctly refused here,
        // because the caller's OWN leaf ("content.policy") is absent, not "None".
        MeshOperations.FieldsLandedIn(live, ExpectedFields).Should().BeFalse(
            "the caller's own field must actually match before Patch/Update may report success — "
            + "an unrelated writer's version bump must not be mistaken for it");
    }

    [Fact]
    public void FieldsLandedIn_Confirms_WhenTheCallersOwnFieldGenuinelyLanded()
    {
        var live = LiveWithTheCallersOwnFieldLanded;

        MeshOperations.FieldsLandedIn(live, ExpectedFields).Should().BeTrue(
            "a write that genuinely landed the caller's own field must still be reported as success");
    }

    [Fact]
    public void FieldsLandedIn_IgnoresFieldsTheCallerNeverTouched()
    {
        // The poller's own concurrent bookkeeping write: only checkedAt/latestAvailableTag change,
        // policy is untouched — this must never be mistaken for a refusal of a change nobody asked
        // for, and must never be mistaken for confirmation of one either.
        var pollerOwnWrite = JsonNode.Parse("""
            {"version": 1493, "content": {"checkedAt": "2026-08-28T10:00:00Z"}}
            """)!.AsObject();
        var pollerOwnExpectedFields = JsonNode.Parse(
            """{"content":{"checkedAt":"2026-08-28T10:00:00Z"}}""")!.AsObject();

        MeshOperations.FieldsLandedIn(pollerOwnWrite, pollerOwnExpectedFields).Should().BeTrue();
    }
}

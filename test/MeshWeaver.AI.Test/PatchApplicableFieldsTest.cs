#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 PINS "NEVER REPORT A WRITE THAT DID NOT HAPPEN."
///
/// <para><c>MeshOperations.Patch</c> merges a FIXED set of keys onto the existing node. Anything
/// else in the payload was parsed and then silently discarded — while the tool still answered
/// <c>"Patched: {path}"</c>. The caller has no way to tell that from a real write.</para>
///
/// <para><b>What it cost.</b> On 2026-08-05, seven <c>{"mainNode":"AgenticPension"}</c> patches were
/// issued against <c>AccessAssignment</c> nodes on a customer mesh whose <c>MainNode</c> was empty —
/// meaning each grant was scoped to ROOT (every partition), including a <c>Public — Editor</c> that
/// gave every authenticated user write access mesh-wide. All seven returned <c>"Patched"</c>. None
/// changed anything: version stayed 1, the two-month-old timestamp stayed. The repair was reported
/// as done and the privilege escalation sat exactly where it was; only re-reading the nodes caught
/// it. A write that fails loudly gets retried — one that lies does not.</para>
/// </summary>
public class PatchApplicableFieldsTest
{
    private static JsonObject Fields(params string[] keys)
    {
        var obj = new JsonObject();
        foreach (var key in keys)
            obj[key] = "x";
        return obj;
    }

    [Theory]
    [InlineData("name")]
    [InlineData("description")]      // was silently dropped before this fix
    [InlineData("icon")]
    [InlineData("category")]
    [InlineData("order")]
    [InlineData("content")]
    [InlineData("preRenderedHtml")]
    [InlineData("mainNode")]         // the one the incident turned on
    public void AnAppliedField_IsAccepted(string key) =>
        Assert.Empty(MeshOperations.UnapplicableFields(Fields(key)));

    /// <summary>
    /// Identity and audit fields belong to the mesh. Refusing them is the point: patching
    /// <c>version</c> or <c>path</c> never worked, and answering "Patched" to it was the lie.
    /// </summary>
    [Theory]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("id")]
    [InlineData("namespace")]
    [InlineData("createdBy")]
    [InlineData("lastModified")]
    [InlineData("nodeType")]
    [InlineData("typo")]
    public void AnUnappliedField_IsRefused(string key) =>
        Assert.Equal([key], MeshOperations.UnapplicableFields(Fields(key)));

    /// <summary>Every offending key is named at once — an agent should not fix them one round-trip
    /// at a time.</summary>
    [Fact]
    public void EveryUnapplicableField_IsReported_NotJustTheFirst()
    {
        var unapplicable = MeshOperations.UnapplicableFields(Fields("name", "version", "content", "path"));

        Assert.Equal(["version", "path"], unapplicable);
    }

    /// <summary>
    /// The matching is case-insensitive because the wire casing depends on the host's naming
    /// policy — a case mismatch would refuse a field that Patch does in fact apply.
    /// </summary>
    [Fact]
    public void TheMatchIsCaseInsensitive() =>
        Assert.Empty(MeshOperations.UnapplicableFields(Fields("MainNode", "PreRenderedHtml")));

    /// <summary>
    /// The guard list and the merge in <c>Patch</c> are one fact expressed twice. If they drift,
    /// Patch either refuses a field it supports or claims success for one it drops — so pin the
    /// list itself, and let this fail loudly when someone changes only one side.
    /// </summary>
    [Fact]
    public void ThePatchableSet_IsExactlyWhatPatchMerges() =>
        Assert.Equal(
            ["category", "content", "description", "icon", "mainNode", "name", "order", "preRenderedHtml"],
            MeshOperations.PatchableFields.Order().ToArray());

    [Fact]
    public void AnEmptyPayload_HasNothingToRefuse() =>
        Assert.Empty(MeshOperations.UnapplicableFields(new JsonObject()));
}

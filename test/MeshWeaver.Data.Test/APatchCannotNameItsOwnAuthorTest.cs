using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// The OWNER side of "a client cannot choose its own author" (ingress audit for Plugins#2601):
/// <see cref="DataExtensions.ApplyMeshNodeMerge"/>, the seam the <c>PatchDataRequest</c> handler
/// runs for every cross-hub MeshNode write. A client sends its own merge patch, so the in-process
/// stamping in <c>MeshNodeStreamHandle</c> never runs for it. The owner applied
/// <c>lastModifiedBy</c> and <c>createdBy</c> from the patch verbatim.
/// </summary>
public class APatchCannotNameItsOwnAuthorTest
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly AccessContext Mallory = new() { ObjectId = "mallory" };
    private static readonly AccessContext System = new() { ObjectId = "system-security" };

    private static JsonObject Apply(JsonObject patch, AccessContext? sender)
    {
        var live = new JsonObject
        {
            ["CreatedBy"] = "alice",
            ["LastModifiedBy"] = "alice",
            ["Name"] = "before",
        };
        var message = new PatchDataRequest(new MeshNodeReference(), new RawJson(patch.ToJsonString(Options)));
        DataExtensions.ApplyMeshNodeMerge(live, patch, isMeshNode: true, message, Options, logger: null, "alice/note", sender);
        return live;
    }

    private static string? Read(JsonObject node, string key) =>
        node.TryGetPropertyValue(key, out var value) && value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Re-asserting the live author is a no-op, not a claim: it stays a no-op.</summary>
    [Fact]
    public void ReAssertingTheLiveAuthor_IsLeftAlone()
    {
        var live = Apply(new JsonObject { ["LastModifiedBy"] = "alice", ["CreatedBy"] = "alice" }, Mallory);

        Read(live, "LastModifiedBy").Should().Be("alice", "the patch claims nothing the node does not already say");
        Read(live, "CreatedBy").Should().Be("alice");
    }

    [Fact]
    public void AClientNamingSystemAsTheAuthor_IsRecordedAsItself()
    {
        var live = Apply(new JsonObject { ["Name"] = "after", ["LastModifiedBy"] = "system-security" }, Mallory);

        Read(live, "LastModifiedBy").Should().Be("mallory",
            "the author of a write is the delivery's principal, never a value in the patch");
        Read(live, "Name").Should().Be("after", "the content change itself still lands");
    }

    [Fact]
    public void AClientCannotRewriteWhoCreatedTheNode()
    {
        var live = Apply(new JsonObject { ["CreatedBy"] = "system-security" }, Mallory);

        Read(live, "CreatedBy").Should().Be("alice", "who created a node is not rewritten by a patch");
    }

    /// <summary>
    /// A raw client patch that changes content but omits the author still records the SENDER:
    /// otherwise the change would be credited to whoever wrote the node last.
    /// </summary>
    [Fact]
    public void AContentChangeWithoutAnAuthor_RecordsTheSender()
    {
        var live = Apply(new JsonObject { ["Name"] = "after" }, Mallory);

        Read(live, "LastModifiedBy").Should().Be("mallory", "the sender made this change, not the previous author");
    }

    /// <summary>A patch that changes nothing stays a no-op: no author is recorded for it.</summary>
    [Fact]
    public void APatchThatChangesNothing_IsLeftAlone()
    {
        var live = Apply(new JsonObject { ["Name"] = "before" }, Mallory);

        Read(live, "LastModifiedBy").Should().Be("alice", "a no-op must stay a no-op for the owner's no-change backstop");
    }

    [Fact]
    public void ThePlatformKeepsTheStampItCarries()
    {
        var live = Apply(new JsonObject { ["LastModifiedBy"] = "bob", ["CreatedBy"] = "bob" }, System);

        Read(live, "LastModifiedBy").Should().Be("bob", "an import or a repair preserves authorship");
        Read(live, "CreatedBy").Should().Be("bob");
    }
}

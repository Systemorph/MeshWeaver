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

    [Fact]
    public void AClientNamingSystemAsTheAuthor_IsRecordedAsItself()
    {
        var live = Apply(new JsonObject { ["Name"] = "after", ["LastModifiedBy"] = "system-security" }, Mallory);

        live["LastModifiedBy"]!.GetValue<string>().Should().Be("mallory",
            "the author of a write is the delivery's principal, never a value in the patch");
        live["Name"]!.GetValue<string>().Should().Be("after", "the content change itself still lands");
    }

    [Fact]
    public void AClientCannotRewriteWhoCreatedTheNode()
    {
        var live = Apply(new JsonObject { ["CreatedBy"] = "system-security" }, Mallory);

        live["CreatedBy"]!.GetValue<string>().Should().Be("alice", "who created a node is not rewritten by a patch");
    }

    [Fact]
    public void APatchThatDoesNotTouchTheAuthor_IsLeftAlone()
    {
        var live = Apply(new JsonObject { ["Name"] = "after" }, Mallory);

        live["LastModifiedBy"]!.GetValue<string>().Should().Be("alice",
            "the stamp is only corrected where the patch claims one, so a no-op stays a no-op");
    }

    [Fact]
    public void ThePlatformKeepsTheStampItCarries()
    {
        var live = Apply(new JsonObject { ["LastModifiedBy"] = "bob", ["CreatedBy"] = "bob" }, System);

        live["LastModifiedBy"]!.GetValue<string>().Should().Be("bob", "an import or a repair preserves authorship");
        live["CreatedBy"]!.GetValue<string>().Should().Be("bob");
    }
}

using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The user directory must key on the profile email in WHATEVER SHAPE the content arrives —
/// the second half of issue #1936, and a textbook instance of the codebase's rule 1
/// ("read <c>node.Content</c> in whatever shape it arrives; <c>is not JsonElement → give up</c>
/// reads nothing, silently").
///
/// <para><see cref="UserIdentityCache"/> used to extract the email with a bare
/// <c>node.Content is JsonElement</c> probe plus a reflection fallback for a typed instance. Content
/// is legitimately other things in a running mesh: a <c>JsonNode</c>/<c>JsonObject</c> (what the
/// node builders emit — <c>NodeElement.Of</c> exists for exactly this), and a same-short-named type
/// from another dynamic assembly, which the framework re-types at the query seam. Each of those
/// dropped the user out of the directory with no error anywhere.</para>
///
/// <para>A dropped user is not a shorter list: <see cref="UserIdentityCache.Lookup"/> then reports
/// a DETERMINATE "no such user", so both the SSR request path and the Blazor circuit keep the
/// claims seed — right <c>ObjectId</c>, right <c>Email</c>, and NO <c>TimeZoneId</c> and NO
/// <c>Locale</c>. Timestamps render UTC, strings render English, and nothing is logged.</para>
/// </summary>
public class UserDirectoryContentShapeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static User Profile(string email) =>
        new() { Email = email, TimeZoneId = "Europe/Zurich", Locale = "de" };

    /// <summary>
    /// Every shape a <c>User</c> node's content legitimately takes must still be indexed AND must
    /// still project the profile onto the identity.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("typed")]
    [InlineData("JsonElement")]
    [InlineData("JsonObject")]
    public async Task EveryContentShape_IsIndexedAndProjects(string shape)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"shape{shape.ToLowerInvariant()}";
        var email = $"{id}@meshweaver.io";
        var camel = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        object content = shape switch
        {
            "typed" => Profile(email),
            "JsonElement" => JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(Profile(email), camel), camel),
            _ => JsonNode.Parse(JsonSerializer.Serialize(Profile(email), camel))!,
        };

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            await MeshService.CreateNode(MeshNode.FromPath(id) with
            {
                Name = id,
                NodeType = "User",
                State = MeshNodeState.Active,
                Content = content
            }).Should().Within(60.Seconds()).Emit();
        }

        using var cache = new UserIdentityCache(
            MeshService, Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<UserIdentityCache>>());

        var lookup = await cache.WhenDetermined(email).Timeout(60.Seconds()).FirstAsync().ToTask(ct);
        Output.WriteLine(
            $"{shape}: content={lookup.Node?.Content?.GetType().Name ?? "(none)"} "
            + $"node={lookup.Node?.Path ?? "(null)"}");

        lookup.Node.Should().NotBeNull(
            $"a User node whose content arrives as '{shape}' must still be indexed by email — "
            + "dropping it makes the directory answer a determinate 'no such user' and the viewer "
            + "silently loses their time zone and language");

        MeshUserProjection
            .Apply(new AccessContext { ObjectId = id, Email = email }, lookup.Node!, Mesh.JsonSerializerOptions)
            .TimeZoneId.Should().Be("Europe/Zurich");
    }
}

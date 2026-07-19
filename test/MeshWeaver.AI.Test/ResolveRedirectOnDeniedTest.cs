using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins that <see cref="MeshOperations.Resolve"/> (the core behind <c>POST /api/mesh/resolve</c>)
/// surfaces the node's configured <see cref="PartitionAccessPolicy.RedirectOnDenied"/> as a
/// <c>redirectOnDenied</c> field. This is the REST twin of <c>hub.GetRedirectOnDenied(path)</c>: it
/// lets a remote shell (portal-next / react-native) do the SAME "no access ⇒ redirect here"
/// navigation the Blazor <c>NamedAreaView</c> does — a not-yet-enrolled visitor who hits a gated
/// course lands on its public cover instead of a dead-end "Access denied" card. The loop-guard
/// (<c>IsSafeRedirect</c>) and the navigation itself live client-side.
/// </summary>
public class ResolveRedirectOnDeniedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Real RLS (ConfigureMeshBase) with NO PublicAdminAccess grant — the redirect hint must resolve
    // from the partition policy regardless of the caller's own permissions, exactly as it does for a
    // denied viewer in the GUI.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // A resolvable course node…
            .AddMeshNodes(
                new MeshNode("RedirectCourse")
                {
                    Name = "Redirect Course",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# course" }
                },
                // …whose partition policy configures the public cover to redirect denied visitors to.
                new MeshNode("_Policy", "RedirectCourse")
                {
                    NodeType = SecurityCollections.PartitionAccessPolicyNodeType,
                    Content = new PartitionAccessPolicy { RedirectOnDenied = "RedirectCourse/Cover" }
                },
                // A plain node under no ancestor policy — its redirect hint must be null.
                new MeshNode("PlainNode")
                {
                    Name = "Plain Node",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# plain" }
                });

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    [Fact(Timeout = 60000)]
    public async Task Resolve_IncludesConfiguredRedirectOnDenied()
    {
        var json = await new MeshOperations(Mesh)
            .Resolve("@RedirectCourse")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("prefix").GetString().Should().Be("RedirectCourse");
        root.GetProperty("redirectOnDenied").GetString().Should().Be("RedirectCourse/Cover",
            "the resolve response must carry the node's PartitionAccessPolicy.RedirectOnDenied so a remote "
            + "shell can redirect a denied viewer to the public cover, exactly like the Blazor NamedAreaView");
    }

    [Fact(Timeout = 60000)]
    public async Task Resolve_RedirectOnDenied_IsNull_WhenNoPolicyConfigured()
    {
        var json = await new MeshOperations(Mesh)
            .Resolve("@PlainNode")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("prefix").GetString().Should().Be("PlainNode");
        // Present-but-null (not a missing field) so the shell can read it unconditionally and fall
        // through to the honest access-denied when no redirect is configured.
        root.GetProperty("redirectOnDenied").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

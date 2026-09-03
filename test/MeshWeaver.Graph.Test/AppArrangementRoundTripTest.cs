using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Apps grid's arrangement — which GROUP a tile sits in and its ORDER inside it — is per user
/// and lives on the <c>InstalledApp</c> records themselves (<see cref="App.Group"/> /
/// <see cref="App.Order"/>). A drop on the grid writes it through the ONE mutation API,
/// <c>GetMeshNodeStream(path).Update</c>, and the grid reads it back off the same records. This
/// test is that round trip: the write lands, the stream reports it, and the fields the drop did
/// not touch (the tile's identity — <see cref="App.Plugin"/>, <see cref="App.OpenPath"/>) survive
/// the merge patch untouched.
/// </summary>
public class AppArrangementRoundTripTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string User = "arrangeuser";
    private const string AppId = "Chess";
    // Every wait is TestTimeouts.Convergence, never a literal (the ratchet guard is about these).
    private static TimeSpan Bound => TestTimeouts.Convergence;

    // 180_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant, so
    // the outer cap cannot be computed. The waits inside use TestTimeouts.Convergence; this one
    // only stops a wedge from running forever.
    [Fact(Timeout = 180_000)]
    public async Task A_drop_writes_group_and_order_onto_the_record_and_the_stream_reads_them_back()
    {
        await CreateAsync(MeshNode.FromPath(User) with
        {
            NodeType = "User", Name = User, State = MeshNodeState.Active, Content = new User(),
        });
        var path = AppNodeType.PathFor(User, AppId);
        await CreateAsync(MeshNode.FromPath(path) with
        {
            NodeType = AppNodeType.NodeType,
            Name = AppId,
            MainNode = AppId,
            State = MeshNodeState.Active,
            Content = new App { Plugin = AppId, OpenPath = $"{AppId}/Board", Source = "user" },
        });

        var workspace = Mesh.GetWorkspace();
        var options = Mesh.JsonSerializerOptions;

        // The drop: the tile lands in "Games" as its second icon. Only those two fields change.
        await workspace.GetMeshNodeStream(path)
            .Update(current => current with
            {
                Content = (current.ContentAs<App>(options) ?? new App()) with { Group = "Games", Order = 2 },
            })
            .FirstAsync()
            .Timeout(Bound);

        var arranged = await workspace.GetMeshNodeStream(path)
            .Select(node => node.ContentAs<App>(options))
            .Where(app => app is { Group: "Games" })
            .FirstAsync()
            .Timeout(Bound);

        arranged!.Order.Should().Be(2);
        arranged.Plugin.Should().Be(AppId, "the arrangement write must not clobber the tile's identity");
        arranged.OpenPath.Should().Be($"{AppId}/Board", "nor where the tile opens");
        arranged.Source.Should().Be("user");
    }

    [Fact(Timeout = 180_000)]
    public async Task Ungrouping_is_an_empty_group_not_a_missing_one_so_no_heal_regroups_it()
    {
        var path = AppNodeType.PathFor(User, "Docs");
        await CreateAsync(MeshNode.FromPath(path) with
        {
            NodeType = AppNodeType.NodeType,
            Name = "Docs",
            MainNode = "Doc",
            State = MeshNodeState.Active,
            Content = new App { Plugin = "Doc", Source = "default", Group = "Platform", Order = 1 },
        });

        var workspace = Mesh.GetWorkspace();
        var options = Mesh.JsonSerializerOptions;

        await workspace.GetMeshNodeStream(path)
            .Update(current => current with
            {
                Content = (current.ContentAs<App>(options) ?? new App()) with { Group = "" },
            })
            .FirstAsync()
            .Timeout(Bound);

        var ungrouped = await workspace.GetMeshNodeStream(path)
            .Select(node => node.ContentAs<App>(options))
            .Where(app => app is { Group: "" })
            .FirstAsync()
            .Timeout(Bound);

        ungrouped!.Order.Should().Be(1, "leaving a group keeps the tile's position among the ungrouped");
    }

    private async Task CreateAsync(MeshNode node)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => mesh.CreateNode(node))
            .FirstAsync().Timeout(Bound).Await();
    }
}

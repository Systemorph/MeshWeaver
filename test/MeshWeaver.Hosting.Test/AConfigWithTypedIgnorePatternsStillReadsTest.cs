using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A sync config whose Ignore patterns were typed into the settings tab still reads as
/// configured</b> (Systemorph/MeshWeaver#7843).
///
/// <para><b>What was measured.</b> A Space's <c>_GitSync</c> rendered its repository URL in the
/// settings tab — that editor binds to the node stream and reads the raw JSON — while every read
/// that types the content answered nothing: the status line said "Not synced yet", and the reimport
/// gate refused with "absent or names no repository". The settings tab's node-content editor offered
/// <c>Ignore</c> (<c>string[]</c>) as a TEXT field, and a text field writes a JSON string, so the
/// stored content carried <c>"ignore": "Release/\nDrafts/"</c>; <c>ContentAs&lt;GitHubSyncConfig&gt;</c>
/// cannot read a string as an array and answers null for the WHOLE record.</para>
///
/// <para>This test writes exactly that stored shape through the same mutation API the editor uses
/// (<c>GetMeshNodeStream(path).Update</c> with a JSON-object content) and reads it back through
/// <see cref="GitHubSyncService.ReadConfig"/> — the one read the trigger gate decides on.</para>
/// </summary>
public class AConfigWithTypedIgnorePatternsStillReadsTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/typed-ignore";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    [Fact(Timeout = 240_000)]
    public async Task IgnorePatternsStoredAsText_StillReadAsAConfiguredSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = "Ti" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Typed ignore patterns",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);

        await Sync.SaveConfig(space, RepoUrl, "main", "mesh/Deployments",
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        // Positive control: the typed read answers before the text write.
        var before = await Sync.ReadConfig(space).Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Equal(RepoUrl, before?.RepositoryUrl);

        // The stored shape the settings tab's text field wrote: the record's JSON with `ignore`
        // replaced by the typed text.
        var configPath = GitHubSyncService.ConfigPath(space);
        await Mesh.GetWorkspace().GetMeshNodeStream(configPath)
            .Update(node =>
            {
                var obj = JsonSerializer.SerializeToNode(node.Content, Mesh.JsonSerializerOptions)!.AsObject();
                obj["ignore"] = JsonValue.Create("Release/\nDrafts/, Scratch/");
                return node with { Content = obj };
            })
            .Timeout(TestTimeouts.Convergence).Await(ct);

        // Wait until the synced query carries the write (the key is absent before it, in whichever
        // form the owner then stored it), and only then take the typed read the gate takes.
        var after = await Sync.WatchConfigNode(space)
            .Where(n => n is not null && JsonSerializer.SerializeToNode(n.Content, Mesh.JsonSerializerOptions)?["ignore"] is not null)
            .Select(_ => Sync.ReadConfig(space))
            .Switch()
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(ct);

        Assert.NotNull(after);
        Assert.Equal(RepoUrl, after!.RepositoryUrl);
        Assert.Equal(new[] { "Release/", "Drafts/", "Scratch/" }, after.Ignore);
    }
}

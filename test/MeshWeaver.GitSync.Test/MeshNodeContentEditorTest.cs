using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Covers the node-bound editor path that replaced the hand-rolled form + SetupAutoSave:
/// the GitHub Sync config is edited as a mesh node, the GUI binds to its stream, and edits
/// persist via <c>GetMeshNodeStream(path).Update(...)</c> — no <c>/data</c> replica, no save
/// subscription. The Blazor view itself is exercised via the exact JSON contract it writes.
/// </summary>
public class MeshNodeContentEditorTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact]
    public void FromType_reflects_editable_fields_and_hides_browsable_false()
    {
        var fields = MeshNodeEditorField.FromType(typeof(GitHubSyncConfig));
        var keys = fields.Select(f => f.Key).ToArray();

        // The five editable properties, by camelCase key — and NOT the [Browsable(false)] last-sync fields.
        Assert.Equal(
            new[] { "repositoryUrl", "branch", "subdirectory", "createBranchIfMissing", "createRepoIfMissing" },
            keys);
        Assert.DoesNotContain("lastSyncedAt", keys);
        Assert.DoesNotContain("lastSyncCommitSha", keys);

        // Kinds follow the property type; labels come from [Description].
        Assert.Equal(MeshNodeEditorFieldKind.Text, fields.First(f => f.Key == "repositoryUrl").Kind);
        Assert.Equal(MeshNodeEditorFieldKind.Bool, fields.First(f => f.Key == "createBranchIfMissing").Kind);
        Assert.Equal("Repository URL", fields.First(f => f.Key == "repositoryUrl").Label);
    }

    [Fact(Timeout = 60000)]
    public void EnsureConfigNode_creates_default_when_absent_and_is_idempotent()
    {
        var space = NewSpace();
        var cfgPath = GitHubSyncService.ConfigPath(space);

        Assert.True(IsAbsent(cfgPath));

        var created = Sync.EnsureConfigNode(space).Timeout(30.Seconds()).Wait();
        Assert.Equal(cfgPath, created.Path);
        Assert.Equal(GitHubSyncService.ConfigNodeType, created.NodeType);

        // Second call must NOT create a duplicate — it returns the existing node.
        var again = Sync.EnsureConfigNode(space).Timeout(30.Seconds()).Wait();
        Assert.Equal(cfgPath, again.Path);
        var node = WaitForNode(cfgPath);
        Assert.NotNull(node);
    }

    [Fact(Timeout = 60000)]
    public void Editing_a_field_via_the_node_stream_persists_and_reads_back_typed()
    {
        var space = NewSpace();
        var cfgPath = GitHubSyncService.ConfigPath(space);
        Sync.EnsureConfigNode(space).Timeout(30.Seconds()).Wait();
        var node = WaitForNode(cfgPath);

        // Reproduce EXACTLY what MeshNodeContentEditorView writes: a per-field JSON patch on the
        // node content using the camelCase key, persisted via GetMeshNodeStream(path).Update.
        const string repo = "https://github.com/acme/widgets";
        Mesh.GetMeshNodeStream(cfgPath)
            .Update(n => n with { Content = PatchField(n.Content, "repositoryUrl", JsonValue.Create(repo)) })
            .Timeout(30.Seconds()).Wait();

        // The camelCase JSON key round-trips to the typed GitHubSyncConfig property.
        var cfg = WaitForConfig(space, c => c.RepositoryUrl == repo);
        Assert.Equal(repo, cfg.RepositoryUrl);
        Assert.Equal("main", cfg.Branch); // untouched default preserved
    }

    [Fact(Timeout = 60000)]
    public void Sync_records_commit_without_clobbering_edited_repo_field()
    {
        var space = NewSpace();
        CreateMarkdown($"{space}/Page", "Page", "# hello");
        var cfgPath = GitHubSyncService.ConfigPath(space);
        Sync.EnsureConfigNode(space).Timeout(30.Seconds()).Wait();
        Connect();

        // Edit the repo URL the GUI way (JSON patch via the node stream), then sync.
        const string repo = "https://github.com/acme/preserved";
        Mesh.GetMeshNodeStream(cfgPath)
            .Update(n => n with { Content = PatchField(n.Content, "repositoryUrl", JsonValue.Create(repo)) })
            .Timeout(30.Seconds()).Wait();
        WaitForConfig(space, c => c.RepositoryUrl == repo);

        Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();

        // RecordLastSync merges only the last-sync fields: the edited repo URL survives.
        var cfg = WaitForConfig(space, c => !string.IsNullOrEmpty(c.LastSyncCommitSha));
        Assert.Equal(repo, cfg.RepositoryUrl);
        Assert.False(string.IsNullOrEmpty(cfg.LastSyncCommitSha));
    }

    [Fact(Timeout = 60000)]
    public void Credential_GetStream_emits_live_after_save()
    {
        // The connect-state binding uses GetStream (live), not a one-shot Get().Take(1) that grabbed
        // the synced query's empty pre-sync snapshot and showed "Not connected" right after connecting.
        Connect(token: "ghp_live", login: "live-octocat");
        var cred = Credentials.GetStream(UserId)
            .Where(c => c is { AccessToken.Length: > 0 })
            .FirstAsync().Timeout(30.Seconds()).Wait();
        Assert.NotNull(cred);
        Assert.Equal("ghp_live", cred!.AccessToken); // decrypted by GetStream
        Assert.Equal("live-octocat", cred.GitHubLogin);
    }

    private string NewSpace()
    {
        var id = "GhEd" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(id, "Editor Space");
        return id;
    }

    private JsonElement PatchField(object? content, string key, JsonNode value)
    {
        var opts = Mesh.JsonSerializerOptions;
        var obj = (content is JsonElement je
            ? JsonNode.Parse(je.GetRawText())
            : JsonSerializer.SerializeToNode(content, opts)) as JsonObject ?? new JsonObject();
        obj[key] = JsonNode.Parse(value.ToJsonString());
        return JsonSerializer.SerializeToElement<object>(obj, opts);
    }
}

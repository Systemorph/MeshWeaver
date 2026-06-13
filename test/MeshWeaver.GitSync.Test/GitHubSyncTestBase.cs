using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Base for GitHub-sync integration tests: wires the GitHub-sync types, services, a
/// real <see cref="IProviderKeyProtector"/> (so credential encryption is exercised),
/// the per-node GitHub Sync settings tab, and the in-memory <see cref="FakeGitHubRepoClient"/>
/// so the full export/import loop runs offline.
/// </summary>
public abstract class GitHubSyncTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected readonly FakeGitHubRepoClient Fake = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureDefaultNodeHub(c => c.AddGitHubSyncSettingsTab())
            .ConfigureServices(s =>
            {
                s.AddGitHubSyncServices();
                // Override the Octokit client with the in-memory fake (last registration wins).
                s.AddSingleton<IGitHubRepoClient>(Fake);
                // Real encryptor with a fixed master key so the credential round-trip is encrypted.
                s.AddSingleton<IMasterKeyProvider>(new FixedMasterKey());
                s.AddSingleton<IProviderKeyProtector, ProviderKeyProtector>();
                return s;
            });

    protected GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    protected GitHubCredentialService Credentials => Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
    protected IProviderKeyProtector Protector => Mesh.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
    // The DevLogin user the test base logs in (TestUsers.Admin). Read directly rather than
    // from AccessService.Context, which is circuit-scoped and null on the test-method thread.
    protected string UserId => TestUsers.Admin.ObjectId!;

    /// <summary>Saves a GitHub credential for the current user and waits for it to be readable.</summary>
    protected GitHubCredential Connect(string token = "ghp_test_token", string login = "octocat")
    {
        Credentials.Save(UserId, new GitHubToken(token, null, "bearer", "repo", null), login)
            .Timeout(30.Seconds()).Wait();
        return WaitForCredential();
    }

    protected GitHubCredential WaitForCredential() =>
        Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Credentials.Get(UserId))
            .Where(c => c is { AccessToken.Length: > 0 })
            .FirstAsync()
            .Timeout(10.Seconds())
            .Wait()!;

    protected MeshNode CreateSpace(string id, string? name = null) =>
        NodeFactory.CreateNode(new MeshNode(id)
        {
            NodeType = "Space",
            Name = name ?? id,
            State = MeshNodeState.Active,
            Content = new Space { Name = name ?? id },
        }).Timeout(60.Seconds()).Wait();

    protected MeshNode CreateMarkdown(string path, string name, string body)
    {
        var seed = MeshNode.FromPath(path);
        return NodeFactory.CreateNode(seed with
        {
            NodeType = "Markdown",
            Name = name,
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = body },
        }).Timeout(60.Seconds()).Wait();
    }

    /// <summary>Waits for a node to be readable at <paramref name="path"/> and returns it.</summary>
    protected MeshNode WaitForNode(string path) =>
        Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadNode(path))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(30.Seconds())
            .Wait()!;

    /// <summary>Confirms a node is absent (stays null over a short window) — for prune assertions.</summary>
    protected bool IsAbsent(string path) =>
        ReadNode(path).Timeout(10.Seconds()).Wait() is null;

    /// <summary>Polls the Space's GitHub sync config until it satisfies <paramref name="predicate"/>.</summary>
    protected GitHubSyncConfig WaitForConfig(string spacePath, Func<GitHubSyncConfig, bool> predicate) =>
        Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Sync.ReadConfig(spacePath))
            .Where(c => c is not null && predicate(c))
            .Select(c => c!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .Wait();

    protected static string MarkdownBody(MeshNode node) => node.Content switch
    {
        MarkdownContent mc => mc.Content,
        string s => s,
        _ => "",
    };

    private sealed class FixedMasterKey : IMasterKeyProvider
    {
        // 32 bytes = AES-256. Fixed so encrypt→decrypt round-trips deterministically.
        private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        public byte[]? GetMasterKey() => Key;
    }
}

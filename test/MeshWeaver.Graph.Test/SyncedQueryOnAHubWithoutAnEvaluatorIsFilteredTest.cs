using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A synced query (<c>hub.GetQuery</c>) opened on a hub that carries no permission evaluator of
/// its own is still filtered to what its reader may read.</b>
///
/// <para>The shape: every Blazor view reads through the circuit's PORTAL hub, and a hub's
/// configuration starts empty — the mesh's <c>EffectivePermissionsDelegate</c> is not inherited.
/// <c>WrapWithPerUserRls</c> asked the CALLING hub whether row-level security was on, got "no
/// evaluator", and handed the System-loaded snapshot through unfiltered, whatever the reader's
/// identity. Measured before the fix: the stranger, who holds no grant on the vault, was shown the
/// secret by a synced query opened on a client hub, under their OWN identity. The filter is now
/// judged by the mesh hub, which carries the evaluator the owner's RLS check runs. The keeper, who
/// holds Read, still sees it (positive control); a query opened on the mesh hub itself is the
/// control that was always filtered.</para>
/// <para>Found while fixing the Blazor render-identity leak (triage
/// <c>rbuergi/Feedback/blazor-render-reads-ambient-identity-2026-10-21</c>; Refs #7038).</para>
/// </summary>
public class SyncedQueryOnAHubWithoutAnEvaluatorIsFilteredTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Stranger = "synced-stranger";
    private const string Keeper = "synced-keeper";
    private const string Lobby = "SyncedLobby";
    private const string Vault = "SyncedVault";
    private const string OpenName = "LOBBY-OPEN-5e11";
    private const string SecretName = "VAULT-SECRET-0c3a";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                Person(Stranger, "Synced Stranger"),
                AssignmentNodeFactory.UserRole(Stranger, "Admin", Stranger),
                Person(Keeper, "Synced Keeper"),
                AssignmentNodeFactory.UserRole(Keeper, "Admin", Keeper),
                new MeshNode(Lobby) { Name = "Synced Lobby", NodeType = "Space" },
                new MeshNode(Vault) { Name = "Synced Vault", NodeType = "Space" },
                AssignmentNodeFactory.UserRole(Stranger, "Viewer", Lobby),
                AssignmentNodeFactory.UserRole(Keeper, "Viewer", Lobby),
                AssignmentNodeFactory.UserRole(Keeper, "Viewer", Vault),
                // The stranger's OWN partition withholds API reach: an API token holds nothing there,
                // however the row is otherwise admitted (the own-scope shortcut).
                AssignmentNodeFactory.Policy(Stranger, new PartitionAccessPolicy { Api = false }))
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(new SilentOnShadowValidator()));

    private const string ShadowName = "VAULT-SHADOW-77d4";
    private const string DiaryName = "STRANGER-DIARY-3b90";

    private static MeshNode Person(string id, string name) => new(id)
    {
        NodeType = UserNodeType.NodeType,
        Name = name,
        State = MeshNodeState.Active,
        Content = new User { FullName = name, Email = $"{id}@meshweaver.io" },
    };

    /// <summary>🚨 THE DEFECT: the stranger's synced query on a hub without an evaluator does not show the vault.</summary>
    [Fact]
    public async Task AStrangersSyncedQueryOnAClientHubDoesNotShowTheVault()
    {
        await Seed();
        var client = GetClient(c => ConfigureClient(c).AddData());
        client.Configuration.Get<EffectivePermissionsDelegate>().Should().BeNull(
            "the premise: the client hub, like a portal hub, carries no permission evaluator of its own");

        var snapshot = await FirstWith(client, Stranger, OpenName);
        snapshot.Should().NotContain(SecretName, "the stranger holds no grant on the vault");
    }

    /// <summary>Control: the same query opened on the mesh hub, which carries the evaluator.</summary>
    [Fact]
    public async Task AStrangersSyncedQueryOnTheMeshHubDoesNotShowTheVault()
    {
        await Seed();
        var snapshot = await FirstWith(Mesh, Stranger, OpenName);
        snapshot.Should().NotContain(SecretName, "the stranger holds no grant on the vault");
    }

    /// <summary>🚨 POSITIVE CONTROL: the keeper's synced query on the same client hub shows the vault.</summary>
    [Fact]
    public async Task AKeepersSyncedQueryOnAClientHubShowsTheVault()
    {
        await Seed();
        var client = GetClient(c => ConfigureClient(c).AddData());
        await FirstWith(client, Keeper, SecretName);
    }

    /// <summary>
    /// 🚨 A node type's own READ rule holds in the filter exactly as in a query: <c>UserAccessRule</c>
    /// opens every root User profile to any signed-in reader, with no scope grant. The scope fold
    /// alone would hide the keeper's profile from the stranger; the read-validator chain shows it.
    /// </summary>
    [Fact]
    public async Task ATypeRulesReadGrantHoldsInASyncedQueryOnAClientHub()
    {
        var client = GetClient(c => ConfigureClient(c).AddData());
        var access = client.ServiceProvider.GetRequiredService<AccessService>();
        var premise = await Mesh.GetEffectivePermissions(Keeper, Stranger)
            .Should().Within(TestTimeouts.Convergence).Emit("the fold answers for the stranger on the keeper's profile");
        premise.HasFlag(Permission.Read).Should().BeFalse(
            "the premise: no scope grant gives the stranger Read on the keeper's profile — only the type rule does");

        IObservable<IEnumerable<MeshNode>> query;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = Stranger, Name = Stranger }))
            query = client.GetQuery($"synced-type-rule-{Guid.NewGuid():N}", $"path:{Keeper}");

        await query
            .Do(nodes => Output.WriteLine($"{Stranger}: [{string.Join(", ", nodes.Select(n => n.Path))}]"))
            .Where(nodes => nodes.Any(n => n.Path == Keeper))
            .Should().Within(TestTimeouts.Convergence).Emit(
                "UserAccessRule grants every signed-in reader the root User profiles, and the synced query honours it");
    }

    /// <summary>
    /// 🚨 NO VERDICT IS NOT CONSENT: a registered Read validator that completes WITHOUT emitting
    /// refuses the row. The keeper may read the vault and every other validator admits the shadow
    /// node, but one validator stays silent on it, so it must not reach the keeper's synced query.
    /// </summary>
    [Fact]
    public async Task AReadValidatorThatGivesNoVerdictHidesTheRow()
    {
        await Seed();
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode(SilentOnShadowValidator.ShadowId, Vault) { Name = ShadowName, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit("the admin seeds the shadow node");
        var client = GetClient(c => ConfigureClient(c).AddData());

        var snapshot = await FirstWith(client, Keeper, SecretName);
        snapshot.Should().NotContain(ShadowName,
            "a validator that completed without a verdict gave no consent, so the row is refused");
    }

    /// <summary>
    /// 🚨 The API-token clamp holds on the synced-query path: the stranger's OWN partition withholds
    /// API reach, so the stranger's API token sees nothing there, although the own-scope shortcut
    /// admits the row before the permission fold (where the clamp lives) is reached. The same person
    /// in a browser sees it (positive control).
    /// </summary>
    [Fact]
    public async Task AnApiTokenReadsNothingInAnApiCappedOwnScope()
    {
        await Seed();
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode("Diary", Stranger) { Name = DiaryName, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit("the admin seeds the stranger's own node");
        var client = GetClient(c => ConfigureClient(c).AddData());
        var access = client.ServiceProvider.GetRequiredService<AccessService>();

        IObservable<IEnumerable<MeshNode>> Open(bool apiToken)
        {
            using (access.SwitchAccessContext(new AccessContext { ObjectId = Stranger, Name = Stranger, IsApiToken = apiToken }))
                return client.GetQuery($"synced-api-{apiToken}-{Guid.NewGuid():N}",
                    $"namespace:{Lobby}", $"namespace:{Stranger}");
        }

        await Open(apiToken: false)
            .Where(nodes => nodes.Any(n => n.Name == DiaryName))
            .Should().Within(TestTimeouts.Convergence).Emit("the stranger in a browser reads their own node");

        var asToken = await Open(apiToken: true)
            .Select(nodes => nodes.Select(n => n.Name ?? n.Id).ToList())
            .Do(names => Output.WriteLine($"token: [{string.Join(", ", names)}]"))
            .Where(names => names.Contains(OpenName))
            .Should().Within(TestTimeouts.Convergence).Emit("the token's snapshot arrives (the lobby is not API-capped)");
        asToken.Should().NotContain(DiaryName, "the partition's policy withholds API reach from every token");
    }

    private async Task Seed()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(new MeshNode("Open", Lobby) { Name = OpenName, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit("the admin seeds the open node");
        await mesh.CreateNode(new MeshNode("Secret", Vault) { Name = SecretName, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit("the admin seeds the secret");
    }

    /// <summary>
    /// Opens one synced query over both spaces on <paramref name="hub"/> as <paramref name="reader"/>
    /// (the identity is captured when the query is opened) and returns the names in the first
    /// snapshot that shows <paramref name="expected"/>.
    /// </summary>
    private async Task<IReadOnlyList<string>> FirstWith(IMessageHub hub, string reader, string expected)
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<IEnumerable<MeshNode>> query;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = reader, Name = reader }))
            query = hub.GetQuery($"synced-evaluator-{Guid.NewGuid():N}", $"namespace:{Lobby}", $"namespace:{Vault}");

        return await query
            .Select(nodes => (IReadOnlyList<string>)nodes.Select(n => n.Name ?? n.Id).ToList())
            .Do(names => Output.WriteLine($"{reader}: [{string.Join(", ", names)}]"))
            .Where(names => names.Contains(expected))
            .Should().Within(TestTimeouts.Convergence).Emit($"{reader}'s synced query shows {expected}");
    }
}

/// <summary>
/// A Read validator that admits everything except the vault's shadow node, on which it completes
/// WITHOUT emitting a verdict: the contract violation the filter must treat as a refusal.
/// </summary>
internal sealed class SilentOnShadowValidator : INodeValidator
{
    /// <summary>The id of the node this validator gives no verdict on.</summary>
    public const string ShadowId = "Shadow";

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Read];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => context.Node.Id == ShadowId && context.Node.Namespace == "SyncedVault"
            ? Observable.Empty<NodeValidationResult>()
            : Observable.Return(NodeValidationResult.Valid());
}

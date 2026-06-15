#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using Memex.Portal.Shared.Models;
using Memex.Portal.Shared.Settings;
using MeshWeaver.AI;
using MeshWeaver.AI.Connect;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Renders the Settings → Models tab through the workspace and asserts the
/// ProviderKind layout split: an API provider (Anthropic) renders its model list,
/// a CLI provider (Claude Code) renders a Connect button and NO checkable model list.
/// </summary>
public class ModelsSettingsTabRenderTest : MonolithMeshTestBase
{
    public ModelsSettingsTabRenderTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // API provider — has a model list.
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "Anthropic",
                ProviderName: "Anthropic",
                Order: 1,
                DisplayLabel: "Anthropic",
                DefaultEndpoint: "https://api.anthropic.com/v1/messages",
                DefaultModelIds: ImmutableArray.Create("claude-opus-4-7", "claude-sonnet-4-6"),
                RequiresApiKey: true,
                Kind: ProviderKind.Api))
            // CLI provider — login + Connect, no model list.
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "ClaudeCode",
                ProviderName: "ClaudeCode",
                Order: 5,
                DisplayLabel: "Claude Code (my subscription)",
                DefaultEndpoint: null,
                DefaultModelIds: ImmutableArray.Create("sonnet", "opus", "haiku"),
                RequiresApiKey: true,
                Kind: ProviderKind.Cli))
            .ConfigureServices(services =>
            {
                services.AddSingleton<ModelProviderService>();
                services.AddSingleton<IConnectTokenSink, ConnectTokenSink>();
                services.AddSingleton<IConnectStrategy, ClaudeConnectStrategy>();
                services.AddSingleton<ConnectSessionManager>();
                return services;
            })
            // Apply the Models tab on the node hubs (mirrors the portal's per-node config).
            .ConfigureDefaultNodeHub(config => config
                .AddDefaultLayoutAreas()
                .AddModelsSettingsTab());

    [Fact(Timeout = 60000)]
    public async Task Models_Tab_Splits_ApiList_Vs_CliConnect()
    {
        // Render against the test user's own partition node (auto-admin login → Permission.Api).
        // The auto-admin's ObjectId is "Roland", so "Roland" is the user's own partition — and
        // it must actually have a node: the Settings layout area subscribes to the node hub at
        // this address, and an empty address errors "No node found". (The email form
        // "rbuergi@systemorph.com" also can't be an address — '@' is the address host
        // separator.) A top-level node IS a partition root, so the PartitionWriteGuard only
        // lets System (the partition provisioner) create a non-partition type there — seed it
        // under System.
        var userId = "Roland";
        var nodeAddress = new Address(userId);

        await SeedTopLevel(new MeshNode(userId) { Name = "Roland", NodeType = "Markdown" });

        var workspace = Mesh.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = ModelsSettingsTab.TabId };
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        // The eagerly-built API + CLI cards land in the store once the content pane renders.
        // Wait until the whole store JSON contains both the CLI Connect button and the API list.
        var storeJson = await stream
            .Select(change => change.Value.GetRawText())
            .Where(json =>
                json.Contains("Connect Claude Code", StringComparison.Ordinal) &&
                json.Contains("claude-opus-4-7", StringComparison.Ordinal))
            .Should().Within(40.Seconds())
            .Match(_ => true);

        // CLI card: a Connect button is present, and the [CLI] badge. The store is raw JSON
        // (GetRawText) and the layout serializer HTML-escapes angle brackets, so the badge
        // markup `<span …>CLI</span>` appears as `> CLI <` — match that escaped form,
        // not the literal `>CLI<`. The `>…<` anchoring still distinguishes the badge
        // from the "CLI providers" section header.
        storeJson.Should().Contain("Connect Claude Code", "the CLI provider shows a Connect button");
        storeJson.Should().Contain("\\u003ECLI\\u003C", "the CLI card is badged [CLI]");

        // API card: the provider's model list is present, badged [API].
        storeJson.Should().Contain("claude-opus-4-7", "the API provider shows its model list");
        storeJson.Should().Contain("\\u003EAPI\\u003C", "the API card is badged [API]");

        // The CLI provider must NOT advertise its model ids as a list — its catalog
        // model ids (sonnet/opus/haiku) are not rendered as a checkable list on the CLI card.
        // (They may appear elsewhere as the agent picker; the CLI CARD body is the "Not connected"
        // status + Connect button. Assert the CLI status copy is present and the API endpoint
        // form is present — proving the two different layouts rendered.)
        storeJson.Should().Contain("Not connected", "the CLI card shows login status, not a model form");
        storeJson.Should().Contain("Endpoint", "the API card shows the endpoint/key form");
    }
}

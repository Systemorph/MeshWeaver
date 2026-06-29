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
/// ProviderKind layout split: API providers surface as the single "add a provider"
/// card (type picker + base URL + key + live <b>Fetch models</b>), while a CLI
/// provider (Claude Code) surfaces as a Connect button + login status with NO
/// model form.
/// </summary>
public class ModelsSettingsTabRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc/>
    protected override bool ShareMeshAcrossTests => false;

    /// <inheritdoc/>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // API provider — surfaces as a selectable type in the add-provider card.
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

    /// <summary>
    /// The tab renders BOTH surfaces: the API "add a provider" card (type + base URL +
    /// key + live Fetch) and the CLI Connect card (Connect button + "Not connected"
    /// status, no model form).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Models_Tab_Splits_ApiAddCard_Vs_CliConnect()
    {
        // Render against the test user's own partition node (auto-admin login → Permission.Api).
        // The auto-admin's ObjectId is "Roland", so "Roland" is the user's own partition — and
        // it must actually have a node: the Settings layout area subscribes to the node hub at
        // this address, and an empty address errors "No node found". A top-level node IS a
        // partition root, so the PartitionWriteGuard only lets System create a non-partition
        // type there — seed it under System.
        var userId = "Roland";
        var nodeAddress = new Address(userId);

        await SeedTopLevel(new MeshNode(userId) { Name = "Roland", NodeType = "Markdown" });

        var workspace = Mesh.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = ModelsSettingsTab.TabId };
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, reference);

        // The eagerly-built add-provider card + CLI card land in the store once the content pane
        // renders. Wait until the whole store JSON contains both the API add-card header and the
        // CLI Connect button.
        var storeJson = await stream
            .Select(change => change.Value.GetRawText())
            .Where(json =>
                json.Contains("Add a provider", StringComparison.Ordinal) &&
                json.Contains("Connect Claude Code", StringComparison.Ordinal))
            .Should().Within(40.Seconds())
            .Match(_ => true);

        // API surface: the single add-provider card — type picker + base URL/key + live fetch.
        // (The model list is NOT baked in anymore; it's fetched on demand, so claude-opus-4-7 is
        // deliberately absent from the initial render.)
        storeJson.Should().Contain("Add a provider", "the API surface is the add-provider card");
        storeJson.Should().Contain("Fetch models", "the add-provider card fetches the model list live");
        storeJson.Should().Contain("Base URL", "the add-provider card takes a base URL");

        // CLI surface: Connect button + login status, NOT a model form.
        storeJson.Should().Contain("Connect Claude Code", "the CLI provider shows a Connect button");
        storeJson.Should().Contain("Not connected", "the CLI card shows login status, not a model form");
    }
}

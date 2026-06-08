#pragma warning disable CS1591

using System;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.AI.AzureOpenAI;
using MeshWeaver.AI.ClaudeCode;
using MeshWeaver.AI.Copilot;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Verifies model-PROVIDER builder extensions register their catalog profiles
/// (so they surface in the picker / Models tab) with the right BYO-key semantics,
/// AND that the CLI HARNESSES do NOT — Claude Code / Copilot are harnesses, not
/// model providers (620f07069: AddClaudeCode/AddCopilot register IHarness instead
/// of an IChatClientFactory and their language-model catalog sources are dropped;
/// they surface through the harness catalog / BuiltInHarnessProvider, with runtime
/// behaviour covered by HarnessTest).
/// </summary>
public class ProviderCatalogRegistrationTest : AITestBase
{
    public ProviderCatalogRegistrationTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // OpenAI is a model provider → its builder extension registers a catalog source.
            .AddOpenAI()
            // 🚨 Claude Code / Copilot are HARNESSES (620f07069). The MeshBuilder
            // .AddClaudeCode()/.AddCopilot() overloads are retained NO-OPS (they used to
            // register catalog sources); the real IHarness registration is the
            // IServiceCollection extension, which is how production wires them
            // (MemexConfiguration: services.AddClaudeCode/AddCopilot). Register them the
            // same way here so the harness-registration assertions exercise the real path.
            .ConfigureServices(services => services.AddClaudeCode().AddCopilot());

    [Fact]
    public void Providers_RegisterCatalogProfiles_WithCorrectKeySemantics()
    {
        var opts = Mesh.ServiceProvider.GetRequiredService<LanguageModelCatalogOptions>();
        var byName = opts.Sources.ToDictionary(s => s.ProviderName, StringComparer.OrdinalIgnoreCase);

        // OpenAI is a first-class model PROVIDER → it registers a catalog source.
        byName.Should().ContainKey("OpenAI");
        byName["OpenAI"].RequiresApiKey.Should().BeTrue();

        // 🚨 Claude Code and Copilot are HARNESSES, not model providers (620f07069).
        // AddClaudeCode/AddCopilot register IHarness instead of an IChatClientFactory,
        // so they must NOT appear as model-provider catalog sources — they live in the
        // harness catalog (BuiltInHarnessProvider) and stream through their own CLI
        // IChatClient, bypassing the model-provider factory chain entirely.
        byName.Should().NotContainKey("ClaudeCode",
            "Claude Code is a harness, not a model-provider catalog source (620f07069)");
        byName.Should().NotContainKey("Copilot",
            "Copilot is a harness, not a model-provider catalog source (620f07069)");
    }

    /// <summary>
    /// The positive counterpart: the real <c>AddClaudeCode()</c> / <c>AddCopilot()</c>
    /// register their CLI <see cref="IHarness"/> implementations (620f07069), with
    /// CLI semantics — <see cref="Harness.SupportsAgentSelection"/> = false (the CLI
    /// runs its own agent loop so the agent/model pickers are hidden) and neither is
    /// the default harness (MeshWeaver is). Also pins case-insensitive resolution via
    /// <see cref="HarnessNodeType.ResolveHarness"/> — the path ThreadExecution uses to
    /// pick a thread's <c>SelectedHarness</c>.
    /// </summary>
    [Fact]
    public void ClaudeCodeAndCopilot_RegisterAsHarnesses_WithCliSemantics()
    {
        var harnesses = Mesh.ServiceProvider.GetServices<IHarness>()
            .ToDictionary(h => h.Id, StringComparer.OrdinalIgnoreCase);

        harnesses.Should().ContainKey(Harnesses.ClaudeCode);
        harnesses.Should().ContainKey(Harnesses.Copilot);

        harnesses[Harnesses.ClaudeCode].Should().BeOfType<ClaudeCodeHarness>();
        var claude = harnesses[Harnesses.ClaudeCode].Definition;
        claude.DisplayName.Should().Be("Claude Code");
        claude.SupportsAgentSelection.Should().BeFalse(
            "a CLI harness runs its own agent loop — it hides the agent/model pickers");
        claude.IsDefault.Should().BeFalse("MeshWeaver is the default harness, not Claude Code");

        harnesses[Harnesses.Copilot].Should().BeOfType<CopilotHarness>();
        var copilot = harnesses[Harnesses.Copilot].Definition;
        copilot.DisplayName.Should().Be("GitHub Copilot");
        copilot.SupportsAgentSelection.Should().BeFalse();
        copilot.IsDefault.Should().BeFalse();

        // Resolve-by-id is case-insensitive — the same lookup ThreadExecution uses.
        HarnessNodeType.ResolveHarness(Mesh.ServiceProvider, "claude code")!.Id
            .Should().Be(Harnesses.ClaudeCode);
        HarnessNodeType.ResolveHarness(Mesh.ServiceProvider, Harnesses.Copilot)
            .Should().BeOfType<CopilotHarness>();
        HarnessNodeType.ResolveHarness(Mesh.ServiceProvider, "not-a-harness")
            .Should().BeNull();
    }
}

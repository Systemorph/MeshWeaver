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
/// Verifies the new provider builder extensions register their catalog profiles
/// (so they surface in the picker / Models tab) with the right BYO-key semantics.
/// </summary>
public class ProviderCatalogRegistrationTest : AITestBase
{
    public ProviderCatalogRegistrationTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddOpenAI()
            .AddClaudeCode()
            .AddCopilot();

    [Fact]
    public void Providers_RegisterCatalogProfiles_WithCorrectKeySemantics()
    {
        var opts = Mesh.ServiceProvider.GetRequiredService<LanguageModelCatalogOptions>();
        var byName = opts.Sources.ToDictionary(s => s.ProviderName, StringComparer.OrdinalIgnoreCase);

        byName.Should().ContainKey("OpenAI");
        byName.Should().ContainKey("ClaudeCode");
        byName.Should().ContainKey("Copilot");

        byName["OpenAI"].RequiresApiKey.Should().BeTrue();
        byName["ClaudeCode"].RequiresApiKey.Should().BeTrue("the subscription OAuth token is the key");
        byName["Copilot"].RequiresApiKey.Should().BeFalse("Copilot authenticates via GitHub OAuth, not a pasted key");

        // Copilot models are retrieved live from the CLI — none hard-coded here.
        byName["Copilot"].DefaultModelIds.Should().BeEmpty();
        byName["ClaudeCode"].DefaultModelIds.Should().Contain("sonnet");
    }
}

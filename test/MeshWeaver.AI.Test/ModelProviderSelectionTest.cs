#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using Memex.Portal.Shared.Models;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Round-trip for the provider-selection node (Phase 4):
/// <see cref="ModelProviderService.SetSelection"/> persists the chosen provider
/// paths (create-or-update) and <see cref="ModelProviderService.GetSelection"/>
/// reads them back live.
/// </summary>
public class ModelProviderSelectionTest : AITestBase
{
    public ModelProviderSelectionTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => { services.AddSingleton<ModelProviderService>(); return services; });

    [Fact]
    public void SetSelection_PersistsAndGetSelection_ReadsBack()
    {
        var owner = $"user-{Guid.NewGuid():N}";
        var service = Mesh.ServiceProvider.GetRequiredService<ModelProviderService>();

        var paths = ImmutableArray.Create($"{owner}/_Provider/Anthropic", "acme/_Provider/OpenAI");
        service.SetSelection(owner, paths).Should().Within(20.Seconds()).Match(ok => ok);

        var selected = service.GetSelection(owner)
            .Should().Within(15.Seconds()).Match(s => s.Length == 2);
        selected.Should().Contain($"{owner}/_Provider/Anthropic");
        selected.Should().Contain("acme/_Provider/OpenAI");
    }

    [Fact]
    public void SetSelection_Overwrites_PreviousSelection()
    {
        var owner = $"user-{Guid.NewGuid():N}";
        var service = Mesh.ServiceProvider.GetRequiredService<ModelProviderService>();

        service.SetSelection(owner, ImmutableArray.Create("a/_Provider/X"))
            .Should().Within(20.Seconds()).Match(ok => ok);
        service.GetSelection(owner).Should().Within(15.Seconds()).Match(s => s.Length == 1);

        service.SetSelection(owner, ImmutableArray.Create("b/_Provider/Y", "c/_Provider/Z"))
            .Should().Within(20.Seconds()).Match(ok => ok);

        var selected = service.GetSelection(owner)
            .Should().Within(15.Seconds()).Match(s => s.Length == 2 && s.Contains("b/_Provider/Y"));
        selected.Should().NotContain("a/_Provider/X");
    }
}

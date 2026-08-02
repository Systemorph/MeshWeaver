#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The per-user install gate for <see cref="Harness.RequiresInstall"/> harnesses (the CLI ones):
/// <see cref="HarnessNodeType.ResolveInstalledHarness"/> runs such a harness only while the picked
/// node path resolves to an Active node — the node a Store plugin localizes into
/// <c>{user}/Harness</c> on install and deletes on uninstall. No node (never installed,
/// uninstalled, or a stale pre-gate global <c>Harness/{id}</c> path) resolves to <c>null</c>, which
/// execution treats as "fall back to the default agent path" — a graceful degrade, never a wedge.
/// </summary>
public class HarnessInstallGateTest : AITestBase
{
    public HarnessInstallGateTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => true;

    private const string GatedId = "GatedCli";

    private sealed class GatedCliHarness : IHarness
    {
        public string Id => GatedId;
        public Harness Definition => new()
        {
            Id = GatedId, DisplayName = "Gated CLI", Order = 9,
            SupportsAgentSelection = false, RequiresInstall = true
        };
        public IChatClient? CreateChatClient(HarnessExecutionContext context) => null;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHarness, GatedCliHarness>();
                return services;
            });

    // The client needs the data/layout wiring for GetWorkspace().GetMeshNodeStream(path) —
    // the same surface ThreadExecution's parentHub has in prod (see ThreadComposerFlowTest).
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>Not installed: the picked path has no node → null → default agent path.</summary>
    [Fact(Timeout = 30000)]
    public async Task RequiresInstall_WithoutInstalledNode_ResolvesNull()
    {
        var hub = GetClient();
        // The absent-node case emits only after the InstallProbeTimeout elapses — give the
        // assertion strictly more than that so it never races the probe.
        var resolved = await HarnessNodeType
            .ResolveInstalledHarness(hub, $"{MonolithMeshTestBase.TestPartition}/Harness/{GatedId}")
            .Should().Within(HarnessNodeType.InstallProbeTimeout + TimeSpan.FromSeconds(10)).Emit();
        resolved.Should().BeNull(
            "a RequiresInstall harness without its installed node must fall back — this is what " +
            "makes uninstall (and the pre-gate stale global path) actually revoke the harness");
    }

    /// <summary>Installed: the localized node exists in the user/space partition → the harness runs.</summary>
    [Fact(Timeout = 30000)]
    public async Task RequiresInstall_WithInstalledNode_ResolvesHarness()
    {
        var hub = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // The node the Store plugin's install localizes into the viewer's partition.
        var installed = new MeshNode(GatedId, $"{MonolithMeshTestBase.TestPartition}/Harness")
        {
            NodeType = HarnessNodeType.NodeType,
            Name = "Gated CLI",
            State = MeshNodeState.Active,
            Content = new Harness
            {
                Id = GatedId, DisplayName = "Gated CLI", Order = 9, RequiresInstall = true
            }
        };
        await meshService.CreateNode(installed).Should().Emit();

        var resolved = await HarnessNodeType
            .ResolveInstalledHarness(hub, installed.Path)
            .Should().Emit();
        resolved.Should().NotBeNull("the installed node is what licenses the harness for this user");
        resolved!.Id.Should().Be(GatedId);
    }

    /// <summary>A non-gated harness (MeshWeaver) resolves with no node probe at all.</summary>
    [Fact(Timeout = 30000)]
    public async Task NonGatedHarness_ResolvesWithoutProbe()
    {
        var hub = GetClient();
        var resolved = await HarnessNodeType
            .ResolveInstalledHarness(hub, $"{HarnessNodeType.RootNamespace}/{Harnesses.MeshWeaver}")
            .Should().Emit();
        resolved.Should().BeOfType<MeshWeaverHarness>(
            "the default harness needs no install and must resolve immediately");
    }
}

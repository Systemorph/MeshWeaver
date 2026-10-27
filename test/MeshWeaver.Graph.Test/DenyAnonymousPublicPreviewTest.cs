using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Link previews on an instance closed to anonymous callers (<c>Access:DenyAnonymous</c>): only the OPERATOR
/// can switch them on — a STATIC (declared) <c>_Policy</c> stating <see cref="PartitionAccessPolicy.PublicPreview"/>,
/// such as the portal's <c>Seo:PublicPreview</c> root policy. A policy written in the mesh can still opt a scope
/// OUT, never in. Before this, the switch refused every preview, so the declared instance-wide opt-in was inert on
/// exactly the private instances that set it and every shared link unfurled as a generic sign-in card.
/// </summary>
public class DenyAnonymousPublicPreviewTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ConfigureMeshBase, never ConfigureMesh: the default test configuration grants Public Admin.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                // The operator's declaration: one static root policy stating only PublicPreview.
                MeshNode.Satellite("_Policy", "") with
                {
                    NodeType = SecurityCollections.PartitionAccessPolicyNodeType,
                    Name = "Access Policy",
                    Content = new PartitionAccessPolicy { PublicPreview = true },
                },
                new MeshNode("Open") { NodeType = "Markdown" },
                new MeshNode("Page", "Open") { NodeType = "Markdown" },
                // A declared opt-out below the declared opt-in.
                new MeshNode("DeclaredOut") { NodeType = "Markdown" },
                AssignmentNodeFactory.Policy("DeclaredOut", new PartitionAccessPolicy { PublicPreview = false }),
                new MeshNode("Page", "DeclaredOut") { NodeType = "Markdown" })
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(
                    ImmutableDictionary<string, string?>.Empty.Add(AnonymousAccess.DenyAnonymousConfigKey, "true")).Build()));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private Task<MeshNode> CreateAsSystem(MeshNode node, CancellationToken cancellationToken)
        => Access.RunAsSystem(() => NodeFactory.CreateNode(node))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: cancellationToken);

    private Task<bool> PreviewReaches(string path, bool expected, CancellationToken cancellationToken)
        => Mesh.GetPublicPreview(path)
            .Where(previews => previews == expected)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"the preview answer for {path} must reach {expected}", cancellationToken);

    /// <summary>The declared root opt-in previews a page on a closed instance.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ADeclaredOptIn_PreviewsOnAClosedInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Mesh.GetPublicPreview("Open/Page").FirstAsync().Timeout(TestTimeouts.Quick).Await(ct))
            .Should().BeTrue("the operator declared the instance-wide opt-in");
    }

    /// <summary>A declared opt-out below it still wins: the nearest stated scope decides.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ADeclaredOptOut_StillWins()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Mesh.GetPublicPreview("DeclaredOut/Page").FirstAsync().Timeout(TestTimeouts.Quick).Await(ct))
            .Should().BeFalse("the nearer declared policy states false");
    }

    /// <summary>A policy written in the mesh can still opt a scope OUT on a closed instance.</summary>
    [Fact(Timeout = 90_000)]
    public async Task AStoredOptOut_StillWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = "StoredPreview" + Guid.NewGuid().ToString("N")[..8];
        await CreateAsSystem(new MeshNode(partition) { Name = partition, NodeType = "Markdown" }, ct);
        await CreateAsSystem(new MeshNode("Page", partition) { Name = "Page", NodeType = "Markdown" }, ct);
        await PreviewReaches($"{partition}/Page", true, ct);

        await CreateAsSystem(AssignmentNodeFactory.Policy(partition, new PartitionAccessPolicy { PublicPreview = false }), ct);
        await PreviewReaches($"{partition}/Page", false, ct);
    }
}

/// <summary>The rule itself, without a mesh: nearest stated scope wins; on a closed instance only a DECLARED true counts.</summary>
public class EffectivePublicPreviewTest
{
    private static readonly ImmutableDictionary<string, PartitionAccessPolicy> None =
        ImmutableDictionary<string, PartitionAccessPolicy>.Empty;

    private static ImmutableDictionary<string, PartitionAccessPolicy> Stating(string scope, bool preview)
        => None.Add(scope, new PartitionAccessPolicy { PublicPreview = preview });

    [Fact]
    public void NothingStated_IsOff()
    {
        PermissionEvaluator.EffectivePublicPreview(None, None, "A/B", declaredOnly: false).Should().BeFalse();
        PermissionEvaluator.EffectivePublicPreview(None, None, "A/B", declaredOnly: true).Should().BeFalse();
    }

    [Fact]
    public void ADeclaredRootTrue_ReachesEveryScope_OpenOrClosed()
    {
        var root = Stating("", true);
        PermissionEvaluator.EffectivePublicPreview(root, root, "A/B", declaredOnly: false).Should().BeTrue();
        PermissionEvaluator.EffectivePublicPreview(root, root, "A/B", declaredOnly: true).Should().BeTrue();
    }

    [Fact]
    public void AStoredTrue_CountsOnAnOpenInstance_ButNotOnAClosedOne()
    {
        var stored = Stating("A", true);
        PermissionEvaluator.EffectivePublicPreview(stored, None, "A/B", declaredOnly: false).Should().BeTrue();
        PermissionEvaluator.EffectivePublicPreview(stored, None, "A/B", declaredOnly: true)
            .Should().BeFalse("on a closed instance only the operator's declaration may switch previews on");
    }

    [Fact]
    public void ANearerStoredTrue_DoesNotReopenADeclaredOrStoredFalse_OnAClosedInstance()
    {
        var policies = Stating("", true).Add("A", new PartitionAccessPolicy { PublicPreview = false })
            .Add("A/B", new PartitionAccessPolicy { PublicPreview = true });
        PermissionEvaluator.EffectivePublicPreview(policies, Stating("", true), "A/B/C", declaredOnly: true).Should().BeFalse();
        PermissionEvaluator.EffectivePublicPreview(policies, Stating("", true), "A/B/C", declaredOnly: false).Should().BeTrue();
    }

    [Fact]
    public void AStoredTrue_UnderADeclaredTrue_KeepsTheDeclaredAnswer_OnAClosedInstance()
    {
        var declared = Stating("", true);
        var policies = declared.Add("A", new PartitionAccessPolicy { PublicPreview = true });
        PermissionEvaluator.EffectivePublicPreview(policies, declared, "A/B", declaredOnly: true)
            .Should().BeTrue("a stored opt-in is skipped, not read as an opt-out of the operator's declaration");
    }

    [Fact]
    public void AStoredTrue_OverADeclaredFalseAtTheSameScope_DoesNotBypassIt_OnAClosedInstance()
    {
        var declared = Stating("", true).Add("A", new PartitionAccessPolicy { PublicPreview = false });
        // The effective map holds ONE policy per scope, so the stored `A` replaces the declared one there.
        var effective = Stating("", true).Add("A", new PartitionAccessPolicy { PublicPreview = true });
        PermissionEvaluator.EffectivePublicPreview(effective, declared, "A/B", declaredOnly: true)
            .Should().BeFalse("the declared opt-out at A still decides when a stored true replaced it");
    }

    [Fact]
    public void ANearerFalse_OptsOutOfADeclaredTrue()
    {
        var policies = Stating("", true).Add("A", new PartitionAccessPolicy { PublicPreview = false });
        PermissionEvaluator.EffectivePublicPreview(policies, Stating("", true), "A/B", declaredOnly: true).Should().BeFalse();
        PermissionEvaluator.EffectivePublicPreview(policies, Stating("", true), "A/B", declaredOnly: false).Should().BeFalse();
    }
}

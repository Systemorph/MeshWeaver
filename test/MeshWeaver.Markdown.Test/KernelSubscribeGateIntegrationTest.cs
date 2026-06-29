using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// End-to-end guard for the <c>onReady</c> gate that closes the subscribe-before-create race:
/// <see cref="MarkdownViewLogic.CreateActivityAndSubmit"/> must invoke <c>onReady</c> ONLY after the
/// per-view Activity node is created AND routable. The Blazor views use that callback to flip their
/// <c>kernelReady</c> flag — so the kernel <c>LayoutAreaView</c> subscription is opened only after the
/// activity exists (no NotFound storm). Here we drive the production helper against a real monolith
/// mesh and assert onReady fires and the activity is readable at that point.
/// </summary>
[Collection("KernelTests")]
public class KernelSubscribeGateIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 30_000)]
    public async Task CreateActivityAndSubmit_FiresOnReady_OnlyAfterActivityNodeIsRoutable()
    {
        var client = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        const string ownerPath = "rbuergi";
        var activityPath = $"{ownerPath}/_Activity/markdown-{kernelId}";
        var kernelAddress = new Address(activityPath);

        var submissions = new[]
        {
            new SubmitCodeRequest("MeshWeaver.Layout.Controls.Markdown(\"gated hello\")") { Id = "gate-demo" }
        };

        // onReady is the signal the GUI waits on before it embeds the live area + subscribes.
        var ready = new AsyncSubject<Unit>();
        MarkdownViewLogic.CreateActivityAndSubmit(
            client, meshService, kernelAddress, ownerPath, kernelId, submissions,
            onReady: () => { ready.OnNext(Unit.Default); ready.OnCompleted(); });

        // It must fire (the activity became routable) within a generous budget — fail closed if not.
        await ready.Should().Within(20.Seconds()).Emit();

        // By construction onReady fires only after the activity node's stream emitted non-null, i.e.
        // the per-node hub is live and routable. Reading it now must return the created node — proving
        // the GUI's deferred subscribe will hit an address that EXISTS, not the not-yet-created path
        // that NotFound-stormed.
        var node = await client.GetWorkspace().GetMeshNodeStream(activityPath)
            .Where(n => n is not null)
            .Take(1)
            .Should().Within(10.Seconds())
            .Match(n => n is not null);

        node!.Path.Should().Be(activityPath);
        node.NodeType.Should().Be("Activity");
    }
}

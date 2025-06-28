using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using MeshWeaver.Todo;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

public class TodoLayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .ConfigureTodoApplication();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient(d => d);
    }

    [HubFact]
    public async Task TodoList_LayoutArea_CanRender()
    {
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        var control = await stream
            .GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        control.Should().NotBeNull();
        Output.WriteLine($"Control type: {control.GetType().Name}");
    }
}

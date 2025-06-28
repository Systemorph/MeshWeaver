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

/// <summary>
/// Minimal test to check Todo layout area functionality
/// </summary>
public class TodoLayoutAreaMinimalTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configures the host with Todo application
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .ConfigureTodoApplication();
    }

    /// <summary>
    /// Configures the client
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient(d => d);
    }

    /// <summary>
    /// Basic test to verify Todo layout area can be created and rendered
    /// </summary>
    [HubFact]
    public async Task TodoList_LayoutArea_CanBeRendered()
    {
        // Arrange
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Act
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        var control = await stream
            .GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert
        control.Should().NotBeNull();
        Output.WriteLine($"Control type: {control.GetType().Name}");
    }
}

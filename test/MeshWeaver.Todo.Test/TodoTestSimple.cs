using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Todo;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

public class TodoTestSimple(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .ConfigureTodoApplication();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource =>
                    dataSource.WithType<TodoItem>())
            );
    }

    [HubFact]
    public async Task TestDataUpdate()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var todoData = await workspace
            .GetObservable<TodoItem>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        todoData.Should().NotBeNull();
        if (todoData != null)
        {
            Output.WriteLine($"Found {todoData.Count} todos");
        }
    }
}

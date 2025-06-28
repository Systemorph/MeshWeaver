using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Todo;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

public class BasicTodoTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .ConfigureTodoApplication();
    }

    [HubFact]
    public void TodoApplication_CanBeConfigured()
    {
        // Test that the host can be configured with TodoApplication
        var host = GetHost();
        host.Should().NotBeNull();
    }
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application;
using OpenSmc.Application.Scope;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.LayoutClient;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test;

public class LayoutStackControlTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string MainStackId = nameof(MainStackId);


    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddLayout(def =>
                def
                    .WithInitialState(Controls.Stack()
                    .WithId(MainStackId)
                    .WithClickAction(context =>
                    {
                        context.Hub.Post(new SetAreaRequest(new SetAreaOptions(TestAreas.NewArea), Controls.TextBox("Hello").WithId("HelloId")));
                        return Task.CompletedTask;
                    })
                    )
                    .WithView("1", _ => Controls.TextBox("1").WithId("1")))
            .WithServices(services =>
                services.Configure<ApplicationAddressOptions>(options =>
                    options.Address = new ApplicationAddress("1", "test")));

    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient(new RefreshRequest(), new HostAddress());
    }


    [Fact]
    public async Task LayoutStackUpdateTest()
    {
        var client = GetClient();
        var stack = await client.GetAreaAsync(state => state.GetAreasByControlId(MainStackId).FirstOrDefault());
        stack.View.Should().BeOfType<LayoutStackControl>().Which.Areas.Should().BeEmpty();
        await client.ClickAsync(state => stack);

        await client.GetAreaAsync(state => state.GetAreasByControlId("HelloId").FirstOrDefault());
        stack = await client.GetAreaAsync(state => state.GetAreasByControlId(MainStackId).FirstOrDefault());
        stack.View.Should().BeOfType<LayoutStackControl>().Which.Areas.Should().HaveCount(1);

    }
    [Fact]
    public async Task GetPredefinedArea()
    {
        var client = GetClient();
        client.Post(new RefreshRequest { Area = "1" }, o => o.WithTarget(new HostAddress()));
        var stack = await client.GetAreaAsync(state => state.GetAreasByName(MainStackId, "1"));
        stack.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be("1");

    }

}


public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}
using System.ComponentModel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Application.Scope;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.LayoutClient;
using OpenSmc.Messaging;
using OpenSmc.Scopes;
using OpenSmc.ServiceProvider;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test;

public class LayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string MainStackId = nameof(MainStackId);

    private const string NamedArea = nameof(NamedArea);
    private const string UpdatingView = nameof(UpdatingView);

    private const string SomeString = nameof(SomeString);
    private const string NewString = nameof(NewString);

    [Inject] private ILogger<LayoutTest> logger;
    public interface ITestScope : IMutableScope
    {
        int Integer { get; set; }
        double Double { get; set; }

        [DefaultValue(SomeString)]
        string String { get; set; } 
    }
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
                .AddLayout(def =>
                    def
                        .WithInitialState(Controls.Stack()
                            .WithId(MainStackId)
                            .WithClickAction(context =>
                            {
                                context.Hub.Post(new SetAreaRequest(new SetAreaOptions(TestAreas.NewArea),
                                    Controls.TextBox("Hello")
                                        .WithId("HelloId")));
                                return Task.CompletedTask;
                            })
                        )
                        .WithView(NamedArea, (_,_) => 
                            Controls.TextBox(NamedArea)
                            .WithId(NamedArea)
                        )
                        .WithView(UpdatingView, (_, _) => 
                            Controls.TextBox(GetFromScope(def.Hub))
                                .WithId(UpdatingView)
                                .WithClickAction(ChangeStringInScope)
                            )
                    )
            ;

    }

    private void ChangeStringInScope(IUiActionContext context)
    {
        context.Hub.ServiceProvider.GetRequiredService<IApplicationScope>().GetScope<ITestScope>().String = NewString;
    }

    private string GetFromScope(IMessageHub hub)
    {
        return hub.ServiceProvider.GetRequiredService<IApplicationScope>().GetScope<ITestScope>().String;
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient(new RefreshRequest(), new HostAddress());
    }


    [Fact]
    public async Task LayoutStackUpdateTest()
    {
        var client = GetClient();
        var area = await client.GetAreaAsync(state => state.GetAreaByControlId(MainStackId));
        area.View.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().BeEmpty();
        await client.ClickAsync(_ => area);

        await client.GetAreaAsync(state => state.GetAreaByControlId("HelloId"));
        area = await client.GetAreaAsync(state => state.GetAreaByControlId(MainStackId));
        area.View.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().HaveCount(1);

    }
    [Fact]
    public async Task GetPredefinedArea()
    {
        var client = GetClient();
        client.Post(new RefreshRequest { Area = NamedArea }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetAreaByIdAndArea(MainStackId, NamedArea));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(NamedArea);
    }



    [Fact]
    public async Task GetAreaWithUpdatingView()
    {
        var client = GetClient();
        client.Post(new RefreshRequest { Area = UpdatingView }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetAreaById(UpdatingView));
        area.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(SomeString);
        await client.ClickAsync(_ => area);

        AreaChangedEvent IsUpdatedView(LayoutClientState layoutClientState)
        {
            var ret = layoutClientState.GetAreaByControlId(UpdatingView);
            if(ret?.View is TextBoxControl { Data: not SomeString })
                return ret;

            logger.LogInformation($"Found view: {ret?.View}" );
            return null;
        }

        var changedArea = await client.GetAreaAsync(IsUpdatedView);
        changedArea.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(NewString);


    }

}


public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}
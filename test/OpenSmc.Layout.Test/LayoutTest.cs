using System.ComponentModel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.DataBinding;
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

    private const string DataBoundView = nameof(DataBoundView);

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
                .AddLayout(layout =>
                    layout
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
                        .WithView(NamedArea, (_, _) =>
                            Controls.TextBox(NamedArea)
                                .WithId(NamedArea)
                        )
                        // this tests proper updating in the case of MVP
                        .WithView(UpdatingView, (_, _) =>
                            Controls.TextBox(layout.ApplicationScope.GetScope<ITestScope>().String)
                                .WithId(UpdatingView)
                                .WithClickAction(_ => layout.ApplicationScope.GetScope<ITestScope>().String = NewString)
                        )
                        // this tests proper updating in the case of MVP
                        .WithView(DataBoundView, (_, _) =>
                            Template.Bind(layout.ApplicationScope.GetScope<ITestScope>(),
                                scope => Controls.TextBox(scope.String).WithId(DataBoundView)
                            )
                        )
                );

    }


    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient(new HostAddress());
    }


    [Fact]
    public async Task LayoutStackUpdateTest()
    {
        var client = GetClient();
        var area = await client.GetAreaAsync(state => state.GetById(MainStackId));
        area.View.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().BeEmpty();
        await client.ClickAsync(_ => area);

        await client.GetAreaAsync(state => state.GetById("HelloId"));
        area = await client.GetAreaAsync(state => state.GetById(MainStackId));
        area.View.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().HaveCount(1);

    }
    [Fact]
    public async Task GetPredefinedArea()
    {
        var client = GetClient();
        client.Post(new RefreshRequest { Area = NamedArea }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetByIdAndArea(MainStackId, NamedArea));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(NamedArea);
        area = await client.GetAreaAsync(state => state.GetById(NamedArea));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(NamedArea);
        var address = ((IUiControl)area.View).Address;
        area = await client.GetAreaAsync(state => state.GetByAddress(address));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(NamedArea);

    }



    [Fact]
    public async Task GetAreaWithUpdatingView()
    {

        var client = GetClient();
        client.Post(new RefreshRequest { Area = UpdatingView }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetById(UpdatingView));
        area.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(SomeString);

        await client.ClickAsync(_ => area);

        AreaChangedEvent IsUpdatedView(LayoutClientState layoutClientState)
        {
            var ret = layoutClientState.GetById(UpdatingView);
            if (ret?.View is TextBoxControl { Data: not SomeString })
                return ret;

            logger.LogInformation($"Found view: {ret?.View}");
            return null;
        }

        var changedArea = await client.GetAreaAsync(IsUpdatedView);
        changedArea.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(NewString);


    }
    [Fact]
    public async Task GetAreaWithDataBoundView()
    {

        var client = GetClient();
        client.Post(new RefreshRequest { Area = DataBoundView }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetById(DataBoundView));
        area.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().BeOfType<Binding>()
            .Which.Path.Should().Be(nameof(ITestScope.String));

        //await client.ClickAsync(_ => area);

        AreaChangedEvent IsUpdatedView(LayoutClientState layoutClientState)
        {
            var ret = layoutClientState.GetById(UpdatingView);
            if (ret?.View is TextBoxControl { Data: not SomeString })
                return ret;

            logger.LogInformation($"Found view: {ret?.View}");
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
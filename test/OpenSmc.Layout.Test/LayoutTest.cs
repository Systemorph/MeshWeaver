using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.DataBinding;
using OpenSmc.Layout.LayoutClient;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;
using OpenSmc.Utils;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test;

public class LayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Inject] private ILogger<LayoutTest> logger;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress<ClientAddress>((a,d)=>d.Package()))
            .AddData(data => data.FromConfigurableDataSource("Local", 
                ds => ds
                    .WithType<TestLayoutPlugin.DataRecord>(t => t.WithInitialData([new("Hello", "World")]))))
            .AddPlugin<TestLayoutPlugin>()    
            .AddLayout(layout => layout.Hub.ServiceProvider.GetRequiredService<TestLayoutPlugin>().Configure(layout));
            

    }


    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient(new HostAddress());
    }


    [Fact]
    public async Task LayoutStackUpdateTest()
    {
        var client = GetClient();
        var area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.MainStackId));
        area.View.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().BeEmpty();
        await client.ClickAsync(_ => area);

        await client.GetAreaAsync(state => state.GetById("HelloId"));
        area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.MainStackId));
        area.View.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().HaveCount(1);

    }
    [Fact]
    public async Task GetPredefinedArea()
    {
        var client = GetClient();
        client.Post(new RefreshRequest { Area = TestLayoutPlugin.NamedArea }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetByIdAndArea(TestLayoutPlugin.MainStackId, TestLayoutPlugin.NamedArea));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayoutPlugin.NamedArea);
        area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.NamedArea));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayoutPlugin.NamedArea);
        var address = ((IUiControl)area.View).Address;
        area = await client.GetAreaAsync(state => state.GetByAddress(address));
        area.View.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayoutPlugin.NamedArea);

    }



    [Fact]
    public async Task UpdatingView()
    {

        var client = GetClient();
        client.Post(new RefreshRequest { Area = TestLayoutPlugin.UpdatingView }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.UpdatingView));
        area.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(TestLayoutPlugin.SomeString);

        await client.ClickAsync(_ => area);

        LayoutArea IsUpdatedView(LayoutClientState layoutClientState)
        {
            var ret = layoutClientState.GetById(TestLayoutPlugin.UpdatingView);
            if (ret?.View is TextBoxControl { Data: not TestLayoutPlugin.SomeString })
                return ret;

            logger.LogInformation($"Found view: {ret?.View}");
            return null;
        }

        var changedArea = await client.GetAreaAsync(IsUpdatedView);
        changedArea.View
            .Should().BeOfType<TextBoxControl>()
            .Which.Data.Should().Be(TestLayoutPlugin.NewString);


    }
    [Fact]
    public async Task DataBoundView()
    {

        var client = GetClient();
        var observer = client.AddObservable();
        client.Post(new RefreshRequest { Area = TestLayoutPlugin.DataBoundView }, o => o.WithTarget(new HostAddress()));
        var area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.DataBoundView));
        area.View
            .Should().BeOfType<MenuItemControl>()
            .Which.Title.Should().BeOfType<Binding>()
            .Which.Path.Should().Be(nameof(TestLayoutPlugin.DataRecord.DisplayName).ToCamelCase());

        client.Click(area);
        var dataChanged = await observer.OfType<DataChangedEvent>().FirstAsync();
        

    }

}


public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}
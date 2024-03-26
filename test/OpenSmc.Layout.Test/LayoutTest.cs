using System.Reactive.Linq;
using System.Runtime.InteropServices.ComTypes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test;

public class LayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Inject] private ILogger<LayoutTest> logger;

    private static readonly Dictionary<LayoutAreaReference, UiControl> TestAreas
        = new()
        {
            { new LayoutAreaReference("/"), (UiControl)Controls.Stack().WithView("Hello", "Hello").WithView("World", "World") },
        };
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress<ClientAddress>((a,d)=>d.Package()))
            .AddData(data => data
                .FromConfigurableDataSource("Local", 
                ds => ds
                    .WithType<TestLayoutPlugin.DataRecord>(t => t.WithInitialData([new("Hello", "World")]))))
            .AddLayout(layout => TestAreas.Aggregate(layout, (l,kvp) => l.WithView(kvp.Key, _ => kvp.Value)));
            

    }


    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddData(data => data.FromHub(new HostAddress()));
    }

    [HubFact]
    public async Task BasicArea()
    {
        var workspace = GetClient().GetWorkspace();
        var reference = new LayoutAreaReference("/");
        var stream = workspace.GetRemoteStream(new HostAddress(), reference);

        var control = await stream.GetControl(reference);
        var areas = control.Should().BeOfType<LayoutStackControl>()
            .Which
            .Areas.Should().HaveCount(2)
                .And.Subject.Should().AllBeOfType<LayoutAreaReference>()
                .And.Subject.Cast<LayoutAreaReference>()
                .ToArray();


        var areaControls = areas
            .ToAsyncEnumerable()
            .SelectAwait(async a => await stream.GetControl(a).FirstAsync())
            .ToArrayAsync();


    }

    //    public async Task LayoutStackUpdateTest()
    //    {
    //        var client = GetClient();
    //        var area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.MainStackId));
    //        area.Control.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().BeEmpty();
    //        await client.ClickAsync(_ => area);

    //        await client.GetAreaAsync(state => state.GetById("HelloId"));
    //        area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.MainStackId));
    //        area.Control.Should().BeOfType<Composition.LayoutStackControl>().Which.Areas.Should().HaveCount(1);

    //    }

    //#if CIRun
    //    [Fact(Skip = "Hangs")]
    //#else
    //    [Fact(Timeout = 5000)]
    //#endif

    //public async Task GetSimpleArea()
    //{
        //var client = GetClient();
        //client.Post(new RefreshRequest { Area = TestLayoutPlugin.NamedArea }, o => o.WithTarget(new HostAddress()));
        //var area = await client.GetAreaAsync(state => state.GetByIdAndArea(TestLayoutPlugin.MainStackId, TestLayoutPlugin.NamedArea));
        //area.Control.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayoutPlugin.NamedArea);
        //area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.NamedArea));
        //area.Control.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayoutPlugin.NamedArea);
        //var address = ((IUiControl)area.Control).Address;
        //area = await client.GetAreaAsync(state => state.GetByAddress(address));
        //area.Control.Should().BeOfType<TextBoxControl>().Which.Data.Should().Be(TestLayoutPlugin.NamedArea);

    //}



    //#if CIRun
    //    [Fact(Skip = "Hangs")]
    //#else
    //    [Fact(Timeout = 5000)]
    //#endif

    //    public async Task UpdatingView()
    //    {

    //        var client = GetClient();
    //        client.Post(new AreaReference(TestLayoutPlugin.UpdatingView), o => o.WithTarget(new HostAddress()));
    //        var area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.UpdatingView));
    //        area.Control
    //            .Should().BeOfType<TextBoxControl>()
    //            .Which.Data.Should().Be(TestLayoutPlugin.SomeString);

    //        await client.ClickAsync(_ => area);

    //        LayoutArea IsUpdatedView(LayoutClientState layoutClientState)
    //        {
    //            var ret = layoutClientState.GetById(TestLayoutPlugin.UpdatingView);
    //            if (ret?.Control is TextBoxControl { Data: not TestLayoutPlugin.SomeString })
    //                return ret;

    //            logger.LogInformation($"Found view: {ret?.Control}");
    //            return null;
    //        }

    //        var changedArea = await client.GetAreaAsync(IsUpdatedView);
    //        changedArea.Control
    //            .Should().BeOfType<TextBoxControl>()
    //            .Which.Data.Should().Be(TestLayoutPlugin.NewString);


    //    }

    //#if CIRun
    //    [Fact(Skip = "Hangs")]
    //#else
    //    [Fact(Timeout = 5000)]
    //#endif

    //    public async Task DataBoundView()
    //    {

    //        var client = GetClient();
    //        var observer = client.AddObservable();
    //        client.Post(new AreaReference { Area = TestLayoutPlugin.DataBoundView }, o => o.WithTarget(new HostAddress()));
    //        var area = await client.GetAreaAsync(state => state.GetById(TestLayoutPlugin.DataBoundView));
    //        area.Control
    //            .Should().BeOfType<MenuItemControl>()
    //            .Which.Title.Should().BeOfType<Binding>()
    //            .Which.Path.Should().Be(nameof(TestLayoutPlugin.DataRecord.DisplayName).ToCamelCase());

    //        client.Click(area);
    //        var dataChanged = await observer.OfType<DataChangedEvent>().FirstAsync();


    //    }

}


public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}
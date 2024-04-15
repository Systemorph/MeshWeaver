using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
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


    private const string StaticView = nameof(StaticView);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
                .WithRoutes(r => r.RouteAddress<ClientAddress>((a, d) => d.Package(r.Hub.SerializationOptions)))
                .AddData(data => data
                    .FromConfigurableDataSource("Local",
                        ds => ds
                            .WithType<TestLayoutPlugin.DataRecord>(t => t.WithInitialData([new("Hello", "World")]))
                            .WithType<Toolbar>(t => t.WithInitialData([new(2024)]))
                        ))
                .AddLayout(
                    layout => layout
                        .WithView(StaticView, Controls.Stack().WithView("Hello", "Hello").WithView("World", "World"))
                        .WithViewDefinition(nameof(ViewWithProgress), ViewWithProgress)
                        .WithViewDefinition(nameof(UpdatingView), layout.Hub.ServiceProvider.GetRequiredService<IWorkspace>().Stream.Select(ws => (Func<LayoutArea, UiControl>)(_ => UpdatingView(GetToolbar(ws.Value)))))

                )
            ;


    }

    private record Toolbar(int Year)
    {
        [Key]
        public string Id { get; } = nameof(Toolbar);
    }
    private Toolbar GetToolbar(WorkspaceState ws)
    {
        return ws.GetData<Toolbar>().Single();
    }


    private static async Task<object> ViewWithProgress(LayoutArea area)
    {
        var percentage = 0;
        var progress = Controls.Progress("Processing", percentage);
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(30);
            area.UpdateView(nameof(ViewWithProgress),
                progress = progress with { Progress = percentage += 10 });

        }

        return Controls.HtmlView("Report");
    }
    private static UiControl UpdatingView(Toolbar toolbar)
    {

        return Controls.HtmlView($"Report for year {toolbar.Year}");
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayout(d => d);
    }

    [HubFact]
    public async Task BasicArea()
    {
        var reference = new LayoutAreaReference(StaticView);

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream(new HostAddress(), reference);

        var control = await stream.GetControl(reference.Area).FirstAsync();
        var areas = control.Should().BeOfType<LayoutStackControl>()
            .Which
            .Areas.Should().HaveCount(2)
                .And.Subject.Should().AllBeOfType<EntityReference>()
                .And.Subject.Cast<EntityReference>()
                .ToArray();


        var areaControls = await areas
            .ToAsyncEnumerable()
            .SelectAwait(async a => await stream.GetData(a).FirstAsync())
            .ToArrayAsync();

        areaControls.Should().HaveCount(2).And.AllBeOfType<HtmlControl>();
    }

    [HubFact]
    public async Task TestViewWithProgress()
    {
        var reference = new LayoutAreaReference(nameof(ViewWithProgress));

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream(new HostAddress(), reference);
        var controls = await stream.GetControl(reference.Area).TakeUntil(o => o is HtmlControl).ToArray();
        controls.Should().HaveCountGreaterThan(1).And.HaveCountLessThan(12);
    }

    [HubFact]
    public async Task TestUpdatingView()
    {
        var reference = new LayoutAreaReference(nameof(UpdatingView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream(new HostAddress(), reference);
        var controls = await stream.GetControl(reference.Area).TakeUntil(o => o is HtmlControl).ToArray();
        controls.Last().Should().BeOfType<HtmlControl>().Which.Data.ToString().Should().Contain("2024");
        stream.Update(x => x.SetValue(x.Value.Update(new Toolbar(2025))));

    }

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

    [HubFact]
    public async Task TestTest()
    {
        int count = 0;
        // Simulating user submissions (e.g., button clicks, form submissions)
        var userSubmissions = Observable.Interval(TimeSpan.FromMilliseconds(1)).Take(100).Select(_ => ++count);//.TakeUntil(Observable.Timer(DateTimeOffset.FromUnixTimeSeconds(1))).Select(i => ++count);
        
        // Define your minimum timeout (adjust as needed)
        var minimumTimeout = TimeSpan.FromMilliseconds(30);


        // Merge user submissions with the minimum timeout observable
        var sampled = userSubmissions.Sample(minimumTimeout);

        var received = await sampled.ToArray();

        received.Last().Should().Be(count);
    }
}

public record ToolbarEntity(int Year)
{
}

public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}
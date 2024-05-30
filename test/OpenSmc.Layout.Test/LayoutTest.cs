using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using FluentAssertions;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataBinding;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;
using Xunit.Abstractions;

namespace OpenSmc.Layout.Test;

public class LayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string StaticView = nameof(StaticView);

    public record DataRecord([property: Key] string SystemName, string DisplayName);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress<ClientAddress>((_, d) => d.Package(r.Hub.JsonSerializerOptions))
            )
            .AddData(data =>
                data.FromConfigurableDataSource(
                    "Local",
                    ds =>
                        ds.WithType<DataRecord>(t =>
                                t.WithInitialData([new("Hello", "Hello"), new("World", "World")])
                            )
                            .WithType<Toolbar>(t => t.WithInitialData([new(2024)]))
                )
            )
            .AddLayout(layout =>
                layout
                    .WithView(
                        StaticView,
                        Controls.Stack().WithView("Hello", "Hello").WithView("World", "World")
                    )
                    .WithView(nameof(ViewWithProgress), ViewWithProgress)
                    .WithView(
                        nameof(UpdatingView),
                        _ =>
                            layout
                                .Hub.GetWorkspace()
                                .Stream.Select(ws => GetToolbar(ws.Value))
                                .DistinctUntilChanged()
                                .Select(UpdatingView)
                    )
                    .WithView(
                        nameof(ItemTemplate),
                        _ =>
                            layout
                                .Hub.GetWorkspace()
                                .Stream.Select(x => x.Value.GetData<DataRecord>())
                                .DistinctUntilChanged()
                                .Select(ItemTemplate)
                    )
                    .WithView(
                        nameof(Counter),
                        _ => layout.Hub.GetWorkspace().Stream.Select(_ => Counter())
                    )
            );
    }

    private UiControl ItemTemplate(IReadOnlyCollection<DataRecord> data) =>
        Controls.Bind(
            data,
            record => Controls.TextBox(record.DisplayName).WithId(record.SystemName)
        );

    private UiControl Counter()
    {
        int counter = 0;
        return Controls
            .Stack()
            .WithView(
                "Button",
                Controls
                    .Menu("Increase Counter")
                    .WithClickAction(context =>
                        context.Layout.Update(
                            $"{nameof(Counter)}/{nameof(Counter)}",
                            Controls.Html((++counter))
                        )
                    )
            )
            .WithView(nameof(Counter), Controls.Html(counter.ToString()));
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    )
    {
        return base.ConfigureClient(configuration)
            .AddLayout(d => d)
            .AddData(data =>
                data.FromHub(
                    new HostAddress(),
                    source => source.WithType<Toolbar>().WithType<DataRecord>()
                )
            );
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
            area.Update(
                nameof(ViewWithProgress),
                progress = progress with { Progress = percentage += 10 }
            );
        }

        return Controls.Html("Report");
    }

    private static UiControl UpdatingView(Toolbar toolbar)
    {
        return Controls
            .Stack()
            .WithView("Toolbar", Controls.Bind(toolbar, tb => Controls.TextBox(tb.Year)))
            .WithView("Content", Controls.Html($"Report for year {toolbar.Year}"));
    }

    [HubFact]
    public async Task BasicArea()
    {
        var reference = new LayoutAreaReference(StaticView);

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetStream(new HostAddress(), reference);

        var control = await stream.GetControl(reference.Area);
        var areas = control
            .Should()
            .BeOfType<LayoutStackControl>()
            .Which.Areas.Should()
            .HaveCount(2)
            .And.Subject;

        var areaControls = await areas
            .ToAsyncEnumerable()
            .SelectAwait(async a =>
                await stream.Select(s => s.Value.GetControl(a)).FirstAsync(x => x != null)
            )
            .ToArrayAsync();

        areaControls.Should().HaveCount(2).And.AllBeOfType<HtmlControl>();
    }

    [HubFact]
    public async Task TestViewWithProgress()
    {
        var reference = new LayoutAreaReference(nameof(ViewWithProgress));

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetStream(new HostAddress(), reference);
        var controls = await stream
            .GetControlStream(reference.Area)
            .TakeUntil(o => o is HtmlControl)
            .ToArray();
        controls.Should().HaveCountGreaterThan(1).And.HaveCountLessThan(12);
    }

    [HubFact]
    public async Task TestUpdatingView()
    {
        var reference = new LayoutAreaReference(nameof(UpdatingView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetStream(new HostAddress(), reference);
        var reportArea = $"{reference.Area}/Content";
        var content = await stream.GetControlStream(reportArea).FirstAsync();
        content.Should().BeOfType<HtmlControl>().Which.Data.ToString().Should().Contain("2024");

        // Get toolbar and change value.
        var toolbarArea = $"{reference.Area}/Toolbar";
        var toolbar = (TextBoxControl)await stream.GetControlStream(toolbarArea).FirstAsync();
        toolbar.Data.Should().BeOfType<Binding>().Which.Path.Should().Be("$.year");
        var toolbarDataReference = toolbar.DataContext.Should().BeOfType<EntityReference>().Which;
        var toolbarData = (Toolbar)await stream.GetDataStream(toolbarDataReference).FirstAsync();
        toolbarData.Year.Should().Be(2024);

        stream.Update(ci => new Data.Serialization.ChangeItem<EntityStore>(
            stream.Id,
            stream.Reference,
            ci.Update(toolbarDataReference, toolbarData with { Year = 2025 }),
            hub.Address,
            hub.Version
        ));

        var updatedControls = await stream
            .GetControlStream(reportArea)
            .TakeUntil(o => o is HtmlControl html && !html.Data.ToString()!.Contains("2024"))
            .ToArray();
        updatedControls
            .Last()
            .Should()
            .BeOfType<HtmlControl>()
            .Which.Data.ToString()
            .Should()
            .Contain("2025");
    }

    [HubFact]
    public async Task TestItemTemplate()
    {
        var reference = new LayoutAreaReference(nameof(ItemTemplate));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetStream(new HostAddress(), reference);
        var controlArea = $"{reference.Area}";
        var content = await stream.GetControlStream(controlArea).FirstAsync();
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        var workspaceReferences = await itemTemplate
            .DataContext.Should()
            .BeAssignableTo<IEnumerable>()
            .Which.OfType<EntityReference>()
            .ToAsyncEnumerable()
            .SelectAwait(async r => (DataRecord)await stream.GetDataStream(r).FirstAsync())
            .ToArrayAsync();

        itemTemplate.Data.Should().BeOfType<Binding>().Which.Path.Should().Be("$");
        itemTemplate
            .View.Should()
            .BeOfType<TextBoxControl>()
            .Which.Data.Should()
            .BeOfType<Binding>()
            .Which.Path.Should()
            .Be("$.displayName");
        workspaceReferences
            .Should()
            .HaveCount(2)
            .And.Contain(r => r.SystemName == "Hello")
            .And.Contain(r => r.SystemName == "World");
    }

    [HubFact]
    public async Task TestClick()
    {
        var reference = new LayoutAreaReference(nameof(Counter));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetStream(new HostAddress(), reference);
        var buttonArea = $"{reference.Area}/Button";
        var content = await stream.GetControlStream(buttonArea).FirstAsync();
        content
            .Should()
            .BeOfType<MenuItemControl>()
            .Which.Title.ToString()
            .Should()
            .Contain("Count");
        stream.Post(new ClickedEvent(buttonArea));
        var counterArea = $"{reference.Area}/Counter";
        content = await stream
            .GetControlStream(counterArea)
            .FirstAsync(x => x is HtmlControl html && html.Data is not "0");
        content.Should().BeOfType<HtmlControl>().Which.Data.Should().Be("1");
    }
}

public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}

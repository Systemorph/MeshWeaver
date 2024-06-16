using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using Json.Patch;
using Json.Pointer;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Views;
using OpenSmc.Messaging;
using OpenSmc.Utils;
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
                )
            )
            .AddLayout(layout =>
                layout
                    .WithView(
                        StaticView,
                        Controls.Stack().WithView("Hello", "Hello").WithView("World", "World")
                    )
                    .WithView(nameof(ViewWithProgress), ViewWithProgress)
                    .WithView(nameof(UpdatingView), UpdatingView())
                    .WithView(
                        nameof(ItemTemplate),
                        area =>
                            layout
                                .Hub.GetWorkspace()
                                .Stream.Select(x => x.Value.GetData<DataRecord>())
                                .DistinctUntilChanged()
                                .Select(data => ItemTemplate(area, data))
                    )
                    .WithView(
                        nameof(Counter),
                        _ => layout.Hub.GetWorkspace().Stream.Select(_ => Counter())
                    )
                    .WithView("int", 3)
                    .WithView(nameof(DataGrid), DataGrid)
            );
    }

    private object ItemTemplate(LayoutArea area, IReadOnlyCollection<DataRecord> data) =>
        area.Bind(
            data,
            nameof(ItemTemplate),
            record => Controls.TextBox(record.DisplayName).WithId(record.SystemName)
        );

    private object Counter()
    {
        int counter = 0;
        return Controls
            .Stack()
            .WithView(
                "Button",
                Controls
                    .Menu("Increase Counter")
                    .WithClickAction(context =>
                        context.Layout.UpdateLayout(
                            $"{nameof(Counter)}/{nameof(Counter)}",
                            Controls.Html((++counter))
                        )
                    )
            )
            .WithView(nameof(Counter), Controls.Html(counter.ToString()));
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => base.ConfigureClient(configuration).AddLayoutClient(d => d);

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
            .SelectAwait(async a => await stream.GetControl(a))
            .ToArrayAsync();

        areaControls.Should().HaveCount(2).And.AllBeOfType<HtmlControl>();
    }

    private static async Task<object> ViewWithProgress(LayoutArea area)
    {
        var percentage = 0;
        var progress = Controls.Progress("Processing", percentage);
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(30);
            area.UpdateLayout(
                nameof(ViewWithProgress),
                progress = progress with { Progress = percentage += 10 }
            );
        }

        return Controls.Html("Report");
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

    private record Toolbar(int Year);

    private static object UpdatingView()
    {
        var toolbar = new Toolbar(2024);

        return Controls
            .Stack()
            .WithView(
                "Toolbar",
                layoutArea =>
                    layoutArea.Bind(toolbar, nameof(toolbar), tb => Controls.TextBox(tb.Year))
            )
            .WithView(
                "Content",
                area =>
                    area.GetDataStream<Toolbar>(nameof(toolbar))
                        .Select(tb => Controls.Html($"Report for year {tb.Year}"))
            );
    }

    [HubFact]
    public async Task TestUpdatingView()
    {
        var reference = new LayoutAreaReference(nameof(UpdatingView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetStream(new HostAddress(), reference);
        var reportArea = $"{reference.Area}/Content";
        var content = await stream.GetControlStream(reportArea).FirstAsync(x => x != null);
        content.Should().BeOfType<HtmlControl>().Which.Data.ToString().Should().Contain("2024");

        // Get toolbar and change value.
        var toolbarArea = $"{reference.Area}/Toolbar";
        var yearTextBox = (TextBoxControl)await stream.GetControlStream(toolbarArea).FirstAsync();
        var jsonPath = yearTextBox.Data.Should().BeOfType<JsonPointerReference>().Which;
        jsonPath.Pointer.Should().Be("/data/toolbar/year");
        var year = await stream.Reduce(jsonPath).FirstAsync();
        year.Value.Should().BeOfType<JsonElement>().Which.GetInt32().Should().Be(2024);

        stream.Update(ci => new Data.Serialization.ChangeItem<JsonElement>(
            stream.Id,
            stream.Reference,
            new JsonPatch(PatchOperation.Replace(JsonPointer.Parse(jsonPath.Pointer), 2025)).Apply(
                ci
            ),
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
        var content = await stream.GetControlStream(controlArea).FirstAsync(x => x != null);
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        var dataReference = itemTemplate.Data.Should().BeOfType<JsonPointerReference>().Which;
        dataReference.Pointer.Should().Be($"/data/{nameof(ItemTemplate)}");
        var data = await stream.Reduce(dataReference).FirstAsync();

        var deserialized = data.Value?.Deserialize<IEnumerable<DataRecord>>(
            hub.JsonSerializerOptions
        );
        deserialized
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

    private object DataGrid(LayoutArea area)
    {
        var data = new DataRecord[] { new("1", "1"), new("2", "2") };
        return data.ToDataGrid(grid =>
            grid.WithColumn(x => x.SystemName).WithColumn(x => x.DisplayName)
        );
    }

    [HubFact]
    public async Task TestDataGrid()
    {
        var reference = new LayoutAreaReference(nameof(DataGrid));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetStream(new HostAddress(), reference);
        var content = await stream.GetControlStream(reference.Area).FirstAsync();
        content
            .Should()
            .BeOfType<DataGridControl>()
            .Which.Columns.Should()
            .HaveCount(2)
            .And.BeEquivalentTo(
                [
                    new DataGridColumn<string> { Property = nameof(DataRecord.SystemName).ToCamelCase(), Title = nameof(DataRecord.SystemName).Wordify() },
                    new DataGridColumn<string> { Property = nameof(DataRecord.DisplayName).ToCamelCase(), Title = nameof(DataRecord.DisplayName).Wordify()}
                ]
            );
    }
}

public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}

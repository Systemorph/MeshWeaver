using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Json.Patch;
using Json.Pointer;
using OpenSmc.Data;
using OpenSmc.Disposables;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.DataGrid;
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
                        (area, _) =>
                            layout
                                .Hub.GetWorkspace()
                                .Stream.Select(x => x.Value.GetData<DataRecord>())
                                .DistinctUntilChanged()
                                .Select(data => ItemTemplate(area, data))
                    )
                    .WithView(nameof(CatalogView), CatalogView)
                    .WithView(
                        nameof(Counter),
                        (_, _) => layout.Hub.GetWorkspace().Stream.Select(_ => Counter())
                    )
                    .WithView("int", 3)
                    .WithView(nameof(DataGrid), DataGrid)
                    .WithView(nameof(DataBoundCheckboxes), DataBoundCheckboxes)
                    .WithView(nameof(DisposalView), DisposalView)
            );
    }

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
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

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

    private static async Task<object> ViewWithProgress(LayoutAreaHost area, RenderingContext ctx)
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
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
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
                (layoutArea, _) =>
                    layoutArea.Bind(toolbar, nameof(toolbar), tb => Controls.TextBox(tb.Year))
            )
            .WithView(
                "Content",
                (area, _) =>
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
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var reportArea = $"{reference.Area}/Content";
        var content = await stream.GetControlStream(reportArea).FirstAsync(x => x != null);
        content.Should().BeOfType<HtmlControl>().Which.Data.ToString().Should().Contain("2024");

        // Get toolbar and change value.
        var toolbarArea = $"{reference.Area}/Toolbar";
        var yearTextBox = (TextBoxControl)await stream.GetControlStream(toolbarArea).FirstAsync();
        yearTextBox.DataContext.Should().Be("/data/\"toolbar\"");

        var dataPointer = yearTextBox.Data.Should().BeOfType<JsonPointerReference>().Which;
        dataPointer.Pointer.Should().Be("/year");
        var pointer = JsonPointer.Parse(dataPointer.Pointer);
        var year = await stream
            .GetDataStream<JsonElement>(new JsonPointerReference(yearTextBox.DataContext))
            .Select(s => pointer.Evaluate(s))
            .FirstAsync();
        year.Value.GetInt32().Should().Be(2024);

        stream.Update(ci =>
        {
            var patch = new JsonPatch(
                PatchOperation.Replace(
                    JsonPointer.Parse(yearTextBox.DataContext + dataPointer.Pointer),
                    2025
                )
            );
            return new Data.Serialization.ChangeItem<JsonElement>(
                stream.Owner,
                stream.Reference,
                patch.Apply(ci),
                hub.Address,
                patch,
                hub.Version
            );
        });

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

    private object ItemTemplate(LayoutAreaHost area, IReadOnlyCollection<DataRecord> data) =>
        area.Bind(
            data,
            nameof(ItemTemplate),
            record => Controls.TextBox(record.DisplayName).WithId(record.SystemName)
        );

    [HubFact]
    public async Task TestItemTemplate()
    {
        var reference = new LayoutAreaReference(nameof(ItemTemplate));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var controlArea = $"{reference.Area}";
        var content = await stream.GetControlStream(controlArea).FirstAsync(x => x != null);
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        itemTemplate.DataContext.Should().Be($"/data/\"{nameof(ItemTemplate)}\"");
        var data = await stream
            .GetDataStream<IEnumerable<JsonElement>>(
                new JsonPointerReference(itemTemplate.DataContext)
            )
            .FirstAsync();

        // data.Should()
        //     .HaveCount(2)
        //     .And.Contain(r => r.SystemName == "Hello")
        //     .And.Contain(r => r.SystemName == "World");

        var view = itemTemplate.View;
        var pointer = view.Should()
            .BeOfType<TextBoxControl>()
            .Which.Data.Should()
            .BeOfType<JsonPointerReference>()
            .Subject;
        pointer.Pointer.Should().Be("/displayName");
        var parsedPointer = JsonPointer.Parse(pointer.Pointer);
        data.Select(d => parsedPointer.Evaluate(d).Value.ToString())
            .Should()
            .BeEquivalentTo("Hello", "World");
    }

    [HubFact]
    public async Task TestClick()
    {
        var reference = new LayoutAreaReference(nameof(Counter));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
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
            .FirstAsync(x => x is HtmlControl html && html.Data is not "0")
            .Timeout(TimeSpan.FromSeconds(5));
        content.Should().BeOfType<HtmlControl>().Which.Data.Should().Be("1");
    }

    private object DataGrid(LayoutAreaHost area, RenderingContext ctx)
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
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var content = await stream
            .GetControlStream(reference.Area)
            .Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync();
        content
            .Should()
            .BeOfType<DataGridControl>()
            .Which.Columns.Should()
            .HaveCount(2)
            .And.BeEquivalentTo(
                [
                    new DataGridColumn<string>
                    {
                        Property = nameof(DataRecord.SystemName).ToCamelCase(),
                        Title = nameof(DataRecord.SystemName).Wordify()
                    },
                    new DataGridColumn<string>
                    {
                        Property = nameof(DataRecord.DisplayName).ToCamelCase(),
                        Title = nameof(DataRecord.DisplayName).Wordify()
                    }
                ]
            );
    }

    public static UiControl DataBoundCheckboxes(LayoutAreaHost area, RenderingContext context)
    {
        var data = new []
        {
            new {Label = "Label1", Value = true}, 
            new { Label = "Label1", Value = true }, 
            new { Label = "Label1", Value = true }
        };
        return area.Bind(data, nameof(DataBoundCheckboxes), x => Controls.CheckBox(x.Label, x.Value));
    }

    [HubFact]
    public async Task TestDataBoundCheckboxes()
    {
        var reference = new LayoutAreaReference(nameof(DataBoundCheckboxes));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var controlArea = $"{reference.Area}";
        var content = await stream
            .GetControlStream(controlArea)
            .Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync(x => x != null);
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        itemTemplate.DataContext.Should().Be($"/data/\"{nameof(DataBoundCheckboxes)}\"");
        var data = await stream.GetDataStream<IReadOnlyCollection<object>>(new JsonPointerReference(itemTemplate.DataContext)).FirstAsync();

        data.Should().HaveCount(3);

        var first = (JsonObject) data.First();

        first["value"].Deserialize<bool>().Should().BeTrue();

        var firstValuePointer = itemTemplate.DataContext + "/0/value";

        stream.Update(ci =>
        {
            var patch = new JsonPatch(
                PatchOperation.Replace(JsonPointer.Parse(firstValuePointer), false)
            );
            return new Data.Serialization.ChangeItem<JsonElement>(
                stream.Owner,
                stream.Reference,
                patch.Apply(ci),
                hub.Address,
                patch,
                hub.Version
            );
        });

        var hostWorkspace = GetHost().GetWorkspace();

        var hostStream = hostWorkspace.GetStreamFor(
            null,
            reference
        );

        await hostStream.Reduce(new JsonPointerReference(firstValuePointer), null)
            .TakeUntil(e => !e.Value?.GetBoolean() ??   false)
            .Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync();
            }
    private IObservable<object> CatalogView(LayoutAreaHost host, RenderingContext context)
    {
        return host
            .Hub.GetWorkspace()
            .Stream.Select(x => x.Value.GetData<DataRecord>())
            .DistinctUntilChanged()
            .Select(data => host.Bind(data, nameof(CatalogView), x => x.ToDataGrid()));
    }

    [HubFact]
    public async Task TestCatalogView()
    {
        var reference = new LayoutAreaReference(nameof(CatalogView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area).FirstAsync();
        var grid = content
            .Should()
            .BeOfType<DataGridControl>()
            .Which;

        grid.DataContext.Should().Be(LayoutAreaReference.GetDataPointer(nameof(CatalogView)));
        grid.Columns.Should()
            .HaveCount(2)
            .And.BeEquivalentTo(
                [
                    new DataGridColumn<string>
                    {
                        Property = nameof(DataRecord.SystemName).ToCamelCase(),
                        Title = nameof(DataRecord.SystemName).Wordify()
                    },
                    new DataGridColumn<string>
                    {
                        Property = nameof(DataRecord.DisplayName).ToCamelCase(),
                        Title = nameof(DataRecord.DisplayName).Wordify()
                    }
                ]
            );

    }

    private static bool isDisposed;

    private static object DisposalView =>
        Controls.Stack().WithView("subview", (a, c) =>
    {
        a.AddDisposable(c.Area, new AnonymousDisposable(() => isDisposed = true));
        return "Ok";
    });

    [HubFact]
    public async Task TestDisposalView()
    {
        var reference = new LayoutAreaReference(nameof(DisposalView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area).FirstAsync(x => x != null);
        var subAreaName = content.Should().BeOfType<LayoutStackControl>().Which.Areas.Should().HaveCount(1).And.Subject.First();
        var subArea = await stream.GetControlStream(subAreaName).FirstAsync();
        subArea.Should().BeOfType<HtmlControl>();
        isDisposed.Should().BeFalse();

        // todo
        // garbage-collect view, check isDisposed=true
        // how do I get LayoutAreaHost?
    }

    // todo write data-disposal test
}

public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}

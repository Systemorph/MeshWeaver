using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Hub.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Xunit.Abstractions;


namespace MeshWeaver.Layout.Test;

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
                        Controls.Stack.WithView("Hello", "Hello").WithView("World", "World")
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
                    .WithView(nameof(AsyncView), AsyncView)
            );
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

        var control = await stream.GetControlStream(reference.Area)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        var areas = control
            .Should()
            .BeOfType<LayoutStackControl>()
            .Which.Areas.Should()
            .HaveCount(2)
            .And.Subject;

        var areaControls = await areas
            .ToAsyncEnumerable()
            .SelectAwait(async a => 
                await stream.GetControlStream(a.Area.ToString())
                .Timeout(3.Seconds())
                .FirstAsync(x => x != null))
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
            area.UpdateProgress(
                new(nameof(ViewWithProgress)),
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
        controls.Should().HaveCountGreaterThan(1);// .And.HaveCountLessThan(12);
    }

    private record Toolbar(int Year);

    private static object UpdatingView()
    {
        var toolbar = new Toolbar(2024);

        return Controls
            .Stack
            .WithView((_, _) =>
                Template.Bind(toolbar, nameof(toolbar), tb => Controls.TextBox(tb.Year)), "Toolbar")
            .WithView((area, _) =>
                area.GetDataStream<Toolbar>(nameof(toolbar))
                    .Select(tb => Controls.Html($"Report for year {tb.Year}")), "Content");
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
        var content = await stream.GetControlStream(reportArea)
            .Timeout(3.Seconds())
            .FirstAsync(x => x is not null);
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
            .FirstAsync(x => x != null);
        year!.Value.GetInt32().Should().Be(2024);

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
            .Timeout(3.Seconds())
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
        Template.Bind(
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
        var content = await stream.GetControlStream(controlArea)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
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

    private object Counter()
    {
        var counter = 0;
        return Controls
            .Stack
            .WithView(Controls
                .Menu("Increase Counter")
                .WithClickAction(context =>
                    context.Host.UpdateArea(
                        new($"{nameof(Counter)}/{nameof(Counter)}"),
                        Controls.Html((++counter))
                    )
                ), "Button")
            .WithView(Controls.Html(counter.ToString()), nameof(Counter));
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
        var content = await stream.GetControlStream(buttonArea)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
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
            //.Timeout(TimeSpan.FromSeconds(5))
            ;
        content.Should().BeOfType<HtmlControl>().Which.Data.Should().Be("1");
    }

    private object DataGrid(LayoutAreaHost area, RenderingContext ctx)
    {
        var data = new DataRecord[] { new("1", "1"), new("2", "2") };
        return area.ToDataGrid(data,grid =>
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
            .FirstAsync(x => x != null);
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
        var data = new FilterEntity([
            new LabelAndBool("Label1", true),
            new LabelAndBool("Label2", true),
            new LabelAndBool("Label2", true)
        ]);

        return Controls.Stack
            .WithView(Template.Bind(data, nameof(DataBoundCheckboxes), x => Template.ItemTemplate(x.Data,y => Controls.CheckBox(y.Label, y.Value))), Filter)
            .WithView((a, ctx) => a.GetDataStream<FilterEntity>(nameof(DataBoundCheckboxes))
                .Select(d => d.Data.All(y => y.Value)
                ), Results) ;
    }

    private const string Filter = nameof(Filter);
    private const string Results = nameof(Results);

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
        var controlArea = $"{reference.Area}/{Filter}";
        var tmp = await stream.Select(
            s =>
            {
                var pointer = JsonPointer.Parse(LayoutAreaReference.GetControlPointer(controlArea));
                var result = pointer.Evaluate(s.Value);
                return result?.Deserialize<object>(hub.JsonSerializerOptions);
            })
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        var content = await stream
            .GetControlStream(controlArea)
            .Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync(x => x != null);
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        var enumReference = itemTemplate.Data.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be($"/data").And.Subject;
        itemTemplate.DataContext.Should().Be($"/data/\"{nameof(DataBoundCheckboxes)}\"");
        var enumerableReference = new JsonPointerReference($"{itemTemplate.DataContext}{enumReference}");
        var filter = await stream.GetDataStream<IReadOnlyCollection<LabelAndBool>>(enumerableReference).FirstAsync();

        filter.Should().HaveCount(3);
        var pointer = itemTemplate.Data.Should().BeOfType<JsonPointerReference>().Subject;
        pointer.Pointer.Should().Be("/data");
        var first = filter.First();
        first.Value.Should().BeTrue();

        var resultsArea = $"{reference.Area}/{Results}";
        var resultsControl = await stream
            .GetControlStream(resultsArea)
            .Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync(x => x != null);
        var resultItemTemplate = resultsControl.Should().BeOfType<HtmlControl>().Which;
        resultItemTemplate.DataContext.Should().BeEmpty();

        resultItemTemplate
            .Data.Should().BeOfType<string>()
            .Which.Should().Contain("<pre>True</pre>");

        var firstValuePointer = enumerableReference.Pointer + "/0/value";

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

        var hasControl = await stream
            .GetControlStream(resultsArea)
            .Select(x =>((string)((HtmlControl)x).Data).Contains("<pre>False</pre>"))
            .Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync(x => !x);
        hasControl.Should().BeTrue();
    }
    [HubFact]
    public void TestSerialization()
    {
        var data = new FilterEntity([
            new LabelAndBool("Label1", true),
            new LabelAndBool("Label2", true),
            new LabelAndBool("Label2", true)
        ]);
        var host = GetHost();
        var serialized = JsonSerializer.Serialize(data, host.JsonSerializerOptions);
        var client = GetClient();
        var deserialized = JsonSerializer.Deserialize<FilterEntity>(serialized, client.JsonSerializerOptions);
        deserialized.Should().BeEquivalentTo(data);
    }

    private IObservable<object> CatalogView(LayoutAreaHost area, RenderingContext context)
    {
        return area
            .Hub.GetWorkspace()
            .Stream.Select(x => x.Value.GetData<DataRecord>())
            .DistinctUntilChanged()
            .Select(data => 
                Template.Bind(data, nameof(CatalogView), x => area.ToDataGrid(x)));
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
        var content = await stream.GetControlStream(reference.Area)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
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

    // TODO V10: Need to rewrite realistic test for disposing views. (29.07.2024, Roland Bürgi)


    private static object AsyncView =>
        Controls.Stack
            .WithView(Observable.Return<ViewDefinition>(async (area, context, ct) =>
            {
                await Task.Delay(3000, ct);
                return "Ok";
            }), "subarea");

    
    [HubFact]
    public async Task TestAsyncView()
    {
        var reference = new LayoutAreaReference(nameof(AsyncView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        var stopwatch = Stopwatch.StartNew();

        var content = await stream.GetControlStream(reference.Area)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);

        var subAreaName = content.Should().BeOfType<LayoutStackControl>().Which.Areas.Should().HaveCount(1).And.Subject.First();
        var subArea = await stream.GetControlStream(subAreaName.Area.ToString()).FirstAsync();

        stopwatch.Stop();

        subArea.Should().BeOfType<SpinnerControl>();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(3000);
    }
}

public static class TestAreas
{
    public const string Main = nameof(Main);
    public const string ModelStack = nameof(ModelStack);
    public const string NewArea = nameof(NewArea);
}
public record FilterEntity(List<LabelAndBool> Data);
public record LabelAndBool(string Label, bool Value);

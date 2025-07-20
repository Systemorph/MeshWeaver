using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Xunit.Abstractions;
using System.Linq;
using System.Threading;


namespace MeshWeaver.Layout.Test;

public class LayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string StaticView = nameof(StaticView);


    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress<ClientAddress>((_, d) => d.Package())
            )
            .AddData(data =>
                data.AddSource(
                    ds =>
                        ds.WithType<DataRecord>(t =>
                            t.WithInitialData(DataRecord.InitialData)
                        )
                )
            )
            .AddLayout(layout =>
                layout
                    .WithView(
                        StaticView,
                        Controls.Stack.WithView(Controls.Html("Hello"), "Hello").WithView(Controls.Html("World"), "World")
                    )
                    .WithView(nameof(ViewWithProgress), ViewWithProgress)
                    .WithView(nameof(UpdatingView), UpdatingView())
                    .WithView(
                        nameof(ItemTemplate),
                            layout
                                .Hub.GetWorkspace()
                                .GetStream(typeof(DataRecord)).Select(x => x.Value!.GetData<DataRecord>())
                                .DistinctUntilChanged()
                                .BindMany(nameof(ItemTemplate), y =>
                                    Controls.Text(y.DisplayName).WithId(y.SystemName))
                                )

                    .WithView(nameof(CatalogView), CatalogView)
                    .WithView(
                        nameof(Counter),
                        Counter()
                    )
                    .WithView("int", Controls.Html(3))
                    .WithView(nameof(DataGrid), DataGrid)
                    .WithView(nameof(DataBoundCheckboxes), DataBoundCheckboxes)
                    .WithView(nameof(AsyncView), AsyncView)
            );
    }


    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => base.ConfigureClient(configuration)
        .AddLayoutClient(d => d);

    [HubFact]
    public async Task BasicArea()
    {
        var reference = new LayoutAreaReference(StaticView);

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        var control = await stream.GetControlStream(reference.Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var areas = control
            .Should()
            .BeOfType<StackControl>()
            .Which.Areas.Should()
            .HaveCount(2)
            .And.Subject;

        var areaControls = await areas
            .ToAsyncEnumerable()
            .SelectAwait(async a =>
                await stream.GetControlStream(a.Area.ToString()!)
                .Timeout(10.Seconds())
                .FirstAsync(x => x != null)!)
            .ToArrayAsync();

        areaControls.Should().HaveCount(2).And.AllBeOfType<HtmlControl>();
    }

    private static async Task<UiControl> ViewWithProgress(LayoutAreaHost area, RenderingContext ctx, CancellationToken ct)
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
            .GetControlStream(reference.Area.ToString()!)
            .TakeUntil(o => o is HtmlControl)
            .Timeout(10.Seconds())
            .ToArray();
        controls.Should().HaveCountGreaterThan(1);// .And.HaveCountLessThan(12);
    }

    private record Toolbar(int Year);

    private static UiControl UpdatingView()
    {
        var toolbar = new Toolbar(2024);

        return Controls
            .Stack
            .WithView(Template.Bind(toolbar, tb => Controls.Text(tb.Year), nameof(toolbar)), "Toolbar")
            .WithView((area, _) =>
                area.GetDataStream<Toolbar>(nameof(toolbar))
                    .Select(tb => Controls.Html($"Report for year {tb?.Year}")), "Content");
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
            // .Timeout(10.Seconds())
            .FirstAsync(x => x is not null)!;
        content.Should().BeOfType<HtmlControl>().Which.Data.ToString().Should().Contain("2024");

        // Get toolbar and change value.
        var toolbarArea = $"{reference.Area}/Toolbar";
        var yearTextBox = (TextFieldControl)await stream
            .GetControlStream(toolbarArea)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null)!;
        yearTextBox.DataContext.Should().Be("/data/\"toolbar\"");

        var dataPointer = yearTextBox.Data.Should().BeOfType<JsonPointerReference>().Which;
        dataPointer.Pointer.Should().Be("year");
        var pointer = JsonPointer.Parse($"/{dataPointer.Pointer}");
        var year = await stream
            .GetDataStream<JsonElement>(new JsonPointerReference(yearTextBox.DataContext!))
            .Select(s => pointer.Evaluate(s))
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null)!;
        year!.Value.GetInt32().Should().Be(2024);

        stream.Update(ci =>
        {
            var patch = new JsonPatch(
                PatchOperation.Replace(
                    JsonPointer.Parse($"{yearTextBox.DataContext}/{dataPointer.Pointer}"),
                    2025
                )
            );
            var updated = patch.Apply(ci);
            return stream.ToChangeItem(ci, updated, patch, stream.StreamId);
        }, null!);

        var updatedControls = await stream
            .GetControlStream(reportArea)
            .TakeUntil(o => o is HtmlControl html && !html.Data.ToString()!.Contains("2024"))
            .Timeout(10.Seconds())
            .ToArray();
        updatedControls
            .Last()
            .Should()
            .BeOfType<HtmlControl>()
            .Which.Data.ToString()
            .Should()
            .Contain("2025");
    }

    private object ItemTemplate(IReadOnlyCollection<DataRecord> data) =>
        data.BindMany(record => Controls.Text(record.DisplayName).WithId(record.SystemName)
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
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        itemTemplate.DataContext.Should().Be($"/data/\"{nameof(ItemTemplate)}\"");
        var data = await stream
            .GetDataStream<IEnumerable<JsonElement>>(
                new JsonPointerReference(itemTemplate.DataContext)
            )
            .Where(x => x is not null)
            .FirstAsync();


        var view = itemTemplate.View;
        var pointer = view.Should()
            .BeOfType<TextFieldControl>()
            .Which.Data.Should()
            .BeOfType<JsonPointerReference>()
            .Subject;
        pointer.Pointer.Should().Be("displayName");
        var parsedPointer = JsonPointer.Parse($"/{pointer.Pointer}");
        data.Select(d => parsedPointer.Evaluate(d)!.Value.ToString())
            .Should()
            .BeEquivalentTo("Hello", "World");
    }

    private UiControl Counter()
    {
        var counter = 0;
        return Controls
            .Stack
            .WithView(Controls
                .Html("Increase Counter")
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
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        content
            .Should()
            .BeOfType<HtmlControl>()
            .Which.Data.ToString()
            .Should()
            .Contain("Count");
        hub.Post(new ClickedEvent(buttonArea, stream.StreamId), o => o.WithTarget(new HostAddress()));
        var counterArea = $"{reference.Area}/Counter";
        content = await stream
            .GetControlStream(counterArea)
            .FirstAsync(x => x is HtmlControl { Data: not "0" })
            .Timeout(TimeSpan.FromSeconds(3))
            ;
        content.Should().BeOfType<HtmlControl>().Which.Data.Should().Be(1);
    }

    private UiControl DataGrid(LayoutAreaHost area, RenderingContext ctx)
    {
        var data = new DataRecord[] { new("1", "1"), new("2", "2") };
        return area.ToDataGrid(data, grid => grid
            .WithColumn(x => x.SystemName)
            .WithColumn(x => x.DisplayName)
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
            .GetControlStream(reference.Area.ToString()!)
            //.Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync(x => x != null);

        var controls = content
            .Should()
            .BeOfType<DataGridControl>()
            .Which.Columns.Should()
            .HaveCount(2)
            .And.Subject.ToArray();
        ;


        controls.Should().BeEquivalentTo(
                [
                    new PropertyColumnControl<string>
                    {
                        Property = nameof(DataRecord.SystemName).ToCamelCase(),
                        Title = nameof(DataRecord.SystemName).Wordify(),
                    },
                    new PropertyColumnControl<string>
                    {
                        Property = nameof(DataRecord.DisplayName).ToCamelCase(),
                        Title = nameof(DataRecord.DisplayName).Wordify(),
                    }
                ],
                options => options.Including(c => c.Property).Including(c => c.Title)
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
            .WithView(Template.Bind(data, x => Template.BindMany(x.Data, y => Controls.CheckBox(y.Value)), nameof(DataBoundCheckboxes)), Filter)
            .WithView((a, ctx) => a.GetDataStream<FilterEntity>(nameof(DataBoundCheckboxes))
                .Select(d => Controls.CheckBox(d!.Data.All(y => y.Value))
                ), Results);
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
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var content = await stream
            .GetControlStream(controlArea)
            .Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync(x => x != null);
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        var enumReference = itemTemplate.Data.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be($"data").And.Subject;
        itemTemplate.DataContext.Should().Be($"/data/\"{nameof(DataBoundCheckboxes)}\"");
        var enumerableReference = new JsonPointerReference($"{itemTemplate.DataContext}/{enumReference}");
        var filter = await stream.GetDataStream<IReadOnlyCollection<LabelAndBool>>(enumerableReference).Timeout(3.Seconds()).FirstAsync();

        filter.Should().HaveCount(3);
        var pointer = itemTemplate.Data.Should().BeOfType<JsonPointerReference>().Subject;
        pointer.Pointer.Should().Be("data");
        var first = filter.First();
        first.Value.Should().BeTrue();

        var resultsArea = $"{reference.Area}/{Results}";
        var resultsControl = await stream
            .GetControlStream(resultsArea)
            .Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync(x => x != null);
        var resultItemTemplate = resultsControl.Should().BeOfType<CheckBoxControl>().Which;
        resultItemTemplate.DataContext.Should().BeNull();

        resultItemTemplate
            .Data.Should().BeOfType<bool>()
            .Which.Should().BeTrue();

        var firstValuePointer = enumerableReference.Pointer + "/0/value";

        stream.Update(ci =>
        {
            var patch = new JsonPatch(
                PatchOperation.Replace(JsonPointer.Parse(firstValuePointer), false)
            );
            var updated = patch.Apply(ci);
            return stream.ToChangeItem(ci, updated, patch, stream.StreamId);
        }, null!);

        resultsControl = await stream
            .GetControlStream(resultsArea)
            .Where(x => x is CheckBoxControl cb && !((bool)cb.Data))
            //.Timeout(TimeSpan.FromSeconds(3))
            .FirstAsync(x => true);

        ((bool)((CheckBoxControl)resultsControl).Data).Should().Be(false);
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

    private IObservable<UiControl> CatalogView(LayoutAreaHost area, RenderingContext context)
    {
        return area
            .Hub.GetWorkspace()
            .GetStream(typeof(DataRecord)).Select(x => x.Value!.GetData<DataRecord>())
            .DistinctUntilChanged()
            .Select(data =>
                Template.Bind(data, x => area.ToDataGrid(x), nameof(CatalogView)));
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
        var content = await stream.GetControlStream(reference.Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var grid = content
            .Should()
            .BeOfType<DataGridControl>()
            .Which;

        grid.DataContext.Should().Be(LayoutAreaReference.GetDataPointer(nameof(CatalogView)));
        var benchmarks = new[]
        {
            new PropertyColumnControl<string>
            {
                Property = nameof(DataRecord.SystemName).ToCamelCase(),
                Title = nameof(DataRecord.SystemName).Wordify()
            },
            new PropertyColumnControl<string>
            {
                Property = nameof(DataRecord.DisplayName).ToCamelCase(),
                Title = nameof(DataRecord.DisplayName).Wordify()
            }
        };
        grid.Columns.Should()
            .HaveCount(benchmarks.Length);

        grid.Columns.Should().BeEquivalentTo(benchmarks, options => options.Including(c => c.Property).Including(c => c.Title));
    }

    // TODO V10: Need to rewrite realistic test for disposing views. (29.07.2024, Roland Bürgi)


    private static UiControl AsyncView =>
        Controls.Stack
            .WithView(Observable.Return<ViewDefinition>(async (area, context, ct) =>
            {
                await Task.Delay(3000, ct);
                return Controls.Html("Ok");
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

        var content = await stream.GetControlStream(reference.Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        var subAreaName = content.Should().BeOfType<StackControl>().Which.Areas.Should().HaveCount(1).And.Subject.First();
        var subArea = await stream.GetControlStream(subAreaName.Area.ToString()!).FirstAsync();

        stopwatch.Stop();

        subArea.Should().BeNull();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(3000);
    }

    [HubFact]
    public void TestSerializationOptionsComparison()
    {
        var client = GetClient();
        var hosted = client.GetHostedHub(new SynchronizationAddress());

        Output.WriteLine("=== CLIENT HUB CONVERTERS ===");
        for (int i = 0; i < client.JsonSerializerOptions.Converters.Count; i++)
        {
            var converter = client.JsonSerializerOptions.Converters[i];
            Output.WriteLine($"[{i}] {converter.GetType().FullName}");
        }

        Output.WriteLine("\n=== HOSTED HUB CONVERTERS ===");
        for (int i = 0; i < hosted.JsonSerializerOptions.Converters.Count; i++)
        {
            var converter = hosted.JsonSerializerOptions.Converters[i];
            Output.WriteLine($"[{i}] {converter.GetType().FullName}");
        }

        // Find the missing converters
        var clientConverterTypes = client.JsonSerializerOptions.Converters.Select(c => c.GetType()).ToHashSet();
        var hostedConverterTypes = hosted.JsonSerializerOptions.Converters.Select(c => c.GetType()).ToHashSet();

        var missingInHosted = clientConverterTypes.Except(hostedConverterTypes).ToList();
        var extraInHosted = hostedConverterTypes.Except(clientConverterTypes).ToList();

        Output.WriteLine("\n=== MISSING IN HOSTED ===");
        foreach (var missing in missingInHosted)
        {
            Output.WriteLine($"MISSING: {missing.FullName}");
        }

        Output.WriteLine("\n=== EXTRA IN HOSTED ===");
        foreach (var extra in extraInHosted)
        {
            Output.WriteLine($"EXTRA: {extra.FullName}");
        }

        // Test simple serialization
        var testObject = new PropertyColumnControl<string>
        {
            Property = "test",
            Title = "Test"
        };

        var clientSerialized = JsonSerializer.Serialize(testObject, client.JsonSerializerOptions);
        Output.WriteLine($"\nClient serialized: {clientSerialized}");

        var hostedSerialized = JsonSerializer.Serialize(testObject, hosted.JsonSerializerOptions);
        Output.WriteLine($"Hosted serialized: {hostedSerialized}");

        try
        {
            var clientDeserialized = JsonSerializer.Deserialize<PropertyColumnControl>(clientSerialized, client.JsonSerializerOptions);
            Output.WriteLine($"Client deserialized type: {clientDeserialized?.GetType().FullName}");
        }
        catch (Exception ex)
        {
            Output.WriteLine($"Client deserialization failed: {ex.Message}");
        }

        try
        {
            var hostedDeserialized = JsonSerializer.Deserialize<PropertyColumnControl>(hostedSerialized, hosted.JsonSerializerOptions);
            Output.WriteLine($"Hosted deserialized type: {hostedDeserialized?.GetType().FullName}");
        }
        catch (Exception ex)
        {
            Output.WriteLine($"Hosted deserialization failed: {ex.Message}");
        }
    }

    [HubFact]
    public void TestPolymorphicCollectionSerialization()
    {
        var client = GetClient();
        var hosted = client.GetHostedHub(new SynchronizationAddress());

        // Test 1: Simple individual object
        var singleColumn = new PropertyColumnControl<string>
        {
            Property = "test",
            Title = "Test"
        };

        var singleSerialized = JsonSerializer.Serialize(singleColumn, client.JsonSerializerOptions);
        Output.WriteLine($"Single object serialized: {singleSerialized}");

        var singleClientDeserialized = JsonSerializer.Deserialize<PropertyColumnControl>(singleSerialized, client.JsonSerializerOptions);
        var singleHostedDeserialized = JsonSerializer.Deserialize<PropertyColumnControl>(singleSerialized, hosted.JsonSerializerOptions);

        Output.WriteLine($"Single - Client deserialized type: {singleClientDeserialized?.GetType().FullName}");
        Output.WriteLine($"Single - Hosted deserialized type: {singleHostedDeserialized?.GetType().FullName}");

        // Test 2: Collection of polymorphic objects
        var columnCollection = new List<object>
        {
            new PropertyColumnControl<string> { Property = "test1", Title = "Test1" },
            new PropertyColumnControl<string> { Property = "test2", Title = "Test2" }
        };

        var collectionSerialized = JsonSerializer.Serialize(columnCollection, client.JsonSerializerOptions);
        Output.WriteLine($"\nCollection serialized: {collectionSerialized}");

        var collectionClientDeserialized = JsonSerializer.Deserialize<List<object>>(collectionSerialized, client.JsonSerializerOptions);
        var collectionHostedDeserialized = JsonSerializer.Deserialize<List<object>>(collectionSerialized, hosted.JsonSerializerOptions);

        Output.WriteLine($"\nCollection - Client deserialized count: {collectionClientDeserialized?.Count}");
        Output.WriteLine($"Collection - Hosted deserialized count: {collectionHostedDeserialized?.Count}");

        if (collectionClientDeserialized != null)
        {
            for (int i = 0; i < collectionClientDeserialized.Count; i++)
            {
                Output.WriteLine($"Collection - Client item {i} type: {collectionClientDeserialized[i].GetType().FullName}");
            }
        }

        if (collectionHostedDeserialized != null)
        {
            for (int i = 0; i < collectionHostedDeserialized.Count; i++)
            {
                Output.WriteLine($"Collection - Hosted item {i} type: {collectionHostedDeserialized[i].GetType().FullName}");
            }
        }
    }

    [HubFact]
    public void TestDataGridSerialization()
    {
        var host = GetHost();

        // Create a DataGridControl with PropertyColumnControl<string> columns
        var originalGrid = new DataGridControl(new DataRecord[] { new("1", "1"), new("2", "2") })
            .WithColumn(
                new PropertyColumnControl<string>
                {
                    Property = nameof(DataRecord.SystemName).ToCamelCase(),
                    Title = nameof(DataRecord.SystemName).Wordify()
                },
                new PropertyColumnControl<string>
                {
                    Property = nameof(DataRecord.DisplayName).ToCamelCase(),
                    Title = nameof(DataRecord.DisplayName).Wordify()
                }
            ); Output.WriteLine($"Original grid columns count: {originalGrid.Columns.Count}");
        foreach (var column in originalGrid.Columns)
        {
            Output.WriteLine($"Original column type: {column.GetType().FullName}");
            if (column is PropertyColumnControl pc)
            {
                Output.WriteLine($"  Property: {pc.Property}, Title: {pc.Title}");
            }
        }

        // Serialize with host options
        var serialized = JsonSerializer.Serialize(originalGrid, host.JsonSerializerOptions);
        Output.WriteLine($"Serialized JSON: {serialized}");

        var client = GetClient();
        var hosted = client.GetHostedHub(new SynchronizationAddress());

        Output.WriteLine($"Client JsonSerializerOptions converters: {client.JsonSerializerOptions.Converters.Count}");
        foreach (var converter in client.JsonSerializerOptions.Converters)
        {
            Output.WriteLine($"  Client converter: {converter.GetType().FullName}");
        }

        Output.WriteLine($"Hosted JsonSerializerOptions converters: {hosted.JsonSerializerOptions.Converters.Count}");
        foreach (var converter in hosted.JsonSerializerOptions.Converters)
        {
            Output.WriteLine($"  Hosted converter: {converter.GetType().FullName}");
        }

        // Deserialize with hosted hub options - this should work the same as client options
        Output.WriteLine($"\nTesting deserialization...");
        Output.WriteLine($"Serialized data: {serialized.Length} characters");
        Output.WriteLine($"First 200 chars: {serialized.Substring(0, Math.Min(200, serialized.Length))}");

        DataGridControl? deserialized = null;
        try
        {
            deserialized = JsonSerializer.Deserialize<DataGridControl>(serialized, hosted.JsonSerializerOptions);
            Output.WriteLine($"Deserialization successful");

            // Debug specific column types
            for (int i = 0; i < deserialized!.Columns.Count; i++)
            {
                var column = deserialized.Columns[i];
                Output.WriteLine($"Column {i}: Type = {column.GetType().FullName}");
                if (column is PropertyColumnControl pc)
                {
                    Output.WriteLine($"  PropertyColumnControl - Property: {pc.Property}, Title: {pc.Title}");
                }
                else if (column is JsonElement je)
                {
                    Output.WriteLine($"  JsonElement: {je.GetRawText()}");
                }
                else
                {
                    Output.WriteLine($"  Other type: {column}");
                }
            }
        }
        catch (Exception ex)
        {
            Output.WriteLine($"Deserialization failed: {ex.Message}");
            throw;
        }

        Output.WriteLine($"Deserialized grid columns count: {deserialized!.Columns.Count}");
        foreach (var column in deserialized.Columns)
        {
            Output.WriteLine($"Deserialized column type: {column.GetType().FullName}");
            if (column is PropertyColumnControl pc)
            {
                Output.WriteLine($"  Property: {pc.Property}, Title: {pc.Title}");
            }
            else if (column is JsonElement je)
            {
                Output.WriteLine($"  JsonElement: {je.GetRawText()}");
            }
        }

        // The test should pass if PropertyColumnControl objects are properly deserialized
        deserialized.Columns.Should().HaveCount(2);
        deserialized.Columns.Should().AllBeAssignableTo<PropertyColumnControl>();

        var firstColumn = deserialized.Columns[0].Should().BeAssignableTo<PropertyColumnControl>().Which;
        firstColumn.Property.Should().Be(nameof(DataRecord.SystemName).ToCamelCase());
        firstColumn.Title.Should().Be(nameof(DataRecord.SystemName).Wordify());

        var secondColumn = deserialized.Columns[1].Should().BeAssignableTo<PropertyColumnControl>().Which;
        secondColumn.Property.Should().Be(nameof(DataRecord.DisplayName).ToCamelCase());
        secondColumn.Title.Should().Be(nameof(DataRecord.DisplayName).Wordify());
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

public record DataRecord([property: Key] string SystemName, string DisplayName)
{
    public static readonly DataRecord[] InitialData = [new("Hello", "Hello"), new("World", "World")];
}

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;


namespace MeshWeaver.Layout.Test;

public class LayoutTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string StaticView = nameof(StaticView);
    private const string RecursiveView = nameof(RecursiveView);

    // A view whose generator throws SYNCHRONOUSLY when invoked — models the
    // LinkedInTelemetryImport compiled view's Invoke throwing during render (the init
    // observable then errors and routes to WithExceptionCallback).
    private static IObservable<UiControl?> ThrowingSyncView(LayoutAreaHost host, RenderingContext ctx)
        => throw new InvalidOperationException("BOOM_sync_render");

    // A view whose generator returns an observable that ERRORS — models the post-init
    // render subscription faulting.
    private static IObservable<UiControl?> ThrowingObservableView(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Throw<UiControl?>(new InvalidOperationException("BOOM_async_render"));

    /// <summary>
    /// A pathologically deep, self-similar control tree (a single-child stack
    /// nested far beyond <c>LayoutAreaHost.MaxRenderDepth</c>) ending in a leaf
    /// marker. Models the rbuergi/CatBond crash shape: a layout that recurses
    /// through RenderArea → Render → RenderArea. With the depth guard the render
    /// stops with a visible error before the leaf; without it the server would
    /// fail-fast on a stack overflow.
    /// </summary>
    private static UiControl BuildDeeplyNestedStack()
    {
        UiControl current = Controls.Markdown("DEEP_LEAF_MARKER");
        for (var i = 0; i < 120; i++)
            current = Controls.Stack.WithView(current, "child");
        return current;
    }


    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress(ClientType, (_, d) => d.Package())
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
                    .WithView(nameof(StartWithLoadingView), StartWithLoadingView)
                    .WithView(nameof(StartWithDelayedView), StartWithDelayedView)
                    .WithView(nameof(StartWithSubjectView), StartWithSubjectView)
                    .WithView(nameof(GatedProgressView), GatedProgressView)
                    .WithView(RecursiveView, BuildDeeplyNestedStack())
                    .WithView(nameof(ThrowingSyncView), ThrowingSyncView)
                    .WithView(nameof(ThrowingObservableView), ThrowingObservableView)
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
            CreateHostAddress(),
            reference
        );

        var control = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);
        var areas = control
            .Should()
            .BeOfType<StackControl>()
            .Which.Areas.Should()
            .HaveCount(2)
            .And.Subject;

        var areaControls = await Task.WhenAll(areas!
            .Select(a => stream.GetControlStream(a.Area.ToString()!).Should().Within(10.Seconds()).Match(x => x != null)));

        areaControls.Should().HaveCount(2).And.AllBeOfType<HtmlControl>();
    }

    /// <summary>
    /// Regression guard: a self-similar / recursive layout must NOT crash the
    /// server. Before the <c>LayoutAreaHost</c> render-depth guard, rendering a
    /// control tree that recurses through RenderArea (a container embedding
    /// itself, or — as in prod — a dynamic NodeType's Overview that references
    /// its own area) blew the stack and fail-fasted the whole process
    /// (exit 0xC0000409), taking every tenant down. The guard converts that into
    /// a visible "Layout recursion detected" error at a safe depth.
    ///
    /// <para>We register a 120-deep single-child stack ending in a leaf marker
    /// and walk the rendered chain on the client. The walk must terminate in the
    /// recursion-error control around the depth limit, and must NEVER reach the
    /// deep leaf below it.</para>
    /// </summary>
    [HubFact]
    public async Task DeeplyNestedLayout_DoesNotCrashServer_SurfacesRecursionError()
    {
        var reference = new LayoutAreaReference(RecursiveView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var area = reference.Area!;
        string? recursionErrorText = null;
        var sawDeepLeaf = false;

        // Walk down the single-child chain. With the guard the chain terminates
        // in a "Layout recursion detected" MarkdownControl at ~MaxRenderDepth;
        // the DEEP_LEAF_MARKER beneath it must never render.
        for (var depth = 0; depth < 130; depth++)
        {
            var control = await stream.GetControlStream(area)
                .Should().Within(10.Seconds()).Match(x => x != null);

            if (control is MarkdownControl md)
            {
                var text = md.Markdown?.ToString() ?? string.Empty;
                if (text.Contains("DEEP_LEAF_MARKER")) sawDeepLeaf = true;
                if (text.Contains("Layout recursion detected")) recursionErrorText = text;
                break;
            }

            if (control is StackControl { Areas.Count: > 0 } stack)
            {
                area = stack.Areas[0].Area!.ToString()!;
                continue;
            }

            break;
        }

        recursionErrorText.Should().NotBeNull(
            "a deeply self-similar layout must surface a visible 'Layout recursion detected' " +
            "error instead of recursing until the server fail-fasts with a stack overflow");
        sawDeepLeaf.Should().BeFalse(
            "rendering must stop at the depth guard, before reaching the deeply-nested leaf");
    }

    /// <summary>
    /// Regression guard: a view generator that throws must SURFACE a visible error on the
    /// area, never leave a subscriber spinning on an indefinite null. Before the fix,
    /// <c>LayoutAreaHost.FailRendering</c> only <c>LogWarning</c>'d and dropped the
    /// exception, so a throwing view (e.g. the LinkedInTelemetryImport compile-guard's
    /// Invoke faulting on a cold CI agent) left the area with only its empty "Building
    /// layout…" base frame — the client/test then timed out after 45 s with the real cause
    /// buried in a log line. Both the synchronous-throw path (routed via the init
    /// observable's WithExceptionCallback) and the observable-error path (the render
    /// subscription's error handler) must now render an error control carrying the cause.
    /// </summary>
    [HubFact]
    public Task ThrowingSyncView_SurfacesError_InsteadOfIndefiniteNull()
        => AssertThrowingViewSurfacesError(nameof(ThrowingSyncView), "BOOM_sync_render");

    [HubFact]
    public Task ThrowingObservableView_SurfacesError_InsteadOfIndefiniteNull()
        => AssertThrowingViewSurfacesError(nameof(ThrowingObservableView), "BOOM_async_render");

    private async Task AssertThrowingViewSurfacesError(string view, string expectedCause)
    {
        var reference = new LayoutAreaReference(view);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // The area must resolve to a control quickly (the fix surfaces the error
        // immediately) — NOT spin on null until a timeout.
        var control = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl);

        var text = control.Should().BeOfType<MarkdownControl>().Subject.Markdown?.ToString() ?? string.Empty;
        text.Should().Contain("failed to render",
            "a faulted render must surface a visible error control on the area, not stay null");
        text.Should().Contain(expectedCause,
            "the surfaced error must carry the underlying exception message so the cause is visible");
    }

    private static async Task<UiControl?> ViewWithProgress(LayoutAreaHost area, RenderingContext ctx, CancellationToken ct)
    {
        var percentage = 0;
        var progress = Controls.Progress("Processing", percentage);
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(30, ct); // Use the cancellation token
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
            CreateHostAddress(),
            reference
        );
        // Wait for the async view to complete and emit the final HtmlControl
        var finalControl = await stream
            .GetControlStream(reference.Area!)
            .Where(o => o is HtmlControl)
            .Should().Within(30.Seconds()).Emit();
        finalControl.Should().BeOfType<HtmlControl>();
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
            CreateHostAddress(),
            reference
        );
        var reportArea = $"{reference.Area}/Content";
        var content = await stream.GetControlStream(reportArea)
            .Should().Within(10.Seconds()).Match(x => x is not null);
        content.Should().BeOfType<HtmlControl>().Which.Data.ToString().Should().Contain("2024");

        // Get toolbar and change value.
        var toolbarArea = $"{reference.Area}/Toolbar";
        var yearTextBox = (TextFieldControl)(await stream
            .GetControlStream(toolbarArea)
            .Should().Within(10.Seconds()).Match(x => x is not null))!;
        yearTextBox.DataContext.Should().Be("/data/\"toolbar\"");

        var dataPointer = yearTextBox.Data.Should().BeOfType<JsonPointerReference>().Which;
        dataPointer.Pointer.Should().Be("year");
        var pointer = JsonPointer.Parse($"/{dataPointer.Pointer}");
        var year = await stream
            .GetDataStream<JsonElement>(new JsonPointerReference(yearTextBox.DataContext!))
            .Select(s => pointer.Evaluate(s))
            .Should().Within(10.Seconds()).Match(x => x != null);
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
            .ToArray()
            .Should().Within(10.Seconds()).Emit();
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
            CreateHostAddress(),
            reference
        );
        var controlArea = $"{reference.Area}";
        var content = await stream.GetControlStream(controlArea)
            .Should().Within(10.Seconds()).Match(x => x != null);
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        itemTemplate.DataContext.Should().Be($"/data/\"{nameof(ItemTemplate)}\"");
        var data = await stream
            .GetDataStream<IEnumerable<JsonElement>>(
                new JsonPointerReference(itemTemplate.DataContext!)
            )
            .Should().Match(x => x is not null);


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
            .BeEquivalentTo(new[] { "Hello", "World" }, System.Text.Json.JsonSerializerOptions.Default);
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
            CreateHostAddress(),
            reference
        );
        var buttonArea = $"{reference.Area}/Button";
        var content = await stream.GetControlStream(buttonArea)
            .Should().Within(10.Seconds()).Match(x => x != null);
        content
            .Should()
            .BeOfType<HtmlControl>()
            .Which.Data.ToString()
            .Should()
            .Contain("Count");
        var counterArea = $"{reference.Area}/Counter";
        var counterStream = stream
            .GetControlStream(counterArea)
            .Where(x => x is HtmlControl { Data: not "0" });
        hub.Post(new ClickedEvent(buttonArea, stream.StreamId), o => o.WithTarget(CreateHostAddress()));
        content = await counterStream.Should().Within(3.Seconds()).Emit();
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
            CreateHostAddress(),
            reference
        );
        var content = await stream
            .GetControlStream(reference.Area!)
            .Should().Match(x => x != null);

        var controls = content
            .Should()
            .BeOfType<DataGridControl>()
            .Which.Columns.Should()
            .HaveCount(2)
            .And.Subject!.ToArray();
        ;


        // Compare only the Property/Title members of each column (the library's
        // BeEquivalentTo equivalency config filters root-level members, not
        // per-element members of a collection, so project both sides first).
        controls.Cast<PropertyColumnControl>().Select(c => new { c.Property, Title = c.Title?.ToString() })
            .Should().BeEquivalentTo(
                new[]
                {
                    new { Property = (string?)nameof(DataRecord.SystemName).ToCamelCase(), Title = (string?)nameof(DataRecord.SystemName).Wordify() },
                    new { Property = (string?)nameof(DataRecord.DisplayName).ToCamelCase(), Title = (string?)nameof(DataRecord.DisplayName).Wordify() }
                },
                System.Text.Json.JsonSerializerOptions.Default);
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
            CreateHostAddress(),
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
            .Should().Within(10.Seconds()).Match(x => x != null);
        var content = await stream
            .GetControlStream(controlArea)
            .Should().Within(3.Seconds()).Match(x => x != null);
        var itemTemplate = content.Should().BeOfType<ItemTemplateControl>().Which;
        var enumReference = itemTemplate.Data.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be($"data").And.Subject;
        itemTemplate.DataContext.Should().Be($"/data/\"{nameof(DataBoundCheckboxes)}\"");
        var enumerableReference = new JsonPointerReference($"{itemTemplate.DataContext}/{enumReference}");
        // Wait for the populated collection — the data stream's first emission
        // can be the empty Initial snapshot; Emit() without a predicate
        // would grab that and the HaveCount(3) assertion would flake.
        var filter = await stream.GetDataStream<IReadOnlyCollection<LabelAndBool>>(enumerableReference)
            .Where(x => x is { Count: 3 })
            .Should().Within(3.Seconds()).Emit();

        filter.Should().HaveCount(3);
        var pointer = itemTemplate.Data.Should().BeOfType<JsonPointerReference>().Subject;
        pointer.Pointer.Should().Be("data");
        var first = filter.First();
        first.Value.Should().BeTrue();

        var resultsArea = $"{reference.Area}/{Results}";
        var resultsControl = await stream
            .GetControlStream(resultsArea)
            .Should().Within(3.Seconds()).Match(x => x != null);
        var resultItemTemplate = resultsControl.Should().BeOfType<CheckBoxControl>().Which;
        resultItemTemplate.DataContext.Should().BeNull();

        resultItemTemplate
            .Data.Should().BeOfType<bool>()
            .Which.Should().BeTrue();

        var firstValuePointer = enumerableReference.Pointer + "/0/value";

        var toggledResultsStream = stream
            .GetControlStream(resultsArea)
            .Where(x => x is CheckBoxControl cb && !((bool)cb.Data));

        stream.Update(ci =>
        {
            var patch = new JsonPatch(
                PatchOperation.Replace(JsonPointer.Parse(firstValuePointer), false)
            );
            var updated = patch.Apply(ci);
            return stream.ToChangeItem(ci, updated, patch, stream.StreamId);
        }, null!);

        resultsControl = (await toggledResultsStream.Should().Within(3.Seconds()).Emit())!;

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
        deserialized.Should().BeEquivalentTo(data, client.JsonSerializerOptions);
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
            CreateHostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);
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

        // Project to the compared members (Property/Title) — the equivalency config
        // filters root members, not per-element members of a collection.
        grid.Columns.Cast<PropertyColumnControl>().Select(c => new { c.Property, Title = c.Title?.ToString() })
            .Should().BeEquivalentTo(
                benchmarks.Select(c => new { c.Property, Title = (string?)c.Title?.ToString() }),
                System.Text.Json.JsonSerializerOptions.Default);
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
            CreateHostAddress(),
            reference
        );

        var stopwatch = Stopwatch.StartNew();

        var content = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);

        var subAreaName = content.Should().BeOfType<StackControl>().Which.Areas.Should().HaveCount(1).And.Subject!.First();
        // The sub-area renders only after the async view's 3s delay; its first emission within
        // the wait window is the null placeholder (the async content has not arrived yet).
        var subArea = await stream.GetControlStream(subAreaName.Area.ToString()!).Should().Emit();

        stopwatch.Stop();

        subArea.Should().BeNull();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(3000);
    }

    [HubFact]
    public void TestSerializationOptionsComparison()
    {
        var client = GetClient();
        var hosted = client.GetHostedHub(SynchronizationAddress.Create());

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


    // Subject for testing controlled emission
    private static readonly System.Reactive.Subjects.ReplaySubject<UiControl> TestSubject = new(1);

    /// <summary>
    /// Test that demonstrates the issue with IObservable views using StartWith.
    /// When a view returns IObservable&lt;UiControl&gt; with .StartWith(loadingControl),
    /// and the underlying data stream needs to initialize, the view can get stuck
    /// at the loading state and never transition to the actual content.
    /// </summary>
    [HubFact]
    public async Task TestStartWithLoading()
    {
        var reference = new LayoutAreaReference(nameof(StartWithLoadingView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // Get the control - we should eventually see "DataGrid" content, not stay at "Loading"
        var controls = await stream
            .GetControlStream(reference.Area!)
            .TakeUntil(o => o is DataGridControl)
            .ToArray()
            .Should().Within(5.Seconds()).Emit();

        // Should have received at least the loading control and the final data grid
        controls.Should().HaveCountGreaterThanOrEqualTo(1);

        // The last control should be the DataGrid, not the Markdown loading message
        controls.Last().Should().BeOfType<DataGridControl>();
    }

    /// <summary>
    /// Test with delayed data emission to better reproduce the StartWith issue.
    /// This simulates a scenario where data loads after a delay, which can cause
    /// the view to get stuck at the loading state if there's a hashing/timing issue.
    /// </summary>
    [HubFact]
    public async Task TestStartWithDelayedData()
    {
        var reference = new LayoutAreaReference(nameof(StartWithDelayedView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // First, we should get the loading control
        var firstControl = await stream
            .GetControlStream(reference.Area!)
            .Should().Within(2.Seconds()).Match(x => x != null);

        firstControl.Should().BeOfType<MarkdownControl>()
            .Which.Markdown.ToString().Should().Contain("Loading");

        // Then, we should eventually get the actual content after the delay
        var finalControl = await stream
            .GetControlStream(reference.Area!)
            .TakeUntil(o => o is HtmlControl)
            .LastAsync()
            .Should().Within(5.Seconds()).Emit();

        finalControl.Should().BeOfType<HtmlControl>()
            .Which.Data.ToString().Should().Contain("Data Loaded");
    }

    private IObservable<UiControl> StartWithLoadingView(LayoutAreaHost area, RenderingContext context)
    {
        // This pattern is commonly used: return a stream that starts with a loading indicator
        // and then emits the actual content when data is available
        return area
            .Hub.GetWorkspace()
            .GetStream(typeof(DataRecord))
            .Select(x => x.Value!.GetData<DataRecord>())
            .DistinctUntilChanged()
            .Select(data => (UiControl)area.ToDataGrid(data))
            .StartWith(Controls.Markdown("# Loading...\n\n*Please wait while data loads...*"));
    }

    private IObservable<UiControl> StartWithDelayedView(LayoutAreaHost area, RenderingContext context)
    {
        // Simulate delayed data loading - this should still transition from loading to content
        return Observable.Timer(TimeSpan.FromMilliseconds(500))
            .Select(_ => (UiControl)Controls.Html("Data Loaded Successfully"))
            .StartWith(Controls.Markdown("# Loading...\n\n*Please wait while data loads...*"));
    }

    private IObservable<UiControl> StartWithSubjectView(LayoutAreaHost area, RenderingContext context)
    {
        // Use a subject to control exactly when the actual content is emitted
        // This helps isolate timing issues with StartWith
        return TestSubject
            .StartWith(Controls.Markdown("# Loading...\n\n*Waiting for data...*"));
    }

    // Gate for GatedProgressView: the view emits its first control only when the
    // test releases the gate — keeps the pre-first-render progress phases stably
    // observable (deterministic, no sleep). Instance field — dies with the test.
    private readonly System.Reactive.Subjects.ReplaySubject<UiControl> GatedViewSubject = new(1);

    private IObservable<UiControl> GatedProgressView(LayoutAreaHost area, RenderingContext context)
    {
        // View-pushed loading phase — rides the same "data/progress" item the
        // framework milestones write ("Building layout…" → "Rendering… awaiting
        // first data"); the client's loading label binds this item.
        area.UpdateProgress("Crunching the numbers...", 42);
        return GatedViewSubject;
    }

    /// <summary>
    /// Phase-aware progress: while an area is still assembling (its view has not
    /// emitted), the EntityStore "data/progress" item must carry the latest
    /// phase — here the view-pushed <c>host.UpdateProgress</c> message, which is
    /// queued AFTER the framework's "Building layout…" seed and "Rendering…"
    /// milestone on the same serialized update queue. Once content renders, the
    /// message clears. This is the server half of the loading UX the Blazor
    /// <c>LayoutAreaView</c> binds.
    /// </summary>
    [HubFact]
    public async Task ProgressPhases_AdvanceWhileAssembling_AndClearOnRender()
    {
        var reference = new LayoutAreaReference(nameof(GatedProgressView));

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        var progressStream = stream.GetDataStream<JsonElement>(
            new JsonPointerReference(LayoutAreaReference.GetDataPointer(LayoutAreaHost.ProgressDataId)));

        // Pre-render: the progress item holds the view-pushed phase (proves the
        // milestone channel advanced past the static "Building layout…" seed).
        await progressStream
            .Where(p => p.ValueKind == JsonValueKind.Object
                        && p.TryGetProperty("message", out var m)
                        && m.ValueKind == JsonValueKind.String
                        && m.GetString() == "Crunching the numbers...")
            .Should().Within(10.Seconds()).Emit(
                "the view-pushed UpdateProgress phase must reach the client through the data/progress item");

        // Release the gate → the control renders → progress clears.
        GatedViewSubject.OnNext(Controls.Html("Numbers crunched"));

        await stream.GetControlStream(reference.Area!)
            .Where(o => o is HtmlControl)
            .Should().Within(10.Seconds()).Emit();

        await progressStream
            .Where(p => p.ValueKind == JsonValueKind.Object
                        && p.TryGetProperty("message", out var m)
                        && m.ValueKind == JsonValueKind.String
                        && m.GetString() == "")
            .Should().Within(10.Seconds()).Emit(
                "the first render must clear the progress message");
    }

    /// <summary>
    /// Test with controlled subject to isolate timing issues with StartWith.
    /// This test ensures that when data is emitted after initialization,
    /// the view properly transitions from loading to content.
    /// </summary>
    [HubFact]
    public async Task TestStartWithControlledSubject()
    {
        // Reset the subject for this test
        var subject = new System.Reactive.Subjects.ReplaySubject<UiControl>(1);

        var reference = new LayoutAreaReference(nameof(StartWithSubjectView));

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // First control should be the loading message from StartWith
        var firstControl = await stream
            .GetControlStream(reference.Area!)
            .Should().Within(2.Seconds()).Match(x => x != null);

        firstControl.Should().BeOfType<MarkdownControl>()
            .Which.Markdown.ToString().Should().Contain("Loading");

        // Set up the transition watch BEFORE emitting to avoid a race
        var finalControlStream = stream
            .GetControlStream(reference.Area!)
            .TakeUntil(o => o is HtmlControl)
            .LastAsync();

        // Now emit the actual content via the subject
        TestSubject.OnNext(Controls.Html("Subject Data Loaded"));

        // Wait for the content to transition
        var finalControl = await finalControlStream.Should().Within(3.Seconds()).Emit();

        // The final control should be the HTML content, not still the loading message
        finalControl.Should().BeOfType<HtmlControl>()
            .Which.Data.ToString().Should().Contain("Subject Data Loaded");
    }

    [HubFact]
    public void TestPolymorphicCollectionSerialization()
    {
        var client = GetClient();
        var hosted = client.GetHostedHub(SynchronizationAddress.Create());

        // Test 1: Simple individual object
        var singleColumn = new PropertyColumnControl<string>
        {
            Property = "test",
            Title = "Test"
        };

        var singleSerialized = JsonSerializer.Serialize(singleColumn, client.JsonSerializerOptions);
        Output.WriteLine($"Single object serialized: {singleSerialized}");

        var singleClientDeserialized = JsonSerializer.Deserialize<object>(singleSerialized, client.JsonSerializerOptions);
        var singleHostedDeserialized = JsonSerializer.Deserialize<object>(singleSerialized, hosted.JsonSerializerOptions);

        Output.WriteLine($"Single - Client deserialized type: {singleClientDeserialized?.GetType().FullName}");
        Output.WriteLine($"Single - Hosted deserialized type: {singleHostedDeserialized?.GetType().FullName}");

        // Both should deserialize to the concrete PropertyColumnControl<string> type
        singleClientDeserialized.Should().BeOfType<PropertyColumnControl<string>>();
        singleHostedDeserialized.Should().BeOfType<PropertyColumnControl<string>>();

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

        // Both should deserialize collections the same way
        collectionClientDeserialized.Should().NotBeNull();
        collectionHostedDeserialized.Should().NotBeNull();
        collectionClientDeserialized!.Count.Should().Be(collectionHostedDeserialized!.Count);

        // Each item should be properly deserialized as PropertyColumnControl<string>
        collectionClientDeserialized.Should().AllBeOfType<PropertyColumnControl<string>>();
        collectionHostedDeserialized.Should().AllBeOfType<PropertyColumnControl<string>>();
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
        var hosted = client.GetHostedHub(SynchronizationAddress.Create());

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

        // The test should pass if PropertyColumnControl objects are properly deserialized.
        // BeOfType<T> here matches any value assignable to T (PropertyColumnControl<string>
        // derives from PropertyColumnControl), and exposes the narrowed value via .Which.
        deserialized.Columns.Should().HaveCount(2);
        deserialized.Columns.Should().AllBeOfType<PropertyColumnControl>();

        var firstColumn = deserialized.Columns[0].Should().BeOfType<PropertyColumnControl>().Which;
        firstColumn.Property.Should().Be(nameof(DataRecord.SystemName).ToCamelCase());
        firstColumn.Title.Should().Be(nameof(DataRecord.SystemName).Wordify());

        var secondColumn = deserialized.Columns[1].Should().BeOfType<PropertyColumnControl>().Which;
        secondColumn.Property.Should().Be(nameof(DataRecord.DisplayName).ToCamelCase());
        secondColumn.Title.Should().Be(nameof(DataRecord.DisplayName).Wordify());
    }

    [HubFact]
    public void DataViewTruncation_TruncatesLongContent()
    {
        // arrange - create JSON that will serialize to more than 100 lines
        var largeData = Enumerable.Range(1, 50)
            .Select(i => new DataRecord(i.ToString(), $"Item {i}"))
            .ToArray();

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(largeData, options);
        var lines = json.Split('\n');

        // act - verify large data has more than 100 lines
        lines.Length.Should().BeGreaterThan(100);

        // Truncating to first 100 lines
        var truncatedLines = lines.Take(100).ToArray();
        var truncatedJson = string.Join('\n', truncatedLines) + "\n...";

        // assert - truncated content should be smaller
        truncatedJson.Split('\n').Length.Should().Be(101); // 100 lines + "..."
        truncatedJson.Should().EndWith("...");
    }
}

/// <summary>
/// Tests for the DefaultArea functionality - when Area is null/empty,
/// the system should use the configured DefaultArea via WithDefaultArea.
/// </summary>
public class DefaultAreaTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string DefaultView = nameof(DefaultView);
    private const string OtherView = nameof(OtherView);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress(ClientType, (_, d) => d.Package())
            )
            .AddLayout(layout =>
                layout
                    .WithDefaultArea(DefaultView) // Configure the default area
                    .WithView(DefaultView, Controls.Html("This is the default view"))
                    .WithView(OtherView, Controls.Html("This is another view"))
            );
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => base.ConfigureClient(configuration)
        .AddLayoutClient(d => d);

    [HubFact]
    public async Task NullArea_ReturnsNamedAreaControlPointingToDefaultArea()
    {
        // Arrange - create a reference with null area
        var reference = new LayoutAreaReference(null);

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // Act - get the control stream for empty area
        var control = await stream.GetControlStream(string.Empty)
            .Should().Within(10.Seconds()).Match(x => x != null);

        // Assert - should get a NamedAreaControl pointing to the default area
        var namedArea = control.Should().BeOfType<NamedAreaControl>().Which;
        namedArea.Area.Should().Be(DefaultView);
    }

    [HubFact]
    public async Task EmptyArea_ReturnsNamedAreaControlPointingToDefaultArea()
    {
        // Arrange - create a reference with empty area
        var reference = new LayoutAreaReference(string.Empty);

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // Act - get the control stream for empty area
        var control = await stream.GetControlStream(string.Empty)
            .Should().Within(10.Seconds()).Match(x => x != null);

        // Assert - should get a NamedAreaControl pointing to the default area
        var namedArea = control.Should().BeOfType<NamedAreaControl>().Which;
        namedArea.Area.Should().Be(DefaultView);
    }

    [HubFact]
    public async Task ExplicitArea_UsesSpecifiedArea()
    {
        // Arrange - create a reference with explicit area
        var reference = new LayoutAreaReference(OtherView);

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // Act - get the control stream for the specified area
        var control = await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null);

        // Assert - should get the other view content, not the default
        control.Should().BeOfType<HtmlControl>()
            .Which.Data.ToString().Should().Contain("another view");
    }

    [HubFact]
    public async Task DefaultAreaContent_IsRenderedCorrectly()
    {
        // Arrange - create a reference with null area
        var reference = new LayoutAreaReference(null);

        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // Act - get the control stream for the actual default view (not empty string)
        var control = await stream.GetControlStream(DefaultView)
            .Should().Within(10.Seconds()).Match(x => x != null);

        // Assert - should get the default view content
        control.Should().BeOfType<HtmlControl>()
            .Which.Data.ToString().Should().Contain("default view");
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

/// <summary>
/// Tests for CodeEditor-style data binding with JsonPointerReference.
/// Simulates the pattern used by CodeEditorView where DataContext points to a data location
/// and Value is an empty JsonPointerReference to bind to the root of that data.
/// </summary>
public class CodeEditorDataBindingTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string CodeEditorView = nameof(CodeEditorView);
    private const string InitialCode = "// Initial code content";
    private const string UpdatedCode = "// Updated code content";
    private const string CodeDataId = "codeData";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress(ClientType, (_, d) => d.Package())
            )
            .AddLayout(layout =>
                layout
                    .WithView(CodeEditorView, BuildCodeEditorView)
            );
    }

    /// <summary>
    /// Build a view that simulates CodeEditorView's data binding pattern:
    /// - Store code in data section with UpdateData
    /// - Bind to it using DataContext + empty JsonPointerReference
    /// </summary>
    private static UiControl BuildCodeEditorView(LayoutAreaHost host, RenderingContext ctx)
    {
        // Initialize the code data (like NodeTypeView does)
        host.UpdateData(CodeDataId, InitialCode);

        // Create a control that binds to the data using the CodeEditor pattern:
        // - DataContext points to the data location
        // - Data (or Value) is an empty JsonPointerReference to bind to root
        var editor = new TextFieldControl(new JsonPointerReference(""))
        {
            DataContext = LayoutAreaReference.GetDataPointer(CodeDataId)
        };

        return Controls.Stack
            .WithView(editor, "Editor")
            .WithView(
                Controls.Button("Save")
                    .WithClickAction(actx =>
                    {
                        // Read the current value from the stream (this is what Save button does).
                        // Reactive, non-blocking: take the current value and store it.
                        host.Stream.GetDataStream<string>(CodeDataId)
                            .Take(1)
                            .Subscribe(currentValue => host.UpdateData("savedValue", currentValue ?? "null"));
                        return Task.CompletedTask;
                    }),
                "SaveButton"
            );
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) => base.ConfigureClient(configuration)
        .AddLayoutClient(d => d);

    /// <summary>
    /// Test that simulates what CodeEditorView does:
    /// 1. View initializes data with UpdateData
    /// 2. Client updates the data via UpdatePointer (simulating user editing)
    /// 3. Save button reads the data back
    /// 4. Verify the updated value is saved, not the initial value
    /// </summary>
    [HubFact]
    public async Task CodeEditor_UpdatePointer_SavesUpdatedValue()
    {
        var reference = new LayoutAreaReference(CodeEditorView);

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // Wait for the editor control to be rendered
        var editorArea = $"{reference.Area}/Editor";
        var editorControl = await stream.GetControlStream(editorArea)
            .Should().Within(10.Seconds()).Match(x => x != null);

        var textField = editorControl.Should().BeOfType<TextFieldControl>().Which;
        textField.DataContext.Should().Be(LayoutAreaReference.GetDataPointer(CodeDataId));
        var valuePointer = textField.Data.Should().BeOfType<JsonPointerReference>().Which;
        valuePointer.Pointer.Should().Be("");

        // Verify initial value is loaded
        var initialValue = await stream
            .GetDataStream<string>(new JsonPointerReference(textField.DataContext!))
            .Should().Within(5.Seconds()).Emit();
        initialValue.Should().Be(InitialCode);

        // Now simulate what CodeEditorView.OnValueChanged does:
        // Update the data using UpdatePointer with the same pattern
        stream.UpdatePointer(UpdatedCode, textField.DataContext, valuePointer);

        // Verify the value was updated in the stream (the predicate waits for the update to apply)
        var updatedValue = await stream
            .GetDataStream<string>(new JsonPointerReference(textField.DataContext!))
            .Should().Within(5.Seconds()).Match(x => x == UpdatedCode);

        // THIS IS THE KEY ASSERTION - the value should be updated, not still initial
        updatedValue.Should().Be(UpdatedCode,
            "UpdatePointer should have updated the value at the data location");
    }

    /// <summary>
    /// Test the GetPointer helper to verify the path construction.
    /// </summary>
    [HubFact]
    public async Task CodeEditor_EmptyPointer_ResolvesToCorrectPath()
    {
        var reference = new LayoutAreaReference(CodeEditorView);

        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );

        // Wait for the editor control
        var editorArea = $"{reference.Area}/Editor";
        await stream.GetControlStream(editorArea)
            .Should().Within(10.Seconds()).Match(x => x != null);

        // Get the data context pointer
        var dataContext = LayoutAreaReference.GetDataPointer(CodeDataId);
        Output.WriteLine($"DataContext: {dataContext}");

        // Test reading with empty pointer - should resolve to the data itself
        var valuePointer = new JsonPointerReference("");

        // Read using DataBind (the observable way)
        var valueFromBind = await stream
            .DataBind<string>(valuePointer, dataContext)
            .Should().Within(5.Seconds()).Emit();

        valueFromBind.Should().Be(InitialCode,
            "DataBind with empty pointer should read the value at DataContext");

        // Now update using UpdatePointer with empty pointer
        stream.UpdatePointer("New Value", dataContext, valuePointer);

        // Read again (the predicate waits for the update to apply)
        var valueAfterUpdate = await stream
            .DataBind<string>(valuePointer, dataContext)
            .Should().Within(5.Seconds()).Match(x => x == "New Value");

        valueAfterUpdate.Should().Be("New Value",
            "UpdatePointer with empty pointer should update the value at DataContext");
    }
}

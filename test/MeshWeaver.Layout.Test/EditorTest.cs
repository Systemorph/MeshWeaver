using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

[Collection("EditorTests")]
public class EditorTest(ITestOutputHelper output) : HubTestBase(output)
{

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }
    #region Calculator Domain
    public record Calculator
    {
        [Description("This is the X value")]
        public double X { get; init; }
        [Description("This is the Y value")]
        public double Y { get; init; }

    }


    private record MyDimension([property: Key] int Code, string DisplayName) : INamed;
    private UiControl? EditorWithoutResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(), "calc");
    private UiControl? EditorWithResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(), c => Controls.Markdown($"{c.X + c.Y}"));
    private UiControl? EditorWithDelayedResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(), c =>
        {
            Thread.Sleep(100);
            return Controls.Markdown($"{c.X + c.Y} @ {DateTime.UtcNow.Second}:{DateTime.UtcNow.Millisecond}");
        });
    #endregion

    private UiControl? EditorWithListFormProperties
    (LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new ListForms());


    [Fact]
    public void TestEditorWithoutResult()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(nameof(EditorWithoutResult)));
        var control = area
            .GetControlStream(nameof(EditorWithoutResult))
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;
        editor.Areas.Should()
            .HaveCount(2)
            .And.AllSatisfy(e =>
            {
                var skin = e.Skins.OfType<PropertySkin>().Should().ContainSingle().Subject;
                skin.Label.Should().NotBeNull();
                skin.Description.Should().NotBeNull();
            });
        var editorAreas = editor.Areas
            .Select(a => area.GetControlStream(a.Area.ToString()!).Should().Within(5.Seconds()).Match(x => x is not null))
            .ToArray();

        editorAreas.Should().HaveCount(2);
        editorAreas.Should()
            .AllBeOfType<NumberFieldControl>()
            ;

    }
    [Fact]
    public void TestEditorWithResult()
    {
        var client = GetClient();

        var workspace = client.GetWorkspace();
        var area = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(nameof(EditorWithResult)));
        var control = area
            .GetControlStream(nameof(EditorWithResult))
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        control = area
            .GetControlStream(stack.Areas.First().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;
        editor.Areas.Should()
            .HaveCount(2)
            .And.AllSatisfy(e =>
            {
                var skin = e.Skins.OfType<PropertySkin>().Should().ContainSingle().Subject;
                skin.Label.Should().NotBeNull();
                skin.Description.Should().NotBeNull();
            });
        var editorAreas = editor.Areas
            .Select(a => area.GetControlStream(a.Area.ToString()!).Should().Within(5.Seconds()).Match(x => x is not null))
            .ToArray();

        editorAreas.Should().HaveCount(2);
        editorAreas.Should()
            .AllBeOfType<NumberFieldControl>()
            ;

        control = area
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        control.Should().BeOfType<MarkdownControl>().Subject.Markdown.Should().Be("0");

        // update once ==> will issue "add", as 0 was not there
        area.UpdatePointer(1, editor.DataContext!, new("x"));
        control = area
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl { Markdown: not "0" });

        control.Should().BeOfType<MarkdownControl>().Subject.Markdown.Should().Be("1");

        // update once ==> will issue "replace"
        area.UpdatePointer(2, editor.DataContext, new("x"));
        control = area
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x is MarkdownControl { Markdown: not "1" });

        control.Should().BeOfType<MarkdownControl>().Subject.Markdown.Should().Be("2");
    }
    [Fact]
    public void TestEditorWithDelayed()
    {
        var client = GetClient();

        var workspace = client.GetWorkspace();
        var area = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(nameof(EditorWithDelayedResult)));
        var control = area
            .GetControlStream(nameof(EditorWithDelayedResult))
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        control = area
            .GetControlStream(stack.Areas.First().Area.ToString()!)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;


        var controlStream = area
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .TakeUntil(x => x is MarkdownControl { Markdown: var data } && data.ToString()!.StartsWith("5"));


        // update once ==> will issue "replace"
        for (var i = 1; i <= 5; i++)
        {
            area.UpdatePointer(i, editor.DataContext!, new("x"));
        }

        // The original async form `await controlStream.Where(...).ToArray()` had NO explicit
        // .Timeout(), so it was bounded only by the 30s xUnit methodTimeout. Five sequential
        // UpdatePointer round-trips through the remote JsonElement sync stream — each render
        // blocked by Thread.Sleep(100) plus serialization — can exceed 10s on a slow CI runner
        // while staying well under 30s. Clamping this final wait to 10s (the value the other two
        // reads legitimately carried) is what made TestEditorWithDelayed fail in CI but pass
        // locally. Restore the original effective bound by waiting up to the method timeout.
        var controls = controlStream.Where(x => x is not null).ToArray().Should().Within(30.Seconds()).Emit();
        controls.Length.Should().BeLessThanOrEqualTo(3);
        controls.Last().Should().BeOfType<MarkdownControl>().Which.Markdown.ToString().Should().StartWith("5");
    }
    private record ListForms
    {
        [Dimension<MyDimension>()]
        public string Dimension { get; init; } = null!;
        [Dimension<MyDimension>(Options = "stream")]
        public string DimensionWithStream { get; init; } = null!;
        [UiControl<RadioGroupControl>(Options = new[] { "chart", "table" })]
        public string Display { get; init; } = null!;
    }

    private record ListPropertyBenchmark<T>(string Data, Option[]? Options, string? OptionPointer = null);

    private static MyDimension[] Dimensions { get; } = [new(1, "One"), new(2, "Two")];

    private static readonly object[] ListPropertyBenchmarks =
    [
        new ListPropertyBenchmark<SelectControl>("dimension", Dimensions.Select(x => (Option)new Option<int>(x.Code,x.DisplayName)).ToArray()),
        new ListPropertyBenchmark<SelectControl>("dimensionWithStream", null, LayoutAreaReference.GetDataPointer("stream")),
        new ListPropertyBenchmark<RadioGroupControl>("display", [new Option<string>("chart", "chart"), new Option<string>("table", "table")])
    ];


    private void ValidateListBenchmark<TControl>(ISynchronizationStream<JsonElement> stream, TControl control, ListPropertyBenchmark<TControl> benchmark)
        where TControl : ListControlBase<TControl>
    {
        control.Data.Should().BeOfType<JsonPointerReference>().Subject.Pointer.Should().Be(benchmark.Data);

        var options = control.Options as IReadOnlyCollection<Option>;

        if (control.Options is JsonPointerReference pointer)
        {
            if (benchmark.OptionPointer != null)
                pointer.Pointer.Should().Be(benchmark.OptionPointer);
            else
                pointer.Pointer.Should().StartWith("/data/");

            if (benchmark.Options is not null)
            {
                options = stream.Reduce(pointer)!
                    .Select(p =>
                    {
                        var valueString = p.Value.ToString();
                        if (string.IsNullOrWhiteSpace(valueString))
                            return null;
                        return JsonNode.Parse(valueString)
                            .Deserialize<IReadOnlyCollection<Option>>(stream.Hub.JsonSerializerOptions);
                    })
                    .Where(x => x is not null)
                    .Should().Within(10.Seconds()).Emit();
            }
        }

        if (benchmark.Options is null)
            options.Should().BeNull();
        else
            options.Should().BeEquivalentTo(benchmark.Options, stream.Hub.JsonSerializerOptions);
    }

    [Fact]
    public void TestEditorWithListFormProperties()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(nameof(EditorWithListFormProperties)));

        var control = stream
            .GetControlStream(nameof(EditorWithListFormProperties))
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;

        var controls = editor.Areas
            .Select(a => stream.GetControlStream(a.Area.ToString()!).Should().Within(5.Seconds()).Match(x => x is not null))
            .ToArray();

        controls.Should().HaveCount(ListPropertyBenchmarks.Length);
        for (int i = 0; i < controls.Length; i++)
            ValidateListBenchmark(stream, (dynamic)controls[i]!, (dynamic)ListPropertyBenchmarks[i]);
    }


    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration).AddLayout(layout => layout
            .WithView(nameof(EditorWithoutResult), EditorWithoutResult)
            .WithView(nameof(EditorWithResult), EditorWithResult)
            .WithView(nameof(EditorWithDelayedResult), EditorWithDelayedResult)
            .WithView(nameof(EditorWithListFormProperties), EditorWithListFormProperties)
        ).AddData(data => data
            .AddSource(source =>
                source.WithType<MyDimension>(type => type.WithInitialData(Dimensions))));
    }


}

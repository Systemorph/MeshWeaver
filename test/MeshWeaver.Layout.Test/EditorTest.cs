﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

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
    private UiControl EditorWithoutResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(), "calc");
    private UiControl EditorWithResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(), c => Controls.Markdown($"{c.X + c.Y}"));
    private UiControl EditorWithDelayedResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(), c =>
        {
            Thread.Sleep(100);
            return Controls.Markdown($"{c.X + c.Y} @ {DateTime.UtcNow.Second}:{DateTime.UtcNow.Millisecond}");
        });
    #endregion

    private UiControl EditorWithListFormProperties
    (LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new ListForms());


    [Fact]
    public async Task TestEditorWithoutResult()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(nameof(EditorWithoutResult)));
        var control = await area
            .GetControlStream(nameof(EditorWithoutResult))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;
        editor.Areas.Should()
            .HaveCount(2)
            .And.AllSatisfy(e =>
            {
                var skin = e.Skins.OfType<PropertySkin>().Should().ContainSingle().Subject;
                skin.Label.Should().NotBeNull();
                skin.Description.Should().NotBeNull();
            });
        var editorAreas = await editor.Areas.ToAsyncEnumerable()
            .SelectAwait(async a => 
                await area.GetControlStream(a.Area.ToString()).Timeout(5.Seconds()).FirstAsync())
            .ToArrayAsync();

        editorAreas.Should().HaveCount(2);
        editorAreas.Should()
            .AllBeOfType<NumberFieldControl>()
            ;

        await Task.Delay(1000);
    }
    [Fact]
    public async Task TestEditorWithResult()
    {
        var client = GetClient();

        var workspace = client.GetWorkspace();
        var area = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(nameof(EditorWithResult)));
        var control = await area
            .GetControlStream(nameof(EditorWithResult))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        control = await area
            .GetControlStream(stack.Areas.First().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;
        editor.Areas.Should()
            .HaveCount(2)
            .And.AllSatisfy(e =>
            {
                var skin = e.Skins.OfType<PropertySkin>().Should().ContainSingle().Subject;
                skin.Label.Should().NotBeNull();
                skin.Description.Should().NotBeNull();
            });
        var editorAreas = await editor.Areas.ToAsyncEnumerable()
            .SelectAwait(async a =>
                await area.GetControlStream(a.Area.ToString()).Timeout(5.Seconds()).FirstAsync(x => x is not null))
            .ToArrayAsync();

        editorAreas.Should().HaveCount(2);
        editorAreas.Should()
            .AllBeOfType<NumberFieldControl>()
            ;

        control = await area
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>().Subject.Markdown.Should().Be("0");

        // update once ==> will issue "add", as 0 was not there
        area.UpdatePointer(1, editor.DataContext, new("x"));
        control = await area
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not MarkdownControl { Markdown: "0" });

        control.Should().BeOfType<MarkdownControl>().Subject.Markdown.Should().Be("1");

        // update once ==> will issue "replace"
        area.UpdatePointer(2, editor.DataContext, new("x"));
        control = await area
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not MarkdownControl { Markdown: "1" });

        control.Should().BeOfType<MarkdownControl>().Subject.Markdown.Should().Be("2");
    }
    [Fact]
    public async Task TestEditorWithDelayed()
    {
        var client = GetClient();

        var workspace = client.GetWorkspace();
        var area = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(nameof(EditorWithDelayedResult)));
        var control = await area
            .GetControlStream(nameof(EditorWithDelayedResult))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        control = await area
            .GetControlStream(stack.Areas.First().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;

        
        var controlStream = area
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .TakeUntil(x => x is MarkdownControl { Markdown: var data } && data.ToString()!.StartsWith("5"));


        // update once ==> will issue "replace"
        for (var i = 1; i <= 5; i++)
        {
            area.UpdatePointer(i, editor.DataContext, new("x"));
        }

        var controls = await controlStream.ToArray();
        controls.Should().HaveCountLessThanOrEqualTo(3);
        controls.Last().Should().BeOfType<MarkdownControl>().Which.Markdown.ToString().Should().StartWith("5");
    }
    private record ListForms
    {
        [Dimension<MyDimension>()]
        public string Dimension { get; init; }
        [Dimension<MyDimension>(Options = "stream")]
        public string DimensionWithStream { get; init; }
        [UiControl<RadioGroupControl>(Options = new[] { "chart", "table" })]
        public string Display { get; init; }
    }

    private record ListPropertyBenchmark<T>(string Data, Option[] Options, string OptionPointer = null);

    private static readonly object[] ListPropertyBenchmarks =
    [
        new ListPropertyBenchmark<SelectControl>("dimension", Dimensions.Select(x => (Option)new Option<int>(x.Code,x.DisplayName)).ToArray()),
        new ListPropertyBenchmark<SelectControl>("dimensionWithStream", null, LayoutAreaReference.GetDataPointer("stream")),
        new ListPropertyBenchmark<RadioGroupControl>("display", [new Option<string>("chart", "chart"), new Option<string>("table", "table")])
    ];
    private static readonly MyDimension[] Dimensions = [new(1, "One"), new(2, "Two")];
    private async Task ValidateListBenchmark<TControl>(ISynchronizationStream<JsonElement> stream, TControl control, ListPropertyBenchmark<TControl> benchmark)
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
                options = await stream.Reduce(pointer)
                    .Select(p =>
                        JsonNode.Parse(p.Value.ToString())
                            .Deserialize<IReadOnlyCollection<Option>>(stream.Hub.JsonSerializerOptions))
                    .Where(x => x is not null)
                    .Timeout(10.Seconds())
                    .FirstAsync();
        }

        if (benchmark.Options is null)
            options.Should().BeNull();
        else
            options.Should().BeEquivalentTo(benchmark.Options);
    }

    [Fact]
    public async Task TestEditorWithListFormProperties()
    {
        var client = GetClient();

        var workspace = client.GetWorkspace();
        var stream = workspace
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(nameof(EditorWithListFormProperties)));
        var control = await stream
            .GetControlStream(nameof(EditorWithListFormProperties))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;

        var controls = await editor.Areas.ToAsyncEnumerable()
            .SelectAwait(async a =>
                await stream.GetControlStream(a.Area.ToString()).Timeout(5.Seconds()).FirstAsync(x => x is not null))
            .ToArrayAsync();

        controls.Should().HaveCount(ListPropertyBenchmarks.Length);
        foreach (var (c, b) in controls.Zip(ListPropertyBenchmarks))
            await ValidateListBenchmark(stream, (dynamic)c, (dynamic)b); 


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

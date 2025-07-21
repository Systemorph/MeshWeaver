using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using FluentAssertions;
using Json.Patch;
using Json.Pointer;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Playground class for testing various JSON operations, patches, and reactive streams
/// </summary>
public class Playground
{
    /// <summary>
    /// Tests reactive stream subscription and merging behavior with replay subjects
    /// </summary>
    [Fact]
    public void ChangeStreamSubscriptionTest()
    {
        ReplaySubject<int> initialDataChanged = new(1);
        Subject<int> outgoingChanges = new();

        var lastValue = 0;

        initialDataChanged.Take(1).Merge(outgoingChanges).Subscribe(x => lastValue = x);

        initialDataChanged.OnNext(1);
        lastValue.Should().Be(1);
    }

    /// <summary>
    /// Test data record for JSON patch operations
    /// </summary>
    /// <param name="Id">The unique identifier</param>
    /// <param name="Value">The numeric value</param>
    private record MyData(string Id, double Value);

    /// <summary>
    /// Tests JSON patch creation and application functionality
    /// </summary>
    [Fact]
    public void JsonPatchTest()
    {
        var instance = new MyData("1", 1);
        var serialized = JsonSerializer.Serialize(instance);
        var altered = instance with { Value = 2 };
        var patch = instance.CreatePatch(altered);
        var applied = patch.Apply(instance);
        applied.Should().BeEquivalentTo(altered);
    }

    /// <summary>
    /// Tests reactive stream changes with combine latest operations
    /// </summary>
    [Fact]
    public void Changes()
    {
        var values = new Subject<int>();
        var changes = new Subject<Func<int, int>>();
        var output = values.CombineLatest(changes.StartWith(x => x), (i, c) => c.Invoke(i));
        var lastValue = 0;
        var sub = output.Subscribe(x => lastValue = x);

        values.OnNext(2);
        lastValue.Should().Be(2);
        changes.OnNext(x => x + 1);
        lastValue.Should().Be(3);
    }

    /// <summary>
    /// Tests JSON pointer navigation and element evaluation
    /// </summary>
    [Fact]
    public void Equality()
    {
        var json = "{\"grandParent\":{\"parent\":{\"child\":{\"name\":\"John\"}}}}";
        var element = JsonDocument.Parse(json).RootElement;
        var pointer = JsonPointer.Parse("/grandParent");
        var grandParent = pointer.Evaluate(element);
        pointer = JsonPointer.Parse("/parent/child");
        var child = pointer.Evaluate(grandParent!.Value);
        child!.Value.GetProperty("name").GetString().Should().Be("John");
    }

    /// <summary>
    /// Tests enumerable extraction from JSON elements using pointers
    /// </summary>
    [Fact]
    public void ExtractEnumerableTest()
    {
        var data = new[] { new { Label = "Label1", Value = true } };
        var dict = new Dictionary<string, object> { { "DataContext", data } };

        var element = JsonSerializer.SerializeToElement(dict);

        var res = Extract<IEnumerable<object>>(element, "/DataContext");
    }

    private TResult? Extract<TResult>(JsonElement element, string path)
    {
        var pointer = JsonPointer.Parse(path);
        var ret = pointer.Evaluate(element);
        return ret == null ? default : ret.Value.Deserialize<TResult>();
    }
}

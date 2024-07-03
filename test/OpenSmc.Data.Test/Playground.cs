using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Json.Patch;
using Json.Pointer;
using Xunit;

namespace OpenSmc.Data.Test;

public class Playground
{
    [Fact]
    public void ChangeStreamSubscriptionTest()
    {
        ReplaySubject<int> initialDataChanged = new(1);
        Subject<int> outgoingChanges = new();

        var lastValue = 0;

        var sub = initialDataChanged.Take(1).Merge(outgoingChanges).Subscribe(x => lastValue = x);

        initialDataChanged.OnNext(1);
        lastValue.Should().Be(1);
    }

    private record MyData(string Id, double Value);

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

    [Fact]
    public void Equality()
    {
        var json = "{\"grandParent\":{\"parent\":{\"child\":{\"name\":\"John\"}}}}";
        var element = JsonDocument.Parse(json).RootElement;
        var pointer = JsonPointer.Parse("/grandParent");
        var grandParent = pointer.Evaluate(element);
        pointer = JsonPointer.Parse("/parent/child");
        var child = pointer.Evaluate(grandParent.Value);
        child.Value.GetProperty("name").GetString().Should().Be("John");
    }
}

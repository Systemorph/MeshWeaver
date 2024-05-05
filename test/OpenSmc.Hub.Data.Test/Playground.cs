using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Xunit;

namespace OpenSmc.Hub.Data.Test;

public class Playground
{
    [Fact]
    public void ChangeStreamSubscriptionTest()
    {
        ReplaySubject<int> initialDataChanged = new(1);
        Subject<int> outgoingChanges = new();

        int lastValue = 0;

        var sub = initialDataChanged.Take(1).Merge(outgoingChanges).Subscribe(x => lastValue = x);

        initialDataChanged.OnNext(1);
        lastValue.Should().Be(1);
    }
}

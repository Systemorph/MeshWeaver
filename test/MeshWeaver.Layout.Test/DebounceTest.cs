using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Layout.DataBinding;
using Xunit;

namespace MeshWeaver.Layout.Test;

public class DebounceTest
{
    [Fact]
    public async Task BasicDebounce()
    {
        // List to capture debounced values
        var random = new Random(20250324);
        var subject = new Subject<int>();
        var resultsAwaiter = subject
            .ToArray()
            .GetAwaiter();

        var results = await Observable.Range(1, 100)
            .Select(i =>
            {
                Thread.Sleep((int)random.NextInt64(30));
                return i;
            })
            .Debounce(TimeSpan.FromMilliseconds(20))
            .ToArray()
            .FirstAsync();




        results.Should().HaveCountLessThan(11);
    }
}

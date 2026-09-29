using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The home's "shared with me" leg re-shapes the content section's union query, so every emission
/// of an EQUAL list rebuilt the home control, and the common case — the real answer is empty too —
/// re-emitted [] over its own StartWith([]) (memex.meshweaver.cloud, 2026-09-29). Pins that only a
/// CHANGED list flows.
/// </summary>
public class HomeSharedTargetsTest
{
    private static readonly UserActivityLayoutAreas.SharedTargetsComparer Comparer =
        UserActivityLayoutAreas.SharedTargetsComparer.Instance;

    [Fact]
    public void Equal_lists_are_equal_regardless_of_instance_and_case()
    {
        Comparer.Equals(new List<string>(), new List<string>()).Should().BeTrue("[] then [] is no change");
        Comparer.Equals(["Acme/Space"], ["acme/space"]).Should().BeTrue("mesh paths compare case-insensitively");
        Comparer.Equals(["A", "B"], ["B", "A"]).Should().BeFalse("order shapes the union query text");
        Comparer.Equals(["A"], ["A", "B"]).Should().BeFalse();
        Comparer.Equals(null, []).Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_answer_over_the_placeholder_emits_once()
    {
        IReadOnlyList<string> empty = [];
        var emitted = await Observable.Return(empty).Concat(Observable.Return(empty))
            .StartWith(empty)
            .DistinctUntilChanged(Comparer)
            .ToList();

        emitted.Should().HaveCount(1, "the placeholder and an equal answer must not rebuild the home twice");
    }
}

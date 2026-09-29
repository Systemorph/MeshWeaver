using System;
using System.Collections.Generic;
using System.Reactive.Linq;
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
    public void Equal_lists_are_equal_regardless_of_instance_but_not_case()
    {
        Comparer.Equals(new List<string>(), new List<string>()).Should().BeTrue("[] then [] is no change");
        Comparer.Equals(["Acme/Space"], ["Acme/Space"]).Should().BeTrue("distinct instances, same paths");
        Comparer.Equals(["Acme/Space"], ["acme/space"]).Should().BeFalse("a case-only change must still reach the union query");
        Comparer.Equals(["A", "B"], ["B", "A"]).Should().BeFalse("order shapes the union query text");
        Comparer.Equals(["A"], ["A", "B"]).Should().BeFalse();
        Comparer.Equals(null, []).Should().BeFalse();
    }

    [Fact]
    public void An_empty_answer_over_the_placeholder_emits_once()
    {
        // Distinct instances, as in production: the StartWith([]) placeholder and each fresh list
        // SharedTargetPaths returns are different objects even when all are empty — so this pins
        // STRUCTURAL equality, not the comparer's ReferenceEquals short-circuit.
        IReadOnlyList<string> placeholder = new List<string>();
        IReadOnlyList<string> firstAnswer = new List<string>();
        IReadOnlyList<string> secondAnswer = new List<string>();
        // Observable.Return runs on the immediate scheduler, so the whole sequence completes inside
        // Subscribe — no bridge, no await on the observable.
        IList<IReadOnlyList<string>>? emitted = null;
        Exception? fault = null;
        Observable.Return(firstAnswer).Concat(Observable.Return(secondAnswer))
            .StartWith(placeholder)
            .DistinctUntilChanged(Comparer)
            .ToList()
            .Subscribe(list => emitted = list, ex => fault = ex);

        fault.Should().BeNull();
        emitted.Should().NotBeNull("the sequence is synchronous and completes inside Subscribe");
        emitted!.Should().HaveCount(1, "the placeholder and an equal answer must not rebuild the home twice");
    }
}

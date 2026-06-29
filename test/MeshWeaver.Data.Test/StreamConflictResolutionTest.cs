using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Data.Serialization;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Dedicated tests for <see cref="StreamConflictResolution"/> — version-based
/// resolution of an incoming change against the stream's current state. The
/// incoming change's <see cref="ChangeItem{T}.Version"/> is the BASE version it
/// was computed from (writers never mint a new version). Pins:
/// <list type="bullet">
///   <item><description>fast-forward when the change is based on current;</description></item>
///   <item><description>a stale FULL frame is rejected (the read-mirror revert);</description></item>
///   <item><description>a stale PATCH is merged field-wise onto current;</description></item>
///   <item><description>concurrent edits to disjoint fields / disjoint text spans both survive.</description></item>
/// </list>
/// </summary>
public class StreamConflictResolutionTest
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private record Doc(string Id, string Title, string Body, int Count);

    private static ChangeItem<Doc> Full(Doc value, long version) =>
        new(value, "writer", "stream", ChangeType.Full, version, null);

    private static ChangeItem<Doc> Patch(Doc baseValue, Doc value, long baseVersion) =>
        new(value, "writer", "stream", ChangeType.Patch, baseVersion,
            [new EntityUpdate("Doc", value.Id, value) { OldValue = baseValue }]);

    [Fact]
    public void NullCurrent_TakesIncoming()
    {
        var incoming = Full(new Doc("d", "T", "B", 1), 1);
        var result = StreamConflictResolution.Resolve(null, incoming, Options, out var keep);
        keep.Should().BeFalse();
        result.Should().Be(incoming.Value);
    }

    [Fact]
    public void IncomingBasedOnCurrentVersion_FastForward()
    {
        var current = Full(new Doc("d", "T0", "B0", 1), 5);
        var incoming = Full(new Doc("d", "T1", "B0", 1), 5); // same base version
        var result = StreamConflictResolution.Resolve(current, incoming, Options, out var keep);
        keep.Should().BeFalse();
        result!.Title.Should().Be("T1");
    }

    [Fact]
    public void IncomingNewer_TakesOver()
    {
        var current = Full(new Doc("d", "T0", "B0", 1), 5);
        var incoming = Full(new Doc("d", "T1", "B1", 2), 9); // newer
        var result = StreamConflictResolution.Resolve(current, incoming, Options, out var keep);
        keep.Should().BeFalse();
        result.Should().Be(incoming.Value);
    }

    [Fact]
    public void StaleFullFrame_KeepsCurrent_DoesNotRevert()
    {
        // The resubmit read-mirror revert: current is the newer committed state;
        // an older FULL frame arrives late and must NOT clobber it.
        var current = Full(new Doc("d", "committed", "body", 2), 9);
        var staleA = Full(new Doc("d", "stale", "", 0), 3); // older version, full snapshot
        var result = StreamConflictResolution.Resolve(current, staleA, Options, out var keep);
        keep.Should().BeTrue();
        result.Should().Be(current.Value, "a stale full frame must not revert the newer state");
    }

    [Fact]
    public void StalePatch_MergesChangedFieldOntoCurrent()
    {
        // Base both writers started from.
        var baseDoc = new Doc("d", "T0", "B0", 1);
        // Current already advanced: a concurrent writer changed Body.
        var current = Full(baseDoc with { Body = "B-current" }, 9);
        // Incoming (stale) changed Title from the SAME base.
        var incoming = Patch(baseDoc, baseDoc with { Title = "T-incoming" }, baseVersion: 5);

        var result = StreamConflictResolution.Resolve(current, incoming, Options, out var keep);
        keep.Should().BeFalse();
        result!.Title.Should().Be("T-incoming", "the writer's field change is applied");
        result.Body.Should().Be("B-current", "the concurrent writer's field is preserved (no clobber)");
    }

    [Fact]
    public void StalePatch_DisjointStringEdits_BothSurvive()
    {
        var baseDoc = new Doc("d", "T", "The quick brown fox jumps", 1);
        // Current changed the END of Body (concurrent edit).
        var current = Full(baseDoc with { Body = "The quick brown fox leaps" }, 9);
        // Incoming (stale) changed the START of Body from the same base.
        var incoming = Patch(baseDoc, baseDoc with { Body = "The VERY quick brown fox jumps" }, baseVersion: 5);

        var result = StreamConflictResolution.Resolve(current, incoming, Options, out var keep);
        keep.Should().BeFalse();
        result!.Body.Should().Be("The VERY quick brown fox leaps",
            "disjoint text edits from the same base merge via StringDelta");
    }

    [Fact]
    public void StalePatch_NoBase_KeepsCurrent()
    {
        var current = Full(new Doc("d", "T", "B", 2), 9);
        // Patch with no EntityUpdate carrying OldValue — can't merge → keep current.
        var incoming = new ChangeItem<Doc>(
            new Doc("d", "X", "Y", 3), "writer", "stream", ChangeType.Patch, 5, null);
        var result = StreamConflictResolution.Resolve(current, incoming, Options, out var keep);
        keep.Should().BeTrue();
        result.Should().Be(current.Value);
    }
}

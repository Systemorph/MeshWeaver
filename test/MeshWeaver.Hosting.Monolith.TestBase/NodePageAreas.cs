using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Reads a rendered layout area's control TREE through the layout client, re-resolved on every
/// frame — the one way a test may look below an area's root without racing a re-shape.
///
/// <para>🚨 A node page RE-SHAPES while it loads (MeshWeaver#6548). Its Overview paints first as the
/// page alone, and when the index rail's queries answer it re-renders as a splitter (the rail
/// beside the page); the first frame's child areas are then gone for good. A test that takes a
/// child key from an early frame and subscribes to it afterwards waits on an area nobody will
/// fill again — a 30 s timeout with "Last of 1 emission(s) was: &lt;null&gt;" when the re-render
/// lands between the two reads, and a pass when it does not. Every walk here is
/// <c>Select</c> + <c>Switch</c> over the parent's stream, so a re-shape disposes the walk of the
/// old children and starts one over the new ones: whichever shape is current is the one read.</para>
///
/// <para>Wait on the result with a predicate (<c>.Should().Within(…).Match(…)</c>), never take its
/// first emission: a walk emits partial trees while the page paints.</para>
/// </summary>
public static class NodePageAreas
{
    /// <summary>
    /// Every control rendered at <paramref name="area"/> and, recursively, in its sub-areas — each
    /// emitted as it (re-)renders, followed live through every re-shape of any ancestor.
    /// </summary>
    /// <param name="stream">The layout-area stream the page was opened on.</param>
    /// <param name="area">The area key to start from (e.g. <c>"Overview"</c>).</param>
    public static IObservable<UiControl?> WalkTree(this ISynchronizationStream<JsonElement> stream, string area)
        => stream.GetControlStream(area)
            .Select(control => control is IContainerControl container
                ? SubAreas(container)
                    .Select(subArea => stream.WalkTree(subArea))
                    .Merge()
                    .StartWith(control)
                : Observable.Return(control))
            .Switch();

    /// <summary>
    /// The WHOLE tree currently rendered at <paramref name="area"/> — the root first, then each
    /// sub-area's tree in order — re-emitted whenever any part of it re-renders or re-shapes. A
    /// sub-area that has not rendered yet (or renders nothing by design) contributes nothing, so
    /// the snapshot is partial while the page paints: wait on it with a predicate.
    /// </summary>
    /// <param name="stream">The layout-area stream the page was opened on.</param>
    /// <param name="area">The area key to start from (e.g. <c>"Overview"</c>).</param>
    public static IObservable<ImmutableList<UiControl>> TreeSnapshots(
        this ISynchronizationStream<JsonElement> stream, string area)
        => stream.GetControlStream(area)
            .Select(control => control switch
            {
                null => Observable.Return(ImmutableList<UiControl>.Empty),
                IContainerControl container when SubAreas(container).ToImmutableList() is { Count: > 0 } subAreas
                    => subAreas
                        .Select(subArea => stream.TreeSnapshots(subArea).StartWith(ImmutableList<UiControl>.Empty))
                        .CombineLatest(children => ImmutableList.Create(control).AddRange(children.SelectMany(c => c))),
                _ => Observable.Return(ImmutableList.Create(control)),
            })
            .Switch();

    private static System.Collections.Generic.IEnumerable<string> SubAreas(IContainerControl container)
        => container.Areas
            .Select(a => a.Area?.ToString())
            .OfType<string>()
            .Where(name => name.Length > 0);
}

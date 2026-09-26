using System;
using System.Reactive;

namespace MeshWeaver.Testing.InMesh;

/// <summary>
/// One explicitly listed case of a <c>Tests</c> area — the shape for a suite whose cases are static
/// methods rather than <see cref="MeshFactAttribute"/> classes. Rendered through
/// <see cref="MeshTestRunner.Area(MeshWeaver.Layout.Composition.LayoutAreaHost, string, System.Collections.Generic.IReadOnlyList{MeshTestCase}, TimeSpan?)"/>,
/// which runs the cases one after another, streams each one's progress and output, and bounds each
/// by its <see cref="Timeout"/> (or the area's deadline).
///
/// <para>Two kinds, because they fail to finish in two different ways. A SYNCHRONOUS case
/// (<see cref="Of(string, Action, TimeSpan?)"/>) is a method that asserts and returns; it runs as one leaf on
/// the mesh's <c>Tests</c> I/O pool, so a body that never returns costs its own bound and a pool
/// slot — never the render thread, which is what made one hung case freeze the whole page. A LIVE
/// case (<see cref="Live(string, Func{IObservable{Unit}}, TimeSpan?)"/>) returns a cold observable
/// that emits once when the assertion held; the bound disposes its subscription, which is what
/// cancels the work behind it.</para>
/// </summary>
public sealed record MeshTestCase
{
    /// <summary>What the row says.</summary>
    public required string Name { get; init; }

    /// <summary>This case's own bound; null uses the area's deadline.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The synchronous body, handed a line writer for its output. Null for a live case.</summary>
    public Action<Action<string>>? Synchronous { get; init; }

    /// <summary>The live body, handed a line writer for its output. Null for a synchronous case.</summary>
    public Func<Action<string>, IObservable<Unit>>? Reactive { get; init; }

    /// <summary>A synchronous case: passes when <paramref name="body"/> returns, fails with its exception's message.</summary>
    public static MeshTestCase Of(string name, Action body, TimeSpan? timeout = null) =>
        new() { Name = name, Timeout = timeout, Synchronous = _ => body() };

    /// <summary>A synchronous case that writes output lines while it runs.</summary>
    public static MeshTestCase Of(string name, Action<Action<string>> body, TimeSpan? timeout = null) =>
        new() { Name = name, Timeout = timeout, Synchronous = body };

    /// <summary>A live case: passes on the body's first emission; an error, an empty completion or the bound fails it.</summary>
    public static MeshTestCase Live(string name, Func<IObservable<Unit>> body, TimeSpan? timeout = null) =>
        new() { Name = name, Timeout = timeout, Reactive = _ => body() };

    /// <summary>A live case that writes output lines while it runs.</summary>
    public static MeshTestCase Live(string name, Func<Action<string>, IObservable<Unit>> body, TimeSpan? timeout = null) =>
        new() { Name = name, Timeout = timeout, Reactive = body };
}

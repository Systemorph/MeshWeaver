namespace MeshWeaver.Data.Serialization;

/// <summary>
/// The pure half of the update-backlog warning on a synchronization stream (MeshWeaver#5799): when
/// a backlog depth is worth reporting, and how the writer that reached it is named. Kept apart from
/// the stream so both decisions are pinned by tests without a hub.
/// </summary>
internal static class UpdateBacklog
{
    /// <summary>
    /// The reporting depth <paramref name="depth"/> has reached and that has not been reported yet,
    /// or null. Reporting depths are <paramref name="floor"/>, then each tenfold of it — so a flood
    /// is reported a handful of times (1,000 · 10,000 · 100,000 …), never once per write. A depth
    /// that falls and climbs again past the same reporting depth is not reported twice: the stream
    /// has already said who its writer is.
    /// </summary>
    /// <param name="depth">The backlog after the write being posted.</param>
    /// <param name="lastReported">The highest reporting depth already reported (0 for none).</param>
    /// <param name="floor">The first reporting depth; 0 or less disables the report.</param>
    /// <returns>The reporting depth to report now, or null.</returns>
    public static int? ReportAt(int depth, int lastReported, int floor)
    {
        if (floor <= 0 || depth < floor)
            return null;
        // The highest reporting depth at or below `depth`.
        long threshold = floor;
        while (threshold * 10 <= depth)
            threshold *= 10;
        return threshold > lastReported ? (int)threshold : null;
    }

    /// <summary>
    /// The writer behind a delegate, as a reader can look it up: the declaring type and method of
    /// the code that was compiled into it. For a lambda this names the CALLER's method — the C#
    /// compiler emits <c>&lt;UpdateArea&gt;b__0</c> on the caller's (closure) type — which is the
    /// one fact the queued message cannot otherwise give.
    /// </summary>
    /// <param name="origin">The delegate the caller passed.</param>
    /// <returns>"Type.Method".</returns>
    public static string Describe(Delegate origin)
    {
        var method = origin.Method;
        var type = method.DeclaringType;
        // A closure's display class (`<>c__DisplayClass12_0`) is nested in the caller's type —
        // name the outer type, which is what a reader searches for.
        while (type is { DeclaringType: not null } && type.Name.StartsWith("<", StringComparison.Ordinal))
            type = type.DeclaringType;
        return $"{type?.FullName ?? "(unknown)"}.{method.Name}";
    }
}

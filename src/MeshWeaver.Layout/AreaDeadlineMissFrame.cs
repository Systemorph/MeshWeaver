namespace MeshWeaver.Layout;

/// <summary>
/// The well-known frame a layout area serves when its render failed on a DEADLINE MISS: the owner of
/// what the view reads did not answer within the transport's deadline (an Orleans grain-placement or
/// response timeout, flattened into a <see cref="MeshWeaver.Messaging.DeliveryFailureException"/>'s
/// text - see <see cref="AreaErrorClassifier.IsDeadlineMiss"/>). Issue #6394.
///
/// <para>A state of its own. The area exists and its content is fine (not area-not-found, not a
/// missing reference), the data store answered (not storage-unavailable), and nothing pushes a
/// replacement on its own - so it is NOT a transient frame. The viewer is told the owner was slow and
/// that re-opening the view asks again.</para>
///
/// <para>The id round-trips through the sync stream; the localized prose does not. A consumer
/// classifies on <see cref="Is"/>, never on the text.</para>
/// </summary>
public static class AreaDeadlineMissFrame
{
    /// <summary>The <see cref="UiControl"/> id stamped on the deadline-miss frame.</summary>
    public const string Id = "area-owner-deadline-miss";

    /// <summary>True when <paramref name="control"/> is the deadline-miss frame.</summary>
    /// <param name="control">The control an area rendered; may be null.</param>
    public static bool Is(UiControl? control)
        => control?.Id is string id && string.Equals(id, Id, System.StringComparison.Ordinal);
}

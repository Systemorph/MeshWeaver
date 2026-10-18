namespace MeshWeaver.Data;

/// <summary>
/// The data layer's own answer to "what does this delivered READ ask for": the reference of a
/// <see cref="GetDataRequest"/> or a <see cref="SubscribeRequest"/>, and null for every other message.
/// For a gate that must classify a read by its SHAPE (a single node, a collection, a layout area)
/// without naming the data-plane messages outside the data layer (Doc/Architecture/DataPlaneMessagesAreStreamPlumbing).
/// </summary>
public static class ReadRequests
{
    /// <summary>The workspace reference a delivered read asks for, or null when <paramref name="message"/> is not a read. Pure.</summary>
    /// <param name="message">The delivered message.</param>
    public static WorkspaceReference? ReferenceOf(object? message) => message switch
    {
        GetDataRequest get => get.Reference,
        SubscribeRequest subscribe => subscribe.Reference,
        _ => null,
    };
}

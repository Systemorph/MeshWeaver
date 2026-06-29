namespace MeshWeaver.Messaging;

/// <summary>
/// Controls whether a hosted-hub lookup is allowed to construct the hub when it
/// does not yet exist in the collection. Passed to
/// <see cref="HostedHubsCollection.GetHub"/> to distinguish a routing read from
/// a deliver-and-create.
/// </summary>
public enum HostedHubCreation
{
    /// <summary>Create the hub if it is not already present, then return it.</summary>
    Always,
    /// <summary>Pure read: return the existing hub or null, never constructing one.</summary>
    Never
}

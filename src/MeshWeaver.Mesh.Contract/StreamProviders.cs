namespace MeshWeaver.Mesh;

/// <summary>
/// Constants for stream provider names.
/// </summary>
public static class StreamProviders
{
    /// <summary>
    /// In-memory stream provider.
    /// </summary>
    public const string Memory = nameof(Memory);

    /// <summary>
    /// Orleans' well-known grain-storage name for the streaming pub-sub subscription registry
    /// (<c>PubSubRendezvousGrain</c>). The name is fixed by Orleans — a provider registered under
    /// any other name is simply not used by streaming.
    ///
    /// <para>🚨 <b>Whatever backs this store decides whether a cross-silo reply can be silently
    /// lost</b> (issue #1729). The rendezvous grain holds the list of consumers subscribed to a
    /// stream; a publish consults it (via the pulling agent) and, finding NO consumer, DISCARDS the
    /// message and reports success. So if the store is non-durable and the silo hosting the
    /// rendezvous grain departs — which every rolling deploy guarantees through its overlap window —
    /// the surviving consumer's subscription record is gone, its <c>StreamSubscriptionHandle</c>
    /// stays valid and silent, and every later publish to that stream evaporates. Permanent, silent,
    /// per-stream, and asymmetric between pods.</para>
    ///
    /// <para>See <c>Doc/Architecture/OrleansStreamPubSubDurability</c>.</para>
    /// </summary>
    public const string PubSubStore = nameof(PubSubStore);
}

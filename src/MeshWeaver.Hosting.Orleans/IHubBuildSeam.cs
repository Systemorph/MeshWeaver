using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The fault-injection harness's view into a <see cref="MessageHubGrain"/>'s hub BUILD: the one
/// stretch of a grain's life that no storage or routing switch can hold on its own, because the build
/// runs node resolution, NodeType binding and assembly load inside the activation (#7470).
///
/// <para><b>Never registered in production.</b> The grain resolves it with <c>GetService</c>, and
/// when it is absent the build and the parking of deliveries behave exactly as they did before it
/// existed. The test-side implementation is <c>HubBuildHold</c> in
/// <c>MeshWeaver.Hosting.Orleans.TestBase</c>, registered per silo by <c>FaultInjectionCluster</c>
/// (see Doc/Architecture/FaultInjectionHarness).</para>
/// </summary>
internal interface IHubBuildSeam
{
    /// <summary>
    /// Wraps the grain's hub build for <paramref name="path"/>. The returned sequence is what the grain
    /// subscribes to; holding it holds the build, and its terminal is the build's terminal.
    /// </summary>
    /// <param name="path">The grain key (the node path).</param>
    /// <param name="build">The build: one enriched node, then completion.</param>
    /// <returns>The build, possibly held.</returns>
    IObservable<MeshNode> Build(string path, IObservable<MeshNode> build);

    /// <summary>
    /// Reports that the grain for <paramref name="path"/> ACCEPTED <paramref name="delivery"/> and parked
    /// it on its unbuilt hub — the moment the grain call was answered <c>Submitted</c>.
    /// </summary>
    /// <param name="path">The grain key (the node path).</param>
    /// <param name="delivery">The parked delivery.</param>
    void Parked(string path, IMessageDelivery delivery);
}

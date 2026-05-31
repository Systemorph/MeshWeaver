using System.Runtime.CompilerServices;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Suppresses a benign in-process Orleans <c>TestCluster</c> shutdown race.
///
/// <para>When a test class's <c>TestCluster</c> is disposed, the silo's Autofac
/// DI container (<c>LifetimeScope</c>) is torn down while the silo's network
/// <c>Orleans.Runtime.Messaging.Connection</c> is still draining in-flight
/// <c>Memory</c>-stream messages on a ThreadPool task. Deserializing those
/// (<c>MemoryMessageBodySerializerFactory.GetOrCreateSerializer</c> /
/// <c>CodecProvider.GetService</c>) resolves a codec from the now-disposed
/// container and throws <see cref="ObjectDisposedException"/> ("LifetimeScope …
/// has already been disposed"). In light runs Orleans catches it internally and
/// it's a harmless first-chance exception; under heavy CI load it can escape the
/// connection task UNOBSERVED, which xUnit v3 escalates to a
/// "Catastrophic failure" that fails the whole test collection (and leaves the
/// run looking red even though every test passed in isolation).</para>
///
/// <para>This is an Orleans TestingHost shutdown-ordering artifact, not product
/// code — the cluster is on its way down, the messages don't matter. Observe the
/// unobserved task exception when (and ONLY when) every inner exception is a
/// disposed-LifetimeScope <see cref="ObjectDisposedException"/>, so it doesn't
/// crash the collection. Any other unobserved exception is left untouched and
/// still surfaces.</para>
/// </summary>
internal static class OrleansShutdownRaceSuppressor
{
    [ModuleInitializer]
    public static void Init()
    {
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var inners = e.Exception?.Flatten().InnerExceptions;
            if (inners is { Count: > 0 } && inners.All(IsDisposedLifetimeScope))
                e.SetObserved();
        };
    }

    private static bool IsDisposedLifetimeScope(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return (ex is ObjectDisposedException
                && msg.Contains("LifetimeScope", StringComparison.OrdinalIgnoreCase))
               || msg.Contains("nested lifetimes cannot be created", StringComparison.OrdinalIgnoreCase);
    }
}

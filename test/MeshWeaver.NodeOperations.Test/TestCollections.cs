using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Ensures the thread pool has enough threads for sequential hub creation/destruction.
/// Without this, 55+ sequential hub instances can exhaust the default thread pool.
/// </summary>
internal static class ThreadPoolSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ThreadPool.SetMinThreads(50, 50);
    }
}

[CollectionDefinition("SamplesGraphData", DisableParallelization = true)]
public class SamplesGraphDataCollection;

[CollectionDefinition("NodeOperationsTests", DisableParallelization = true)]
public class NodeOperationsTestsCollection;

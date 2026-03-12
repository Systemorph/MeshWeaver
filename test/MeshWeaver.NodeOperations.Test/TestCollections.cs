using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MeshWeaver.NodeOperations.Test;

[CollectionDefinition("SamplesGraphData", DisableParallelization = true)]
public class SamplesGraphDataCollection;

[CollectionDefinition("NodeOperationsTests", DisableParallelization = true)]
public class NodeOperationsTestsCollection;

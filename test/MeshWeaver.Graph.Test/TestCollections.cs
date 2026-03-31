using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Collection definition for tests that use samples/Graph/Data.
/// These tests share file system resources and compilation cache,
/// so they must not run in parallel with each other.
/// </summary>
[CollectionDefinition("SamplesGraphData", DisableParallelization = true)]
public class SamplesGraphDataCollection;

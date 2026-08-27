using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

// The xunit COLLECTION DEFINITION stays in the test assembly — xunit resolves a [Collection(name)]
// against definitions in the assembly that declares the test — while the fixture it names lives in
// MeshWeaver.Hosting.Orleans.TestBase, so a test project in another repository declares its own
// definition over the same fixture.
/// <summary>
/// xUnit collection that shares a single Orleans TestCluster.
/// All test classes annotated with [Collection(nameof(OrleansClusterCollection))]
/// share the same cluster instance.
/// </summary>
[CollectionDefinition(nameof(OrleansClusterCollection))]
public class OrleansClusterCollection : ICollectionFixture<SharedOrleansFixture>;

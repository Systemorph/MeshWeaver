// Runs this assembly's cases on the collection-scoped execution host.
//
// Nothing about how the tests are DECLARED changes: [Theory], [InlineData], Skip=, [Collection]
// and per-row reporting stay xunit's. The only difference is that a class declaring
// ShareMeshAcrossTests => true gets a real lifetime for its mesh — one boot per test collection,
// disposed when the collection ends. A class that does not declare it is unaffected.
//
// See Doc/Architecture/CollectionScopedTestFixtures.
[assembly: Xunit.TestFramework(typeof(MeshWeaver.Testing.Xunit.MeshTestFramework))]

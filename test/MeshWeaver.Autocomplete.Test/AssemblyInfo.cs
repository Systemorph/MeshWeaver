// Runs this assembly's cases on the collection-scoped execution host (facility 4).
//
// Nothing about how the tests are DECLARED changes: [Theory], [InlineData], Skip=, [Collection]
// and per-row reporting stay xunit's. The only difference is that a class which already asked to
// share its mesh (ShareMeshAcrossTests => true) now gets a real lifetime for it — one boot per
// test collection, disposed when the collection ends — instead of the static cache that had no
// end and was therefore switched off for the whole estate.
[assembly: Xunit.TestFramework(typeof(MeshWeaver.Testing.Xunit.MeshTestFramework))]

// xUnit v3 discovers [assembly: AssemblyFixture] on the TEST assembly itself — an attribute in a
// referenced base-library assembly registers nothing here. So every test assembly that wants the
// pooled meshes declares it, one line (this is the porting step for other repos' Orleans suites).
[assembly: Xunit.AssemblyFixture(typeof(MeshWeaver.Hosting.Orleans.Test.OrleansMeshPool))]

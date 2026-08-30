# MeshWeaver.Hosting.Orleans.TestBase

Test infrastructure for running MeshWeaver over an in-process Orleans cluster — the Orleans
counterpart of `MeshWeaver.Hosting.Monolith.TestBase`.

- `OrleansTestBase<TSiloConfigurator>` — one cluster per test class, sized grain directory,
  drained disposal.
- `OrleansTestCluster` / `OrleansTestClusterHost` / `OrleansTestBackingStore` — the cluster
  builder and the shared in-memory store every silo reads.
- `OrleansClusterDisposal`, `OrleansDisposalDrainFixture`, `OrleansShutdownRaceSuppressor` —
  teardown that waits for the cluster instead of racing it.
- `TestGrainDirectorySizing` — the grain-directory size every test cluster must set.
- `SharedOrleansFixture` / `OrleansSharedTestBase` — the per-class fixture shape.

It lives in `src/` rather than inside the Orleans test project so that test projects in other
repositories — the storage (PostgreSql, Cosmos, Snowflake) and transport (gRPC) suites in
MeshWeaver.Plugins — build on the same machinery instead of a copy. The namespace stays
`MeshWeaver.Hosting.Orleans.Test` on purpose: it is the machinery that moved, not its identity.

An xunit `[CollectionDefinition]` must live in the assembly that declares the tests, so each test
project declares its own collection over `SharedOrleansFixture` (see
`test/MeshWeaver.Hosting.Orleans.Test/OrleansClusterCollection.cs`).

# MeshWeaver.Hosting.Orleans.TestBase

Test infrastructure for running MeshWeaver over an in-process Orleans cluster — the Orleans
counterpart of `MeshWeaver.Hosting.Monolith.TestBase`.

- `OrleansMeshTestBase` — **the one base** a suite derives from. Sized grain directory, drained
  disposal, tracked client hubs. A suite says what it wants through the same `IMeshBootstrap` seam
  `MonolithMeshTestBase` uses:

  ```csharp
  protected override IMeshBootstrap Bootstrap => MeshBootstrap.Orleans(o => o.WithSilos(2));
  protected override Type SiloConfiguratorType => typeof(MySiloConfigurator);
  ```

  It replaced `OrleansTestBase<T>` and `OrleansSharedTestBase`, which were the same machinery
  written twice — what actually differed between them was three values (silo configurator, silo
  count, leased-or-dedicated cluster), and values do not need a second base class. The two names
  survive in `OrleansTestBaseCompat.cs` **only** until MeshWeaver.Plugins' suites convert; nothing
  may be added to that file.
- `OrleansBootstrapRegistration` — arms `OrleansBootstrap.Applicator` from a `[ModuleInitializer]`.
  That hook is why `MeshWeaver.Hosting.Monolith.TestBase` can define the seam without ever
  referencing Orleans.
- `OrleansTestCluster` / `OrleansTestClusterHost` / `OrleansTestBackingStore` — the cluster
  builder and the shared in-memory store every silo reads.
- `OrleansMeshPool` — a pool of RUNNING clusters, leased per test class and keyed on the whole
  `OrleansClusterShape` (never on the fixture type alone: a lease must not hand a suite a cluster
  built by someone else's silo configurator).
- `OrleansClusterDisposal`, `OrleansDisposalDrainFixture`, `OrleansShutdownRaceSuppressor` —
  teardown that waits for the cluster instead of racing it.
- `TestGrainDirectorySizing` — the grain-directory size every test cluster must set.
- `SharedOrleansFixture` — the object that carries one cluster; built to a described
  `OrleansClusterShape`, or subclassed by a repo that ships its own rig.

It lives outside the Orleans test project so that test projects in other
repositories — the storage (PostgreSql, Cosmos, Snowflake) and transport (gRPC) suites in
MeshWeaver.Plugins — build on the same machinery instead of a copy. The namespace stays
`MeshWeaver.Hosting.Orleans.Test` on purpose: it is the machinery that moved, not its identity.

An xunit `[CollectionDefinition]` must live in the assembly that declares the tests, so each test
project declares its own collection over `SharedOrleansFixture` (see
`test/MeshWeaver.Hosting.Orleans.Test/OrleansClusterCollection.cs`).

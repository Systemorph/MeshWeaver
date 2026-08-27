# MeshWeaver.AI.Orleans.TestBase

The AI-flavoured half of the Orleans test machinery, split out of
`MeshWeaver.Hosting.Orleans.TestBase` so the base can become AI-free and this half can travel to
MeshWeaver.Plugins together with the AI engine and its test suites (MeshWeaver#2276):

- `OrleansTestBase<TSiloConfigurator>` / `TestSiloConfigurator` / `TestClientConfigurator` — the
  per-class cluster base whose mesh folds `AddAI()` and whose client registers the AI types.
- `SharedOrleansFixture` / `OrleansSharedTestBase` — the shared-cluster shape, with the swappable
  chat-client factory.
- `FakeChatClient` / `FakeChatClientFactory` / `SwappableChatClientFactory`.
- `OrleansTestSeedProvider` / `OrleansTestMeshNodeAttribute` — the default agent, thread and app
  test seeds this assembly installs.

The namespace stays `MeshWeaver.Hosting.Orleans.Test` on purpose: it is the machinery that split,
not its identity — no test file changes a `using`. During the transition the BASE project still
references this one (so every existing consumer keeps compiling unchanged); consumers that need
the AI flavour take a direct reference before that transitional edge is cut.

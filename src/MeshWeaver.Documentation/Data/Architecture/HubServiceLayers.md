---
Name: Hub Service Layers
Category: Architecture
Description: >-
  MeshWeaver's own dependency-injection container, MeshServiceProvider: one class for the mesh's root,
  each hub's level and every plain scope, over standard ServiceDescriptors. A hub level holds only its own
  descriptors, frozen, and the instances it built; a parent's scoped service resolved in a hub is built
  with the hub's dependencies; module types are answered by a registration source. It replaced Autofac
  (MeshWeaver#7145), whose per-hub wrapper registrations were the largest block of the portal heap.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="5" rx="1"/><rect x="5" y="10" width="14" height="5" rx="1"/><rect x="7" y="17" width="10" height="4" rx="1"/></svg>
---

# Hub Service Layers

Every hub is a **level** of one container: a scope that holds the descriptors the hub itself declared
and the instances it built — and nothing of its parents'. The container is
`MeshWeaver.ServiceProvider.MeshServiceProvider`: one class for the mesh's root container, each hub's
level, each module generation's own container, and every plain scope begun from any of them (a Blazor
circuit, a request, a module scope a caller owns). It implements the standard
`Microsoft.Extensions.DependencyInjection` contracts over `ServiceDescriptor`s, so every library that
registers into an `IServiceCollection` runs on it unchanged.

It replaced Autofac (MeshWeaver#7145). The defect it removed is in
[The Portal Heap Is Hubs](../PortalHeapIsHubs) → *Per-hub DI and type metadata*: a hub that was an
Autofac child scope with registrations of its own answered every service its parents provided by
wrapping each parent registration in a wrapper registration with its own resolve pipeline, kept for the
life of the hub — about 120 wrappers, ≈58 KB, per hub in the memex OOM dump, multiplied by ~100,000 hubs.

## Why not the standard provider alone

The platform needs three things the standard provider cannot give, and the reason a container of our
own exists is exactly these three — nothing else differs from the standard contracts:

1. **A hub EXTENDS the container it is built under.** Its own registrations win in the hub, and a
   parent's SCOPED or TRANSIENT service resolved in the hub is built with the HUB's dependencies — the
   per-hub `INodeValidator`s (`AddScoped<INodeValidator, RlsNodeValidator>()` at the root) take the
   hub's own `IMessageHub`. A standard scope cannot carry registrations of its own.
2. **Module types are answered at the last step of a lookup.** A live-updatable module's services
   live in a container of their own, and the root answers the types a module declares from the
   module's CURRENT generation (`ModuleOwnedRootSource`), so the mesh hub, every per-node hub and every
   Blazor circuit see them. The standard provider has no hook, and it hands its own scope factory to
   the host — so a circuit's scope could never reach a module's types.
3. **Scopes are hierarchical.** A scope begun from a hub sees the hub's registrations.

## Lookup and lifetimes

- **A single resolve** asks the scope's level, then each parent level in turn, then the root, and last
  the root's `IServiceRegistrationSource`s; the first level that answers wins, and on it the last
  registration (an exact one before an open generic, a key before `KeyedService.AnyKey`).
- **An enumeration** is every level's registrations, the root's first, each level followed by what its
  sources add.
- **A singleton** is one instance in the container level that registers it, built with that level's
  dependencies. A root singleton is the root's one instance, whoever asks — **except** a root open
  generic closed over a COLLECTIBLE type (a NodeType's compiled type, a module's): that instance is held
  by the level that asked, so the root never pins a collectible load context
  (`ACollectibleTypeIsNeverHeldByTheRootTest`, with the negative control that the same closure resolved
  AT the root is retained).
- **A scoped service** is one instance per RESOLVING scope and **a transient** one per resolve, both
  built with the resolving scope's dependencies — which is how a root-scoped service resolved in a hub
  sees the hub's overrides (`HubScopeDeclaresOnlyItsOwnServicesTest`).
- **Implicit relationships**, kept from the container it replaced because the platform's constructors
  take them: every collection shape of `T` (`IEnumerable`, `IReadOnlyList`, `IReadOnlyCollection`,
  `IList`, `ICollection`, arrays), `Lazy<T>`, `Func<T>`, and a collection of deferred services —
  🚨 `IEnumerable<Lazy<T>>` is ONE deferred element PER REGISTRATION OF `T`. Read as "registrations of
  `Lazy<T>`" it is empty, and every node validator — row-level security among them — would be silently
  skipped (`AModuleNodeValidatorIsNeverBuiltForNoCallerTest` went red on exactly that;
  `MeshServiceProviderTest` pins it).
- **A factory that returns null is refused**, never answered as "absent" — as before.

## Threads, cycles and faults

Registrations are frozen when a level is built and read without locks. A shared instance is built at
most once, under **its own slot's lock** — taken synchronously, never awaited, and never covering more
than that one instance. Two first builds therefore wait for each other only when one really needs the
other's instance; the scope-wide lock of the container this replaced is what made the root↔module and
caller↔module lock-order deadlocks possible (MeshWeaver#6432, #6542, #6584), and its absence is pinned
by `RootAndModuleSingletonsBuildInOneLockOrderTest`, `AScopedRootServiceIsNeverBuiltInTheRootTest` and
`CallerAndModuleScopedServicesBuildInOneLockTest`, each forcing the formerly deadlocking interleaving
and asserting that both constructions were inside their builds at once.

A cycle is refused by name: through constructors by the resolve chain, through a factory where the
same thread re-enters a shared instance it is building. A fault is **not cached**: the next resolve
builds afresh, so a transient construction fault never becomes a permanent one (which is why a slot is
a lock and a flag, not a `Lazy<T>` — a `Lazy` in `ExecutionAndPublication` mode caches the exception).

## Ownership and disposal

A scope disposes only what it built, last first, when it ends — never a provided instance, a
`NotOwnedServiceDescriptor` forwarder's result, or a source's stand-in. `Ending` is raised first, after
the scope is already marked disposed, so from that instant every resolve throws
`ObjectDisposedException` (object name `MeshWeaver.ServiceScope`; the platform classifies it through
`HubDisposingException.IsDisposedContainer`). An instance that is only asynchronously disposable is
disposed asynchronously by a synchronous `Dispose` — never blocked on; its fault is logged. A parent
does not track its children: a scope nobody closes is garbage once nobody holds it.

A level's cost is therefore its own descriptors (one small registration object each, in a frozen
lookup) and a slot per instance it built — no per-hub copy of anything a parent registered. The
container keeps **no process-static cache**: an activation plan lives on the registration it belongs
to, so there is nothing to evict when a collectible context unloads (the hand-maintained eviction of
the container it replaced is gone).

## Deliberate differences from the standard provider

The standard `Microsoft.Extensions.DependencyInjection.Specification.Tests` suite, run against this
container directly, through an empty level, and with every registration on a level, passes except:

- **Scopes are hierarchical, not flat** (`ScopesAreFlatNotHierarchical`, `ServiceScopeFactoryIsSingleton`):
  the scope factory a scope hands out begins a CHILD of that scope, because a hub's child scopes must
  see the hub's registrations.
- **A level does not own the root's singletons** (`DisposingScopeDisposesService` and
  `DisposesInReverseOrderOfCreation` through a level): disposing a hub's level leaves the root's
  singletons to the root.
- **`GetKeyedServices(KeyedService.AnyKey)` is enumerated afresh on each call**, not served as the
  same enumerable instance.

## Measured

See [The Portal Heap Is Hubs](../PortalHeapIsHubs) → *Per-hub DI and type metadata* for the 516-hub
test-mesh measurement before and after the replacement.

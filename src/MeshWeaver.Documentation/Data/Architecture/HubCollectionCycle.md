---
Name: A Hub Collection That Leads Back To Itself
Category: Architecture
Description: "A hub-backed content collection whose remote answer is another hub-backed config naming a hop already asked used to resolve forever, one nested SelectMany per round, until one terminal signal overflowed the stack. The cycle is now refused by name on the hop that closes it."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3v6h-6"/></svg>
---

# A hub collection that leads back to itself

A content collection with `SourceType = "Hub"` has no files of its own. `HubStreamProviderFactory`
asks the hub at its `Address` for the collection's config (`GetDataRequest(ContentCollectionReference)`)
and builds the provider the answer names. The answer can be another `Hub` config, and the factory
resolves that one the same way.

## 🚨 The one sentence

**If the chain of answers ever names a hop it has already asked, there is no answer, and the
resolution is refused with `HubCollectionCycleException`, which lists the hops.**

## How a cycle forms

The commonest shape needs no misconfiguration in any single place:

1. A node has no collection called `exports` of its own.
2. Its content service falls back to its parent's (`ContentService.GetCollectionConfig`).
3. The parent holds a `Hub` config called `exports` naming that node. A portal's `/content` page
   registers exactly that when someone opens the node's `exports` collection.
4. Asked for `exports`, the node answers with the parent's config, `Hub` at itself.

## Why it crashed a silo instead of failing a read

Each round is one more asynchronous request, so nothing fails while the loop runs and no stack
grows. Each round also nests one more `SelectMany` inner observer, because round *n + 1* is the
inner observable of round *n*. When a terminal signal finally arrives, it travels through every
level on one stack.

On a memex portal pod on 2026-10-30, that signal was the `ShuttingDown` answer of an owner that
was quiescing (see [Riding Out a Shutting-Down Address](../RidingOutAShuttingDownAddress)). It
travelled through **130,840** nested `SelectMany+InnerObserver.OnError` frames, and the process
died of a `StackOverflowException`. The crash dump then froze the process for about 42 s. Orleans'
`LocalSiloHealthMonitor` logged that freeze as `.NET Thread Pool execution stalled for 42.2s`
([MeshWeaver#5388](https://github.com/Systemorph/MeshWeaver/issues/5388)).

## Reading a stall that is a crash

The liveness heartbeat (`[LIVENESS]`) runs on its own thread. When it overran by about the stall's
length and almost none of that was GC pause, the whole process was suspended. Look for `createdump`
and `Stack overflow.` lines in the 30 s before the stall, and for the runtime's
`Repeated N times:` block, which names the recursing frame. Neither the thread pool nor the GC
caused it.

## The fix and its proof

`HubStreamProviderFactory` carries the hops a resolution has asked, each as an (address,
collection) pair. A `Hub` answer is resolved on the same chain, and an answer that repeats a hop
errors on that hop. The two fields are compared exactly as the mesh compares them: `Address`
equality, and ordinal collection names, because the content service's lookup is case-sensitive.
A single `{address}/{collection}` string would refuse two legitimate chains. One is `Exports` →
`exports` on the same node. The other is a slash-qualified collection name, where `host/1` +
`folder/exports` and `host/1/folder` + `exports` would print alike.

`HubCollectionCycleTest` pins the refusal and two controls, and each test guards the others:

- a self-referential inherited `Hub` config errors with the chain
  `[host/1 [inherited], host/1 [inherited]]`;
- a `Hub` config the host serves from its own FileSystem collection still resolves;
- a `Hub` config named `Served` that redirects to `served` on the same host still resolves.

## What the dump proves

A governed `AnalyzeDump` of the 2026-10-30 dsfp6 dump ties the overflow to this factory.
`dumpheap -stat` held about 2.42 million instances of each of these types:

- `HubStreamProviderFactory+<>c__DisplayClass3_0`, the closure of the `SelectMany` selector in
  `Create`;
- `SelectMany<IMessageDelivery<GetDataResponse>, IStreamProvider>+ObservableSelector+_` and its
  `InnerObserver`;
- the matching `Func<…>`, `GetDataRequest` and `ContentCollectionReference`.

Those type arguments and that closure class belong only to `HubStreamProviderFactory.Create`. The
stack frames print generic arguments as `__Canon`, so they could not name the factory; the heap
statistics do.

## Not established

- Which config made `exports` on that node resolve back to itself. The node has since gone. The
  test reproduces one route, an inherited parent `Hub` config like the one a `/content` page
  registers.
- Whether any other reactive loop in the platform recurses through `SelectMany` in the same way.
  A static rule for it does not exist yet.

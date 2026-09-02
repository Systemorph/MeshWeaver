---
Name: Update Validators See Typed Content
Category: Architecture
Description: An update validator compares the existing node with the proposed one, so the pipeline owes it both sides typed alike. How a hub used to learn content types by accident, how #3056 removed that accident, and why the update pipeline now types the existing snapshot off the proposal's own CLR type.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="m9 12 2 2 4-4"/></svg>
---

# Update Validators See Typed Content

**An `INodeValidator` for `Update` compares `context.ExistingNode` with `context.Node`. The
pipeline that calls it — `NodeUpdatePipeline` behind `IMeshService.UpdateNode` — owes it BOTH
sides with content of the same CLR type.** A validator that finds a `JsonElement` on one side and a
typed record on the other skips its own comparison and answers Valid, and a write that should have
been refused lands with nothing logged as an error. This page records how that happened once, and
where the guarantee now lives.

## How a hub learned content types by accident

Every hub carries a `TypeRegistry` behind its `JsonSerializerOptions`; `$type` discriminators are
resolved against it. The documented way to teach a hub a content type is `WithType(typeof(T), …)`.
There was, until 2026-09-02, a second, undocumented way: `MessageService.Post` rendered every
delivery through the logging options — eagerly, as a method argument, whether or not Debug logging
was on — and `ObjectPolymorphicConverter.Write` calls `typeRegistry.GetOrAddType(valueType)` for
every value it serialises. So a hub that merely handed a typed instance to `CreateNode` had, by the
time the request left, registered that instance's type in its own registry. Reading the node back
on the same hub then re-typed it. Nothing declared that dependency; everything that did not call
`WithType` relied on it.

Core #3056 removed the eager render (it was the allocation that threw `OutOfMemoryException` on a
production pod), and with it the accidental registration. The very next Plugins run against core
`main` failed `NodeOperationsWithUpdateValidatorTest.UpdateNode_VersionDowngrade_ShouldFail`: the
test hub never registered `UpdatableContent`, `MeshNodeStreamCache.GetStream` logged
`Content for … stayed an untyped JsonElement after deserialization`, the validator's
`ExistingNode.Content is UpdatableContent` was false, and the downgrade went through. The bisect is
exact — `804b9beca` (#3059) passes, `3d6faa284` (#3056) fails — and the warning line is the
mechanism, not a guess.

## Where the guarantee lives now

`NodeUpdatePipeline` reads the existing node through the stream cache (typed with the running hub's
options) and, before building the `NodeValidationContext`, types its content **like the proposal**:
the proposed node carries a live CLR instance of exactly the type the existing content must have
(same node, same NodeType), and the hub running the validators demonstrably holds that type. The
degraded snapshot is deserialised as `node.Content.GetType()` through the runtime-`Type` overload
of `As` (`ObjectAsExtensions.As(object, Type, options, …)`) — a concrete-type deserialisation, which
needs no registry entry for the discriminator. It is the same recovery `ContentAs<T>` performs at a
consumer, done once for every validator instead of being each one's problem.

A snapshot that will not deserialise as the proposal's type is left as it was and logged at Error by
`As`: hiding a shape mismatch from the validators would be a second silent pass.

`UpdateValidatorSeesTypedExistingContentTest` (MeshWeaver.Graph.Test) pins the contract with a
content type registered on no hub, records the CLR type the validator actually saw, and asserts the
refusal. It exists in core because the test that found the hole runs only in MeshWeaver.Plugins —
the cross-repo blind spot that let #3056 merge green.

## What this does not change

- A validator should still read payloads through `.As<T>()` / `.ContentAs<T>()` rather than a cast
  (see [CQRS and content access](../CqrsAndContentAccess)); the pipeline guarantee makes the fleet's
  existing validators safe, it does not make the cast idiom right.
- A test fixture that posts a custom content type should still register it with `WithType` on the
  hub that reads it back. The Plugins fixture above does not, and should.
- No registry is mutated on the read path. `As` deliberately deserialises to the concrete type
  without adopting it, so a same-named type from another collectible assembly cannot poison the
  running hub's `$type` resolution.

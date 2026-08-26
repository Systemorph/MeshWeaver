---
Name: A module type that was never baked now says so, instead of compiling in secret
Category: Fix
Description: On a mesh that requires prebuilt assemblies, opening a module type whose build never landed no longer triggers a silent on-mesh compile — the type parks with a clear message naming the missing bundle, the framework lane and the fix, and every page of that type shows it.
Icon: ShieldError
Order: -20260825
---

# A module type that was never baked now says so, instead of compiling in secret

`Modules:RequirePrebuilt` already refused to compile when a package's bundle could not be fetched
at install time. The deeper case — a type that is simply *touched* later, on first access, with no
adopted assembly — still slipped into a Roslyn compile, because that path runs through the
type's own compile watcher rather than the install lane.

It no longer does. On a require-prebuilt mesh, any trigger that would compile such a type — first
access, a release request, a self-heal, a framework-stale rebuild — parks it instead, with one
message: which type, which framework identity and architecture the assembly is missing for, which
package's bundle publishes it, and how to retry once it lands. Every page of that type renders the
reason through the usual compilation-error overlay instead of waiting on a compile that was never
going to be allowed, and the park keeps the refusal bounded — no storm, no retry loop, zero
compiles started.

Meshes that do not set the flag are unchanged: they compile exactly as before.

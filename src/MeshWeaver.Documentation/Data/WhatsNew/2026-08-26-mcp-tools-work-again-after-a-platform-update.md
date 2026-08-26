---
Name: MCP tools work again after a platform update
Category: Fix
Description: Every MCP tool call from an external client — get, search, create, update, render_area and the rest — failed with a type-loading error after the last platform update, because a piece of the platform the MCP module needs had been filed under a new name. The old name is restored and redirected, so already-installed modules keep working across updates, and a new check refuses the next change of this shape.
Icon: PlugConnected
Order: -20260826
---

# MCP tools work again after a platform update

The MCP surface is how external clients — Claude Code, the Copilot harness, any MCP-speaking tool —
reach a deployment's mesh. After the platform updated on 26 August, every one of those calls failed
the moment it arrived:

```
System.TypeLoadException: Could not load type 'MeshWeaver.AI.MeshOperations'
    from assembly 'MeshWeaver.AI, Version=3.0.0.0, …'
```

Not one tool: **all** of them. The MCP server builds its tool target freshly for each invocation, so
the failure sat in a constructor that every single call has to run — `get`, `search`, `create`,
`update`, `render_area`, the diagnostics and chunk tools alike. Nothing was wrong with the requests,
and nothing was recoverable from the client side.

## What happened

The MCP server ships as an installed module: a compiled add-on, delivered separately from the
platform and deliberately allowed to keep working across ordinary platform updates. That promise
holds as long as the platform keeps the *names* the module was built against.

A tidy-up moved the shared mesh-operations code into its own component and renamed it on the way.
Nothing in the source noticed — the module still compiled perfectly against the new platform. But the
copy of the module already installed on the deployment was compiled earlier, and it looks the code up
by its old name. The name was gone, so the lookup failed, and it failed on every call.

## What changed

The old names are restored and formally redirected to the code's new home, so a module built against
either the old or the new platform finds it. The redirect is the mechanism .NET provides for exactly
this — one piece of code, reachable under both names, never a copy that could drift.

Two guards now stop it happening again:

- a test that fails the build if any of those names stops resolving, or drifts;
- a repository check that refuses any change moving a shared, publicly usable piece of the platform
  to a new name without leaving the redirect behind — the missing half of an existing check that
  already covers a related shape.

The pre-existing check that compiles installed modules against each change could not catch this, and
still cannot by construction: it proves the module's *source* still compiles, while the module a
deployment is actually running was compiled weeks earlier.

## What you will notice

MCP tools work again, and installed modules keep working across platform updates rather than needing
to be rebuilt in lockstep.

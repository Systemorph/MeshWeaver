---
Name: Platform and content — two layers, two cadences
Category: Architecture
Description: The distinction the rest of the architecture rests on. The platform is a harness that makes code ADDRESSABLE and routes a mesh address to the code that answers it; content is the code and data it routes to. They deploy independently, and content iterates far faster than the platform underneath it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="7" rx="2"/><rect x="3" y="14" width="18" height="7" rx="2"/><path d="M12 10v4"/></svg>
---

# Platform and content — two layers, two cadences

MeshWeaver is **two independently deployable layers**. Almost every architectural question has a
different answer depending on which one it is about, and most confusion about the framework — what
gets rebuilt, what needs a release, what a customer owns, why a change is live without a deploy —
dissolves once the split is explicit.

## The platform makes code addressable

The platform layer does one thing above all: it makes code **addressable**. Everything in the mesh
has an address, and the platform's job is to route a request for that address to the code that
answers it — activating the hub that owns it, feeding it exactly the context it needs, enforcing who
may see it, and versioning what it writes.

That is the whole of it. The platform is a **harness**, not an application:

| The platform provides | It does not provide |
|---|---|
| addressing and the message hub | your data model |
| the data mesh — versioning, audit, point-in-time restore | your business rules |
| the agent harness — tools, context, threads | your agents' instructions |
| the UI runtime — reactive layout areas | your screens |
| security — SSO, partition-scoped access control | who may see *your* partitions |

It ships as **one versioned container image**, it is Apache-2.0 / MIT, and it changes rarely
relative to what runs on it.

## Content is what the harness routes to

The content layer is the code and data the platform routes *to*: node types and their `Source/`,
business rules, layout areas, agents and skills, seed data. It lives in a **git repo of node
repos**, it is **compiled by the mesh on import** (see
[Node Type Compilation](/Doc/Architecture/NodeTypeCompilation)), and it needs no platform rebuild
and no NuGet package — see [Plugins](/Doc/Architecture/Plugins).

Because of that, it is iterated **far faster than the platform underneath it**: daily during a build
phase, and thereafter by whoever owns the domain rather than by whoever owns the framework.

<div style='border:1px solid #dbe4ee;border-radius:10px;padding:18px 20px;margin:20px 0'>
<svg class="mw-figure" viewBox="0 0 900 452" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;display:block" role="img" aria-label="Two layers: content modules deployed from Git and iterated daily, sitting on a platform harness deployed as a versioned image and changed rarely; the harness routes a mesh address to the code that answers it.">
<style>@media (prefers-color-scheme: dark){svg.mw-figure{filter:invert(1) hue-rotate(180deg)}} :root[data-theme="dark"] svg.mw-figure{filter:invert(1) hue-rotate(180deg)} :root[data-theme="light"] svg.mw-figure{filter:none}</style>
<rect x="0" y="0" width="900" height="452" rx="8" fill="#ffffff"/>
<rect x="176" y="26" width="706" height="132" rx="10" fill="#eef2ff" stroke="#c7d2fe"/>
<text x="192" y="50" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="13" font-weight="700" letter-spacing="1.6" fill="#4338ca">CONTENT — WHAT THE DOMAIN OWNER CHANGES</text>
<rect x="192" y="62" width="158" height="52" rx="7" fill="#ffffff" stroke="#c7d2fe"/>
<text x="271" y="85" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="13" font-weight="600" fill="#312e81">Node types</text>
<text x="271" y="102" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#6366f1">model + Source/</text>
<rect x="362" y="62" width="158" height="52" rx="7" fill="#ffffff" stroke="#c7d2fe"/>
<text x="441" y="85" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="13" font-weight="600" fill="#312e81">Business rules</text>
<text x="441" y="102" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#6366f1">scopes</text>
<rect x="532" y="62" width="158" height="52" rx="7" fill="#ffffff" stroke="#c7d2fe"/>
<text x="611" y="85" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="13" font-weight="600" fill="#312e81">Layout areas</text>
<text x="611" y="102" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#6366f1">the screens</text>
<rect x="702" y="62" width="164" height="52" rx="7" fill="#ffffff" stroke="#c7d2fe"/>
<text x="784" y="85" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="13" font-weight="600" fill="#312e81">Agents &amp; skills</text>
<text x="784" y="102" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#6366f1">instructions</text>
<text x="192" y="140" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12.5" fill="#4f46e5">Git repo of node repos · compiled live by the mesh on import · no platform rebuild, no NuGet</text>
<rect x="176" y="186" width="706" height="76" rx="10" fill="#f8fafc" stroke="#2563eb" stroke-dasharray="5 4"/>
<text x="529" y="212" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="13.5" font-weight="700" fill="#1d4ed8">THE HARNESS: an address resolves to the code that answers it</text>
<text x="529" y="234" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12.5" fill="#475569">activate · route · feed context · enforce access · version every write</text>
<text x="529" y="252" text-anchor="middle" font-family="ui-monospace,SFMono-Regular,Menlo,monospace" font-size="12" fill="#2563eb">Acme/Submission/2026-0417  →  the code, the rules and the view that serve it</text>
<path d="M529 158 L529 184" stroke="#2563eb" stroke-width="2" fill="none" marker-end="url(#arL)"/>
<path d="M529 288 L529 264" stroke="#2563eb" stroke-width="2" fill="none" marker-end="url(#arL)"/>
<rect x="176" y="290" width="706" height="132" rx="10" fill="#f1f5f9" stroke="#cbd5e1"/>
<text x="192" y="314" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="13" font-weight="700" letter-spacing="1.6" fill="#334155">PLATFORM — THE MESHWEAVER HARNESS</text>
<rect x="192" y="326" width="128" height="52" rx="7" fill="#ffffff" stroke="#cbd5e1"/>
<text x="256" y="349" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12.5" font-weight="600" fill="#0f172a">Addressing</text>
<text x="256" y="366" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#64748b">message hub</text>
<rect x="332" y="326" width="128" height="52" rx="7" fill="#ffffff" stroke="#cbd5e1"/>
<text x="396" y="349" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12.5" font-weight="600" fill="#0f172a">Data mesh</text>
<text x="396" y="366" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#64748b">versions · audit</text>
<rect x="472" y="326" width="128" height="52" rx="7" fill="#ffffff" stroke="#cbd5e1"/>
<text x="536" y="349" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12.5" font-weight="600" fill="#0f172a">Agent harness</text>
<text x="536" y="366" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#64748b">tools · context</text>
<rect x="612" y="326" width="128" height="52" rx="7" fill="#ffffff" stroke="#cbd5e1"/>
<text x="676" y="349" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12.5" font-weight="600" fill="#0f172a">UI runtime</text>
<text x="676" y="366" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#64748b">reactive areas</text>
<rect x="752" y="326" width="114" height="52" rx="7" fill="#ffffff" stroke="#cbd5e1"/>
<text x="809" y="349" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12.5" font-weight="600" fill="#0f172a">Security</text>
<text x="809" y="366" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#64748b">SSO · RBAC</text>
<text x="192" y="404" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12.5" fill="#475569">One versioned container image · Apache-2.0 / MIT · rolled on the deployment's update policy</text>
<rect x="18" y="26" width="132" height="132" rx="10" fill="#ffffff" stroke="#c7d2fe"/>
<text x="84" y="60" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12" font-weight="700" letter-spacing="1.2" fill="#4338ca">ITERATION</text>
<text x="84" y="94" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="22" font-weight="700" fill="#4338ca">daily</text>
<text x="84" y="118" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#6366f1">by whoever owns</text>
<text x="84" y="134" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#6366f1">the domain</text>
<rect x="18" y="290" width="132" height="132" rx="10" fill="#ffffff" stroke="#cbd5e1"/>
<text x="84" y="324" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="12" font-weight="700" letter-spacing="1.2" fill="#334155">ITERATION</text>
<text x="84" y="358" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="22" font-weight="700" fill="#334155">rarely</text>
<text x="84" y="382" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#64748b">a platform roll,</text>
<text x="84" y="398" text-anchor="middle" font-family="system-ui,-apple-system,Segoe UI,sans-serif" font-size="11.5" fill="#64748b">on a chosen policy</text>
<defs><marker id="arL" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#2563eb"/></marker></defs>
</svg>
</div>

## What follows from the split

**They deploy independently, and by different mechanisms.** Content reaches an instance by GitSync
from its repo plus a recompile; the platform reaches it as an **image roll**. Neither blocks the
other, and the two failure modes are different — which is why
[Deploying a plugin change](/Doc/Architecture/DeployingPluginChanges) exists as its own page and
opens with *"merging is not shipping"*.

**A module is the third thing, and it belongs to the platform side.** A compiled assembly a
deployment turns on by listing it ([Modules](/Doc/Architecture/Modules)) travels with the image and
activates at boot, so it moves at the platform's cadence, not the content's. A *package* may carry
both — content nodes and a module — which is why the store install can require a restart while a
pure-content install never does.

**The cadence gap is the point, not an accident.** Domain logic that must be changed by the people
who understand it cannot sit behind a framework release train. The two-layer split is what lets an
appetite rule, a price, or a screen change in an afternoon while the runtime beneath it is upgraded
deliberately, on a schedule someone else controls.

**Ownership divides on the same line.** The platform is open source and replaceable in principle;
the content is written against its programming model and is where the domain work actually
accumulates. When a deployment asks "what do we own", the honest answer is drawn here: the data and
the rules are portable, the layer that renders and routes them is not.

## Where the boundary is easy to get wrong

- **Putting domain logic in a module.** It compiles and it works, and it has just moved that logic
  onto the platform's release cadence — every change now needs an image. If a domain expert should
  be able to change it, it belongs in content.
- **Reaching for a platform change to serve one deployment.** The extensibility hooks
  ([Extensible Defaults](/Doc/Architecture/ExtensibleDefaults),
  [UI Extensibility](/Doc/Architecture/UiExtensibility)) exist so that content can specialise
  behaviour without the harness learning about a specific domain.
- **Assuming a merged content change is live.** It is not; the mesh serves what it last imported and
  compiled.

## See also

- [Plugins](/Doc/Architecture/Plugins) — content as a repo of mesh nodes
- [Modules](/Doc/Architecture/Modules) — the compiled-assembly lane
- [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) — how in-mesh `Source/` is compiled
- [Deploying a plugin change](/Doc/Architecture/DeployingPluginChanges) — the mesh-side tail
- [Plugin Packaging](/Doc/Architecture/PluginPackaging) — how a package carries content, a module, or both

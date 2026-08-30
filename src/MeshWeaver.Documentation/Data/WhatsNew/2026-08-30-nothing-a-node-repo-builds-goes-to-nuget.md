---
Name: Nothing a node repository builds goes to NuGet
Category: Fix
Description: The lane that compiled every plugin's in-mesh source against a NuGet "floor" — and had been red since the AI module left the platform, because that floor stopped at rc7 — is retired. In-mesh source is checked against the portal image it runs in, and plugins reach consumers as bundles from a sealed publication, never from a package feed.
Icon: PackageDismiss
Order: -20260830
---

# Nothing a node repository builds goes to NuGet

Every node repository carries C# that compiles inside the portal at runtime. One lane checked that
source against the newest **released NuGet packages** — the "framework floor" — and packed a
`.nupkg` per plugin that nothing consumed. When the AI and collaboration modules moved out of the
platform repository their NuGet packages stopped at rc7, the floor froze there, and every plugin
using an rc8 type failed to pack. The proposed cure was a NuGet publish token.

That lane is retired instead. In-mesh source runs *inside the portal image*, so the only honest
reference set is that image's assemblies plus the modules its publication was sealed against —
which is exactly what the compile check and the gates already use. Consumers fetch bundles assembled
from a sealed publication, never packages from a feed, so no repository's CI needs a NuGet
credential and none can publish there by accident. The build-side tool keeps its two module verbs
(`module-pack`, `module-fetch`) and nothing else.

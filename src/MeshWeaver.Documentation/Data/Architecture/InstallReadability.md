---
Name: Install Readability
Category: Architecture
Description: What makes an installed partition READABLE — the two doors an install can open, the cover-grant deadlock detector that watches the second one, and the rule that an install must never block on a node it knows is optional.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/></svg>
---

# Install Readability

Writing a package's nodes is only half an install. A partition nobody may read is not installed in
any useful sense — and the two failure modes are *silent* in opposite directions: an install that
returns too early reports success over a partition that denies every viewer, and an install that
waits for the wrong proof burns wall-clock on every package that was never going to produce it.

Both were live at once in `PackageInstaller`. This page is the model that resolves them.

## Two doors, and only two

An installed partition becomes readable through exactly one of two mechanisms.

| Door | Who opens it | Proof it opened |
|---|---|---|
| **Declared access** — core's own write, driven by the manifest | `PackageInstaller.EnsureDeclaredAccess` | the marker node it wrote, re-read by `VerifyDeclaredAccess` |
| **A gating pass** — the plugin-side machinery (`AddPluginGating` / `PluginGate`) | the node type, on hub activation | the **cover grant** at `{partition}/_Access/Public_Access` |

`EnsureDeclaredAccess` is the ONE access-establishment step of an install, and its shape is decided
by the manifest, not by the node type:

- **pre-installed** (platform baseline) → `{partition}/_Policy · PublicRead = true`;
- **free**, no declared `publicSegments` → the same fully-public policy;
- **free with `publicSegments`** → root `Public`/`Anonymous` Viewer grants (the root grant *is* the
  cover grant) plus per-child denies outside the declaration;
- **commercial** — any non-zero price, or a `contactEmail` (sold contact-sales) → **nothing at all**.
  The partition lands gated on purpose: entitlement is the only way in.

So the second door matters in exactly one shape. For every non-commercial package core has already
opened the first door itself and verified it; for a commercial one core has deliberately opened
neither, and the gating pass's cover grant is the single observable proof that anything can be read
— including the `Subscribe` cover that would sell the package.

## The cover grant is a PATH, not a question you can ask

`Store/Plugin`, `PluginContent` and `AddPluginGating` live in
[MeshWeaver.Plugins](/Doc/Architecture/Plugins), compiled live on the mesh. Core cannot reference them, so it cannot
ask a node type whether it gates. That is why the grant is addressed as a well-known path
(`PackageInstaller.CoverGrantPath`) and why `InstallGatingHandshakeTest` pins that literal against
the one `PluginGate` actually writes: the path is a contract between two repositories, and a
contract nothing checks is one that drifts.

The consequence people get wrong: **"core cannot tell whether this type gates" does not mean core
has no honest discriminator.** It has its own decision. `DeclaredAccessMarker` names the node
`EnsureDeclaredAccess` wrote and is `null` exactly when it wrote nothing — which is precisely the
commercial branch. `PackageInstaller.CoverGrantExpected` is that, and nothing more.

## The detector, and its budget

After warming each installed root (activation is what triggers the gating pass), the installer
watches for the cover grant on the roots that are owed one:

- **it lands** → Information: the partition is gated *and* readable;
- **the budget expires** → **Warning**, naming the missing path and the consequence (every viewer
  denied, including on the cover that sells the package) — never an exception, because the content
  is already committed and failing the install would trade an unreadable partition for no partition
  at all;
- **nothing is owed** → Debug, and no wait at all.

The watch is a `path:{grant}` **query**, never an exact-path `GetMeshNodeStream`. An exact-path
stream read on an *absent* node does not wait: the owner answers an authoritative routing NotFound
that terminates the stream and opens `MeshNodeStreamCache`'s storm-breaker window on the path — and
the breaker fast-fails writes too, so the wait once **suppressed the very write it was waiting for**
(MeshWeaver#2229). A query is empty-on-absent and live. See
[CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) → "An OPTIONAL node".

### 🚨 Never block on a node you have documented as optional

The detector's budget was 30 seconds, and it ran on **every** in-package-typed root while its own
doc comment called the missing grant normal — "a partition whose node type does not gate never
writes it". On the shape the code itself called normal the query could never emit, so every such
install paid the entire budget and then reported success. The only trace was one Information line
worded to cover both outcomes, which is why review and monitoring both missed it: the healthy case
and the wedged case printed the same sentence.

Measured in `MeshWeaver.PluginCatalog.Test`, before the fix:

| Test | Installs | Duration |
|---|---|---|
| `SelfTypedRootInstallTest.SelfTypedRoot_ReinstallImmediately_WritesNothing` | 2 | **60.28 s** |
| `StaleStampRootBindingTest.StaleStampSelfTypedRoot_RootServesItsTypesArea` | 1 | 30.33 s |
| `StaleStampRootBindingTest.StaleStampSelfTypedRootShippingContent_RootServesItsTypesArea` | 1 | 30.29 s |
| `SelfTypedRootInstallTest.RootTypedByAnInPackageNodeType_Installs` | 1 | 30.25 s |
| `InstallReleaseOrderingTest.DeferredNodeTypeReleases_AreRequestedAfterTheRootRecycle` | 1 | 30.24 s |

One install, one 30 s stall — 181 s of a 336 s suite. `PackageInstaller.Install` is the production
install path, so a live mesh paid the same 30 seconds per non-gating package.

The fix is two changes that only work together:

1. **Do not wait for what is not owed.** `CoverGrantExpected` gates the watch, so the case that can
   never succeed is not watched at all (0 ms, measured).
2. **Make the remaining wait a DETECTOR, not a settle barrier** — budget 5 s, and its expiry
   reported at Warning rather than folded into the healthy line. When a gating pass really is
   running, the grant is observable in **44–55 ms** end-to-end over repeated runs (`GatingDetectorTest`
   measures the real write→observe latency on a live mesh), so 5 s is a hundred-fold margin over the only thing left
   outstanding at that point: one access-table write on an already-warm hub.

The trade is deliberate and named: a gating pass slower than 5 s is now *reported* rather than
waited out. Nothing relies on the waiting — the phase's result is discarded and roots typed outside
the package were always skipped — so the step already could not change what is installed or who can
read it. What it can do is say so, loudly.

### Why Warning and not Error

The budget's expiry proves the grant is not there *yet*, not that it never will be.
`VerifyDeclaredAccess` owns the Error-level verdict, because that one is about access **core itself
promised to write** and therefore knows must exist. Keeping the two levels apart is what makes
either of them worth alerting on.

## Testing this

`GatingDetectorTest` pins all three outcomes — nothing owed (instant), the grant lands (ms), the
grant never comes (bounded and loud) — and deliberately mirrors the budget rather than importing the
production constant, so a test that read the constant could not pass at 30 s the way the old code
did. A detector nobody exercises is a detector nobody knows is broken; that is exactly how this one
came to report the wedged case and the normal case with the same line at the same level.

## See also

- [Plugins](/Doc/Architecture/Plugins) — the node-repo plugin model and where `Store/Plugin` actually lives
- [Granting Access](/Doc/Architecture/GrantingAccess) · [Access Control](/Doc/Architecture/AccessControl)
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — reading a node that may not exist

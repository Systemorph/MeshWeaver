---
Name: React Testing & Parity Scorecard
Category: Documentation
Description: How React↔Blazor parity is enforced — the parity ratchet test, the vitest suites, the transport round-trip test, and the opt-in ReactDocViewsTest E2E scorecard that renders every doc example through the React portal.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# React Testing & Parity Scorecard

Parity with the Blazor portal is enforced by tests at three altitudes: the **registry** (does the React pack cover the Blazor control vocabulary?), the **renderer** (do the components actually render and bind?), and the **portal end-to-end** (do real documentation pages render through the React SPA against a live mesh?).

## 1. The parity ratchet — `render/parity.test.ts`

`clients/react/src/render/parity.test.ts` pins the authoritative Blazor vocabulary: the `*Control` / `*Skin` types in `src/MeshWeaver.Layout` (the `$type` is the class name minus the suffix). When a new control is added to Layout, its `$type` is added to the list — and the test fails until the React pack covers it, keeping the port at 1:1:

- **`BLAZOR_LEAF_CONTROLS`** — all 52 leaf `$type`s must exist in `controlRegistry`, must be functions (React components, not accidental values), and coverage must be exactly 52/52 — no silent shortfall.
- **`BLAZOR_SKINS`** — the 18 container/item skins must exist in `skinRegistry` (containers like Stack/Tabs/Toolbar render via *skins*, not the control map).
- **The placeholder ratchet** — the registered-but-placeholder long-tail (`placeholderControlTypes` in `controls/mesh.tsx`) is pinned exactly, and it is now **empty**: every mesh control (`DocumentSource`, `ExportDocument`, `FileBrowser`, `NodeExport`, `NodeImport` — the last controls that used to render a bare badge) has a real implementation.

```ts
it("the placeholder long-tail only ever shrinks", () => {
  const pinned: string[] = [];
  expect([...placeholderControlTypes].sort()).toEqual(pinned.sort());
});
```

Adding a *new* placeholder fails here — every new control must ship a real implementation. A companion test (`the un-placeholdered controls are all real, distinct components`) further asserts those five are real, distinct React components, so none may regress to a shared badge.

## 2. The vitest suites

```bash
cd clients/react && npm test        # renderer + controls
cd clients/grpc-web && npm test     # transport + thread submission
```

In `clients/react/src`:

| Suite | Covers |
|---|---|
| `area/pointer.test.ts`, `area/source.test.ts` | JSON pointer resolution, RFC 7396 merge-patch, the `StaticAreaSource` contract (optimistic updates) |
| `render/core.test.tsx` | The renderer core in jsdom: `$type` dispatch, skin popping, binding resolution, event emission |
| `render/parity.test.ts` | The ratchet above |
| `render/gallery.test.tsx` | Rendering the control gallery |
| `controls/dialog.test.tsx`, `controls/itemTemplate.test.tsx`, `controls/threadChat.test.tsx` | Per-control behavior — the chat suite covers thread watching, queued bubbles, composer gating, submission |
| `theme/theme.test.tsx` | The Blazor-compatible localStorage contract, mode resolution, cross-instance sync |
| `live/grpcSource.test.ts` | Folding the live wire: `Full` snapshots, RFC 6902 patch arrays, JSON-encoded instance keys, event posting |

In `clients/grpc-web/src`: `envelope.test.ts` (delivery JSON build/parse), `connection.test.ts` (the Connect+Deliver split, ack, demux), `mesh.test.ts` (the ops surface), `threads.test.ts` (the `startThread` / `submitMessage` wire shapes and guards).

The server side of the split has its own C# round-trip test: `MeshGrpcTransportTest.WebSplit_request_round_trips_via_connect_and_deliver` (`test/MeshWeaver.Hosting.Grpc.Test`) proves a request posted through the unary `Deliver` comes back down the server-streaming `Connect`.

## 3. The E2E scorecard — `ReactDocViewsTest`

`test/MeshWeaver.Portal.E2E.Test/ReactDocViewsTest.cs` is the **doc-views-through-React scorecard**: it drives Playwright at `{E2E_BASE_URL}/app/#/{docPath}` for every documentation page with embedded interactive examples — the same page list the Blazor-side `DocExamplesRenderTest` covers — and asserts each page reaches real rendered content through the React SPA over live gRPC-web.

- **Opt-in**: the whole class skips unless `E2E_REACT=true`. The React renderer is *expected* to fail on some of these pages while parity catches up — the suite measures the gap rather than gating CI.
- **Auth**: the SPA mints its short-lived API token off the fixture's DevLogin session cookie (`POST /api/tokens`) — the test context needs nothing beyond what the Blazor E2E tests use.
- **Per page**, three assertions:
  1. the live shell engages (`[data-mw-live-area]` attached — the SPA connected and routed the doc path);
  2. **no failure placeholders**: `[data-mw-offline]` (fell back to bundled sample data — a wiring defect, not a parity gap) and `[data-mw-area-error]` (the live layout-area stream faulted) must be absent, re-checked *after* content settles so a late stream fault fails too;
  3. **real content**: the page's kernel-executed output marker becomes visible (e.g. `"live from the kernel"` on `Doc/GUI`), or — for pages without a marker — the area renders something beyond the "Building layout…" progress frame.
- Each page is screenshotted to `/tmp/react-doc-views/` — the visual scorecard.

One Playwright detail worth knowing when writing similar tests: never wait for `NetworkIdle` against the SPA — it holds the gRPC-web `Connect` stream open for its whole life, so the network never goes idle.

```bash
E2E_REACT=true dotnet test test/MeshWeaver.Portal.E2E.Test \
  --filter "FullyQualifiedName~ReactDocViewsTest" --no-restore
```

## Related

- [React Frontend overview](/Doc/GUI/React) — the current parity state these tests enforce.
- [Getting Started](../GettingStarted) — how the SPA under test is served and connected.
- [Writing Tests](/Doc/Architecture/WritingTests) — the repo-wide testing standards.

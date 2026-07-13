---
Name: Disposable-mesh e2e testing
Category: Architecture
Description: The canonical shape for every MeshWeaver e2e suite — boot a disposable instance from the platform image (ACR/GHCR), dev-login a self-provisioned admin, install plugins + content through the real Plugin Catalog fed by a stub registry, then drive Playwright against the product. Covers the compose harness, DevLogin, the stub registry wire format, cold-mesh timing rules, and the CI gating pattern.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8"/><path d="M12 16v4"/><path d="m7 9 2 2 4-4"/></svg>
---

# Disposable-mesh e2e testing

Every MeshWeaver e2e suite follows one shape:

```
mesh/up.sh            # 1. BOOT a disposable instance from the platform image
npx playwright test   # 2. dev-login → install plugins + content via the Plugin Catalog →
                      #    drive the product end to end
mesh/down.sh          # 3. throw the mesh away (volumes included)
```

The mesh under test is **never a shared instance** — suites carry a `CAN_MUTATE` guard that
refuses mutations against `*.meshweaver.cloud` / `*.systemorph.com`; only read-only smokes may
point at production. The reference implementation is the **education repo's course suite**
(`Systemorph/education → e2e/`): it installs the Edu plugin and the AgenticEngineering course
through the real catalog, installs the exercises as a user and executes all 27 workbenches plus
all 27 solutions.

## 1. The harness: compose from the platform image

A four-service `docker-compose.yml` — postgres (pgvector) + `memex-migration` +
`memex-portal-ai` + a registry stub — with throwaway volumes (`down -v` resets the mesh). Images
come from ACR (`meshweaver.azurecr.io/memex-portal-ai:main`, `az acr login -n meshweaver`) or the
GitHub registry (`ghcr.io/systemorph/…`). The portal env is the standard Filesystem+PostgreSql
pair the Helm chart uses, plus two e2e-specific settings:

| Setting | Why |
|---|---|
| `Authentication__EnableDevLogin=true` | the seedable test identity (§2) |
| `PluginCatalog__RegistryUrl=http://registry-stub:4873` | the consumer installs from the stub (§3) |

## 2. Identity: DevLogin self-provisioning

With `Authentication:EnableDevLogin` on, `POST /dev/signin {personId}` **self-provisions any
user** through the same `UserOnboardingService` dual-write the Entra flow runs — and **the first
user of a fresh mesh receives the platform-Admin grant** (`Admin/_Access`). That is the entire
auth story of an e2e run: the suite's global setup posts the form, captures the cookie as
Playwright storage state, and the user is a global admin who can open **Settings ▸
Administration ▸ Plugin Catalog**. No OAuth app, no secrets, no hand-carried storage-state
files. DevLogin is forced off in production builds — this only ever works on a throwaway mesh.

Two operational notes:

- The cookie is data-protection-keyed to the container: **recreating the portal invalidates old
  sessions** — always re-login in global setup, never cache state across boots.
- Sign in **before** asserting anything; a brand-new user's first page can take a while on a
  cold mesh (§5).

## 3. Content: install through the product, fed by a stub registry

Plugins and content enter the mesh through the **real consumer path** — the [Plugin
Catalog](/Doc/Architecture/PluginRegistry) settings tab, `PackageInstaller` and the live
NodeType compile — never via out-of-band SQL or bespoke import scripts. Credential-free and
offline: the harness runs a ~150-line **stub registry** speaking the registry wire format over
the local checkouts:

| Endpoint | Serves |
|---|---|
| `GET /api/plugins` | one `PackageManifest` (kind `NodeRepo`) per top-level folder whose `index.json` is a `nodeType: "Space"` root — `NodeRepoPackageSource`'s own listing rule |
| `POST /api/plugins/files {id}` | every text file under that folder (binaries — videos — skipped) |

The portal's `PluginCatalog__RegistryUrl` points at the stub, so the admin tab lists and
installs the checkouts exactly like a production instance installs from
`memex.meshweaver.cloud`.

> ⚠️ **Fresh-mesh gotcha — the `Plugins` records partition.** `PackageInstaller` writes its
> install record to `Plugins/{id}` *without provisioning that partition*, so on a fresh mesh the
> content imports fine but the record write dies with `42P01: relation "plugins.mesh_nodes" does
> not exist` — and the card never flips to "✓ Installed". Until core ensures the partition, the
> stub serves a synthetic **`Plugins (bootstrap)`** package (a lone `Plugins` Space root);
> installing it first provisions the schema via the Space-root create.

Bootstrap installs are verified by **outcome** — the imported pages render — never by the
catalog card's "✓ Installed" flip, whose read-back can lag on a fresh mesh.

## 4. What to assert

- **Enumerate expectations from the repo under test**, not from prose: the education suite
  globs the course's `*/Exercise/*.md` files at load time and refuses to run when the expected
  count is missing — a deleted workbench fails `playwright test --list` with no mesh at all.
- **Assert outcomes, not intermediate UI states**: pages render, copies exist (deep — a child
  page per subtree, never just the root), workbench `.md-code-cell-output`s hold real kernel
  output.
- **Exercises execute** (they ship deliberately red); **solutions must compile AND pass their
  benchmarks** (no `✗` rows, every `x/y green` tally complete).

## 5. Timing rules (Blazor + Orleans + Roslyn + kernel)

- A fresh mesh spends its **first minutes** importing static repos and compiling NodeTypes; hub
  init can exceed the portal's 30s subscribe gate ("Something went wrong" dialog) and
  admin-gated settings tabs can miss their bounded admin-check window on a given render.
  **Retry from the top with reloads**, dismissing error dialogs between attempts.
- The **fluent-dialog host element reports hidden** to Playwright while its light-DOM content is
  visible — wait on the dialog **title text**; use the host locator only as a scope.
- The C# kernel **cold-starts in ~a minute**; warm cells are fast. Budget 120s per workbench,
  run `workers: 1`, `fullyParallel: false`.
- Read models can lag writes (install records, live query flips) — poll by **reload** with
  `expect.poll`, generous timeouts, and assert the durable outcome.

## 6. CI gating

The workflow mirrors the local flow and **skips cleanly where credentials are absent** so forks
stay green:

| Job | Gate | Runs |
|---|---|---|
| static | always | `tsc --noEmit` + `playwright test --list` (the repo-enumeration gate) |
| smoke | `vars.E2E_SMOKE_BASE_URL` | the read-only smoke against a live instance |
| mesh | `vars.MW_E2E_ENABLED` + registry secrets (+ a token for sibling private checkouts) | registry login → `mesh/up.sh` → the full suite |

See the education repo's `.github/workflows/ci.yml` for the worked example, and its
`.claude/skills/course-e2e/SKILL.md` for the step-by-step authoring skill (mirrored on the mesh
as `Skill/course-e2e`).

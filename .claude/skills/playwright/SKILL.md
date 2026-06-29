---
name: playwright
description: Run Playwright browser E2E against a REAL portal, not the dev Monolith. The Monolith has DevLogin but no language model; the running `memex` stack has a model but uses Entra OAuth and is the user's data. This skill stands up a throwaway, DevLogin portal on Colima k3s — built from the CURRENT working tree, with its OWN database (memex_e2e), reusing the existing memex Postgres + host Ollama + ingress/TLS — then points Playwright at it over the ingress (reverse proxy). Use when an E2E must actually log in, render Blazor/SignalR, AND execute a chat round (or any flow needing a model); or when a test "skips execution" for lack of a model. One command: `memex-local e2e up` → `e2e test` → `e2e down`. Built on memex-local / LocalColimaMac.
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
  - Write
---

# /playwright — deploy a throwaway portal on Colima, then drive it with Playwright

**Never run a chat/model E2E against the dev Monolith** — it has DevLogin but no language model,
so `Send` is gated and the test can only assert the composer *renders*, never that it *executes*.
And **never point Playwright at the user's `memex` portal** — it authenticates through Entra
(no `/dev/signin`) and holds their real data.

Instead: **build the image from the working tree, deploy a dedicated DevLogin portal on Colima k3s,
and drive THAT.** It reuses the running `memex` stack's Postgres, host Ollama and ingress/TLS, but
runs against its **own** database (`memex_e2e`) with **DevLogin on**, behind the ingress
(reverse proxy) at `https://e2e.memex.localhost:8444`. It is additive — it never touches the
`memex` release, DB, or config.

> Canonical references:
> - [LocalColimaMac.md](../../../src/MeshWeaver.Documentation/Data/Architecture/LocalColimaMac.md) — the Colima k3s stack this builds on (§15 = the e2e portal).
> - `deploy/homebrew/bin/memex-local` — the CLI; the `e2e` subcommand is the whole flow.
> - `test/MeshWeaver.Portal.E2E.Test/PortalFixture.cs` — the DevLogin fixture every E2E uses.

## The flow (one CLI, three steps)

```bash
# 0. Prereq: the memex stack must already be up (provides PG, Ollama, ingress, TLS):
memex-local up                      # or: memex-local status   (skip if already running)

# 1. Build the working tree → image, create memex_e2e, migrate, deploy, reverse-proxy:
memex-local e2e up                  # add --skip-build to reuse the last image (fast iterate)

# 2. Run a Playwright test against the e2e portal (E2E_BASE_URL + DevLogin are preset):
memex-local e2e test HomeChatExecuteTest    # default filter is HomeChatExecuteTest

# 3. Tear down — delete the e2e objects + drop the e2e DB (keeps the memex stack):
memex-local e2e down                # add --keep-db to keep memex_e2e between runs
```

Run the CLI from the repo (it auto-resolves the chart/repo), or `MEMEX_REPO=$PWD memex-local …`
when invoking `deploy/homebrew/bin/memex-local` directly. `memex-local e2e status` / `e2e logs`
inspect the running instance.

## What `e2e up` does (and why each step)

1. **`image_build_local`** — `dotnet publish -t:PublishContainer` for the portal + migration
   (native arm64, into Colima's image store). `--skip-build` reuses the last build for fast loops.
2. **Create `memex_e2e`** in the existing `memex-postgres` (a *separate* database — never `memex`).
3. **Migrate** it (a one-off `Job` on the migration image). The portal's `DbVersionGate` refuses to
   start against an un-migrated DB, so this must finish first.
4. **Deploy** `memex-e2e-portal` (Deployment + Service + Ingress) in the `memex` namespace, reusing
   `memex-portal-config` / `memex-portal-secrets` via `envFrom`, overriding **only**:
   `ConnectionStrings__memex` → `memex_e2e`, and `Authentication__EnableDevLogin=true`.
   Clustering is `Localhost` (its own in-process silo) so it never joins memex's cluster.
5. **Reverse proxy** — an Ingress for `e2e.memex.localhost` (covered by the `*.memex.localhost`
   mkcert cert), plus a `port-forward` of `ingress-nginx` `:443` to host `:8444`.

## Driving it with Playwright

`PortalFixture` authenticates via **DevLogin** (`POST /dev/signin?personId=Roland`) and sets
`IgnoreHTTPSErrors=true`, so the self-signed `*.memex.localhost` cert is fine. `memex-local e2e test`
exports `E2E_BASE_URL=https://e2e.memex.localhost:8444` and `E2E_USER=Roland` for you. To run a test
by hand:

```bash
E2E_BASE_URL=https://e2e.memex.localhost:8444 E2E_USER=Roland \
  dotnet test test/MeshWeaver.Portal.E2E.Test --filter "FullyQualifiedName~<TestName>"
```

When writing a new browser test, follow the existing ones:
- Put it in `test/MeshWeaver.Portal.E2E.Test`, `[Collection("portal-e2e")]`, ctor takes `PortalFixture`.
- First line: `Assert.SkipUnless(fixture.Available, fixture.SkipReason)` — so it no-ops when E2E is off.
- `await fixture.NewAuthenticatedContextAsync()` for a logged-in browser context; seed mesh content
  it can't create via the UI with `MintTokenAsync` + `CreateNodeAsync`.
- Assert on **rendered DOM**, not typed-but-unsent input (the `HomeChatExecuteTest` lesson: matching
  the editor's own text false-passes). Wait for a real message bubble / state change.
- A model-dependent step should detect "No language model is available" and `Assert.Skip` rather
  than false-pass — but under THIS skill a model IS present, so execution actually runs.

## Iterate fast

After a code change: `memex-local e2e up` rebuilds + rolls the portal (the DB/migration are
idempotent). For a UI-only change with the image unchanged, `--skip-build` skips the long publish.

## Troubleshooting

- **`namespace 'memex' not found`** → run `memex-local up` first; `e2e` reuses that stack.
- **Portal not ready / `e2e logs`** shows a DB error → the migration Job didn't complete; check
  `kubectl -n memex logs job/memex-e2e-migration`.
- **`Send` still gated in the test** → host Ollama isn't serving the model. `ollama list` must show
  the chat model (`qwen3.6-code`); the portal addresses it as `http://ollama:11434/v1`.
- **502 / not 200 at the URL** → the ingress port-forward dropped; `memex-local e2e status` restarts
  it, or `kubectl -n ingress-nginx port-forward svc/ingress-nginx-controller 8444:443`.
- **Leftover env** → `memex-local e2e down` (idempotent). It only removes e2e objects + the e2e DB.
```

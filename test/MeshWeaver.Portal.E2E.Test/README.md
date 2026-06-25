# MeshWeaver.Portal.E2E

Playwright browser tests for the **collaborative-markdown** UI — opening a markdown doc and adding a
text-anchored comment, seeing it render **inline** and in the **sidebar**.

These are **not** part of the normal `dotnet test` solution run. They drive a real portal in a real
browser and **Skip themselves** unless E2E is enabled.

## Run against a portal you already have up (recommended)

```bash
# 1. Start the standalone monolith (one terminal). Note the URL it prints.
dotnet run --project memex/Memex.Portal.Monolith

# 2. First time only — install the browser the tests drive.
dotnet build test/MeshWeaver.Portal.E2E.Test
pwsh test/MeshWeaver.Portal.E2E.Test/bin/Debug/net10.0/playwright.ps1 install chromium

# 3. Run the tests, pointing at the portal's URL.
E2E_BASE_URL=https://localhost:7122 dotnet test test/MeshWeaver.Portal.E2E.Test
```

## Let the tests launch the monolith themselves

```bash
E2E_LAUNCH=1 dotnet test test/MeshWeaver.Portal.E2E.Test
# Boots memex/Memex.Portal.Monolith on http://localhost:5099, runs, then tears it down.
```

## Configuration (environment variables)

| Variable        | Default  | Meaning                                                                 |
|-----------------|----------|-------------------------------------------------------------------------|
| `E2E_BASE_URL`  | —        | URL of a running portal. When set, the tests run against it.            |
| `E2E_LAUNCH`    | —        | `1` ⇒ launch `Memex.Portal.Monolith` on `http://localhost:5099`.        |
| `E2E_USER`      | `Roland` | DevLogin person id (a `User` node id). Auth is a POST to `/dev/signin`.  |

If neither `E2E_BASE_URL` nor `E2E_LAUNCH` is set, every test is **skipped** (so the project is safe
to leave in the repo without affecting CI).

## What the tests cover

- `DocPage_RendersCollaborativeMarkdown_WhenAuthenticated` — DevLogin works, the doc route renders the
  collaborative markdown view (smoke test).
- `Comment_AddViaSelection_ShowsInlineHighlightAndSidebarCard` — select text → floating **Comment**
  button → dialog → submit → the highlight appears **inline** (`.comment-highlight`) and a sidebar
  card (`.annotation-card`) carries the comment text; then deletes it to keep re-runs clean.

Reply and accept/reject flows can be added the same way; the comment flow is the template.

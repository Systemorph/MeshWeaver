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

## Where do I see results?

- **Terminal** — `dotnet test` prints the per-test outcome and a `Passed!/Failed!/Skipped!` summary.
- **TRX file** — every run writes `test/MeshWeaver.Portal.E2E.Test/TestResults/_<machine>_<utc>.trx`
  (per-test outcome + captured stdout). Open it in an IDE, or `dotnet test --logger "console;verbosity=detailed"`.
- **Watch it live** — add `E2E_HEADED=1` to open a visible, slow-motion browser:
  ```bash
  E2E_BASE_URL=https://localhost:7122 E2E_HEADED=1 dotnet test test/MeshWeaver.Portal.E2E.Test
  ```
- **Video of each test** — written to `test/MeshWeaver.Portal.E2E.Test/bin/Debug/net10.0/TestResults/videos/*.webm`.
- **Step through** — `PWDEBUG=1 dotnet test …` opens the Playwright Inspector to run a test action-by-action.

## What the tests cover

- `DocPage_RendersCollaborativeMarkdown_WhenAuthenticated` — DevLogin works, the doc route renders the
  collaborative markdown view (smoke test).
- `Comment_AddViaSelection_ShowsInlineHighlightAndSidebarCard` — select text → floating **Comment**
  button → dialog → submit → the highlight appears **inline** (`.comment-highlight`) and a sidebar
  card carries the comment text; then deletes it to keep re-runs clean.
- `Comment_Reply_AddsAReplyUnderThePageComment` — add a page comment, open its **Reply** box (a Monaco
  editor), type, Create, and see the reply appear.
- `Change_Accept_AppliesTheSuggestionToTheDocument` / `Change_Reject_DropsTheSuggestion…` — tracked
  changes have no GUI creation path, so the test **seeds** an insertion suggestion via the REST API
  (`POST /api/mesh/create` with a token minted from the DevLogin session), then drives the sidebar
  **Accept** / **Reject** buttons and asserts the document is (or is not) changed.

> Notes for the first live run: the reply test drives a **Monaco** editor and the change tests seed a
> node via the API — if a selector or the seed JSON needs a tweak for your build, those are the two to
> watch. The underlying logic is also covered headlessly by the C# suite (`AnchorMath`,
> `CommentRendering`, `ChangeRendering`, and the suggest-edit / reply integration tests).

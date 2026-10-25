---
Name: The Shared GitHub REST Client
Category: Architecture
Description: The CI scripts under .github/scripts read GitHub through ONE module, ghrest.py, instead of seven hand-rolled clients. Why the bearer token must never follow a redirect off GitHub's origin, what stock urllib actually does (measured), which scripts could have leaked and which could not, the one script deliberately left on its own client, and how a lane that fetches scripts one file at a time must carry the module along.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h10"/><path d="M10 6l6 6-6 6"/><path d="M20 4v16"/></svg>
---

# The Shared GitHub REST Client

**`.github/scripts/ghrest.py` is the one GitHub client the CI scripts share.** It offers two
families, kept apart on purpose:

| Family | Helpers | For |
|---|---|---|
| the `gh` CLI | `gh_cli`, `installation_repo_names`, `gh_cli_status`, `gh_json`, `gh_bytes` | scripts whose credential and pagination are `gh`'s (`GH_TOKEN`, `--paginate --jq`) |
| REST over urllib | `request`, `get`, `post`, `paginate`, `download_artifact`, `urlopen` | scripts that must not depend on `gh`'s output policy (it refused escape-bearing job logs, #4534) |

plus `die` (an `::error::` headline, indented reasons, exit 1). Every helper raises what the
transport raised; each script keeps its own error text, exit codes and retry policy.

## The redirect rule

An artifact `…/zip` or a job `…/logs` read answers a **302 to signed blob storage on another
host**. The bearer token must not follow it.

**What stock urllib does — measured by `ghrest.py --self-test`, two local servers on two ports:**

- A header set with `Request.add_header` or `Request(headers=…)` is **forwarded** to whatever host
  the `Location` names. `HTTPRedirectHandler.redirect_request` copies `req.headers` onto the new
  request, and a `POST` answered 301/302/303 is followed as a `GET` with the same headers. The self-test
  drives both shapes through the stock opener as its **negative control**, and both deliver the token
  to the second port.
- A header set with `add_unredirected_header` is **never** forwarded — not even to the same host.

**What ghrest does:** the token is an ordinary header, and `_CredentialDroppingRedirect` strips
`Authorization` whenever a redirect leaves the request's **origin** (scheme, host and port). A
same-origin redirect, such as a renamed repository's 301 on `api.github.com`, keeps the credential.
Once the header has been stripped it stays stripped for every later hop. `ghrest.urlopen(req)` applies
the same rule to a request a script builds itself, such as the name-check scan's MCP `POST`, whose
bearer is not GitHub's.

## Which scripts could leak, before the move

| Script | Old client | Could the token cross a redirect to another host? |
|---|---|---|
| `check-pin-set-consistency.py`, `check-pinned-digests.py` | `gh api` (byte-identical copies) | **No.** `gh`'s Go HTTP client drops `Authorization` on a cross-domain redirect, and the scripts add no header of their own (by `gh`'s design, not measured here) |
| `sentinel-issue.py` | `gh api -i` | No, for the same reason |
| `node-repo-publication-base.py` | `gh api` (the artifact zip included) | No, for the same reason |
| `retry-known-transients.py` | urllib with `add_unredirected_header` | **No** (its own self-test already proved it) |
| `resolve-platform.py` | urllib with `add_unredirected_header` | **No** |
| `arm-promoted-set.py` | urllib: a no-redirect opener for raw downloads, the stock opener for JSON | **Raw downloads: no** (the 302 was followed by hand, unauthenticated). **JSON reads: yes, in principle.** `Request(headers={"Authorization": …})` went through the stock opener, so any redirect would have carried the token. None of the endpoints it reads is known to redirect off `api.github.com` |
| `scan-issue-text-names.py` | urllib, `Request(headers={"Authorization": …})`, stock opener, for the GitHub listings **and** the MCP `POST` | **Yes, in principle, for both.** No observed redirect off-host was found; the shape was the defect |

So no leak was observed anywhere. Two scripts carried the leaking shape and are now on `ghrest`.

## The one script deliberately NOT moved

**`resolve-platform.py` keeps its own client.** Every satellite **vendors** a copy as
`scripts/resolve-platform.py` and runs it in its own `platform-ref` job, and
`check-resolver-copy.py` compares that copy's AST against the canonical (see
[Module Build Architecture](../ModuleBuildArchitecture)). If the canonical imported `ghrest`, every
vendored copy would need a vendored `ghrest.py` beside it, and every repository would read as drifted
the moment this landed. Its transport already uses `add_unredirected_header`, so the move would buy
deduplication only, at the price of a fleet-wide break.

## Lanes that fetch one file at a time

A script imports the client from beside itself:

```python
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ghrest
```

Scripts run from a checkout (full or sparse over `.github/scripts`) find it automatically. A
`node-repo-*.yml` lane that fetches a script **one file at a time** with `mw-core-file` must fetch
`ghrest.py` into the same directory in the same step. `node-repo-retry-transients.yml` does this for
`retry-known-transients.py`, and only when the script at that `scripts-ref` imports the client: an
older ref has no `ghrest.py` to fetch. `CiRestBudgetGuard.EveryFetchedScriptThatImportsTheSharedClient_IsFetchedWithIt`
holds every lane to this, with a negative control for the literal and the `for s in …` shapes. The
budget reasoning for fetching over git rather than REST is [The CI REST Budget](../CiRestBudget).

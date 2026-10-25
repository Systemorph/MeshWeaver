#!/usr/bin/env python3
"""cap-debt-triage.py — a package capped below `latest` owes a migration, and triage is told so DAILY.

WHY (policy `package-mesh-version-cap`, Doc/Architecture/DeployTestSwitchLatest §5). A package may
author an upper platform cap, `maxMeshVersion`, so it builds against an older build-server
deployment while `latest` has moved on. A cap is DEBT, never a resting place: nobody would notice a
package that quietly stays on an old platform for months. So once a day this script files, or
refreshes, ONE triage item per capped package — "migrate <package> to latest" — and closes the item
of a cap that was removed.

FIRST IT PUBLISHES THE CAPS (the retention half). The repository's WHOLE cap set goes to the control
inbox as ONE `build-caps` event signed with the fleet secret (`X-Hub-Signature-256`, the
ci-failure lane's mechanism): `{event, repo, sha, caps:[{package, floor, cap, version, deployed,
reason}]}`. The intake (MeshWeaver.Plugins `BuildCapIntake`) authorizes it by that signature and
writes `Hosting/BuildCaps/<package>` as System; the build hosts' retention reads those nodes, so
the deployment a capped package builds on is never torn down under it. `version` is the highest
COMPLETE deployed version (`complete: true`, scaled to zero or not) in [floor, cap] from `GET <build API>/hosts`; with none deployed it is the
version that must be (`deployed: false`). The set is sent even when EMPTY: a package missing from
it is a removed cap, and its node is deleted.

THEN IT ASKS FOR WHAT IS MISSING (the deploy half). A capped package with NO complete deployed
version in [floor, cap] gets one `build-host-deploy-requested` event per needed version, signed like
the caps event, in the receiver's shape (MeshWeaver.Plugins `BuildHostDeploy`,
`Hosting/StandingBuildHosts` §7b): `{event, deployment: "Deployments/<control>", version,
testerDigest, portalDigest, runnerDigest, coreTestsDigest?, reason: "cap:<package>"}`. The digests are
read from the registry by the set's version tag (`ACR_USERNAME` / `ACR_PASSWORD`, needed only then);
`coreTestsDigest` is named exactly when the set has an `mw-core-tests` image. The 202 is the inbox
ACCEPTING the request, never the deployment: the next run reads `GET /hosts` again, and the cap is
admitted (`--check-caps`) only once the version is listed complete. A refusal (409 included) is RED
naming the receiver's answer.

`GET /hosts` is read through the resolver: a 503 (a partial listing refused fail closed) is an
OUTAGE retried inside a bounded window, never an empty list — an empty list would request a
deployment that exists and drop a package's retention. `caps` (the anonymous count) says how many
caps hold a version; `cappedBy` (the names) is read only with the worker token
(`MW_BUILD_WORKER_TOKEN`), and both are REPORTED against the published set, never trusted for it.

WHERE IT FILES. Into bug triage, the way every agent and lane files a finding: a node of TYPE
`Feedback/Feedback` (Doc/Architecture/IncidentalFindings), created over the control instance's MCP
endpoint with the lane's service-user token — the same `create` call core's
`scan-issue-text-names.py` makes. The namespace is the caller's declared one (the service user's own
`<user>/Feedback`); `Feedback/<id>` itself refuses a create by design. The item is submitted (no
`status`, which means `New`), never a draft.

IDEMPOTENT. The item id is derived from (repository, package, cap), so a second day `create`s the
same id, is told it already exists, and PATCHES the message (distance and age move) — never a
duplicate. A cap that is gone, or moved, leaves an item whose id this run did not derive: it is
patched to `Resolved`. Re-adding the very same cap after its item was resolved does not reopen it
(the refresh patches the message only, so it never undoes triage's own status); a different cap
value is a new item.

NO SKIP-TRAPDOORS. A tree with no cap files nothing and says so. A tree WITH a cap and any input
missing — the mesh URL, the token, the namespace, the build API, a readable `latest` — is RED naming
it. A create or patch the instance refuses is RED. A search answer that is truncated or unreadable
is RED (closing from a partial listing would leave stale items open, silently).

  cap-debt-triage.py --root . --resolver <resolve-platform.py> --repo owner/name --sha <sha> \
      --build-api https://<control>/api/builds --mesh-url https://<control> --namespace <user>/Feedback \
      --control-webhook-url https://<control>/api/hooks/Hosting/PlatformBuilds
      [--deployment control]
  env: MW_TRIAGE_TOKEN (mesh), CONTROL_WEBHOOK_SECRET (inbox), MW_BUILD_API_TOKEN (optional),
       MW_BUILD_WORKER_TOKEN (optional: reads `cappedBy`), ACR_USERNAME / ACR_PASSWORD (only when a
       capped package needs a deployment requested)
  cap-debt-triage.py --self-test
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

PROTOCOL_VERSION = "2025-06-18"
NODE_TYPE = "Feedback/Feedback"
CONTENT_TYPE = "FeedbackContent"
SUBMITTED_BY = "node-repo-cap-debt"
CATEGORY = "platform-cap"
TOKEN_ENV = "MW_TRIAGE_TOKEN"
HTTP_TIMEOUT_S = 60
SEARCH_LIMIT = 200


class Red(RuntimeError):
    """A finding that makes the run RED, naming what to fix."""


def load_resolver(path: str):
    spec = importlib.util.spec_from_file_location("resolve_platform", path)
    if spec is None or spec.loader is None:
        raise Red(f"the resolver at {path} cannot be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    # `floor_admits` marks a resolver whose `highest_in_range` takes the floor TEXT (release floors
    # such as `3.1.0` included); an older one took a run number and would misread it.
    for name in ("read_package_bounds", "HttpBuildHosts", "set_run", "highest_in_range", "floor_admits",
                 "canonical_set_name", "ResolutionError", "deployable_version", "set_digests", "registry_resolver",
                 "BUILD_API_OUTAGE_WINDOW_S"):
        if not hasattr(module, name):
            raise Red(f"the resolver at {path} predates the package caps (no `{name}`) — fetch it at a "
                      "scripts ref that carries Doc/Architecture/DeployTestSwitchLatest phase P5")
    return module


def slug(text: str) -> str:
    """Lower-case `[a-z0-9-]`, dash runs collapsed — so a slug NEVER contains `--`. Pure."""
    return re.sub(r"-+", "-", re.sub(r"[^a-z0-9]+", "-", text.lower())).strip("-")


def digest(*parts: str) -> str:
    """12 hex chars of SHA-256 over the EXACT parts, NUL-separated — what tells apart tuples whose
    slugs coincide (`Foo.Bar` / `Foo-Bar`, `O/R` / `o.r`). Pure."""
    import hashlib
    return hashlib.sha256("\0".join(parts).encode("utf-8")).hexdigest()[:12]


def repo_prefix(repo: str) -> str:
    """This repository's id prefix. It ends in `--<digest of the exact repo name>--`, and since a
    slug never contains `--`, no other repository's prefix (`O/R-Tools` beside `O/R`) can start with
    it. Pure."""
    return f"mesh-cap-{slug(repo)}--{digest(repo)}--"


def item_id(repo: str, package: str, cap: str) -> str:
    """The item's identity: (repository, package, cap) — readable slugs plus a digest of the exact
    (package, cap) pair, so two packages whose names slug alike never share one item. Pure."""
    return repo_prefix(repo) + f"{slug(package)}-{slug(cap)}-{digest(package, cap)}"


def cap_age(root: str, package: str, cap: str) -> str:
    """When the CURRENT cap value entered `<package>/index.json`, from git (oldest commit whose diff
    carries that cap line), as `N days (since YYYY-MM-DD)`. Informational: an unreadable history says
    so in the message rather than guessing."""
    # POSIX classes, not `\s`: `git log -G` reads a basic regex unless git was built with PCRE.
    pattern = '"maxMeshVersion"[[:space:]]*:[[:space:]]*"' + re.escape(cap) + '"'
    try:
        out = subprocess.run(["git", "-C", root, "log", "--format=%cI", "-G", pattern, "--",
                              f"{package}/index.json"], capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as error:
        return f"unknown (git: {type(error).__name__})"
    dates = [line for line in out.stdout.splitlines() if line.strip()]
    if out.returncode != 0 or not dates:
        return "unknown (no commit carries it — a shallow checkout or an uncommitted cap)"
    since = datetime.fromisoformat(dates[-1].replace("Z", "+00:00"))
    days = (datetime.now(timezone.utc) - since).days
    return f"{days} day(s) (since {since.date().isoformat()})"


def message_for(repo: str, b, latest: str, distance: int | None, age: str) -> str:
    """The triage message. Pure."""
    return (f"Migrate {b.package} ({repo}) to latest.\n\n"
            f"The package caps its platform at maxMeshVersion {b.cap}; the build hosts' `latest` is "
            f"{latest} — {distance if distance is not None else 'an unknown number of'} platform build(s) "
            f"ahead. The cap has stood for {age}.\n"
            f"Floor: {b.floor or '(none)'}. Why capped: {b.reason or '(not stated)'}. "
            f"Tracking: {b.tracking or '(none)'}.\n\n"
            "A cap is debt, not a resting place (Doc/Architecture/DeployTestSwitchLatest §5): adapt the "
            f"package to {latest} and remove `maxMeshVersion`, `capReason` and `capTracking` from "
            f"{b.package}/index.json. Removing the cap closes this item.")


# ── MCP over HTTP (the create/patch/search verbs only) ────────────────────────────────────────

class Mesh:
    def __init__(self, base_url: str, token: str):
        self.endpoint = base_url.rstrip("/") + "/mcp"
        self.token = token
        self.session_id: str | None = None
        self._id = 0
        self._ready = False

    def _post(self, method: str, params: dict, notification: bool = False) -> dict | None:
        body: dict = {"jsonrpc": "2.0", "method": method, "params": params}
        if not notification:
            self._id += 1
            body["id"] = self._id
        headers = {"Authorization": f"Bearer {self.token}", "Content-Type": "application/json",
                   "Accept": "application/json, text/event-stream", "MCP-Protocol-Version": PROTOCOL_VERSION}
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
        request = urllib.request.Request(self.endpoint, data=json.dumps(body).encode(), headers=headers, method="POST")
        opener = urllib.request.build_opener(_NoRedirect)
        try:
            with opener.open(request, timeout=HTTP_TIMEOUT_S) as resp:
                if resp.headers.get("Mcp-Session-Id"):
                    self.session_id = resp.headers["Mcp-Session-Id"]
                raw = resp.read().decode("utf-8", "replace")
                if notification or resp.status == 202 or not raw:
                    return None
                ctype = (resp.headers.get("Content-Type") or "").lower()
        except urllib.error.HTTPError as error:
            raise Red(f"the mesh endpoint {self.endpoint} answered HTTP {error.code} to {method}"
                      + (f" — check {TOKEN_ENV}" if error.code in (401, 403) else "")) from error
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            raise Red(f"the mesh endpoint {self.endpoint} is unreachable ({type(error).__name__})") from error
        messages: list = []
        if "text/event-stream" in ctype:
            for block in raw.replace("\r\n", "\n").split("\n\n"):
                payload = "\n".join(l[5:].lstrip() for l in block.split("\n") if l.startswith("data:"))
                if payload.strip():
                    messages.append(json.loads(payload))
        else:
            parsed = json.loads(raw)
            messages = parsed if isinstance(parsed, list) else [parsed]
        for m in messages:
            if isinstance(m, dict) and m.get("id") == body.get("id"):
                if "error" in m:
                    raise Red(f"{method}: JSON-RPC error {m['error'].get('code')}")
                return m.get("result") or {}
        raise Red(f"{method}: no JSON-RPC response from the mesh endpoint")

    def call(self, tool: str, arguments: dict) -> str:
        if not self._ready:
            self._post("initialize", {"protocolVersion": PROTOCOL_VERSION, "capabilities": {},
                                      "clientInfo": {"name": "cap-debt-triage", "version": "1"}})
            self._post("notifications/initialized", {}, notification=True)
            self._ready = True
        result = self._post("tools/call", {"name": tool, "arguments": arguments}) or {}
        text = "".join(c.get("text", "") for c in result.get("content", [])
                       if isinstance(c, dict) and c.get("type") == "text")
        if result.get("isError"):
            raise Red(f"{tool} was refused by the mesh endpoint: {text[:300]}")
        return text


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: ANN001
        return None


# ── the run ───────────────────────────────────────────────────────────────────────────────────

def file_items(mesh: Mesh, repo: str, namespace: str, items: dict[str, tuple[str, str]], now: str,
               say=print) -> None:
    """Create or refresh every item in `items` (`id → (name, message)`), then resolve this
    repository's items whose id this run did not derive."""
    for nid, (name, message) in sorted(items.items()):
        node = {"id": nid, "namespace": namespace, "name": name, "nodeType": NODE_TYPE,
                "content": {"$type": CONTENT_TYPE, "message": message, "submittedBy": SUBMITTED_BY,
                            "timestamp": now, "category": CATEGORY}}
        answer = mesh.call("create", {"node": json.dumps(node)}).strip()
        if answer.startswith("Created"):
            say(f"  filed {namespace}/{nid}")
            continue
        if "already exists" not in answer:
            raise Red(f"the triage item {namespace}/{nid} was not created: {answer[:300]}")
        patched = mesh.call("patch", {"path": f"{namespace}/{nid}",
                                      "fields": json.dumps({"content": {"message": message, "timestamp": now}})}).strip()
        if not (patched.startswith("Patched") or patched.startswith("No change")):
            raise Red(f"the triage item {namespace}/{nid} exists and could not be refreshed: {patched[:300]}")
        say(f"  refreshed {namespace}/{nid} (no duplicate filed)")
    listing = mesh.call("search", {"query": f"namespace:{namespace} nodeType:{NODE_TYPE}", "limit": SEARCH_LIMIT})
    try:
        doc = json.loads(listing)
    except ValueError as error:
        raise Red(f"the search for this repository's open cap items answered no JSON: {listing[:200]}") from error
    if not isinstance(doc, dict) or not isinstance(doc.get("results"), list):
        raise Red(f"the search for this repository's cap items carries no `results`: {listing[:200]}")
    if doc.get("truncated"):
        raise Red(f"the search of {namespace} is truncated at {SEARCH_LIMIT} — a removed cap's item cannot "
                  "be told apart from one past the limit; narrow the namespace")
    prefix = repo_prefix(repo)
    for row in doc["results"]:
        nid = str(row.get("path", "")).rsplit("/", 1)[-1]
        if not nid.startswith(prefix) or nid in items:
            continue
        answer = mesh.call("patch", {"path": f"{namespace}/{nid}",
                                     "fields": json.dumps({"content": {"status": "Resolved"}})}).strip()
        if not (answer.startswith("Patched") or answer.startswith("No change")):
            raise Red(f"the item {namespace}/{nid} of a removed cap could not be resolved: {answer[:300]}")
        say(f"  resolved {namespace}/{nid} — its cap is gone" + (" (already resolved)" if answer.startswith("No change") else ""))


BUILD_CAPS_EVENT = "build-caps"
WEBHOOK_SECRET_ENV = "CONTROL_WEBHOOK_SECRET"
DEFAULT_WEBHOOK_HOST = "control.systemorph.com"


def cap_rows(resolver, capped: list, deployed: list) -> list[dict]:
    """One row per capped package for the `build-caps` event — the content of its
    `Hosting/BuildCaps/<package>` node on control (retention reads them as `capVersions`).
    `version` is the highest COMPLETE deployed version inside [floor, cap]; when none is deployed it is
    the version that must BE deployed (the cap itself: a cap names a sealed set), and `deployed` says
    which, so the intake can ask for that deployment (BuildHostDeploy, reason `cap:<package>`). Pure."""
    rows = []
    for b in capped:
        fit = resolver.highest_in_range(b.floor, resolver.set_run(b.cap), deployed)
        rows.append({"package": b.package, "floor": b.floor, "cap": b.cap,
                     "version": fit.version if fit else resolver.canonical_set_name(b.cap) or b.cap,
                     "deployed": fit is not None, "reason": b.reason})
    return sorted(rows, key=lambda r: r["package"])


def caps_event(repo: str, sha: str, rows: list[dict]) -> str:
    """The signed body: the repository's WHOLE cap set, so an absent package is a removal. Pure."""
    return json.dumps({"event": BUILD_CAPS_EVENT, "repo": repo, "sha": sha, "caps": rows},
                      sort_keys=True, separators=(",", ":"))


def validate_webhook_url(url: str, expected_host: str) -> str:
    """https, the expected host, a path under /api/hooks/ (plain http only on loopback). Nothing is
    signed for any other URL: an HMAC sent anywhere else is a credential handed to that host."""
    parsed = urllib.parse.urlsplit((url or "").strip())
    loopback = parsed.hostname in ("127.0.0.1", "localhost", "::1")
    if parsed.scheme != "https" and not (parsed.scheme == "http" and loopback):
        raise Red(f"--control-webhook-url {url!r} is not https — the signature would travel in clear")
    if not loopback and (parsed.hostname or "") != expected_host:
        raise Red(f"--control-webhook-url {url!r} is not on {expected_host} — nothing is signed for another host")
    if not parsed.path.startswith("/api/hooks/") or parsed.username or parsed.password:
        raise Red(f"--control-webhook-url {url!r} is not an inbox path (/api/hooks/…) or carries credentials")
    return url.strip()


def post_event(url: str, secret: str, body: str, say=print, what: str = "the build-caps event") -> None:
    """POST `body` signed `X-Hub-Signature-256: sha256=<HMAC>`; the inbox's verdict is judged three
    ways, as the ci-failure lane judges it: `verified` passes, `not-required` is RED (the receiver
    never checked the secret), no verdict at all is a receiver that predates the field (warned).
    Any non-2xx is RED carrying the receiver's own answer; a 409 is a REFUSAL of the request (the
    version names another set, the record holds its retention bound), never "requested"."""
    import hashlib
    import hmac
    signature = "sha256=" + hmac.new(secret.encode(), body.encode(), hashlib.sha256).hexdigest()
    request = urllib.request.Request(url, data=body.encode(), method="POST", headers={
        "Content-Type": "application/json", "X-Hub-Signature-256": signature, "User-Agent": "cap-debt"})
    try:
        with urllib.request.build_opener(_NoRedirect).open(request, timeout=HTTP_TIMEOUT_S) as resp:
            answer = resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        try:
            said = error.read().decode("utf-8", "replace")[:300]
        except Exception:  # noqa: BLE001 — the answer's body is evidence, not a requirement
            said = ""
        if error.code == 409:
            raise Red(f"the control inbox REFUSED {what} (HTTP 409 — a conflict on the record, e.g. the version "
                      f"already names another set or retention holds its bound): {said or '(no reason given)'}") from error
        raise Red(f"the control inbox answered HTTP {error.code} to {what}"
                  + (f" — check {WEBHOOK_SECRET_ENV}" if error.code in (401, 403) else "")
                  + (f": {said}" if said else "")) from error
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        raise Red(f"the control inbox is unreachable ({type(error).__name__})") from error
    try:
        verdict = (json.loads(answer) or {}).get("signature")
    except (ValueError, AttributeError):
        verdict = None
    if verdict == "not-required":
        raise Red(f"the inbox ACCEPTED {what} WITHOUT checking its signature (`not-required`) — "
                  "the receiving instance declares no secret for Hosting/PlatformBuilds; fix it there")
    if verdict != "verified":
        say("::warning::the inbox answered no signature verdict — a receiver that predates it; the event was delivered")


DEPLOY_EVENT = "build-host-deploy-requested"
REGISTRY_USER_ENV, REGISTRY_PASSWORD_ENV = "ACR_USERNAME", "ACR_PASSWORD"
WORKER_TOKEN_ENV = "MW_BUILD_WORKER_TOKEN"
DEFAULT_DEPLOYMENT = "control"
PACKAGE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
DEPLOYMENT_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")


def deploy_requests(resolver, rows: list[dict], deployment: str, resolve) -> list[tuple[str, list[str], str]]:
    """`[(version, packages, body)]` — ONE `build-host-deploy-requested` per version some capped
    package needs and no complete deployment carries, its reason `cap:<first package>` (the others
    named in the log). The body is the receiver's shape, key order the wire order, every field
    validated; the digests are the set's own, by its version tag. Raises Red (nothing is sent)."""
    name = (deployment or "").strip()
    name = name[len("Deployments/"):] if name.startswith("Deployments/") else name
    if not DEPLOYMENT_NAME.fullmatch(name):
        raise Red(f"--deployment {deployment!r} is not a plain Hosting/Deployment record name")
    wanted: dict[str, list[str]] = {}
    for row in rows:
        if row["deployed"]:
            continue
        if not PACKAGE_NAME.fullmatch(row["package"]):
            raise Red(f"package {row['package']!r} cannot name a deploy reason (`cap:<package>`)")
        try:
            version = resolver.deployable_version(row["cap"])
        except resolver.ResolutionError as error:
            raise Red(f"{row['package']}: {error}") from error
        wanted.setdefault(version, []).append(row["package"])
    out = []
    for version, packages in sorted(wanted.items()):
        try:
            digests = resolver.set_digests(resolve, version)
        except resolver.ResolutionError as error:
            raise Red(f"the deployment {', '.join(packages)} need(s) cannot be requested: {error}") from error
        event = {"event": DEPLOY_EVENT, "deployment": f"Deployments/{name}", "version": version,
                 "testerDigest": digests["testerDigest"], "portalDigest": digests["portalDigest"],
                 "runnerDigest": digests["runnerDigest"]}
        if "coreTestsDigest" in digests:
            event["coreTestsDigest"] = digests["coreTestsDigest"]
        event["reason"] = f"cap:{sorted(packages)[0]}"
        out.append((version, sorted(packages), json.dumps(event, separators=(",", ":"))))
    return out


def retention_lines(rows: list[dict], deployed: list) -> list[str]:
    """What `GET /hosts` says about each deployed cap's retention, read BEFORE today's publication
    (so it reflects the previous one): the names with the worker token, the count without. A
    mismatch is a WARNING — the intake applies a publication asynchronously — never the verdict.
    `cappedBy` is worker-only because it names packages of EVERY repository, private ones included,
    and this log is public on a public repository: only THIS repository's own packages are named,
    the others only counted. Pure."""
    by_version = {d.version: d for d in deployed}
    own = {row["package"] for row in rows}
    lines = []
    for row in rows:
        d = by_version.get(row["version"]) if row["deployed"] else None
        if d is None:
            continue
        if d.capped_by is not None:
            held = row["package"] in d.capped_by
            ours = sorted(p for p in d.capped_by if p in own)
            others = len(d.capped_by) - len(ours)
            lines.append(("" if held else "::warning::")
                         + f"retention: build-{d.version} cappedBy {ours}"
                         + (f" (+{others} other package(s), names withheld)" if others else "") + " — "
                         + ("holds " if held else "does NOT name ") + row["package"]
                         + ("" if held else " yet (the intake applies today's publication asynchronously)"))
        else:
            lines.append(("" if d.caps else "::warning::") + f"retention: build-{d.version} is needed by {d.caps} "
                         "package cap(s) (anonymous count; the names need the worker token)"
                         + ("" if d.caps else " — none yet, the intake applies today's publication asynchronously"))
    return lines


def run(root: str, resolver_path: str, repo: str, build_api: str, mesh_url: str, namespace: str,
        token: str, build_token: str, say=print, sha: str = "", webhook_url: str = "",
        webhook_secret: str = "", webhook_host: str = DEFAULT_WEBHOOK_HOST, deployment: str = DEFAULT_DEPLOYMENT,
        worker_token: str = "", registry_user: str = "", registry_password: str = "", resolve=None,
        outage_window: float | None = None, outage_poll: float | None = None) -> int:
    resolver = load_resolver(resolver_path)
    try:
        bounds = resolver.read_package_bounds(root)
    except resolver.ResolutionError as error:
        raise Red(str(error)) from error
    capped = [b for b in bounds if b.cap is not None]
    missing = [what for what, value in (("--repo", repo), ("--mesh-url (the control instance)", mesh_url),
                                        ("--namespace (the service user's <user>/Feedback)", namespace),
                                        (TOKEN_ENV, token), ("--build-api (vars.MW_BUILD_API)", build_api),
                                        ("--control-webhook-url (vars.CONTROL_WEBHOOK_URL)", webhook_url),
                                        (WEBHOOK_SECRET_ENV, webhook_secret))
               if not value.strip()]
    if missing:
        raise Red("missing input(s): " + ", ".join(missing) + " — provision them on the caller of "
                  "node-repo-cap-debt.yml")
    webhook_url = validate_webhook_url(webhook_url, webhook_host)
    try:
        # The mesh token is sent to this URL, so it is held to the build API's rule: https (plain
        # http only on loopback, where the self-test's fake lives), no credentials, no query.
        mesh_url = resolver.build_api_base(mesh_url)
    except resolver.ResolutionError as error:
        raise Red(f"--mesh-url: {error}") from error
    try:
        hosts = resolver.HttpBuildHosts(
            build_api, build_token, worker_token=worker_token,
            outage_window=resolver.BUILD_API_OUTAGE_WINDOW_S if outage_window is None else outage_window,
            outage_poll=resolver.BUILD_API_OUTAGE_POLL_S if outage_poll is None else outage_poll)
        latest = hosts.latest()
        deployed = hosts.deployed()
        if hosts.outages:
            say(f"::notice::GET /hosts answered 503 {hosts.outages} time(s) (an outage: a partial listing refused) "
                "before the listing was read")
    except resolver.ResolutionError as error:
        raise Red(str(error)) from error
    latest_run = resolver.set_run(latest)

    # 1. The caps, published for retention: the WHOLE set, so a removed cap deletes its node.
    rows = cap_rows(resolver, capped, deployed)
    post_event(webhook_url, webhook_secret, caps_event(repo, sha, rows), say)
    for row in rows:
        say(f"  cap {row['package']} [{row['floor'] or 'no floor'}, {row['cap']}] → {row['version']}"
            + ("" if row["deployed"] else " (NOT deployed — the intake asks for it)"))
    say(f"published {len(rows)} cap(s) of {repo} to the control inbox (an absent package is removed there)")
    for line in retention_lines(rows, deployed):
        say("  " + line if not line.startswith("::") else line)

    # 2. The debt, filed for triage.
    items: dict[str, tuple[str, str]] = {}
    for b in capped:
        cap_run = resolver.set_run(b.cap)
        distance = latest_run - cap_run if cap_run is not None and latest_run is not None else None
        items[item_id(repo, b.package, b.cap)] = (
            f"Migrate {b.package} to latest (capped at {b.cap})",
            message_for(repo, b, latest, distance, cap_age(root, b.package, b.cap)))
    say(f"{len(capped)} capped package(s) of {len(bounds)} in {repo}; `latest` is {latest}")
    file_items(Mesh(mesh_url, token), repo, namespace, items,
               datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), say)
    say(f"✓ {len(items)} cap item(s) filed or refreshed" if items else "✓ no package is capped — nothing owed")

    # 3. The deployments no complete version provides, REQUESTED (after the retention and the debt
    #    are recorded, so a refused request leaves both in place and is red on its own).
    if any(not row["deployed"] for row in rows):
        if resolve is None:
            missing = [env for env, value in ((REGISTRY_USER_ENV, registry_user),
                                              (REGISTRY_PASSWORD_ENV, registry_password)) if not value.strip()]
            if missing:
                raise Red(f"a capped package needs a deployment requested, and the set's digests are read from the "
                          f"registry: missing {', '.join(missing)} — pass the registry credential to "
                          "node-repo-cap-debt.yml (secrets registry-username / registry-password)")
            resolve = resolver.registry_resolver(registry_user, registry_password)
        for version, packages, event in deploy_requests(resolver, rows, deployment, resolve):
            post_event(webhook_url, webhook_secret, event, say, what=f"the {DEPLOY_EVENT} event for {version}")
            say(f"  requested build-{version} ({json.loads(event)['reason']}; needed by {', '.join(packages)}) — "
                "ACCEPTED, not deployed: GET /hosts lists it once BuildHostDeploy ran, and the next run reads it")
    return 0


# ── self-test ─────────────────────────────────────────────────────────────────────────────────

WEBHOOK_PATH = "/api/hooks/Hosting/PlatformBuilds"
TEST_SECRET = "the-fleet-inbox-secret"


class _FakeControl:
    """One loopback server playing the control instance: the build API's `GET /hosts`, the signed
    inbox (it VERIFIES the HMAC and keeps every verified body), and the MCP endpoint with an in-memory
    node store whose answers copy MeshOperations' wording."""

    def __init__(self, latest: str | None = "3.1.10412", versions: list[tuple[str, bool]] | None = None,
                 refuse_create: bool = False, truncated: bool = False, inbox_verdict: str = "verified",
                 hosts_503: int = 0, deploy_status: int = 202, caps: dict | None = None):
        import hashlib
        import hmac
        import http.server
        import threading
        self.nodes: dict[str, dict] = {}
        self.calls: list[str] = []
        self.events: list[dict] = []
        self.hosts_reads = 0
        self.refused: list[dict] = []
        rows = versions if versions is not None else [("3.1.10412", True), ("3.1.10398", True)]
        caps = caps or {}
        # `(version, complete)` in the shape `/hosts` serves (no `ready`); the decoy `ready: not
        # complete` proves the lane never reads `ready` — a complete version reads as scaled to zero.
        hosts_doc = {"aliases": {"latest": latest, "candidate": None, "previous": None},
                     "versions": [{"version": v, "deploymentName": "build-" + v.replace(".", "-"),
                                   "complete": c, "ready": not c, "maxReplicas": 2, "aliases": [],
                                   "pinned": 0, "caps": caps.get(v, (0, []))[0]} for v, c in rows]}
        worker_doc = json.loads(json.dumps(hosts_doc))
        for row in worker_doc["versions"]:  # `cappedBy` is served to the worker token only
            row["cappedBy"] = caps.get(row["version"], (0, []))[1]
        fake = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def _send(self, status: int, body: dict | str) -> None:
                data = (body if isinstance(body, str) else json.dumps(body)).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):  # noqa: N802
                if self.path.endswith("/api/builds/hosts") and latest is not None:
                    fake.hosts_reads += 1
                    if hosts_503 < 0 or fake.hosts_reads <= hosts_503:
                        return self._send(503, {"error": "a deployment record could not be read"})
                    worker = self.headers.get("Authorization") == "Bearer worker-tok"
                    return self._send(200, worker_doc if worker else hosts_doc)
                return self._send(404, {})

            def do_POST(self):  # noqa: N802
                raw = self.rfile.read(int(self.headers["Content-Length"]))
                if self.path == WEBHOOK_PATH:
                    want = "sha256=" + hmac.new(TEST_SECRET.encode(), raw, hashlib.sha256).hexdigest()
                    if not hmac.compare_digest(want, self.headers.get("X-Hub-Signature-256") or ""):
                        return self._send(401, {"signature": "invalid"})
                    event = json.loads(raw)
                    if event.get("event") == DEPLOY_EVENT and deploy_status != 202:
                        fake.refused.append(event)
                        return self._send(deploy_status, {"error": "version 3.1.10400 already names another set"})
                    fake.events.append(event)
                    return self._send(202, {"signature": inbox_verdict})
                body = json.loads(raw)
                if self.headers.get("Authorization") != "Bearer mesh-token":
                    return self._send(401, {})
                if "id" not in body:
                    self.send_response(202)
                    self.end_headers()
                    return None
                if body["method"] != "tools/call":
                    return self._send(200, {"jsonrpc": "2.0", "id": body["id"], "result": {}})
                tool, args = body["params"]["name"], body["params"]["arguments"]
                fake.calls.append(tool)
                if tool == "create":
                    node = json.loads(args["node"])
                    path = f"{node['namespace']}/{node['id']}"
                    if refuse_create:
                        text = f"Error: creating node: Access denied: Create permission required for node '{path}'"
                    elif path in fake.nodes:
                        text = f"Error: creating node: Node already exists at path '{path}'"
                    else:
                        fake.nodes[path] = node
                        text = f"Created: {path}"
                elif tool == "patch":
                    node = fake.nodes.get(args["path"])
                    fields = json.loads(args["fields"])["content"]
                    if node is None:
                        text = f"Error: patching {args['path']}: not found"
                    elif all(node["content"].get(k) == v for k, v in fields.items()):
                        text = f"No change: {args['path']} (v1)"
                    else:
                        node["content"].update(fields)
                        text = f"Patched: {args['path']}"
                elif tool == "search":
                    ns = args["query"].split()[0].split(":", 1)[1]
                    found = [{"path": p} for p in fake.nodes if p.rsplit("/", 1)[0] == ns]
                    text = json.dumps({"count": len(found), "truncated": truncated, "results": found})
                else:
                    text = "Error: unknown tool"
                result = {"content": [{"type": "text", "text": text}]}
                return self._send(200, {"jsonrpc": "2.0", "id": body["id"], "result": result})

            def log_message(self, *args):
                return

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    def __enter__(self) -> "_FakeControl":
        self.thread.start()
        return self

    def __exit__(self, *exc) -> None:
        self.server.shutdown()
        self.server.server_close()


def self_test() -> int:
    import shutil
    import tempfile
    resolver_path = os.environ.get("MW_RESOLVER") or os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                                   "resolve-platform.py")
    failures: list[str] = []
    total = 0

    def check(name: str, ok: bool, detail: object = "") -> None:
        nonlocal total
        total += 1
        if not ok:
            failures.append(f"{name} — {detail}")

    def git(root: str, *args: str) -> None:
        subprocess.run(["git", "-C", root, *args], check=True, capture_output=True,
                       env={**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@example.com",
                            "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.com"})

    def package(root: str, name: str, content: dict) -> None:
        os.makedirs(os.path.join(root, name), exist_ok=True)
        with open(os.path.join(root, name, "index.json"), "w", encoding="utf-8") as handle:
            json.dump({"id": name, "content": content}, handle, indent=2)

    capped = {"minMeshVersion": "3.1.10300", "maxMeshVersion": "3.1.10400", "capReason": "old Foo API",
              "capTracking": "acme/Feedback/migrate-capped"}
    ns = "svc/Feedback"
    logs: list[str] = []

    digests = {image: "sha256:" + c * 64 for image, c in (("mw-plugin-test", "1"), ("memex-portal-ai", "2"),
                                                          ("mw-build-host", "3"), ("mw-core-tests", "4"))}

    def registry(*absent: str):
        def resolve(image: str, tag: str):
            short = image.rsplit("/", 1)[-1]
            return None if short in absent or not tag.startswith("3.1.") else digests.get(short)
        return resolve

    def go(fake: _FakeControl, root: str, **over) -> str:
        args = dict(root=root, resolver_path=resolver_path, repo="Systemorph/Example",
                    build_api=fake.url + "/api/builds", mesh_url=fake.url, namespace=ns, token="mesh-token",
                    build_token="", say=logs.append, sha="c" * 40, webhook_url=fake.url + WEBHOOK_PATH,
                    webhook_secret=TEST_SECRET, resolve=registry(), outage_window=0.3, outage_poll=0.02)
        args.update(over)
        try:
            run(**args)
        except Red as error:
            return str(error)
        return ""

    def caps_of(fake: _FakeControl) -> list[dict]:
        published = [e for e in fake.events if e.get("event") == BUILD_CAPS_EVENT]
        return published[-1]["caps"] if published else [{"missing": True}]

    def deploys(fake: _FakeControl) -> list[dict]:
        return [e for e in fake.events if e.get("event") == DEPLOY_EVENT]

    with tempfile.TemporaryDirectory() as root:
        git(root, "init", "-q")
        package(root, "Free", {"minMeshVersion": "3.1.10300"})
        git(root, "add", "-A")
        git(root, "commit", "-q", "-m", "free")
        with _FakeControl() as fake:
            red = go(fake, root)
            check("NEGATIVE: a tree with no cap files nothing", red == "" and fake.nodes == {}
                  and "create" not in fake.calls, (red, fake.calls))
            check("NEGATIVE: a tree with no cap still PUBLISHES its (empty) cap set, so a removed cap is "
                  "deleted on control", len(fake.events) == 1 and caps_of(fake) == [], fake.events)
        package(root, "Capped", capped)
        git(root, "add", "-A")
        git(root, "commit", "-q", "-m", "cap")
        with _FakeControl() as fake:
            red = go(fake, root)
            path = f"{ns}/{item_id('Systemorph/Example', 'Capped', '3.1.10400')}"
            node = fake.nodes.get(path, {})
            message = node.get("content", {}).get("message", "")
            check("PUBLISH: a capped package is published with the highest COMPLETE deployed version in range (scaled to zero)",
                  caps_of(fake) == [{"package": "Capped", "floor": "3.1.10300", "cap": "3.1.10400",
                                     "version": "3.1.10398", "deployed": True, "reason": "old Foo API"}]
                  and fake.events[-1]["event"] == BUILD_CAPS_EVENT and fake.events[-1]["repo"] == "Systemorph/Example",
                  fake.events)
            check("NEGATIVE CONTROL: a cap WITH a complete deployment in range requests NO deployment",
                  deploys(fake) == [], deploys(fake))
            check("a capped package files ONE Feedback/Feedback item, submitted (no status)",
                  red == "" and list(fake.nodes) == [path] and node.get("nodeType") == NODE_TYPE
                  and "status" not in node.get("content", {}), (red, fake.nodes))
            check("the message names the package, the cap, latest, the distance, the age and the tracking",
                  all(s in message for s in ("Capped", "3.1.10400", "3.1.10412", "12 platform build(s)",
                                             "0 day(s)", "acme/Feedback/migrate-capped")), message)
            red = go(fake, root)
            check("anonymously, retention is read as the CAPS COUNT (never names) and a zero is warned, not red",
                  any("build-3.1.10398 is needed by 0 package cap(s)" in line and line.startswith("::warning::")
                      for line in logs), logs[-6:])
            check("a second day refreshes the SAME item, never a duplicate",
                  red == "" and list(fake.nodes) == [path] and fake.calls.count("create") == 2
                  and "patch" in fake.calls, (red, fake.calls))
            package(root, "Capped", {**capped, "maxMeshVersion": "3.1.10405"})
            go(fake, root)
            check("UPDATE: a moved cap publishes its new bounds (the node is updated in place)",
                  [c["cap"] for c in caps_of(fake)] == ["3.1.10405"], caps_of(fake))
            package(root, "Capped", {"minMeshVersion": "3.1.10300"})
            red = go(fake, root)
            check("REMOVE: a removed cap publishes a set without it (its node is deleted on control)",
                  red == "" and caps_of(fake) == [], caps_of(fake))
            check("removing the cap resolves its item",
                  fake.nodes.get(path, {}).get("content", {}).get("status") == "Resolved", list(fake.nodes))
            fake.nodes[f"{ns}/mesh-cap-systemorph-other-x-3-1-1"] = {"content": {}}
            go(fake, root)
            check("NEGATIVE: another repository's item is never touched",
                  fake.nodes[f"{ns}/mesh-cap-systemorph-other-x-3-1-1"]["content"] == {}, fake.nodes)
            # A repository whose name EXTENDS this one's (`Example` → `Example-Tools`): its open item
            # must survive this repository's cleanup (#7192 review — a bare slug prefix matched it).
            sibling = f"{ns}/{item_id('Systemorph/Example-Tools', 'Capped', '3.1.10400')}"
            fake.nodes[sibling] = {"content": {}}
            go(fake, root)
            check("a repository whose name extends this one's keeps its items (prefix is collision-safe)",
                  fake.nodes[sibling]["content"] == {}, fake.nodes[sibling])
        with _FakeControl() as fake:
            package(root, "Foo.Bar", capped)
            package(root, "Foo-Bar", capped)
            red = go(fake, root)
            filed = sorted(p for p in fake.nodes if "foo-bar" in p)
            check("two packages whose names slug alike (Foo.Bar, Foo-Bar) file TWO items, never one",
                  red == "" and len(filed) == 2, (red, filed))
            shutil.rmtree(os.path.join(root, "Foo.Bar"))
            shutil.rmtree(os.path.join(root, "Foo-Bar"))
        package(root, "Capped", capped)
        no_fit = [("3.1.10412", True), ("3.1.10399", False), ("3.1.10200", True)]
        with _FakeControl(versions=no_fit) as fake:
            red = go(fake, root)
            check("a cap with no COMPLETE deployment in range (10399 incomplete, decoy `ready: true`) publishes the version it NEEDS (the cap) and "
                  "says it is not deployed", caps_of(fake) and caps_of(fake)[0]["version"] == "3.1.10400"
                  and caps_of(fake)[0].get("deployed") is False, caps_of(fake))
            check("… and SENDS build-host-deploy-requested for it, signed, in the receiver's shape, reason cap:<package>",
                  red == "" and deploys(fake) == [{"event": DEPLOY_EVENT, "deployment": "Deployments/control",
                                                   "version": "3.1.10400", "testerDigest": digests["mw-plugin-test"],
                                                   "portalDigest": digests["memex-portal-ai"],
                                                   "runnerDigest": digests["mw-build-host"],
                                                   "coreTestsDigest": digests["mw-core-tests"], "reason": "cap:Capped"}]
                  and list(deploys(fake)[0]) == ["event", "deployment", "version", "testerDigest", "portalDigest",
                                                 "runnerDigest", "coreTestsDigest", "reason"], (red, deploys(fake)))
            check("… after the caps were published and the debt filed (a refused request leaves both in place)",
                  [e["event"] for e in fake.events] == [BUILD_CAPS_EVENT, DEPLOY_EVENT] and len(fake.nodes) == 1,
                  ([e["event"] for e in fake.events], list(fake.nodes)))
            check("… and the log says the 202 is ACCEPTED, not deployed",
                  any("ACCEPTED, not deployed" in line for line in logs), logs[-3:])
        with _FakeControl(versions=no_fit) as fake:
            go(fake, root, resolve=registry("mw-core-tests"))
            check("a set with no core-tests image is requested WITHOUT coreTestsDigest (the set names none)",
                  len(deploys(fake)) == 1 and "coreTestsDigest" not in deploys(fake)[0], deploys(fake))
        package(root, "Twin", capped)
        with _FakeControl(versions=no_fit) as fake:
            red = go(fake, root)
            check("two packages needing the SAME version send ONE request (reason: the first package)",
                  red == "" and [d["reason"] for d in deploys(fake)] == ["cap:Capped"], (red, deploys(fake)))
        shutil.rmtree(os.path.join(root, "Twin"))
        with _FakeControl(versions=no_fit) as fake:
            red = go(fake, root, deployment="Deployments/control")
            check("a deployment given as its record path is kept", [d["deployment"] for d in deploys(fake)] == ["Deployments/control"]
                  and red == "", (red, deploys(fake)))
            red = go(fake, root, deployment="a/b")
            check("a deployment that is not a record name is RED", "--deployment" in red, red)
        with _FakeControl(versions=no_fit) as fake:
            red = go(fake, root, resolve=None)
            check("NEGATIVE: a needed request with no registry credential is RED naming both, and sends nothing",
                  "ACR_USERNAME, ACR_PASSWORD" in red and deploys(fake) == [] and caps_of(fake)[0].get("deployed") is False,
                  (red, deploys(fake)))
        with _FakeControl() as fake:
            red = go(fake, root, resolve=None)
            check("… while a tree whose caps are all deployed needs NO registry credential",
                  red == "" and deploys(fake) == [], red)
        with _FakeControl(versions=no_fit) as fake:
            red = go(fake, root, resolve=registry("mw-build-host"))
            check("NEGATIVE: a set with no RUNNER image is RED naming it, and nothing is requested",
                  "mw-build-host:3.1.10400 does not exist" in red and deploys(fake) == [], red)
        with _FakeControl(versions=no_fit, deploy_status=409) as fake:
            red = go(fake, root)
            check("NEGATIVE: the inbox REFUSING the deploy request (409) is RED carrying the receiver's reason",
                  "REFUSED the build-host-deploy-requested event for 3.1.10400 (HTTP 409" in red
                  and "already names another set" in red and len(fake.refused) == 1, red)

        # ── GET /hosts: a 503 is an OUTAGE, retried — never an empty listing ──
        with _FakeControl(hosts_503=2) as fake:
            red = go(fake, root)
            check("a 503 from /hosts (a partial listing) is retried, then the run proceeds on the real listing",
                  red == "" and fake.hosts_reads == 3 and caps_of(fake)[0].get("deployed") is True and deploys(fake) == [],
                  (red, fake.hosts_reads, caps_of(fake)))
        with _FakeControl(hosts_503=-1) as fake:
            red = go(fake, root)
            check("NEGATIVE CONTROL: a 503 that outlasts the window is RED naming the OUTAGE — nothing published, "
                  "requested or filed (an empty listing would request a deployed version and drop retention)",
                  "OUTAGE" in red and fake.events == [] and fake.nodes == {} and fake.hosts_reads > 1,
                  (red, fake.hosts_reads, fake.events))

        # ── retention, reported: names with the worker token, the count without ──
        with _FakeControl(caps={"3.1.10398": (1, ["Capped"])}) as fake:
            logs.clear()
            red = go(fake, root, worker_token="worker-tok")
            check("with the worker token, `cappedBy` is read and names the package holding its version",
                  red == "" and any("cappedBy ['Capped'] — holds Capped" in line for line in logs), logs)
        with _FakeControl(caps={"3.1.10398": (2, ["Capped", "Private.Elsewhere"])}) as fake:
            logs.clear()
            red = go(fake, root, worker_token="worker-tok")
            check("NEGATIVE: the worker-only names of OTHER repositories' packages never reach the (public) log — "
                  "only this repository's own packages are named, the rest counted",
                  red == "" and not any("Private.Elsewhere" in line for line in logs)
                  and any("cappedBy ['Capped'] (+1 other package(s), names withheld) — holds Capped" in line
                          for line in logs), logs)
        with _FakeControl(caps={"3.1.10398": (1, ["Capped"])}) as fake:
            logs.clear()
            red = go(fake, root)
            check("NEGATIVE: anonymously the names are never read — only the count",
                  red == "" and any("needed by 1 package cap(s)" in line for line in logs)
                  and not any("cappedBy" in line for line in logs), logs)
        with _FakeControl(caps={"3.1.10398": (1, [])}) as fake:
            logs.clear()
            red = go(fake, root, worker_token="worker-tok")
            check("a worker listing that does not name the package is a WARNING (the intake is asynchronous), not red",
                  red == "" and any(line.startswith("::warning::") and "does NOT name Capped" in line for line in logs), logs)
        with _FakeControl(latest=None) as fake:
            red = go(fake, root)
            check("an unreadable build API is RED, publishes and files nothing",
                  "HTTP 404" in red and fake.nodes == {} and fake.events == [], red)
        with _FakeControl() as fake:
            red = go(fake, root, webhook_secret="a-drifted-secret")
            check("a drifted inbox secret is RED (the inbox refuses the signature)",
                  "HTTP 401" in red and fake.events == [], red)
        with _FakeControl(inbox_verdict="not-required") as fake:
            red = go(fake, root)
            check("an inbox that did not check the signature is RED", "WITHOUT checking" in red, red)
        with _FakeControl() as fake:
            red = go(fake, root, webhook_url="https://elsewhere.example.com/api/hooks/Hosting/PlatformBuilds")
            check("nothing is signed for another host", "not on control.systemorph.com" in red
                  and fake.events == [], red)
            red = go(fake, root, token="")
            check("a missing mesh token is RED naming it", TOKEN_ENV in red, red)
            red = go(fake, root, webhook_secret="")
            check("a missing inbox secret is RED naming it", WEBHOOK_SECRET_ENV in red, red)
            red = go(fake, root, namespace="")
            check("a missing namespace is RED naming it", "--namespace" in red, red)
            red = go(fake, root, mesh_url="http://control.example.com")
            check("a plain-http mesh URL off loopback is refused (the token would travel in clear)",
                  "--mesh-url" in red and fake.calls == [], (red, fake.calls))
            red = go(fake, root, token="wrong")
            check("a token the mesh refuses is RED", "HTTP 401" in red, red)
        with _FakeControl(refuse_create=True) as fake:
            red = go(fake, root)
            check("a refused create is RED, never 'filed'", "was not created" in red, red)
        with _FakeControl(truncated=True) as fake:
            red = go(fake, root)
            check("a truncated listing is RED (a removed cap's item would stay open)", "truncated" in red, red)
    check("the item id is keyed by (repository, package, cap)",
          item_id("O/R", "P", "3.1.1") != item_id("O/R", "P", "3.1.2") != item_id("O/S", "P", "3.1.2"))
    check("slug-alike packages and repositories get distinct ids",
          item_id("O/R", "Foo.Bar", "3.1.1") != item_id("O/R", "Foo-Bar", "3.1.1")
          and item_id("O/R", "P", "3.1.1") != item_id("o.r", "P", "3.1.1"))
    check("no repository's prefix is a prefix of another's whose name extends it",
          not item_id("O/R-Tools", "P", "3.1.1").startswith(repo_prefix("O/R"))
          and item_id("O/R", "P", "3.1.1").startswith(repo_prefix("O/R")))

    if failures:
        print(f"✗ cap-debt-triage self-test: {len(failures)} failure(s)")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print(f"✓ cap-debt-triage self-test: {total} cases — every capped package is published to the signed "
          "inbox with the deployed version it builds on (or the one it needs), a version no complete deployment "
          "provides is REQUESTED (build-host-deploy-requested, reason cap:<package>, the set's digests; a 409 "
          "refusal and a missing runner are RED), a /hosts 503 is an outage retried and never an empty list, "
          "retention is read as cappedBy with the worker token and as the caps count without, a moved cap is updated and a "
          "removed one dropped from the set, one submitted Feedback/Feedback item per capped package is "
          "refreshed (never duplicated) and resolved when the cap goes, and every missing or refused input "
          "is RED")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--root", default=".")
    parser.add_argument("--resolver", default="")
    parser.add_argument("--repo", default="")
    parser.add_argument("--sha", default="")
    parser.add_argument("--build-api", default="")
    parser.add_argument("--mesh-url", default="")
    parser.add_argument("--namespace", default="")
    parser.add_argument("--control-webhook-url", default="")
    parser.add_argument("--control-webhook-host", default=DEFAULT_WEBHOOK_HOST)
    parser.add_argument("--deployment", default=DEFAULT_DEPLOYMENT,
                        help="the control instance's Hosting/Deployment record (core .github/control-instance.json)")
    arguments = parser.parse_args()
    if arguments.self_test:
        return self_test()
    try:
        return run(arguments.root, arguments.resolver, arguments.repo, arguments.build_api,
                   arguments.mesh_url, arguments.namespace, os.environ.get(TOKEN_ENV, ""),
                   os.environ.get("MW_BUILD_API_TOKEN", ""), sha=arguments.sha,
                   webhook_url=arguments.control_webhook_url,
                   webhook_secret=os.environ.get(WEBHOOK_SECRET_ENV, ""),
                   webhook_host=arguments.control_webhook_host, deployment=arguments.deployment,
                   worker_token=os.environ.get(WORKER_TOKEN_ENV, ""),
                   registry_user=os.environ.get(REGISTRY_USER_ENV, ""),
                   registry_password=os.environ.get(REGISTRY_PASSWORD_ENV, ""))
    except Red as error:
        print("::error title=Cap debt could not be published or filed::" + str(error).replace("\n", "%0A"))
        return 1


if __name__ == "__main__":
    sys.exit(main())

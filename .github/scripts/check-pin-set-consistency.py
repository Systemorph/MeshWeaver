#!/usr/bin/env python3
"""check-pin-set-consistency.py — do all of a repository's platform pins name ONE promoted build?

(The name on this first line is load-bearing in the same way compile-check.py's and
check-pinned-digests.py's are: a lane that fetches this file at the platform ref refuses a body
whose first 400 bytes do not name it.)

WHY THIS EXISTS (MeshWeaver#3454, 2026-09-06)
---------------------------------------------
A satellite reaches for the platform under several identities that must all come from the SAME
promoted build — the tester image it runs its gates inside, the portal image its modules compile
against, and the core commit whose shared scripts the reusable lanes execute. Each identity is
written down in MORE THAN ONE PLACE, because a reusable workflow's `with:` cannot read the
workflow `env:`. Nothing in GitHub Actions relates one copy to another, so a pin move that edits
one copy and misses another is GREEN, and the divergence surfaces days later as a framework
mismatch on a portal, pointing at nothing.

MeshWeaver.Plugins and MeshWeaver.Reinsurance have carried `scripts/check-platform-pins.py`
against exactly this since 2026-09-01. Measured 2026-09-06, MeshWeaver.SocialMedia,
MeshWeaver.Manufacturing and MeshWeaver.Crm had **zero matches** for any such gate:

    repo                       pin-consistency gate      literal pin sites a move must pass
    MeshWeaver.Plugins         yes                       14
    MeshWeaver.Reinsurance     yes                       12
    MeshWeaver.SocialMedia     NONE                       4
    MeshWeaver.Manufacturing   NONE                       3
    MeshWeaver.Crm             NONE                       4

🚨 That is not a gate that SKIPS. It is a gate that does not exist, so there is nothing to notice.
The precedent is on record and it was expensive: a withdrawal that took a line it did not add
dropped a fourth pin, took core CD down for an hour, and every check was green (#3344).

WHAT THIS IS, AND WHAT IT IS NOT
--------------------------------
It is NOT `check-pinned-digests.py` (#3462). That one asks a DIFFERENT question of the same
subject — "does each pinned digest still EXIST in the registry?" — org-wide, nightly, against ACR.
It is the answer to #3454's requirement 2 and it needs no help here.

This one asks requirement 1: **do the pins in one repository agree with each other, and do they
name ONE promoted build?** Two manifests that both exist perfectly satisfy #3462 and are still the
whole defect when one is `ci.7917` and the other `ci.7574`.

The two scripts deliberately share their extraction SHAPES (an `env:` assignment, a `$GITHUB_OUTPUT`
step output, an inline `…/repo@sha256:…`), so they can never disagree about what a pin IS.

THE INVARIANTS
--------------
I1  SAME NAME, ONE VALUE — every literal pin site sharing a declaration name within one repository
    carries the same value. `platform-image-digest:` is written twice in each of the three repos
    above (the gate lane and the publish-bake lane); `platform-image:` likewise. Editing one is the
    #3344 shape exactly.

I2  SAME IMAGE, ONE DIGEST — every literal digest resolving to the SAME ACR repository carries the
    same value, EVEN UNDER DIFFERENT NAMES. On 2026-08-31 MeshWeaver.Plugins' `80ebad09` moved
    `MW_PORTAL_IMAGE_DIGEST` and left both `platform-image-digest:` literals behind; I1 alone
    cannot see that, because each name was internally consistent.

I3  A LANE CALL IS INTERNALLY COHERENT — a `uses: Systemorph/MeshWeaver/.github/workflows/X@<sha>`
    call that also passes a LITERAL `platform-ref:` must pass its OWN sha. The lane executes core's
    `.github/scripts/*` at `platform-ref`, so a lane newer than its ref calls a script that does not
    exist there: MeshWeaver.SocialMedia run 33073476996 died on
    `can't open file …/node-repo-scope.py`, and the same file's header records the state that
    produced it — "`uses:` was 04a2401c6, `platform-ref` 94d18f9cb and MW_PLATFORM_REF 89f886275 —
    three different commits under a comment asserting they are one. **Nothing in CI compares these
    three lines**". This is that comparison.

    🚨 It compares a call against ITSELF and never against another call. Two lanes at two different
    core commits is the NORMAL state — SocialMedia's validate/tag-modules/compile-check sit at
    `0a2b9017` while its gate and publish-bake ride `1b5350d5`, and its own comment says so:
    "lane DEFINITION pins, not this platform pin; they move with the shared-workflow adoption
    work." A gate demanding one sha per repo would red that, and a gate that must be bypassed is
    worse than none. Equally deliberately, `MW_PLATFORM_REF` is NOT required to equal a lane's sha:
    the image an env pins and the source a lane runs are two different objects (Plugins#1268 moved
    two of them together and went red; its landed fix splits them again).

I4  NO PLACEHOLDER VACUITY — a pin-shaped declaration whose value ATTEMPTS to be a digest without
    being one is a FAILURE, never an absence. `check-platform-pins.py` matches `sha256:[0-9a-f]+`,
    so `sha256:PLACEHOLDER` reads to it as "no literal pin present" and it passes having checked
    nothing; a truncated `sha256:df19f10a` reads to it as a perfectly good pin. Both are red here.

I5  NO UNCLASSIFIED PIN — a literal digest whose IMAGE cannot be determined is a FAILURE. This is
    the denominator rule applied to a single site: a pin the extractor cannot place has not been
    checked, and "not checked" must never be spelled the same way as "consistent". It is also what
    keeps the small alias table below from becoming the trap it exists to avoid — a repository that
    starts pinning under a NEW name reddens and names it, rather than passing silently.

I6  THE DENOMINATOR IS PRINTED — "N pin sites found, of which M classified, over K image roles" —
    and under `--require-pins` a repository that yields ZERO is RED. 🚨 This is the trap that was
    walked into four separate times on 2026-09-06, twice while relaying a warning about it:
    MeshWeaver.Education pins under `MW_PORTAL_DIGEST` / `MW_MIGRATION_DIGEST` / `MW_TEST_DIGEST`,
    so a sweep keyed on the two common names reports it CLEAN while verifying none of its pins.

I8  NO ORPHANED SOURCE REF — a literal `MW_PLATFORM_REF`-shaped core commit must be named by at
    least one platform lane this repository actually calls. It is deliberately NOT "MW_PLATFORM_REF
    equals a lane's sha": several lanes legitimately sit at older DEFINITION pins while the platform
    pin moves. What it refuses is the value being moved ALONE — the orphan that SocialMedia's own
    header records ("three different commits under a comment asserting they are one"). A BRANCH name
    has no identity to compare and is reported, not judged; whether a bare branch is an acceptable
    source at all is a different gate's question.

I7  ONE PROMOTED BUILD (`--check-tags`, needs the registry) — every DISTINCT manifest a repository
    pins must share at least one TAG with every other. A digest carries no tag, so this is the only
    way to relate two of them, and it works without the repository naming its build at all: the
    three repos here declare no `MW_PLATFORM_SET`. Measured 2026-09-06 — the tester
    `mw-plugin-test@sha256:642f686f…` and the portal `memex-portal-ai@sha256:31aab07d…` share
    `3.0.0-ci.7917`, `cb675ec` and `staging-cb675ec-34048513298`; a half-moved set shares nothing.
    Where a `MW_PLATFORM_SET`-shaped name IS declared it must be among the shared tags, which turns
    the build's NAME from a comment into an assertion.

USAGE
-----
    check-pin-set-consistency.py --root .                 static arms (I1-I6) over a checkout
    check-pin-set-consistency.py --root . --require-pins  …and zero classified pins is RED
    check-pin-set-consistency.py --repos Systemorph/A,…   read the workflows over the API instead
    check-pin-set-consistency.py --discover               …for every repo the App installation sees
    check-pin-set-consistency.py … --check-tags           add I7 (needs `az`, logged in)
    check-pin-set-consistency.py --self-test              prove every arm FIRES and stays silent

Exit 0 only when every arm asked for passes. Every failure is a `::error` naming the repository,
the file, the line and the two values that disagree.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field

REGISTRY_DEFAULT = "meshweaver"
REGISTRY_HOST_RE = r"(?P<host>[a-z0-9]+)\.azurecr\.io"

# A digest literal, in the only form a registry accepts: sha256 and exactly 64 lowercase hex.
DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")

# ── Shapes. Deliberately the same three check-pinned-digests.py extracts, so the two gates can
#    never disagree about what a pin IS. ─────────────────────────────────────────────────────────

# Shape 1 — `NAME: value` at any indent, value optionally quoted, trailing comment tolerated.
ASSIGN_RE = re.compile(
    r"^(?P<indent>[ \t]*)(?:-[ \t]+)?(?P<name>[A-Za-z_][A-Za-z0-9_.-]*)[ \t]*:[ \t]*"
    r"(?P<quote>['\"]?)(?P<value>[^\r\n]*?)(?P=quote)[ \t]*(?:#[^\r\n]*)?$",
    re.MULTILINE,
)

# Shape 2 — a shell assignment into $GITHUB_OUTPUT. SocialMedia and Crm pin their tester digest
# this way, which is invisible to any scan of `env:` blocks.
#
# 🚨 IT MATCHES AN ATTEMPTED DIGEST, NOT A WELL-FORMED ONE, and that is the whole point. This gate's
# own first draft required `sha256:[0-9a-f]{64}` here, and the both-ways falsification on the REAL
# ci.yml of the three repos it was written for caught it: `image-digest=sha256:PLACEHOLDER` matched
# NOTHING, so SocialMedia and Crm — whose tester pin is written exactly this way — went GREEN over a
# placeholder while Manufacturing (whose tester pin is an `env:` key) went red. The vacuity trap
# this file exists to close, reproduced inside it, in the shape the file itself had not covered.
# Classification into pin / MALFORMED happens below, once, for both shapes.
OUTPUT_RE = re.compile(
    r"(?P<name>[A-Za-z_][A-Za-z0-9_.-]*)=(?P<quote>['\"]?)(?P<value>sha256:[^\s'\"]*)(?P=quote)"
)

# Shape 3 — an image reference binding an ACR repository to a digest, literally or through a name.
INLINE_LITERAL_RE = re.compile(
    REGISTRY_HOST_RE + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*)@(?P<digest>sha256:[0-9a-f]{64})"
)
INLINE_VAR_RE = re.compile(
    REGISTRY_HOST_RE + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*)@\$\{\{\s*"
    r"(?:env|steps\.[A-Za-z0-9_-]+\.outputs)\.(?P<name>[A-Za-z0-9_.-]+)\s*\}\}"
)
FORMAT_VAR_RE = re.compile(
    r"format\(\s*'" + REGISTRY_HOST_RE + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*)@\{0\}'\s*,\s*"
    r"(?:env|steps\.[A-Za-z0-9_-]+\.outputs)\.(?P<name>[A-Za-z0-9_.-]+)\s*\)"
)

# A literal image NAME with no digest — `platform-image: meshweaver.azurecr.io/memex-portal-ai`.
IMAGE_NAME_RE = re.compile(r"^" + REGISTRY_HOST_RE + r"/(?P<repo>[A-Za-z0-9][A-Za-z0-9._/-]*)$")

# A reusable-workflow call into the platform, and the ref it is pinned at.
LANE_CALL_RE = re.compile(
    r"^(?P<indent>[ \t]*)uses:[ \t]*['\"]?Systemorph/MeshWeaver/"
    r"(?P<lane>\.github/workflows/[A-Za-z0-9._-]+)@(?P<ref>[^\s'\"#]+)",
    re.MULTILINE,
)

# A declaration is PIN-SHAPED when its name says digest. Same predicate as check-pinned-digests.py.
PIN_NAME_RE = re.compile(r"(?i)(?:^|[^a-z])digest(?:[^a-z]|$)|digest$|^digest")

# 🚨 Narrow on purpose, and for the same reason it is narrow in check-pinned-digests.py: the value
# ATTEMPTS to be a digest (it starts with `sha256`) and is not one. Not "any pin-shaped name whose
# value is not a digest" — the text scan reads `run:` shell and nested mappings too, where
# `digest: $D` and a bare `digest:` with a block under it are both perfectly ordinary.
ATTEMPTED_DIGEST_RE = re.compile(r"(?i)^sha256")

# A `${{ … }}` value forwards another declaration; the thing it forwards is extracted where it is
# declared, so a forward is neither an independent copy nor a defect. Comparing expression TEXT
# would red a workflow that is correct by construction.
EXPRESSION_RE = re.compile(r"\$\{\{.*\}\}")

# A 40-character git commit. A lane ref that is anything else (a tag, a branch) is reported and not
# compared: whether a bare branch is an acceptable platform source is a DIFFERENT gate's question
# (MeshWeaver.Plugins' check_core_refs, #724) and the two must not be folded.
SHA40_RE = re.compile(r"^[0-9a-f]{40}$")

# The name of a promoted build, e.g. `3.0.0-ci.7917`. Matched only to decide whether a declared
# set name can be ASSERTED against the registry; its absence is reported, never inferred as clean.
PLATFORM_SET_NAME_RE = re.compile(r"(?i)platform.?set")

# A name that declares the platform SOURCE — the core commit this repository's lanes run at.
# `MW_PLATFORM_REF`, and the `platform-ref:` lane input (which I3 already pairs with its own call,
# so it is excluded here to keep one site from being judged by two arms).
PLATFORM_REF_NAME_RE = re.compile(r"(?i)platform.?ref")

# ── The alias table, and why it cannot become the trap it exists to avoid ───────────────────────
#
# A digest's image is resolved from the FILE wherever the file says it (an inline `…/repo@…`, an
# `@${{ env.X }}` binding, a `format('…/repo@{0}', env.X)`, or a sibling `<prefix>:` naming the
# image whose `<prefix>-digest:` this is). Measured 2026-09-06, that resolves every pin in
# MeshWeaver.Education, and both `platform-image-digest:` sites in each of SocialMedia,
# Manufacturing and Crm.
#
# THREE names remain unbindable from the text, because the image they pair with is a repository
# VARIABLE (`vars.MW_TEST_IMAGE`) that no file spells out. They are listed here, with the repo that
# writes them, and NOTHING ELSE IS ASSUMED: a digest under any other unbound name is I5-RED and
# says so. That is the difference between a name-keyed sweep (which reports Education clean while
# checking none of its pins) and a name-keyed FALLBACK whose miss is a failure.
ROLE_ALIASES: dict[str, str] = {
    # Plugins, Reinsurance, Manufacturing: the tester image the gates run inside.
    "MW_IMAGE_DIGEST": "mw-plugin-test",
    # SocialMedia, Crm: the same pin, written into a $GITHUB_OUTPUT by the `pin` step.
    "image-digest": "mw-plugin-test",
    # Plugins, Reinsurance: the portal image every module compiles against (core #940).
    "MW_PORTAL_IMAGE_DIGEST": "memex-portal-ai",
}

# The registry's own words for "that manifest is not here". Anything else az says is INDETERMINATE
# — a credential, a network or a service fault, never evidence of absence.
ABSENT_MARKERS = ("manifest unknown", "manifestunknown", "not found", "notfound")


@dataclass
class Site:
    """One literal pin, and where it is written."""

    filename: str
    line: int
    name: str          # the declaration name, or ":inline" for a bare image reference
    value: str
    kind: str          # "digest" | "image-name"
    role: str | None = None       # the ACR repository, once resolved
    how: str = ""                 # how the role was resolved — printed, so it can be argued with


@dataclass
class LaneCall:
    filename: str
    line: int
    lane: str
    ref: str
    platform_ref: str | None = None
    platform_ref_line: int = 0


@dataclass
class Malformed:
    filename: str
    line: int
    name: str
    value: str


@dataclass
class RepoScan:
    gh_repo: str
    workflows: int = 0
    sites: list[Site] = field(default_factory=list)
    lanes: list[LaneCall] = field(default_factory=list)
    malformed: list[Malformed] = field(default_factory=list)
    platform_set: tuple[str, str] | None = None   # (name, value)
    platform_refs: list[tuple[str, int, str, str]] = field(default_factory=list)
    unreadable: str | None = None


def _line_of(text: str, pos: int) -> int:
    return text.count("\n", 0, pos) + 1


def _indent_width(s: str) -> int:
    return len(s.replace("\t", "    "))


# ── Extraction ─────────────────────────────────────────────────────────────────────────────────


def extract(filename: str, text: str) -> tuple[
    list[Site], list[LaneCall], list[Malformed], tuple[str, str] | None,
    list[tuple[str, int, str, str]],
]:
    sites: list[Site] = []
    lanes: list[LaneCall] = []
    malformed: list[Malformed] = []
    platform_set: tuple[str, str] | None = None
    platform_refs: list[tuple[str, int, str, str]] = []

    # name -> ACR repository, wherever the file wires the two together.
    bindings: dict[str, str] = {}
    for pattern in (INLINE_VAR_RE, FORMAT_VAR_RE):
        for match in pattern.finditer(text):
            bindings.setdefault(match.group("name"), match.group("repo"))

    # A literal image NAME under `<prefix>:` binds every `<prefix>-digest:` / `<prefix>_DIGEST:`.
    # This is what resolves `platform-image-digest:` from its `platform-image:` sibling — the shape
    # all three ungated repos write, and the one Plugins writes for `tester-image-digest:` too.
    image_names: dict[str, str] = {}
    for match in ASSIGN_RE.finditer(text):
        value = match.group("value").strip()
        named = IMAGE_NAME_RE.match(value)
        if named:
            name = match.group("name")
            image_names[name] = named.group("repo")
            sites.append(Site(filename, _line_of(text, match.start()), name, value, "image-name",
                              role=named.group("repo"), how="a literal image reference"))

    def role_for(name: str) -> tuple[str | None, str]:
        if name in bindings:
            return bindings[name], f"the file binds it to meshweaver.azurecr.io/{bindings[name]}"
        for suffix in ("-digest", "_DIGEST", "-DIGEST", "_digest"):
            if name.endswith(suffix):
                prefix = name[: -len(suffix)]
                if prefix in image_names:
                    return image_names[prefix], f"its sibling `{prefix}:` names that image"
        if name in ROLE_ALIASES:
            return ROLE_ALIASES[name], "a declared alias (the image it pairs with is a repo VARIABLE)"
        return None, ""

    # Shape 3 first: a literal `…/repo@sha256:…` binds its own image with no inference at all.
    for match in INLINE_LITERAL_RE.finditer(text):
        sites.append(Site(filename, _line_of(text, match.start()), ":inline", match.group("digest"),
                          "digest", role=match.group("repo"), how="a literal image reference"))

    # Shape 1 — and the I4 vacuity guard, which lives here because this is the only shape in which
    # a placeholder can masquerade as an absence.
    for match in ASSIGN_RE.finditer(text):
        name, value = match.group("name"), match.group("value").strip()
        line = _line_of(text, match.start())
        if PLATFORM_SET_NAME_RE.search(name) and value and not EXPRESSION_RE.search(value):
            platform_set = (name, value)
        # I8's subject. A BRANCH name here has no identity to compare — an unpinned lane simply
        # moves with core — so only a 40-character commit is read, and anything else is reported
        # by the SOURCE-identity question, which is a different gate (Plugins' check_core_refs).
        if (name != "platform-ref" and PLATFORM_REF_NAME_RE.search(name)
                and SHA40_RE.match(value)):
            platform_refs.append((filename, line, name, value))
        if not PIN_NAME_RE.search(name):
            continue
        if EXPRESSION_RE.search(value):
            continue
        if DIGEST_RE.match(value):
            role, how = role_for(name)
            sites.append(Site(filename, line, name, value, "digest", role=role, how=how))
        elif ATTEMPTED_DIGEST_RE.match(value):
            malformed.append(Malformed(filename, line, name, value))

    # Shape 2 — a step output. `>> "$GITHUB_OUTPUT"` makes it an env value for every later job, so
    # it is a pin in every sense that matters.
    for match in OUTPUT_RE.finditer(text):
        name, value = match.group("name"), match.group("value")
        if not PIN_NAME_RE.search(name):
            continue
        line = _line_of(text, match.start())
        if DIGEST_RE.match(value):
            role, how = role_for(name)
            sites.append(Site(filename, line, name, value, "digest", role=role, how=how))
        else:
            malformed.append(Malformed(filename, line, name, value))

    # I3 — every reusable-workflow call into the platform, paired with the LITERAL `platform-ref:`
    # inside ITS OWN job block.
    #
    # 🚨 The block is the JOB, not "everything after the `uses:` line". `uses:` and `with:` are
    # SIBLINGS under the job key, so a scan that stops at the first line indented no deeper than
    # `uses:` stops on `with:` itself and finds nothing — a pairing that silently never fires,
    # which is this gate's own defect class turned inward. So: walk BACK to the job key (the
    # nearest preceding line indented less than `uses:`), then forward until the block ends.
    lines = text.splitlines()

    def _significant(idx: int) -> bool:
        raw = lines[idx]
        return bool(raw.strip()) and not raw.lstrip().startswith("#")

    for match in LANE_CALL_RE.finditer(text):
        start = _line_of(text, match.start())          # 1-based
        own_indent = _indent_width(match.group("indent"))
        call = LaneCall(filename, start, match.group("lane"), match.group("ref"))

        header = start - 1                              # 0-based index of the `uses:` line
        job_indent = 0
        while header > 0:
            header -= 1
            if not _significant(header):
                continue
            raw = lines[header]
            indent = _indent_width(raw[: len(raw) - len(raw.lstrip())])
            if indent < own_indent:
                job_indent = indent
                break
        else:
            header = 0

        for offset in range(header + 1, len(lines)):
            if not _significant(offset):
                continue
            raw = lines[offset]
            indent = _indent_width(raw[: len(raw) - len(raw.lstrip())])
            if indent <= job_indent:
                break
            inner = ASSIGN_RE.match(raw)
            if inner and inner.group("name") == "platform-ref":
                value = inner.group("value").strip()
                if value and not EXPRESSION_RE.search(value):
                    call.platform_ref = value
                    call.platform_ref_line = offset + 1
                break
        lanes.append(call)

    return sites, lanes, malformed, platform_set, platform_refs


# ── Reading a repository: a local checkout, or the API ──────────────────────────────────────────


def scan_local(root: str, gh_repo: str) -> RepoScan:
    # 🚨 `.github/workflows` AND NOTHING ELSE, structurally. MeshWeaver.Plugins'
    # `clients/react/src/i18n/catalog-source.json` holds the SAME core sha as the platform pins and
    # is NOT one: it is the commit the i18n mirror's drift guard compares VALUES against. A
    # repo-wide grep-and-replace moves it silently; a gate that read it would then demand it move
    # WITH the platform set, which is the same mistake with a red build attached. The exclusion is
    # a property of where this scan looks, not a rule anyone has to remember.
    scan = RepoScan(gh_repo=gh_repo)
    wf_dir = os.path.join(root, ".github", "workflows")
    if not os.path.isdir(wf_dir):
        scan.unreadable = (
            f"{wf_dir} does not exist, so this checkout's pins were never looked for "
            "(not 'it has no pins')."
        )
        return scan
    names = sorted(n for n in os.listdir(wf_dir) if n.endswith((".yml", ".yaml")))
    scan.workflows = len(names)
    for name in names:
        with open(os.path.join(wf_dir, name), encoding="utf-8", errors="replace") as handle:
            text = handle.read()
        sites, lanes, malformed, platform_set, platform_refs = extract(name, text)
        scan.sites.extend(sites)
        scan.lanes.extend(lanes)
        scan.malformed.extend(malformed)
        scan.platform_refs.extend(platform_refs)
        scan.platform_set = scan.platform_set or platform_set
    return scan


def gh_api(path: str, paginate: bool = False, jq: str | None = None) -> tuple[int, str, str]:
    cmd = ["gh", "api", "-H", "X-GitHub-Api-Version: 2022-11-28", path]
    if jq:
        cmd += ["--jq", jq]
    if paginate:
        cmd.insert(2, "--paginate")
    proc = subprocess.run(cmd, capture_output=True, text=True)
    return proc.returncode, proc.stdout, proc.stderr


def discover_repos() -> list[str]:
    """Every repository the App installation reaches — discovered, never committed."""
    # 🚨 LET `gh` DO THE EXTRACTION. `gh api --paginate` emits one JSON DOCUMENT PER PAGE, so
    # recovering the pages by splitting text is guesswork, and guesswork that loses a page loses a
    # REPOSITORY — which then appears in the report neither as a failure nor as a measured zero.
    rc, out, err = gh_api("/installation/repositories", paginate=True,
                          jq=".repositories[].full_name")
    if rc != 0:
        die("could not enumerate the App installation's repositories.",
            ["This needs an INSTALLATION token (GH_TOKEN from actions/create-github-app-token).",
             "Locally, pass --repos instead.",
             f"gh said: {err.strip()[:400]}"])
    names = sorted({line.strip() for line in out.splitlines() if line.strip()})
    if not names:
        die("the App installation reaches ZERO repositories.",
            ["A sweep over an empty fleet reports 'every pin agrees' having checked nothing."])
    return names


def scan_remote(gh_repo: str) -> RepoScan:
    scan = RepoScan(gh_repo=gh_repo)
    rc, out, err = gh_api(f"repos/{gh_repo}/contents/.github/workflows")
    if rc != 0:
        blob = (err + out).lower()
        if "404" in blob or "not found" in blob:
            # 🚨 A 404 HERE HAS TWO CAUSES AND THEY ARE OPPOSITE VERDICTS: the repository is fine
            # and has no `.github/workflows` (a MEASURED ZERO), or it is not there / not readable
            # by this token (NOT CHECKED). Ask about the REPOSITORY before believing the absence.
            rc_repo, _, err_repo = gh_api(f"repos/{gh_repo}")
            if rc_repo != 0:
                scan.unreadable = ("the repository itself could not be read, so its pins were never "
                                   f"looked for. gh said: {err_repo.strip()[:200]}")
            return scan
        scan.unreadable = err.strip()[:400] or "unknown error listing .github/workflows"
        return scan
    try:
        entries = json.loads(out)
    except json.JSONDecodeError as exc:
        scan.unreadable = f".github/workflows listing is not JSON: {exc}"
        return scan
    for entry in entries:
        name = entry.get("name", "")
        if entry.get("type") != "file" or not name.endswith((".yml", ".yaml")):
            continue
        rc, body, err = gh_api(f"repos/{gh_repo}/contents/{entry['path']}")
        if rc != 0:
            scan.unreadable = f"could not read {entry['path']}: {err.strip()[:200]}"
            return scan
        try:
            doc = json.loads(body)
            text = base64.b64decode(doc["content"]).decode("utf-8", "replace")
        except (json.JSONDecodeError, KeyError, ValueError) as exc:
            scan.unreadable = f"could not decode {entry['path']}: {exc}"
            return scan
        scan.workflows += 1
        sites, lanes, malformed, platform_set, platform_refs = extract(name, text)
        scan.sites.extend(sites)
        scan.lanes.extend(lanes)
        scan.malformed.extend(malformed)
        scan.platform_refs.extend(platform_refs)
        scan.platform_set = scan.platform_set or platform_set
    return scan


# ── The registry, for I7 ───────────────────────────────────────────────────────────────────────


def az(args: list[str]) -> tuple[int, str, str]:
    proc = subprocess.run(["az", *args], capture_output=True, text=True)
    return proc.returncode, proc.stdout, proc.stderr


def registry_control_probe(registry: str) -> None:
    """Prove the registry ANSWERS before reading any absence as evidence.

    Without this, one refused credential turns every pin in the fleet into "cannot be related" and
    the gate reports a catastrophe that is not happening — the mirror image of what it catches.
    """
    rc, out, err = az(["acr", "repository", "list", "--name", registry, "-o", "json"])
    if rc != 0:
        die(f"cannot reach the container registry '{registry}'.",
            ["Nothing was checked. A gate that cannot reach its subject has not checked it, so it",
             "fails closed instead of reporting a verdict.",
             f"az said: {err.strip()[:400]}"])
    try:
        repos = json.loads(out)
    except json.JSONDecodeError as exc:
        die(f"'az acr repository list' did not return JSON: {exc}", [])
    if not repos:
        die(f"registry '{registry}' lists ZERO repositories.",
            ["Every manifest would read as absent, so this is an input fault, not a verdict."])
    print(f"registry {registry}: reachable, {len(repos)} repositories.")


def acr_tags(registry: str, acr_repo: str, digest: str, cache: dict) -> tuple[list[str] | None, str]:
    """The tags on one manifest. `None` means NOT ANSWERED — never 'it has no tags'."""
    key = (acr_repo, digest)
    if key in cache:
        return cache[key]
    rc, out, err = az(["acr", "repository", "show", "--name", registry,
                       "--image", f"{acr_repo}@{digest}", "-o", "json"])
    if rc == 0:
        try:
            result = (list(json.loads(out).get("tags") or []), "")
        except json.JSONDecodeError as exc:
            result = (None, f"INDETERMINATE: az returned no JSON ({exc})")
    else:
        blob = err.lower()
        if any(marker in blob for marker in ABSENT_MARKERS):
            result = (None, "GONE: the manifest no longer exists in the registry")
        else:
            result = (None, "INDETERMINATE: " + " ".join(err.split())[:300])
    cache[key] = result
    return result


# ── Reporting ──────────────────────────────────────────────────────────────────────────────────


def die(headline: str, lines: list[str]) -> None:
    print(f"::error::{headline}")
    for line in lines:
        print(f"  {line}")
    sys.exit(1)


def emit(text: str) -> None:
    print(text)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text + "\n")


def _where(site: Site) -> str:
    return f"{site.filename}:{site.line}:{site.name}"


def check_static(scan: RepoScan, require_pins: bool) -> list[str]:
    """I1-I6 and I8 over one repository — every arm that needs no registry.

    Returns the failure verdicts; prints the DENOMINATOR either way, because a report that only
    appears when something is wrong cannot distinguish a clean repository from an unread one.
    """
    failures: list[str] = []
    digests = [s for s in scan.sites if s.kind == "digest"]
    classified = [s for s in digests if s.role]
    unclassified = [s for s in digests if not s.role]
    roles = sorted({s.role for s in classified if s.role})
    lanes_with_ref = [c for c in scan.lanes if c.platform_ref]

    # 🚨 THE DENOMINATOR, PRINTED. "0 disagreements" has two causes — every pin agrees, or nothing
    # was extracted — and they are indistinguishable from the verdict alone.
    emit("")
    emit(f"### {scan.gh_repo} — platform pin set")
    emit("")
    emit(f"    workflow files read                   {scan.workflows}")
    emit(f"    literal DIGEST pin sites found        {len(digests)}")
    emit(f"    …of which CLASSIFIED to an image      {len(classified)}")
    emit(f"    …UNCLASSIFIED (checked nothing)       {len(unclassified)}")
    emit(f"    distinct image ROLES pinned           {len(roles)}"
         + (f"  ({', '.join(roles)})" if roles else ""))
    emit(f"    literal image-NAME sites              {len([s for s in scan.sites if s.kind == 'image-name'])}")
    emit(f"    platform lane calls                   {len(scan.lanes)}")
    emit(f"    …passing a LITERAL platform-ref       {len(lanes_with_ref)}")
    emit(f"    platform SOURCE refs declared         {len(scan.platform_refs)}")
    emit(f"    pin-shaped declarations MALFORMED     {len(scan.malformed)}")
    emit(f"    promoted build NAMED                  "
         + (f"{scan.platform_set[0]} = {scan.platform_set[1]}" if scan.platform_set
            else "(none declared — the tag intersection is the only cross-check)"))
    emit("")
    for site in sorted(scan.sites, key=lambda s: (s.filename, s.line)):
        target = site.role or "UNCLASSIFIED"
        emit(f"      {site.kind:<10} {target:<18} {site.value[:26]:<26} {site.filename}:{site.line}:{site.name}")
    for filename, line, name, value in sorted(scan.platform_refs):
        emit(f"      source-ref {'core commit':<18} {value[:26]:<26} {filename}:{line}:{name}")
    for call in sorted(scan.lanes, key=lambda c: (c.filename, c.line)):
        ref = call.ref if SHA40_RE.match(call.ref) else f"{call.ref} (not a commit)"
        emit(f"      lane       {call.lane.rsplit('/', 1)[-1]:<32} {ref[:26]:<26} {call.filename}:{call.line}"
             + (f"  platform-ref@L{call.platform_ref_line}" if call.platform_ref else ""))
    emit("")

    if scan.unreadable:
        print(f"::error::{scan.gh_repo}: its workflows could not be read, so its pins were NOT checked.")
        print(f"  {scan.unreadable}")
        print("  Failing closed: an unswept repository must never read as a swept one.")
        failures.append("unreadable")
        return failures

    # I4 — placeholder vacuity.
    for bad in scan.malformed:
        print(f"::error::{scan.gh_repo} — {bad.filename}:{bad.line} `{bad.name}` is pin-shaped but "
              f"its value is not a digest: {bad.value!r}")
        print("  A registry accepts exactly `sha256:` + 64 lowercase hex. A looser matcher reads a")
        print("  placeholder as 'no pin present' and passes having checked nothing; a truncated")
        print("  digest it reads as a good pin. Write the real digest, or a `${{ … }}` forward.")
        failures.append("malformed")

    # I5 — an unplaceable pin has not been checked.
    for site in unclassified:
        print(f"::error::{scan.gh_repo} — {_where(site)} pins {site.value} but nothing says "
              "WHICH image it names, so its consistency was never checked.")
        print("  Bind it in the file — write the reference as")
        print("  `meshweaver.azurecr.io/<repo>@${{ env.<NAME> }}`, or declare the sibling that")
        print("  names the image (`<prefix>: meshweaver.azurecr.io/<repo>` beside")
        print("  `<prefix>-digest:`) — or add the name to ROLE_ALIASES in this script with the")
        print("  repository that writes it. 'Not checked' must never read the same as 'consistent'.")
        failures.append("unclassified")

    # I1 — same name, one value, WITHIN ONE FILE.
    #
    # 🚨 Scoped to the file on purpose. Grouping by name across the whole repository would red a
    # `platform-image:` that legitimately names the portal image in `ci.yml` and a different image
    # in another workflow — two unrelated declarations that happen to share a key. The copies this
    # invariant exists for are always in ONE file, because they are copies of one `env:` value that
    # a `with:` block cannot read. Cross-file DIGEST disagreement is not lost: I2 catches it by
    # image, which is the semantically correct grouping, and anything I2 cannot place is I5-red.
    by_name: dict[tuple[str, str], dict[str, list[Site]]] = {}
    for site in scan.sites:
        if site.name == ":inline":
            continue
        by_name.setdefault((site.filename, site.name), {}).setdefault(site.value, []).append(site)
    for (filename, name), values in sorted(by_name.items()):
        if len(values) < 2:
            continue
        print(f"::error::{scan.gh_repo} — `{name}` is written "
              f"{sum(len(v) for v in values.values())} times in {filename} with {len(values)} "
              "DIFFERENT values. A pin move edited some and not others.")
        for value, sites in sorted(values.items()):
            for site in sites:
                print(f"    {value}   {site.filename}:{site.line}")
        failures.append("name-split")

    # I2 — same image, one digest, even across names.
    by_role: dict[str, dict[str, list[Site]]] = {}
    for site in classified:
        by_role.setdefault(site.role or "", {}).setdefault(site.value, []).append(site)
    for role, values in sorted(by_role.items()):
        if len(values) < 2:
            continue
        print(f"::error::{scan.gh_repo} — the image `{role}` is pinned to {len(values)} DIFFERENT "
              "digests in the same repository. Half of a set was moved.")
        for value, sites in sorted(values.items()):
            for site in sites:
                print(f"    {value}   {site.filename}:{site.line}:{site.name}   [{site.how}]")
        print("  Every gate and every module in one run must reach ONE promoted build; two digests")
        print("  for one image means some jobs test a platform the others do not compile against,")
        print("  and the divergence surfaces later as a framework mismatch pointing at nothing.")
        failures.append("role-split")

    # I3 — a lane call is internally coherent.
    for call in lanes_with_ref:
        if call.platform_ref == call.ref:
            continue
        print(f"::error::{scan.gh_repo} — {call.filename}:{call.line} calls {call.lane}@{call.ref} "
              f"but passes platform-ref {call.platform_ref} (line {call.platform_ref_line}).")
        print("  The lane executes core's .github/scripts/* AT platform-ref, so a lane newer than")
        print("  its ref calls a script that does not exist there: the job dies on")
        print("  \"can't open file …/node-repo-scope.py\" and the message names no pin at all.")
        print("  Move both, in one commit, by grepping the OLD value.")
        failures.append("lane-split")

    # I8 — a declared platform SOURCE ref that nothing else in the repository names.
    #
    # 🚨 This is the ONE thing that can be said about `MW_PLATFORM_REF` without demanding a
    # coupling the fleet does not have. It is NOT "MW_PLATFORM_REF equals a lane's sha" — several
    # lanes legitimately sit at older DEFINITION pins while the platform pin moves, and
    # MeshWeaver.SocialMedia says so in the comment above its own variable. What IS asserted is
    # that the value is not ORPHANED: the file that declares it also calls at least one platform
    # lane pinned at it. MeshWeaver.SocialMedia's header records the state that motivates it —
    # "`uses:` was 04a2401c6, `platform-ref` 94d18f9cb and MW_PLATFORM_REF 89f886275 — three
    # different commits under a comment asserting they are one" — and moving ONE of the three
    # alone is exactly what leaves an orphan.
    lane_refs = {c.ref for c in scan.lanes} | {c.platform_ref for c in lanes_with_ref if c.platform_ref}
    for filename, line, name, value in scan.platform_refs:
        if value in lane_refs:
            continue
        print(f"::error::{scan.gh_repo} — {filename}:{line} declares `{name}` = {value}, but no "
              "platform lane in this repository is pinned at it.")
        print("  The source ref was moved alone, or every lane was moved and it was not. Either")
        print("  way the repository is reaching for two core commits under a comment that says")
        print("  one. Lane refs actually called: "
              + (", ".join(sorted(r[:12] for r in lane_refs)) or "(none)"))
        failures.append("orphan-source-ref")

    if not classified and not unclassified:
        if require_pins:
            print(f"::error::{scan.gh_repo}: not one literal platform pin was found.")
            print("  This repository is required to pin (--require-pins), so zero means the")
            print("  extractor stopped matching, not that pinning stopped. Run --self-test.")
            failures.append("no-pins")
        else:
            emit(f"  {scan.gh_repo}: declares no literal platform pin — a measured zero, "
                 "not an unchecked one.")

    return failures


def check_tags(scan: RepoScan, registry: str, cache: dict) -> list[str]:
    """I7 — every distinct manifest this repository pins shares a tag with every other."""
    failures: list[str] = []
    classified = [s for s in scan.sites if s.kind == "digest" and s.role]
    manifests = sorted({(s.role or "", s.value) for s in classified})
    if not manifests:
        return failures

    tagsets: dict[tuple[str, str], list[str]] = {}
    for role, digest in manifests:
        tags, detail = acr_tags(registry, role, digest, cache)
        if tags is None:
            print(f"::error::{scan.gh_repo} — {role}@{digest[:19]}…: {detail}")
            print("  The build this pin belongs to could not be read, so 'do all the pins name one")
            print("  build' was NOT answered for this repository. Indeterminate is not a pass.")
            failures.append("tags-unread")
            continue
        tagsets[(role, digest)] = tags

    if failures:
        return failures

    untagged = [key for key, tags in tagsets.items() if not tags]
    for role, digest in untagged:
        print(f"::error::{scan.gh_repo} — {role}@{digest[:19]}… carries NO TAG, so it belongs to no "
              "promoted build and cannot be related to the repository's other pins.")
        failures.append("untagged")
    if failures:
        return failures

    shared: set[str] | None = None
    for tags in tagsets.values():
        shared = set(tags) if shared is None else (shared & set(tags))
    shared = shared or set()

    emit(f"    manifests pinned                      {len(tagsets)}")
    emit(f"    …tags shared by ALL of them           {len(shared)}"
         + (f"  ({', '.join(sorted(shared))})" if shared else ""))

    if len(tagsets) < 2:
        emit("    (one manifest only — nothing to cross-check; the tag arm is a measured no-op here)")
        return failures

    if not shared:
        print(f"::error::{scan.gh_repo} — its {len(tagsets)} pinned manifests share NO tag, so they "
              "are NOT one promoted build.")
        for (role, digest), tags in sorted(tagsets.items()):
            print(f"    {role}@{digest[:19]}…  tags: {', '.join(tags) or '(none)'}")
        print("  Each digest exists — this is exactly the half-moved set that #3462's existence")
        print("  check passes and nothing else was asking about. Move the whole set to one build.")
        failures.append("not-one-build")
        return failures

    if scan.platform_set:
        name, value = scan.platform_set
        if value not in shared:
            print(f"::error::{scan.gh_repo} — `{name}` says the promoted build is {value!r}, but "
                  "that tag is not on every pinned manifest.")
            print(f"    shared tags: {', '.join(sorted(shared))}")
            print("  The build's NAME and the digests disagree; one of them was moved alone.")
            failures.append("set-name-mismatch")

    return failures


# ── Self-test: both-ways falsification, offline ─────────────────────────────────────────────────

CONSISTENT = """
name: fixture
env:
  MW_PLATFORM_SET: 3.0.0-ci.7917
  MW_IMAGE_DIGEST: sha256:642f686fed743289ca1211b73bf8091c5d7deaaa3d4435746a4868b653e4fc8c
  MW_PORTAL_IMAGE_DIGEST: sha256:31aab07d271aedc257700780417a060a6511eb8271b2e3982d47145a4fa5e9c9
  MW_MIGRATION_DIGEST: sha256:d81df6cc79ad68fcfd9fa3d4cf40588e97c7adfe8c2516e1626231c0412b1f3f
  MW_PLATFORM_REF: 1b5350d547473a5e2ca81e793e774cc962acfeb3
jobs:
  preflight:
    outputs:
      image-digest: ${{ steps.pin.outputs.image-digest }}
    steps:
      - id: pin
        run: |
          echo "image-digest=sha256:642f686fed743289ca1211b73bf8091c5d7deaaa3d4435746a4868b653e4fc8c" >> "$GITHUB_OUTPUT"
          echo "digest: $SOME_SHELL_VARIABLE"
  validate:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-validate.yml@0a2b9017d8fc58b14055c4a642d8f99d17b3b626
    with:
      platform-ref: 0a2b9017d8fc58b14055c4a642d8f99d17b3b626
  gate:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@1b5350d547473a5e2ca81e793e774cc962acfeb3
    with:
      test-image: ${{ vars.MW_TEST_IMAGE }}
      image-digest: ${{ needs.preflight.outputs.image-digest }}
      platform-image: meshweaver.azurecr.io/memex-portal-ai
      platform-image-digest: sha256:31aab07d271aedc257700780417a060a6511eb8271b2e3982d47145a4fa5e9c9
      platform-ref: 1b5350d547473a5e2ca81e793e774cc962acfeb3
  publish-bake:
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-publish-bake.yml@1b5350d547473a5e2ca81e793e774cc962acfeb3
    with:
      platform-image: meshweaver.azurecr.io/memex-portal-ai
      platform-image-digest: sha256:31aab07d271aedc257700780417a060a6511eb8271b2e3982d47145a4fa5e9c9
  edu-shaped:
    steps:
      - env:
          MW_MIGRATION_IMAGE: ${{ vars.MW_MIGRATION_IMAGE || format('meshweaver.azurecr.io/memex-migration@{0}', env.MW_MIGRATION_DIGEST) }}
          FORWARD: ${{ env.MW_IMAGE_DIGEST }}
        run: echo hi
"""

# The SAME file with ONE half of the set moved on, in each of the four ways a hand edit does it.
HALF_MOVED = CONSISTENT.replace(
    # I2 — the portal digest moves in `env:` and the two `platform-image-digest:` literals stay.
    "  MW_PORTAL_IMAGE_DIGEST: sha256:31aab07d271aedc257700780417a060a6511eb8271b2e3982d47145a4fa5e9c9",
    "  MW_PORTAL_IMAGE_DIGEST: sha256:b6acc0821f4ba2ec54ec2f1f7b1e0c7de2f6a34e5c9d80b1a2f3c4d5e6f70819",
).replace(
    # I1 — one of the two `platform-image-digest:` literals moves and the other does not.
    """      platform-image: meshweaver.azurecr.io/memex-portal-ai
      platform-image-digest: sha256:31aab07d271aedc257700780417a060a6511eb8271b2e3982d47145a4fa5e9c9
  edu-shaped:""",
    """      platform-image: meshweaver.azurecr.io/memex-portal-ai
      platform-image-digest: sha256:c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0
  edu-shaped:""",
).replace(
    # I3 — the lane ref moves and the `platform-ref:` on the SAME call does not.
    "uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@1b5350d547473a5e2ca81e793e774cc962acfeb3",
    "uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@9999999999999999999999999999999999999999",
).replace(
    # I4 — a pin replaced by a placeholder, which a `sha256:[0-9a-f]+` matcher reads as no pin.
    "  MW_IMAGE_DIGEST: sha256:642f686fed743289ca1211b73bf8091c5d7deaaa3d4435746a4868b653e4fc8c",
    "  MW_IMAGE_DIGEST: sha256:PLACEHOLDER",
).replace(
    # I4 in the OTHER shape — the same placeholder inside a $GITHUB_OUTPUT, which is how
    # SocialMedia and Crm write their tester pin. This case is here because the real-input
    # falsification caught this file passing it green; a fixture that only carries the `env:`
    # shape proves the gate for Manufacturing and for neither of the other two.
    'echo "image-digest=sha256:642f686fed743289ca1211b73bf8091c5d7deaaa3d4435746a4868b653e4fc8c"',
    'echo "image-digest=sha256:0BADHEX"',
)

# A pin under a name nothing binds and no alias covers — I5's subject.
UNCLASSIFIED = """
env:
  MW_SIDECAR_DIGEST: sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
"""


def _scan_of(text: str, gh_repo: str = "Systemorph/Fixture") -> RepoScan:
    scan = RepoScan(gh_repo=gh_repo, workflows=1)
    sites, lanes, malformed, platform_set, platform_refs = extract("ci.yml", text)
    scan.sites, scan.lanes, scan.malformed = sites, lanes, malformed
    scan.platform_set, scan.platform_refs = platform_set, platform_refs
    return scan


def _quiet(fn, *args, **kwargs):
    """Run a checker without its report reaching the log — the self-test prints its own verdict."""
    import contextlib
    import io

    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        result = fn(*args, **kwargs)
    return result, buf.getvalue()


def self_test() -> int:
    failures: list[str] = []
    env_summary = os.environ.pop("GITHUB_STEP_SUMMARY", None)

    # ── ARM A: the consistent set is GREEN, and is not green by having found nothing. ───────────
    good = _scan_of(CONSISTENT)
    verdicts, _ = _quiet(check_static, good, True)
    if verdicts:
        failures.append(f"the CONSISTENT fixture failed: {sorted(set(verdicts))}")
    classified = [s for s in good.sites if s.kind == "digest" and s.role]
    if len(classified) < 6:
        failures.append(f"the CONSISTENT fixture yielded only {len(classified)} classified pins — "
                        "a green verdict over nothing is the defect this gate exists to name")
    roles = {s.role for s in classified}
    for expected in ("mw-plugin-test", "memex-portal-ai", "memex-migration"):
        if expected not in roles:
            failures.append(f"the extractor did not place a pin on {expected}")
    if len(good.lanes) != 3:
        failures.append(f"expected 3 platform lane calls, found {len(good.lanes)}")
    if len([c for c in good.lanes if c.platform_ref]) != 2:
        failures.append("the literal platform-ref of a lane call was not paired with its own uses:")
    if good.platform_set != ("MW_PLATFORM_SET", "3.0.0-ci.7917"):
        failures.append(f"the promoted build name was not read: {good.platform_set}")
    if len(good.platform_refs) != 1:
        failures.append(f"expected 1 declared platform source ref, found {len(good.platform_refs)}")

    # ── ARM B: the half-moved set is RED, and red for each of the four reasons separately. ──────
    bad = _scan_of(HALF_MOVED)
    verdicts, log = _quiet(check_static, bad, True)
    for expected in ("role-split", "name-split", "lane-split", "malformed"):
        if expected not in verdicts:
            failures.append(f"the HALF-MOVED fixture did not fire {expected}")
    for needle in ("sha256:b6acc082", "sha256:c1c2c3c4", "9999999999", "sha256:PLACEHOLDER",
                   "sha256:0BADHEX"):
        if needle not in log:
            failures.append(f"the failure report does not NAME the offending value {needle}")
    # 🚨 Both SHAPES of the vacuity trap, separately — an `env:` key and a $GITHUB_OUTPUT — because
    # the first draft covered only the first and passed a placeholder in two of the three repos
    # this gate was written for.
    if len({m.name for m in bad.malformed}) < 2:
        failures.append("only ONE shape of pin-shaped-but-not-a-digest was detected; the "
                        "$GITHUB_OUTPUT shape is how SocialMedia and Crm write their tester pin")

    # ── ARM B2: the source ref moved alone, so nothing else in the file names it. ───────────────
    orphaned = _scan_of(CONSISTENT.replace(
        "  MW_PLATFORM_REF: 1b5350d547473a5e2ca81e793e774cc962acfeb3",
        "  MW_PLATFORM_REF: 7777777777777777777777777777777777777777"))
    verdicts, log = _quiet(check_static, orphaned, True)
    if "orphan-source-ref" not in verdicts:
        failures.append("a MW_PLATFORM_REF no lane names passed as consistent")
    if "7777777777" not in log:
        failures.append("the orphaned-source-ref report does not name the value")

    # ── ARM C: an unplaceable pin is a FAILURE, never a silent pass. ────────────────────────────
    unk = _scan_of(UNCLASSIFIED)
    verdicts, log = _quiet(check_static, unk, False)
    if "unclassified" not in verdicts:
        failures.append("a digest under an unbound, unaliased name passed as 'consistent'")
    if "MW_SIDECAR_DIGEST" not in log:
        failures.append("the unclassified-pin report does not name the declaration")

    # ── ARM D: --require-pins turns an empty repository RED; without it, it is a measured zero. ─
    empty = _scan_of("name: nothing\njobs:\n  a:\n    steps:\n      - run: echo hi\n")
    verdicts, _ = _quiet(check_static, empty, True)
    if "no-pins" not in verdicts:
        failures.append("--require-pins let a repository with ZERO extracted pins pass")
    verdicts, _ = _quiet(check_static, empty, False)
    if verdicts:
        failures.append("a repository that legitimately pins nothing was failed without "
                        "--require-pins")

    # ── ARM E: the tag arm — one build passes, two builds fail, and an unread tag is not a pass. ─
    def fake(tagmap):
        def resolve(registry, acr_repo, digest, cache):
            return tagmap.get((acr_repo, digest), (None, "GONE: not in the fixture registry"))
        return resolve

    real_tags = globals()["acr_tags"]
    tester = "sha256:642f686fed743289ca1211b73bf8091c5d7deaaa3d4435746a4868b653e4fc8c"
    portal = "sha256:31aab07d271aedc257700780417a060a6511eb8271b2e3982d47145a4fa5e9c9"
    migration = "sha256:d81df6cc79ad68fcfd9fa3d4cf40588e97c7adfe8c2516e1626231c0412b1f3f"
    try:
        one_build = {
            ("mw-plugin-test", tester): (["3.0.0-ci.7917", "cb675ec"], ""),
            ("memex-portal-ai", portal): (["3.0.0-ci.7917", "cb675ec", "cb675ec-p1098103"], ""),
            ("memex-migration", migration): (["3.0.0-ci.7917", "cb675ec"], ""),
        }
        globals()["acr_tags"] = fake(one_build)
        verdicts, _ = _quiet(check_tags, good, "meshweaver", {})
        if verdicts:
            failures.append(f"one promoted build was reported inconsistent: {verdicts}")

        two_builds = {
            ("mw-plugin-test", tester): (["3.0.0-ci.7917"], ""),
            ("memex-portal-ai", portal): (["3.0.0-ci.7574"], ""),
            ("memex-migration", migration): (["3.0.0-ci.7917"], ""),
        }
        globals()["acr_tags"] = fake(two_builds)
        verdicts, log = _quiet(check_tags, good, "meshweaver", {})
        if "not-one-build" not in verdicts:
            failures.append("two DIFFERENT promoted builds passed the tag arm")
        if "3.0.0-ci.7574" not in log:
            failures.append("the tag-arm report does not name the tags that disagree")

        wrong_name = {
            ("mw-plugin-test", tester): (["3.0.0-ci.7574"], ""),
            ("memex-portal-ai", portal): (["3.0.0-ci.7574"], ""),
            ("memex-migration", migration): (["3.0.0-ci.7574"], ""),
        }
        globals()["acr_tags"] = fake(wrong_name)
        verdicts, _ = _quiet(check_tags, good, "meshweaver", {})
        if "set-name-mismatch" not in verdicts:
            failures.append("MW_PLATFORM_SET naming a build the digests do not carry passed")

        globals()["acr_tags"] = fake({})
        verdicts, _ = _quiet(check_tags, good, "meshweaver", {})
        if "tags-unread" not in verdicts:
            failures.append("a manifest whose tags could not be read passed as consistent")

        untagged = {
            ("mw-plugin-test", tester): ([], ""),
            ("memex-portal-ai", portal): (["3.0.0-ci.7917"], ""),
            ("memex-migration", migration): (["3.0.0-ci.7917"], ""),
        }
        globals()["acr_tags"] = fake(untagged)
        verdicts, _ = _quiet(check_tags, good, "meshweaver", {})
        if "untagged" not in verdicts:
            failures.append("a manifest belonging to no promoted build passed as consistent")
    finally:
        globals()["acr_tags"] = real_tags
        if env_summary is not None:
            os.environ["GITHUB_STEP_SUMMARY"] = env_summary

    for line in failures:
        print(f"::error::self-test: {line}")
    if failures:
        return 1
    print(f"self-test: the consistent set greens over {len(classified)} classified pins across "
          f"{len(roles)} images and {len(good.platform_refs)} source ref; each of the four "
          "hand-edit splits, the orphaned source ref, the unplaceable pin, the empty repository "
          "and all five tag-arm cases FIRE.")
    return 0


# ── Entry point ────────────────────────────────────────────────────────────────────────────────


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", help="a local checkout to scan (its .github/workflows)")
    parser.add_argument("--repo-name", default=None,
                        help="the owner/name to print for --root (default: the directory)")
    parser.add_argument("--repos", help="comma-separated owner/name list to read over the API")
    parser.add_argument("--discover", action="store_true",
                        help="every repository the App installation this token belongs to reaches")
    parser.add_argument("--require-pins", action="store_true",
                        help="a repository that yields ZERO classified pins is RED")
    parser.add_argument("--check-tags", action="store_true",
                        help="add I7: every pinned manifest shares a tag (needs `az`)")
    parser.add_argument("--registry", default=REGISTRY_DEFAULT)
    parser.add_argument("--self-test", action="store_true",
                        help="prove every arm fires and stays silent; no network")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    chosen = [bool(args.root), bool(args.repos), bool(args.discover)]
    if sum(chosen) != 1:
        parser.error("give exactly one of --root, --repos or --discover")

    scans: list[RepoScan] = []
    if args.root:
        name = args.repo_name or os.path.basename(os.path.abspath(args.root))
        scans.append(scan_local(args.root, name))
    else:
        repos = discover_repos() if args.discover else [
            r.strip() for r in args.repos.split(",") if r.strip()
        ]
        print(f"scanning {len(repos)} repository(ies): {', '.join(repos)}")
        for gh_repo in repos:
            scans.append(scan_remote(gh_repo))

    if args.check_tags:
        registry_control_probe(args.registry)

    cache: dict = {}
    failed = False
    consistent = 0
    for scan in scans:
        verdicts = check_static(scan, args.require_pins)
        if args.check_tags and not scan.unreadable:
            verdicts += check_tags(scan, args.registry, cache)
        if verdicts:
            failed = True
        else:
            consistent += 1

    # 🚨 The fleet denominator, again and last: "N repositories checked, of which M consistent".
    # Never "M consistent" alone — a sweep whose numerator is its only number cannot tell a clean
    # fleet from a fleet it never looked at.
    emit("")
    emit(f"pin-set consistency: {len(scans)} repository(ies) examined, {consistent} consistent, "
         f"{len(scans) - consistent} not.")
    if not scans:
        print("::error::ZERO repositories were examined, which is not a verdict about anything.")
        return 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

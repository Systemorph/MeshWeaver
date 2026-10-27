#!/usr/bin/env python3
"""release-channels.py — the ONE reader of `.github/release-channels.json`.

WHY. The platform publishes MOVING image labels — `3-latest`, `3.1-latest`, `main`, `latest` — and
reserves edition labels (`3-mac`, `3-enterprise`). Retention must never delete one, and a pruner
that hard-codes its own list protects whatever its author remembered on the day it was written:
the next label a writer mints is purge-eligible and nobody notices until a fresh install cannot
resolve its seed. So the labels are DECLARED in one file (policy `declared-release-channels`), and
every reader goes through this script:

  release-channels.py tags --version <v> --writer <workflow> [--job <job>] [--repository <repo>]
        # the `written` labels that writer moves for a set of version <v> (one per line)
  release-channels.py is-protected <tag>
        # exit 0: <tag> is a declared label (any status) — PROTECTED
        # exit 1: <tag> matches no declared label — the ONLY status that means "not protected"
        # exit 2: the lookup FAILED (declaration missing or malformed) — a caller must ABORT, never
        #         read it as "not protected" (a pruner that did would delete every label)
  release-channels.py protected-patterns
        # JSON: [{id, tag, regex, status, repositories}] — what a pruner excludes BY NAME
  release-channels.py check [<repo root>]
        # the PR gate: well formed, writers exist, line pointers agree with platform-version.py,
        # and lock-pinned-digests.py classifies every declared label as floating
  release-channels.py --self-test

A label is protected by NAME, never locked to a digest: it moves, and a lock would pin the bytes
it has already left (lock-pinned-digests.py's floating rule). A pruner skips the tag and the
manifest the tag names at the moment of its sweep.

Manual: src/MeshWeaver.Documentation/Data/Architecture/ReleaseEditions.md (the concept: ReleaseChannels.md).
"""
from __future__ import annotations

import importlib.util
import json
import re
import sys
import tempfile
from functools import lru_cache
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_FILE = HERE.parent / "release-channels.json"

STATUSES = ("written", "declared", "retired")
NOTATIONS = ("any", "legacy")
PLACEHOLDERS = ("major", "minor", "patch")
REQUIRED_FIELDS = ("id", "tag", "edition", "meaning", "writers", "notation", "status")
BINDING_FIELDS = ("workflow", "job", "repositories")
# The repository whose workflows this file's bindings name by default. A binding may name ANOTHER
# repository's workflow with `"repo": "<owner>/<name>"` — the hosting operator's image is published
# by Systemorph/MeshWeaver.Plugins (its hosting-operator.yml), and its `main` label must stay
# declared, and so protected, here. Such a binding is shape-validated, but its workflow cannot be
# read from this tree, so `check` does not open it; that repository's lane spells the tag and the
# repository itself.
THIS_REPO = "Systemorph/MeshWeaver"
REPO_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
# A label must look like a tag and must NEVER be SELECTABLE as a version. The self-updater
# (MeshWeaver.Plugins src/Memex.Portal.Shared/SelfUpdate/VersionSelect.cs) keeps every tag matching
# `PlatformVersionTag` = ^\d+\.\d+\.\d+([-+].*)?$ EXCEPT one ending in `-latest` (`MovingPointer`), so
# a label of that dotted shape with any other suffix (`3.1.0-mac`) would be rolled to as if it were
# a build. Both regexes are mirrored here; VersionSelectTest pins the C# side.
TAG_CHARS_RE = re.compile(r"^[A-Za-z0-9{}][A-Za-z0-9._{}-]*$")
VERSION_SHAPED_RE = re.compile(r"^\d+\.\d+\.\d+([-+].*)?$")
MOVING_POINTER_RE = re.compile(r"-latest$", re.IGNORECASE)
# The repositories a self-updater LISTS (SelfUpdateHostedService rolls the portal and its migration;
# the control lane rolls memex-control). The version-shape refusal guards THESE — a model-service
# line tag such as whisper's `1.7.4-flix-large-v3-q8` is never a roll candidate.
SELF_UPDATE_REPOSITORIES = frozenset({"memex-portal-ai", "memex-migration", "memex-control"})
VERSION_CORE_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)")
LEGACY_SET_RE = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?[.-]ci\.(\d+)$")


class ChannelError(ValueError):
    """The declaration is malformed, or a question was asked of it that it cannot answer."""


def load(path: Path = DEFAULT_FILE, validated: bool = True) -> dict:
    """The declaration, VALIDATED. A malformed one raises rather than loading: an empty or broken
    channel list would otherwise yield an empty protected set, and every label it no longer matches
    would be read as an ordinary pin by the lock and as deletable by a pruner."""
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise ChannelError(f"{path} is missing — no channel can be protected without it") from error
    except json.JSONDecodeError as error:
        raise ChannelError(f"{path} is not valid JSON: {error}") from error
    if validated:
        problems = validate(doc)
        if problems:
            raise ChannelError(f"{path} is malformed — refusing to protect from it: " + "; ".join(problems))
    return doc


def channels(doc: dict) -> list[dict]:
    value = doc.get("channels")
    if not isinstance(value, list):
        raise ChannelError("`channels` is missing or not a list")
    return value


def template_regex(template: str) -> re.Pattern[str]:
    """`{major}.{minor}-latest` → ^\\d+\\.\\d+-latest$ (case-insensitive, like the lock's floating rule)."""
    parts = re.split(r"(\{[a-z]+\})", template)
    out = []
    for part in parts:
        if part.startswith("{") and part.endswith("}"):
            name = part[1:-1]
            if name not in PLACEHOLDERS:
                raise ChannelError(f"unknown placeholder {part} in {template!r}")
            out.append(r"\d+")
        else:
            out.append(re.escape(part))
    return re.compile("^" + "".join(out) + "$", re.IGNORECASE)


def validate(doc: dict) -> list[str]:
    problems: list[str] = []
    # TYPES FIRST: a wrong-typed root or field must be a validation problem (so load() raises
    # ChannelError and is-protected exits 2), never an AttributeError/TypeError that escapes as a
    # generic failure a caller could read as "not protected".
    if not isinstance(doc, dict):
        return [f"the declaration's root must be an object, got {type(doc).__name__}"]
    if doc.get("schema") != 1:
        problems.append(f"`schema` must be 1, got {doc.get('schema')!r}")
    try:
        entries = channels(doc)
    except ChannelError as error:
        return problems + [str(error)]
    if not entries:
        problems.append("`channels` is empty — a declaration that protects nothing reads exactly like a clean one")
    seen_ids: set[str] = set()
    seen_tags: set[str] = set()
    for i, entry in enumerate(entries):
        where = f"channels[{i}]"
        if not isinstance(entry, dict):
            problems.append(f"{where} is not an object")
            continue
        for field in REQUIRED_FIELDS:
            if field not in entry or entry[field] in (None, "", []):
                problems.append(f"{where} ({entry.get('id', '?')}) has no `{field}`")
        for field in ("id", "tag", "edition", "meaning", "notation", "status"):
            if field in entry and not isinstance(entry[field], str):
                problems.append(f"{where}: `{field}` must be a string, got {type(entry[field]).__name__}")
        cid, tag = entry.get("id"), entry.get("tag")
        if not isinstance(cid, str):
            cid = repr(cid)
        if cid in seen_ids:
            problems.append(f"{where}: duplicate id {cid!r}")
        seen_ids.add(cid)
        if isinstance(tag, str):
            if tag.lower() in seen_tags:
                problems.append(f"{where}: duplicate tag {tag!r}")
            seen_tags.add(tag.lower())
            if not TAG_CHARS_RE.match(tag):
                problems.append(f"{where}: {tag!r} is not a valid tag template")
            rendered = render(tag, 3, 1, 0)
            if rendered is None or "{" in rendered or "}" in rendered or not TAG_CHARS_RE.match(rendered):
                problems.append(
                    f"{where}: {tag!r} does not render to a valid image tag (got {rendered!r}) — only "
                    f"{{{'}, {'.join(PLACEHOLDERS)}}} placeholders, lower case and balanced, are allowed")
            elif (VERSION_SHAPED_RE.match(rendered) and not MOVING_POINTER_RE.search(rendered)
                  and SELF_UPDATE_REPOSITORIES & set(_binding_repositories(entry))):
                problems.append(
                    f"{where}: {tag!r} renders as {rendered!r}, which VersionSelect would SELECT as a version "
                    "(dotted shape without the excluded `-latest` suffix) — a self-updater would roll to a moving label")
            try:
                template_regex(tag)
            except ChannelError as error:
                problems.append(f"{where}: {error}")
        if entry.get("status") not in STATUSES:
            problems.append(f"{where} ({cid}): status {entry.get('status')!r} is not one of {STATUSES}")
        if entry.get("notation") not in NOTATIONS:
            problems.append(f"{where} ({cid}): notation {entry.get('notation')!r} is not one of {NOTATIONS}")
        # A writer is a BINDING — workflow, job and the repositories THAT job writes the label to,
        # together. Separate job and repository lists cannot express "promote writes `latest` to the
        # tester only" and would hand a pointer to the wrong publishing job.
        writers = entry.get("writers")
        if not isinstance(writers, list) or not writers:
            problems.append(f"{where} ({cid}): `writers` must be a non-empty list of workflow/job/repositories bindings")
            writers = []
        for k, binding in enumerate(writers):
            at = f"{where} ({cid}) writers[{k}]"
            if not isinstance(binding, dict):
                problems.append(f"{at} is not an object")
                continue
            for field in BINDING_FIELDS:
                if not binding.get(field):
                    problems.append(f"{at} has no `{field}`")
            for field in ("workflow", "job"):
                if field in binding and not isinstance(binding[field], str):
                    problems.append(f"{at}: `{field}` must be a string")
            repos = binding.get("repositories")
            if repos and (not isinstance(repos, list) or not all(isinstance(r, str) and r for r in repos)):
                problems.append(f"{at}: `repositories` must be a list of repository names")
            if binding.get("notation", "any") not in NOTATIONS:
                problems.append(f"{at}: notation {binding.get('notation')!r} is not one of {NOTATIONS}")
            if "repo" in binding and not (isinstance(binding["repo"], str) and REPO_RE.match(binding["repo"])):
                problems.append(f"{at}: `repo` must be '<owner>/<name>', got {binding['repo']!r}")
    return problems


def _binding_repositories(entry: dict) -> list[str]:
    out: list[str] = []
    for b in entry.get("writers") or []:
        if isinstance(b, dict) and isinstance(b.get("repositories"), list):
            out += [r for r in b["repositories"] if isinstance(r, str)]
    return out


def render(template: str, major: int, minor: int, patch: int) -> str | None:
    """The tag a template names for one line, or None when it is not a renderable template — an
    unknown or mis-cased placeholder, or an unbalanced brace."""
    try:
        out = template.format(major=major, minor=minor, patch=patch)
    except (KeyError, IndexError, ValueError, AttributeError):
        return None
    return None if ("{" in out or "}" in out) else out


def version_core(version: str) -> tuple[int, int, int]:
    m = VERSION_CORE_RE.match((version or "").strip())
    if not m:
        raise ChannelError(f"{version!r} is not a platform version — it names no line")
    return int(m.group(1)), int(m.group(2)), int(m.group(3))


def tags_for(doc: dict, version: str, workflow: str, job: str | None = None,
             repository: str | None = None) -> list[str]:
    """The `written` labels `workflow` (optionally `job`) moves for a set of `version` — templated
    line labels rendered for that line, and literal labels (`main`, `latest`) unchanged."""
    major, minor, patch = version_core(version)
    legacy = bool(LEGACY_SET_RE.match(version.strip()))
    out: list[str] = []
    for entry in channels(doc):
        if entry.get("status") != "written":
            continue
        for binding in entry.get("writers") or []:
            if binding.get("workflow") != workflow:
                continue
            if job is not None and binding.get("job") != job:
                continue
            if repository is not None and repository not in (binding.get("repositories") or []):
                continue
            notation = binding.get("notation") or entry.get("notation")
            if notation == "legacy" and not legacy:
                continue
            tag = render(entry["tag"], major, minor, patch)
            if tag not in out:
                out.append(tag)
    return out


def repositories(entry: dict) -> list[str]:
    """Every repository any writer binding puts the label on — what a pruner protects it in."""
    return sorted({r for b in entry.get("writers") or [] for r in (b.get("repositories") or [])})


def protected_patterns(doc: dict) -> list[dict]:
    # `(?i)` IN the exported expression: the predicate matches case-insensitively, and a pruner
    # compiling the bare pattern would protect a NARROWER set than is-protected and the lock do.
    return [{"id": e["id"], "tag": e["tag"], "regex": "(?i)" + template_regex(e["tag"]).pattern,
             "status": e["status"], "repositories": repositories(e)}
            for e in channels(doc)]


def is_protected(doc: dict, tag: str) -> bool:
    text = (tag or "").strip()
    return any(template_regex(e["tag"]).match(text) for e in channels(doc))


@lru_cache(maxsize=None)
def _load_module(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    if spec is None or spec.loader is None:
        raise ChannelError(f"cannot load {filename}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


# Sample sets the parity check renders: one per notation, across a minor bump.
PARITY_VERSIONS = ("3.0.0-ci.8080", "3.1.10050", "3.2.10400", "4.0.11000")


def check(doc: dict, root: Path, platform_version=None, is_floating=None) -> list[str]:
    problems = validate(doc)
    if problems:
        return problems
    workflows = root / ".github" / "workflows"
    for entry in channels(doc):
        for binding in entry["writers"]:
            if binding.get("repo", THIS_REPO) != THIS_REPO:
                continue  # another repository's workflow: shape-validated above, not readable here
            wf = binding["workflow"]
            path = workflows / wf
            if not path.is_file():
                problems.append(f"{entry['id']}: writer workflow .github/workflows/{wf} does not exist")
                continue
            text = path.read_text(encoding="utf-8")
            # A `declared` entry names the job that is OWED, so only a `written` binding must exist.
            if entry["status"] == "written" and not re.search(rf"^  {re.escape(binding['job'])}:\s*$", text, re.MULTILINE):
                problems.append(f"{entry['id']}: {wf} has no job `{binding['job']}`")
            # A LITERAL label must appear in its writer, beside each repository it is bound to — so a
            # binding that no longer matches what the workflow writes is caught, not just a missing file.
            if entry["status"] == "written" and "{" not in entry["tag"]:
                if not re.search(rf"(?<![A-Za-z0-9_.-]){re.escape(entry['tag'])}(?![A-Za-z0-9_.-])", text):
                    problems.append(f"{entry['id']}: {wf} never spells `{entry['tag']}` — the binding is stale")
                for repo in binding["repositories"]:
                    if repo not in text:
                        problems.append(f"{entry['id']}: {wf} never names repository `{repo}` — the binding is stale")
    # The line pointers are spelled by platform-version.py in main-cd today. They must be EXACTLY
    # the `written` main-cd labels declared here — a pointer CD moves but this file does not declare
    # is a pointer retention may delete, and a declared one CD never moves protects nothing.
    if platform_version is None:
        platform_version = _load_module("mw_platform_version", "platform-version.py")
    for version in PARITY_VERSIONS:
        want = sorted(platform_version.line_pointers(version))
        got = sorted(t for t in tags_for(doc, version, "main-cd.yml", job="arm") if t.endswith("-latest"))
        if want != got:
            problems.append(
                f"line pointers disagree for {version}: platform-version.py moves {want}, "
                f"release-channels.json declares {got} for main-cd.yml#arm")
    # Every declared label must be FLOATING to the lock: a label the lock reads as a pin is locked to
    # whichever digest it names that night, protecting bytes the label has already left.
    if is_floating is None:
        is_floating = _load_module("mw_lock_pinned_digests_rc", "lock-pinned-digests.py").is_floating_tag
    for entry in channels(doc):
        sample = render(entry["tag"], 3, 1, 0)
        if sample is None:
            problems.append(f"{entry['id']}: {entry['tag']!r} does not render, so nothing it names is protected")
        elif not is_floating(sample):
            problems.append(
                f"{entry['id']}: lock-pinned-digests.py reads {sample!r} as a PIN, not a floating label")
    return problems


def self_test() -> int:
    failures: list[str] = []

    def expect(name: str, cond: bool) -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}")
        if not cond:
            failures.append(name)

    real = load()
    expect("the committed declaration validates", validate(real) == [])

    # tags_for — notation and status
    # `latest` on the portal and the migration moves WITH the line pointers, in `arm` (ImageTagContract).
    expect("SemVer set: arm moves N-latest, N.M-latest and latest only",
           sorted(tags_for(real, "3.1.10050", "main-cd.yml", job="arm")) == ["3-latest", "3.1-latest", "latest"])
    expect("legacy set: arm also moves the patch pointer",
           sorted(tags_for(real, "3.0.0-ci.8080", "main-cd.yml", job="arm")) == ["3-latest", "3.0-latest", "3.0.0-latest", "latest"])
    expect("arm moves `latest` on the portal AND the migration",
           all("latest" in tags_for(real, "3.1.10050", "main-cd.yml", job="arm", repository=r)
               for r in ("memex-portal-ai", "memex-migration")))
    expect("a `declared` label is never handed to a writer",
           "3-enterprise" not in tags_for(real, "3.1.0", "release.yml"))
    flipped = json.loads(json.dumps(real))
    for e in flipped["channels"]:
        if e["id"] == "enterprise":
            e["status"] = "written"
    expect("flipping enterprise to `written` hands release.yml N-enterprise",
           sorted(tags_for(flipped, "3.1.0", "release.yml", repository="memex-portal-ai")) == ["3-enterprise"])
    expect("literal labels are returned to their writer (promote moves main and latest on the tester)",
           sorted(tags_for(real, "3.1.10050", "main-cd.yml", job="promote", repository="mw-plugin-test")) == ["latest", "main"])
    expect("bindings hold together: promote does NOT write `latest` to the portal",
           tags_for(real, "3.1.10050", "main-cd.yml", job="promote", repository="memex-portal-ai") == ["main"])
    expect("bindings hold together: promote writes no `main` to memex-control (control-promote does)",
           tags_for(real, "3.1.10050", "main-cd.yml", job="promote", repository="memex-control") == []
           and tags_for(real, "3.1.10050", "main-cd.yml", job="control-promote", repository="memex-control") == ["main"])
    expect("release.yml mirrors the tester's `latest` on every release",
           tags_for(real, "3.1.0", "release.yml", repository="mw-plugin-test") == ["latest"])
    expect("release.yml moves NO portal/migration `latest` for a SemVer set (CD's arm already did; it could only go backwards)",
           tags_for(real, "3.1.0", "release.yml", repository="memex-portal-ai") == []
           and tags_for(real, "3.1.0", "release.yml", repository="memex-migration") == [])
    expect("release.yml moves the line pointers only for a legacy-notation set",
           sorted(tags_for(real, "3.0.0-ci.8080", "release.yml", repository="memex-portal-ai"))
           == ["3-latest", "3.0-latest", "3.0.0-latest"])
    # `latest` on the portal and the migration is arm's ALONE: a release can promote an OLDER set
    # than the armed one (a legacy set always is), so a release-side write could only go backwards.
    expect("release.yml never moves the portal's or the migration's `latest`, in either notation",
           all("latest" not in tags_for(real, v, "release.yml", repository=r)
               for v in ("3.0.0-ci.8080", "3.1.0") for r in ("memex-portal-ai", "memex-migration")))
    expect("control-first moves the line pointers on memex-control only",
           sorted(tags_for(real, "3.1.10050", "main-cd.yml", job="control-first")) == ["3-latest", "3.1-latest"]
           and tags_for(real, "3.1.10050", "main-cd.yml", job="control-first", repository="memex-portal-ai") == [])
    # The tester carries the armed set's LINE pointers (a matched channel with the portal) but never
    # `latest` from arm — its `latest` is promote's, following promoted sets (MW_TEST_IMAGE).
    expect("--repository narrows: arm moves the tester's line pointers and NOT its `latest`",
           sorted(tags_for(real, "3.1.10050", "main-cd.yml", job="arm", repository="mw-plugin-test"))
           == ["3-latest", "3.1-latest"])
    expect("--repository narrows to the declared repositories",
           tags_for(real, "3.1.10050", "main-cd.yml", job="arm", repository="memex-control") == [])
    try:
        tags_for(real, "main", "main-cd.yml")
        expect("a non-version is refused, never guessed", False)
    except ChannelError:
        expect("a non-version is refused, never guessed", True)

    # is_protected — every status protects; immutable tags never do
    for tag in ("3-latest", "3.1-latest", "3.0.0-latest", "3-mac", "3-enterprise", "4-mac", "main", "latest", "3-LATEST"):
        expect(f"{tag} is protected", is_protected(real, tag))
    for tag in ("3.1.10050", "3.0.0-ci.8080", "staging-abc1234-123", "abc1234", "3-latest-old", "mac", "3-macos", "arm-complete-3.1.10050"):
        expect(f"{tag} is NOT protected", not is_protected(real, tag))

    # validate — each check fires on its defect
    def broken(mutate) -> list[str]:
        doc = json.loads(json.dumps(real))
        mutate(doc)
        return validate(doc)

    expect("an empty channel list is refused", bool(broken(lambda d: d.update(channels=[]))))
    expect("an unknown status is refused", bool(broken(lambda d: d["channels"][0].update(status="maybe"))))
    expect("a missing writer is refused", bool(broken(lambda d: d["channels"][0].pop("writers"))))
    expect("a binding without repositories is refused",
           bool(broken(lambda d: d["channels"][0]["writers"][0].pop("repositories"))))
    expect("a binding without a job is refused",
           bool(broken(lambda d: d["channels"][0]["writers"][0].pop("job"))))
    expect("a duplicate tag is refused", bool(broken(lambda d: d["channels"].append(dict(d["channels"][0], id="dup")))))
    expect("an unknown placeholder is refused", bool(broken(lambda d: d["channels"][0].update(tag="{epoch}-latest"))))
    expect("a VERSION-SHAPED label is refused",
           bool(broken(lambda d: d["channels"][0].update(tag="{major}.{minor}.{patch}"))))
    expect("a dotted label with a non-`-latest` suffix is refused (VersionSelect would select it)",
           bool(broken(lambda d: d["channels"][3].update(tag="{major}.{minor}.{patch}-mac"))))
    expect("a wrong schema is refused", bool(broken(lambda d: d.update(schema=2))))
    expect("a mis-cased placeholder is refused ({Major}-mac renders nothing)",
           bool(broken(lambda d: d["channels"][0].update(tag="{Major}-mac"))))
    expect("an unbalanced brace is refused ({major}-mac})",
           bool(broken(lambda d: d["channels"][0].update(tag="{major}-mac}"))))
    with tempfile.TemporaryDirectory() as tmp:
        empty = Path(tmp) / "release-channels.json"
        empty.write_text(json.dumps({"schema": 1, "channels": []}))
        try:
            load(empty)
            expect("the operational loader REFUSES a malformed declaration (no silent empty protected set)", False)
        except ChannelError:
            expect("the operational loader REFUSES a malformed declaration (no silent empty protected set)", True)

    # check — parity with platform-version.py and the lock, with fakes so each arm is shown to fire
    class FakePV:
        @staticmethod
        def line_pointers(version: str) -> list[str]:
            major, minor, patch = version_core(version)
            base = [f"{major}-latest", f"{major}.{minor}-latest"]
            return base + ([f"{major}.{minor}.{patch}-latest"] if LEGACY_SET_RE.match(version) else [])

    class ExtraPV(FakePV):
        @staticmethod
        def line_pointers(version: str) -> list[str]:
            return FakePV.line_pointers(version) + ["edge-latest"]

    root = HERE.parent.parent
    always_floating = lambda tag: True  # noqa: E731
    expect("check passes on the real tree with faithful fakes",
           check(real, root, FakePV, always_floating) == [])
    expect("check fires when CD moves a pointer the file does not declare",
           any("disagree" in p for p in check(real, root, ExtraPV, always_floating)))
    expect("check fires when the lock reads a declared label as a pin",
           any("as a PIN" in p for p in check(real, root, FakePV, lambda tag: tag != "3-mac")))
    missing_writer = json.loads(json.dumps(real))
    missing_writer["channels"][3]["writers"][0]["workflow"] = "no-such-workflow.yml"
    expect("check fires on a writer workflow that does not exist",
           any("does not exist" in p for p in check(missing_writer, root, FakePV, always_floating)))

    # protected-patterns and is-protected agree on EVERY spelling (case included).
    exported = [re.compile(p["regex"]) for p in protected_patterns(real)]
    for tag in ("3-latest", "3-LATEST", "3-MAC", "MAIN", "Edge", "3.1.10050", "staging-abc1234-1",
                "gliner-multi-2.1", "1.7.4-FLIX-large-v3-q8", "abc1234"):
        expect(f"protected-patterns and is-protected agree on {tag}",
               is_protected(real, tag) == any(r.match(tag) for r in exported))
    expect("the model-service line tags are protected (s1-extract, whisper)",
           is_protected(real, "gliner-multi-2.1") and is_protected(real, "1.7.4-flix-large-v3-q8")
           and tags_for(real, "3.1.10050", "whisper-image.yml", job="publish", repository="whisper-swiss-german") == ["1.7.4-flix-large-v3-q8"]
           and tags_for(real, "3.1.10050", "s1-extract-image.yml", job="publish", repository="s1-extract") == ["gliner-multi-2.1"])
    # The service images moved to MeshWeaver.Deploy (core slimming): their writers are declared there,
    # so `check` does not look for those workflows here, and the line tags stay protected.
    expect("the four service-image writers name MeshWeaver.Deploy",
           sorted(b["workflow"] for e in real["channels"] for b in e["writers"]
                  if b.get("repo") == "Systemorph/MeshWeaver.Deploy")
           == ["ocr-image.yml", "ollama-image.yml", "s1-extract-image.yml", "whisper-image.yml"])
    expect("the moved service lines are still protected (ocr, ollama)",
           is_protected(real, "tesseract-5.5") and is_protected(real, "0.33.2-qwen3.6-bge-m3"))
    expect("a version-shaped label is still refused on a SELF-UPDATE repository",
           bool(broken(lambda d: [e for e in d["channels"] if e["id"] == "whisper-line"][0]["writers"][0].update(repositories=["memex-portal-ai"]))))

    # The publishers outside main-cd/release.yml are declared too (regression: they were omitted).
    expect("edge is protected (edge-images.yml writes it for portal and migration)",
           is_protected(real, "edge") and tags_for(real, "3.1.10050", "edge-images.yml", repository="memex-migration") == ["edge"])
    expect("hosting-operator:main is declared (MeshWeaver.Plugins hosting-operator.yml#operator)",
           tags_for(real, "3.1.10050", "hosting-operator.yml", job="operator", repository="hosting-operator") == ["main"])
    expect("the hosting-operator writer names the repository that publishes it",
           any(b.get("repo") == "Systemorph/MeshWeaver.Plugins" and b["workflow"] == "hosting-operator.yml"
               for e in real["channels"] if e["id"] == "main" for b in e["writers"]))
    # A binding into ANOTHER repository is not opened here; one into THIS repository still is.
    elsewhere = json.loads(json.dumps(real))
    elsewhere["channels"][3]["writers"][0].update(workflow="no-such-workflow.yml", repo="Systemorph/Elsewhere")
    expect("a writer in another repository is not looked for in this tree",
           not any("no-such-workflow.yml" in p for p in check(elsewhere, root, FakePV, always_floating)))
    here = json.loads(json.dumps(real))
    here["channels"][3]["writers"][0].update(workflow="no-such-workflow.yml", repo=THIS_REPO)
    expect("a writer that names THIS repository explicitly is still looked for",
           any("does not exist" in p for p in check(here, root, FakePV, always_floating)))
    expect("validate refuses a malformed `repo`",
           bool(broken(lambda d: d["channels"][3]["writers"][0].update(repo="not a repo"))))
    expect("memex-portal-ai-base:latest is declared (base-image-acr.yml#base)",
           tags_for(real, "3.1.10050", "base-image-acr.yml", job="base", repository="memex-portal-ai-base") == ["latest"])
    patterns = {p["id"]: p["repositories"] for p in protected_patterns(real)}
    expect("protected-patterns names the base-image and operator repositories",
           "memex-portal-ai-base" in patterns["latest"] and "hosting-operator" in patterns["main"])
    stale = json.loads(json.dumps(real))
    for e in stale["channels"]:
        if e["id"] == "edge":
            e["writers"][0]["repositories"].append("no-such-repo")
    expect("check fires on a literal-label binding naming a repository its workflow never writes",
           any("never names repository" in p for p in check(stale, root, FakePV, always_floating)))
    wrong_job = json.loads(json.dumps(real))
    wrong_job["channels"][0]["writers"][0]["job"] = "no-such-job"
    expect("check fires on a binding naming a job its workflow does not have",
           any("has no job" in p for p in check(wrong_job, root, FakePV, always_floating)))

    # is-protected's exit statuses: 0 protected, 1 not, 2 the lookup FAILED (a pruner must abort).
    with tempfile.TemporaryDirectory() as tmp:
        missing = Path(tmp) / "absent.json"
        malformed = Path(tmp) / "malformed.json"
        malformed.write_text(json.dumps({"schema": 1, "channels": []}))
        root_list = Path(tmp) / "root-list.json"
        root_list.write_text("[]")
        int_tag = Path(tmp) / "int-tag.json"
        bad = json.loads(json.dumps(real))
        for e in bad["channels"]:
            if e["id"] == "mac":
                e["tag"] = 3
        int_tag.write_text(json.dumps(bad))
        expect("is-protected: a JSON root of [] exits 2, never 1",
               main(["rc", "is-protected", "3-mac"], declaration=root_list) == 2)
        expect("is-protected: a non-string tag exits 2, never 1",
               main(["rc", "is-protected", "3-mac"], declaration=int_tag) == 2)
        expect("is-protected: a declared label exits 0", main(["rc", "is-protected", "3-mac"]) == 0)
        expect("is-protected: an immutable tag exits 1", main(["rc", "is-protected", "3.1.10050"]) == 1)
        expect("is-protected: a MISSING declaration exits 2, never 1",
               main(["rc", "is-protected", "3-mac"], declaration=missing) == 2)
        expect("is-protected: a MALFORMED declaration exits 2, never 1",
               main(["rc", "is-protected", "3-mac"], declaration=malformed) == 2)

    print(f"release-channels self-test: {'FAILED' if failures else 'passed'} ({len(failures)} failure(s))")
    return 1 if failures else 0


def main(argv: list[str], declaration: Path = DEFAULT_FILE) -> int:
    if len(argv) == 3 and argv[1] == "is-protected":
        try:
            return 0 if is_protected(load(declaration), argv[2]) else 1
        except Exception as error:  # noqa: BLE001 — ANY failed lookup is "unknown", never "unprotected"
            print(f"::error::release-channels: {error} — protection UNKNOWN, abort", file=sys.stderr)
            return 2
    try:
        if argv[1:] == ["--self-test"]:
            return self_test()
        if len(argv) >= 2 and argv[1] == "check":
            root = Path(argv[2]) if len(argv) > 2 else HERE.parent.parent
            problems = check(load(root / ".github" / "release-channels.json"), root)
            for p in problems:
                print(f"::error::release-channels: {p}")
            if not problems:
                n = len(channels(load(root / ".github" / "release-channels.json")))
                print(f"release-channels: {n} declared channel label(s), all consistent")
            return 1 if problems else 0
        if argv[1:] == ["protected-patterns"]:
            print(json.dumps(protected_patterns(load(declaration)), indent=2))
            return 0
        if len(argv) >= 2 and argv[1] == "tags":
            opts = dict(zip(argv[2::2], argv[3::2]))
            if "--version" not in opts or "--writer" not in opts:
                raise ChannelError("tags needs --version <v> and --writer <workflow>")
            for tag in tags_for(load(declaration), opts["--version"], opts["--writer"], opts.get("--job"), opts.get("--repository")):
                print(tag)
            return 0
    except ChannelError as error:
        print(f"::error::release-channels: {error}", file=sys.stderr)
        return 1
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))

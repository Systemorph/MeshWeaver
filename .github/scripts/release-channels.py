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
        # exit 0 when <tag> is a declared channel label (any status), 1 when it is not
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
from functools import lru_cache
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_FILE = HERE.parent / "release-channels.json"

STATUSES = ("written", "declared", "retired")
NOTATIONS = ("any", "legacy")
PLACEHOLDERS = ("major", "minor", "patch")
REQUIRED_FIELDS = ("id", "tag", "edition", "meaning", "repositories", "writer", "notation", "status")
# A label must look like a tag and must NEVER be SELECTABLE as a version. The self-updater
# (memex/Memex.Portal.Shared/SelfUpdate/VersionSelect.cs) keeps every tag matching
# `PlatformVersionTag` = ^\d+\.\d+\.\d+([-+].*)?$ EXCEPT one ending in `-latest` (`MovingPointer`), so
# a label of that dotted shape with any other suffix (`3.1.0-mac`) would be rolled to as if it were
# a build. Both regexes are mirrored here; VersionSelectTest pins the C# side.
TAG_CHARS_RE = re.compile(r"^[A-Za-z0-9{}][A-Za-z0-9._{}-]*$")
VERSION_SHAPED_RE = re.compile(r"^\d+\.\d+\.\d+([-+].*)?$")
MOVING_POINTER_RE = re.compile(r"-latest$", re.IGNORECASE)
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
        cid, tag = entry.get("id"), entry.get("tag")
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
            elif VERSION_SHAPED_RE.match(rendered) and not MOVING_POINTER_RE.search(rendered):
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
        writer = entry.get("writer")
        if not isinstance(writer, dict) or not writer.get("workflow") or not writer.get("jobs"):
            problems.append(f"{where} ({cid}): `writer` must name a `workflow` and its `jobs`")
        repos = entry.get("repositories")
        if repos is not None and (not isinstance(repos, list) or not all(isinstance(r, str) and r for r in repos)):
            problems.append(f"{where} ({cid}): `repositories` must be a list of repository names")
    return problems


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
        writer = entry.get("writer") or {}
        if writer.get("workflow") != workflow:
            continue
        if job is not None and job not in (writer.get("jobs") or []):
            continue
        if repository is not None and repository not in (entry.get("repositories") or []):
            continue
        if entry.get("notation") == "legacy" and not legacy:
            continue
        out.append(render(entry["tag"], major, minor, patch))
    return out


def protected_patterns(doc: dict) -> list[dict]:
    return [{"id": e["id"], "tag": e["tag"], "regex": template_regex(e["tag"]).pattern,
             "status": e["status"], "repositories": list(e.get("repositories") or [])}
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
        wf = entry["writer"]["workflow"]
        if not (workflows / wf).is_file():
            problems.append(f"{entry['id']}: writer workflow .github/workflows/{wf} does not exist")
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
    expect("SemVer set: arm moves N-latest and N.M-latest only",
           sorted(tags_for(real, "3.1.10050", "main-cd.yml", job="arm")) == ["3-latest", "3.1-latest"])
    expect("legacy set: arm also moves the patch pointer",
           sorted(tags_for(real, "3.0.0-ci.8080", "main-cd.yml", job="arm")) == ["3-latest", "3.0-latest", "3.0.0-latest"])
    expect("a `declared` label is never handed to a writer",
           tags_for(real, "3.1.0", "release.yml") == [])
    flipped = json.loads(json.dumps(real))
    for e in flipped["channels"]:
        if e["id"] == "enterprise":
            e["status"] = "written"
    expect("flipping enterprise to `written` hands release.yml N-enterprise",
           tags_for(flipped, "3.1.0", "release.yml") == ["3-enterprise"])
    expect("literal labels are returned to their writer (promote moves main and latest on the tester)",
           sorted(tags_for(real, "3.1.10050", "main-cd.yml", job="promote", repository="mw-plugin-test")) == ["latest", "main"])
    expect("--repository narrows to the declared repositories",
           tags_for(real, "3.1.10050", "main-cd.yml", job="arm", repository="mw-plugin-test") == [])
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
    expect("a missing writer is refused", bool(broken(lambda d: d["channels"][0].pop("writer"))))
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
    import tempfile
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
    missing_writer["channels"][3]["writer"]["workflow"] = "no-such-workflow.yml"
    expect("check fires on a writer workflow that does not exist",
           any("does not exist" in p for p in check(missing_writer, root, FakePV, always_floating)))

    print(f"release-channels self-test: {'FAILED' if failures else 'passed'} ({len(failures)} failure(s))")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
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
        if len(argv) == 3 and argv[1] == "is-protected":
            return 0 if is_protected(load(), argv[2]) else 1
        if argv[1:] == ["protected-patterns"]:
            print(json.dumps(protected_patterns(load()), indent=2))
            return 0
        if len(argv) >= 2 and argv[1] == "tags":
            opts = dict(zip(argv[2::2], argv[3::2]))
            if "--version" not in opts or "--writer" not in opts:
                raise ChannelError("tags needs --version <v> and --writer <workflow>")
            for tag in tags_for(load(), opts["--version"], opts["--writer"], opts.get("--job"), opts.get("--repository")):
                print(tag)
            return 0
    except ChannelError as error:
        print(f"::error::release-channels: {error}", file=sys.stderr)
        return 1
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))

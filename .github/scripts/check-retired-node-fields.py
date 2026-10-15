#!/usr/bin/env python3
"""check-retired-node-fields: a node file carrying a RETIRED MeshNode member is an ERROR.

GitSync reads a repository strictly (GitHubSyncService: `UnmappedMemberHandling.Disallow`), so ONE
node file that still carries a member `MeshNode` no longer has refuses the WHOLE snapshot: nothing is
written, the commit is not acknowledged, and the Space stays on its previous commit (#6638 — 15
MeshWeaver.Manufacturing files carrying `isPersistent`, measured refusing on two portals). The
platform stays strict on purpose — a reader that tolerated unknown members would let an OLDER image
silently drop fields a NEWER record carries — so the content is migrated, never the reader. This gate
is where that becomes a build error: the pull request that adds (or keeps) a retired member is red,
naming the file, the field and what replaced it.

The register is `.github/meshnode-fields.json` in core: `members` is the type's JSON members (kept
equal to `MeshNode` by reflection in MeshNodeFieldsRegisterTest, so a member removed from the type
fails that test until its name is moved to `retired`), and `retired` maps each removed member to
what to do instead. A name is compared case-insensitively: under any casing it is the same unmapped
member to the strict reader.

What is a node file (mirrors check-node-path-collisions.py / JsonFileParser.LooksLikeMeshNode): a
`.json` whose top level is an object carrying `$type` (exact), `id` or `nodeType`
(case-insensitive). Only TOP-LEVEL members are checked — `content` is the node's payload and may carry
any name (a package root's `content.requires` is the live dependency declaration). Not node files by
design: anything under a `content/` segment (assets), and — in a node repo — the top-level `src/`
module sources and dot-directories. Markdown front matter is not checked here.

Modes:
    --root R                  a node repo (every top-level directory but dot-directories and src/)
    --tree DIR [--tree DIR …] a node tree served from DIR (core's Data trees)
    --register FILE           the register (default: ../meshnode-fields.json beside this script)
    --self-test               prove the gate can fail
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import os
import sys
import tempfile
from pathlib import Path

DEFAULT_REGISTER = Path(__file__).resolve().parent.parent / "meshnode-fields.json"
SKIPPED_DIRS = {"bin", "obj", "node_modules"}


def load_register(path: Path) -> dict[str, str]:
    """The retired members, lower-cased name → (canonical name, advice). Refuses a register whose
    retired names overlap its members — that register would red a valid file."""
    doc = json.loads(path.read_text(encoding="utf-8"))
    members = {m.lower() for m in doc.get("members", [])}
    retired = doc.get("retired") or {}
    if not retired:
        raise ValueError(f"{path}: the register lists no retired member — the gate would pass vacuously")
    overlap = sorted(n for n in retired if n.lower() in members)
    if overlap:
        raise ValueError(f"{path}: {overlap} are listed as both a member and retired")
    return {name.lower(): (name, advice) for name, advice in retired.items()}


def is_content_asset(relative: str) -> bool:
    segments = [s for s in relative.replace("\\", "/").strip("/").split("/") if s]
    return any(s.lower() == "content" for s in segments[:-1])


def strip_jsonc(text: str) -> str:
    """TEXT with `//` and `/* */` comments and trailing commas removed, outside string literals.

    The importer reads node files with comments skipped and trailing commas allowed
    (JsonFileParser: `CommentHandling` / `AllowTrailingCommas`), so a comment-bearing node is a node
    to GitSync. Parsing only strict JSON here would skip exactly that node and pass over its retired
    members — this reads the same dialect instead."""
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        elif c == ",":
            j = i + 1
            while j < n and text[j] in " \t\r\n":
                j += 1
            if j < n and text[j] in "}]":
                i += 1            # a trailing comma
            else:
                out.append(c)
                i += 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def node_json(path: Path) -> dict | None:
    """The parsed top-level object when PATH is a node file, else None. Reads the importer's dialect
    (comments, trailing commas); text that is not JSON even then is not a node to the importer either."""
    try:
        text = path.read_text(encoding="utf-8-sig")
    except (OSError, UnicodeDecodeError):
        return None
    try:
        doc = json.loads(text)
    except ValueError:
        try:
            doc = json.loads(strip_jsonc(strip_jsonc(text)))   # comments first, then a comma they hid
        except ValueError:
            return None
    if not isinstance(doc, dict):
        return None
    if not any(k == "$type" or k.lower() in ("id", "nodetype") for k in doc):
        return None
    return doc


def walk(directory: Path):
    if directory.is_file():
        yield directory
        return
    for d, dirs, files in os.walk(directory):
        dirs[:] = sorted(x for x in dirs if not x.startswith(".") and x not in SKIPPED_DIRS)
        for f in sorted(files):
            if f.lower().endswith(".json"):
                yield Path(d) / f


def scan(base: Path, dirs: list[Path], register: dict) -> tuple[list[str], int]:
    """(findings, node files seen); paths relative to BASE."""
    findings: list[str] = []
    nodes = 0
    for directory in dirs:
        for f in walk(directory):
            relative = f.relative_to(base).as_posix()
            if is_content_asset(relative):
                continue
            doc = node_json(f)
            if doc is None:
                continue
            nodes += 1
            for key in doc:
                hit = register.get(key.lower())
                if hit:
                    name, advice = hit
                    spelled = "" if key == name else f" (as '{name}')"
                    findings.append(
                        f"{relative}: field '{key}' was RETIRED from MeshNode{spelled} — {advice}. "
                        "GitSync's strict reader refuses the whole snapshot on it.")
    return findings, nodes


def repo_dirs(root: Path) -> list[Path]:
    return sorted(e for e in root.iterdir()
                  if (e.is_file() and e.suffix.lower() == ".json")
                  or (e.is_dir() and not e.name.startswith(".") and e.name != "src"))


def report(label: str, findings: list[str], nodes: int, base: Path) -> int:
    for finding in findings:
        file = base / finding.split(":", 1)[0]
        with contextlib.suppress(ValueError):
            file = file.relative_to(Path.cwd())   # annotations want a workspace-relative path
        print(f"::error file={file.as_posix()}::{finding}")
    verdict = "FAIL" if findings or nodes == 0 else "ok"
    print(f"check-retired-node-fields [{label}]: {verdict} — {len(findings)} retired field(s) over "
          f"{nodes} node file(s)")
    if nodes == 0:
        print("::error::no node file was found — the gate would pass vacuously; check the roots")
        return 1
    return 1 if findings else 0


def self_test() -> int:
    failures: list[str] = []
    cases: list[str] = []
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)
        reg_file = base / "register.json"
        reg_file.write_text(json.dumps({
            "members": ["id", "name", "nodeType", "content", "order"],
            "retired": {"isPersistent": "remove it", "displayOrder": "use `order`"}}), encoding="utf-8")
        register = load_register(reg_file)

        def case(name: str, files: dict[str, str], expect: int, root_mode: bool = False) -> list[str]:
            cases.append(name)
            root = base / name
            for rel, text in files.items():
                p = root / rel
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text(text, encoding="utf-8")
            dirs = repo_dirs(root) if root_mode else [root]
            got, _ = scan(root, dirs, register)
            if len(got) != expect:
                failures.append(f"{name}: expected {expect} finding(s), got {len(got)}: {got}")
            return got

        # MUST FAIL — the measured shape, its casing, and two in one file.
        got = case("measured", {"Manufacturing/MorningBrief.json":
                                '{"$type":"MeshNode","id":"MorningBrief","isPersistent":true}'}, 1)
        if got and not ("Manufacturing/MorningBrief.json" in got[0] and "'isPersistent'" in got[0]
                        and "RETIRED" in got[0]):
            failures.append(f"measured: the finding must name the file, the field and that it was retired: {got[0]}")
        case("casing", {"A.json": '{"id":"a","IsPersistent":true}'}, 1)
        case("two-in-one", {"B.json": '{"id":"b","isPersistent":true,"displayOrder":2}'}, 2)
        # The importer's dialect: a comment-bearing or trailing-comma node is still a node.
        case("jsonc-comments", {"J.json": '// header\n{"id":"j", /* c */ "isPersistent":true}'}, 1)
        case("jsonc-trailing-comma", {"K.json": '{"id":"k","displayOrder":2,}'}, 1)
        case("jsonc-comma-before-comment", {"M.json": '{"id":"m","isPersistent":true, // c\n}'}, 1)
        case("jsonc-slashes-in-string", {"L.json": '{"id":"l","icon":"https://x/y", // c\n"isPersistent":true}'}, 1)
        case("root-mode-package", {"Pkg/C.json": '{"nodeType":"Markdown","isPersistent":true}'}, 1, root_mode=True)

        # MUST PASS — nested payloads, non-node JSON, assets, module sources, tooling scratch.
        case("nested-in-content", {"D.json": '{"id":"d","content":{"isPersistent":true,"requires":["x"]}}'}, 0)
        case("not-a-node", {"package.json": '{"name":"x","isPersistent":true}'}, 0)
        case("asset", {"E/content/data.json": '{"id":"e","isPersistent":true}'}, 0)
        case("not-json", {"F.json": "not json"}, 0)
        case("root-mode-excludes", {"src/M/G.json": '{"id":"g","isPersistent":true}',
                                    ".agents/H.json": '{"id":"h","isPersistent":true}',
                                    "Pkg/I.json": '{"id":"i"}'}, 0, root_mode=True)

        # A register that would red a valid file is refused.
        cases.append("register-overlap")
        bad = base / "bad.json"
        bad.write_text(json.dumps({"members": ["id", "order"], "retired": {"order": "x"}}), encoding="utf-8")
        try:
            load_register(bad)
            failures.append("register-overlap: a retired name that is also a member was accepted")
        except ValueError:
            pass
        cases.append("register-empty")
        empty_reg = base / "empty-reg.json"
        empty_reg.write_text(json.dumps({"members": ["id"], "retired": {}}), encoding="utf-8")
        try:
            load_register(empty_reg)
            failures.append("register-empty: an empty retired list was accepted")
        except ValueError:
            pass

        # The vacuity guard: no node file is RED, never a clean zero.
        with contextlib.redirect_stdout(io.StringIO()):
            if report("self-test empty", [], 0, base) != 1:
                failures.append("an empty scan passed")

    for f in failures:
        print(f"✗ {f}")
    print(f"check-retired-node-fields --self-test: {'FAIL' if failures else 'ok'} "
          f"({len(cases)} cases + vacuity guard; {len(failures)} failure(s))")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--root", type=Path)
    ap.add_argument("--tree", type=Path, action="append", default=[])
    ap.add_argument("--register", type=Path, default=DEFAULT_REGISTER)
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    if args.tree and args.root:
        ap.error("--tree is its own mode; do not combine it with --root")
    if not args.tree and not args.root:
        ap.error("give --root (a node repo) or one or more --tree")
    try:
        register = load_register(args.register)
    except (OSError, ValueError) as e:
        print(f"::error::the MeshNode field register could not be read: {e}")
        return 1
    if args.tree:
        rc = 0
        for tree in args.tree:
            tree = tree.resolve()
            if not tree.is_dir():
                print(f"::error::--tree {tree} is not a directory")
                rc = 1
                continue
            findings, nodes = scan(tree, [tree], register)
            rc |= report(str(tree), findings, nodes, tree)
        return rc
    root = args.root.resolve()
    findings, nodes = scan(root, repo_dirs(root), register)
    return report(str(root), findings, nodes, root)


if __name__ == "__main__":
    sys.exit(main())

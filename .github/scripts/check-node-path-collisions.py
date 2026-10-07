#!/usr/bin/env python3
"""check-node-path-collisions: refuse two source files that import to the SAME mesh node path.

The importers (PackageInstaller.NodePathForFile / ParseCanonical, GitHubSyncService.ParseFile,
FileSystemStorageAdapter) derive a node's path from the FILE's location, never from its content:
`NodeFileMapper.FromRelativePath` strips the extension, folds `X/index.*` onto `X`, and mesh paths
compare OrdinalIgnoreCase. So `Hosting/StuckDetector.md` and `Hosting/StuckDetector.json` are ONE
node, and so are `A.md` + `A/index.json` and `Foo.md` + `foo.cs`. Nothing refuses that pair at import:
the bulk writer treats a duplicate path as last-writer-wins (StaticRepoImporter, `seenInStage`), the
file-system adapter returns whichever extension it tries first, and which file wins depends on
enumeration order — so one author's node is silently replaced by another's, with no error, no log
line and nothing to grep. This gate is where it becomes visible: every colliding group is named with
ALL its files.

The rule mirrored here, and where each half lives in core:
  * node-file extensions: `.md`, `.cs`, `.json`, case-insensitive (FileFormatParserRegistry —
    the built-in parsers; every parser MeshWeaver.Plugins contributes — agent, skill, slide —
    claims `.md` only, so a new contributed extension must be added here too);
  * a `.json` is a node only when it is an object carrying `$type` (exact), `id` or `nodeType`
    (case-insensitive) — JsonFileParser.LooksLikeMeshNode; a `.json` that is not JSON is not a
    node either (the installer counts it unreadable); each one is NAMED on a ::warning:: line,
    never compared — the hub's options may accept a comment-bearing file this parser refuses, so
    a same-stem pair beside one is the gap this gate cannot close and must not hide;
  * not a node by design (PackageInstaller.IsNotANodeFile): the tree-root `README.md`, any
    `manifest.lock`, every file under a `content/` segment (ContentAssetMapper), and — in a node
    repo — the top-level `src/` module sources (IsModuleSourcePath);
  * path = NodeFileMapper.FromRelativePath, compared case-insensitively.

Two modes:
    --root R                                      a node repo: every Git-visible file except the
                                                  top-level dot-directories and `src/`, paths
                                                  repo-relative (a package's first segment IS its
                                                  partition, so this is the install's own key)
    --tree DIR [--tree DIR …]                     a node tree served from DIR (core's Data trees,
                                                  a GitSync subdirectory), paths DIR-relative
      [--filesystem-layout]                       DIR is served by FileSystemStorageAdapter, which
                                                  MERGES `X.json` + `X/index.md` by design
    --self-test                                   prove the gate can fail
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import subprocess
import sys
import tempfile
from pathlib import Path, PurePosixPath

NODE_EXTENSIONS = {".md", ".cs", ".json"}
CONTENT_SEGMENT = "content"


def node_path(relative: str) -> str:
    """NodeFileMapper.FromRelativePath, joined back into a path ("" is the tree root)."""
    p = relative.replace("\\", "/").strip("/")
    slash = p.rfind("/")
    dot = p.rfind(".")
    if dot > slash:
        p = p[:dot]
    slash = p.rfind("/")
    if slash < 0:
        return "" if p.lower() == "index" else p
    ns, leaf = p[:slash], p[slash + 1:]
    return ns if leaf.lower() == "index" else p


def is_content_asset(relative: str) -> bool:
    """ContentAssetMapper.TrySplit: a `content` segment with at least one segment after it."""
    segments = [s for s in relative.replace("\\", "/").strip("/").split("/") if s]
    return any(s.lower() == CONTENT_SEGMENT for s in segments[:-1])


def looks_like_node_json(text: str) -> bool | None:
    """JsonFileParser.LooksLikeMeshNode; None when the text is not JSON at all."""
    try:
        doc = json.loads(text)
    except ValueError:
        return None
    if not isinstance(doc, dict):
        return False
    return any(k == "$type" or k.lower() in ("id", "nodetype") for k in doc)


def candidate_kind(path: Path, relative: str) -> str:
    """'node', 'skip' (not a node file by path), or 'unreadable' (a .json that is not JSON)."""
    ext = PurePosixPath(relative).suffix.lower()
    if ext not in NODE_EXTENSIONS:
        return "skip"
    # `manifest.lock` (IsNotANodeFile) needs no arm here: `.lock` is not a node extension.
    if relative.lower() == "readme.md":
        return "skip"
    if is_content_asset(relative):
        return "skip"
    if ext == ".json":
        try:
            verdict = looks_like_node_json(path.read_text(encoding="utf-8-sig"))
        except (OSError, UnicodeDecodeError):
            return "unreadable"
        if verdict is None:
            return "unreadable"
        if not verdict:
            return "skip"
    return "node"


def visible_files(directory: Path) -> list[Path]:
    """Git-visible files (tracked + untracked-not-ignored) under DIRECTORY; a plain walk when the
    directory is not inside a work tree (the self-test's fixtures). A FILE answers itself."""
    if directory.is_file():
        return [directory]
    try:
        out = subprocess.run(
            ["git", "-C", str(directory), "ls-files", "-z", "--cached", "--others",
             "--exclude-standard", "--", "."],
            check=True, capture_output=True)
        names = [n for n in out.stdout.decode("utf-8").split("\0") if n]
        return sorted({directory / n for n in names if (directory / n).is_file()})
    except (subprocess.CalledProcessError, FileNotFoundError):
        return sorted(p for p in directory.rglob("*") if p.is_file())


def is_filesystem_split(files: list[str]) -> bool:
    """`X.json` + `X/index.md` — FileSystemStorageAdapter.MergeIndexMarkdownAsync's "JSON registry +
    index.md split": the adapter reads the JSON and lays the markdown in as its Content. Only that
    adapter merges them (core's samples tree); the package installer and GitSync do NOT, so the same
    pair in a node repo is an ordinary collision."""
    if len(files) != 2:
        return False
    a, b = sorted(files, key=lambda f: PurePosixPath(f).suffix.lower() != ".json")
    return (PurePosixPath(a).suffix.lower() == ".json"
            and PurePosixPath(b).name.lower() == "index.md"
            and a[:-len(".json")].lower() == str(PurePosixPath(b).parent).lower())


def scan(base: Path, dirs: list[Path], filesystem_layout: bool = False) -> tuple[list[str], int, list[str]]:
    """Returns (findings, node files seen, unreadable .json paths); paths relative to BASE.
    FILESYSTEM_LAYOUT admits the one pair FileSystemStorageAdapter merges (see is_filesystem_split)."""
    groups: dict[str, list[str]] = {}
    nodes = 0
    unreadable: list[str] = []
    for directory in dirs:
        for f in visible_files(directory):
            relative = f.relative_to(base).as_posix()
            kind = candidate_kind(f, relative)
            if kind == "unreadable":
                unreadable.append(relative)
            if kind != "node":
                continue
            nodes += 1
            groups.setdefault(node_path(relative).lower(), []).append(relative)
    findings = []
    for key in sorted(groups):
        files = sorted(groups[key])
        if len(files) > 1 and not (filesystem_layout and is_filesystem_split(files)):
            shown = node_path(files[0]) or "(the tree root)"
            findings.append(
                f"{len(files)} files import to the same node path '{shown}': "
                + ", ".join(files)
                + " — the importer keeps ONE of them (last writer wins) and drops the rest "
                "without a word; delete or rename all but one")
    return findings, nodes, unreadable


def repo_dirs(root: Path) -> list[Path]:
    """The node repo's scan roots: every top-level directory but the dot-directories (tooling
    scratch — never a package, gen-manifests.plugin_dirs) and `src/` (module sources, never a node —
    PackageInstaller.IsModuleSourcePath).

    🚨 Deliberately WIDER than the package enumeration, and deliberately not read from it: the
    canonical `gen-manifests.py --list-packages` refuses a repo with no `gen-manifests.config.json`
    (two lane callers have none), and a gate that cannot enumerate must neither skip nor red the
    fleet. Over-inclusion can only ADD files to compare; measured over every caller's main
    (Plugins, Education, Reinsurance, SocialMedia, Manufacturing, Crm, Memex — 12,476 files) it
    finds no group outside a package, so it costs no false red today. Top-level FILES are included:
    a root `index.*` is the GitSync Space root."""
    return sorted(e for e in root.iterdir()
                  if e.is_file() or (e.is_dir() and not e.name.startswith(".") and e.name != "src"))


def report(label: str, findings: list[str], nodes: int, unreadable: list[str], scope: int) -> int:
    for finding in findings:
        print(f"::error::{finding}")
    for path in unreadable:
        print(f"::warning::{path} is not strict JSON, so this gate did not compare it — if the "
              "importer reads it as a node, a same-stem sibling would collide with it unseen")
    note = f", {len(unreadable)} unreadable .json named above, not compared" if unreadable else ""
    verdict = "FAIL" if findings or nodes == 0 else "ok"
    print(f"check-node-path-collisions [{label}]: {verdict} — {len(findings)} collision(s) over "
          f"{nodes} node file(s) in {scope} root(s){note}")
    if nodes == 0:
        print("::error::no node file was found — the gate would pass vacuously; check the roots")
        return 1
    return 1 if findings else 0


def self_test() -> int:
    failures: list[str] = []

    def write(root: Path, rel: str, text: str = "---\nnodeType: Markdown\n---\n") -> None:
        p = root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")

    node_json = '{"$type": "MeshNode", "id": "x", "nodeType": "Markdown"}'
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)

        cases: list[str] = []   # the banner's count is READ from here, never written by hand

        def case(name: str, files: dict[str, str], expect: int, fs: bool = False) -> list[str]:
            cases.append(name)
            root = base / name
            for rel, text in files.items():
                write(root, rel, text)
            findings, _, _ = scan(root, [root], fs)
            if len(findings) != expect:
                failures.append(f"{name}: expected {expect} collision(s), got {len(findings)}: {findings}")
            return findings

        # MUST FAIL — the measured shape and its siblings.
        found = case("md-vs-json", {"Hosting/StuckDetector.md": "# x",
                                    "Hosting/StuckDetector.json": node_json}, 1)
        if found and not ("Hosting/StuckDetector.md" in found[0] and "Hosting/StuckDetector.json" in found[0]):
            failures.append(f"md-vs-json: the finding must name BOTH files: {found[0]}")
        case("leaf-vs-index", {"A.md": "# a", "A/index.json": node_json, "A/B.md": "# b"}, 1)
        case("case-folded", {"Foo.md": "# f", "foo.cs": "// c"}, 1)
        case("ext-case", {"X/Y.MD": "# y", "X/Y.cs": "// y"}, 1)
        case("three-way", {"N.md": "#", "N.json": node_json, "N.cs": "//"}, 1)
        case("root-index", {"index.md": "#", "index.json": node_json}, 1)

        # MUST PASS — the importer's own non-node files never collide.
        case("distinct", {"A.md": "#", "B.json": node_json, "A/C.cs": "//"}, 0)
        case("non-node-json", {"P.md": "#", "P.json": '{"name": "pkg", "version": "1.0.0"}'}, 0)
        case("not-json", {"Q.md": "#", "Q.json": "this is not json"}, 0)
        if scan(base / "not-json", [base / "not-json"])[2] != ["Q.json"]:
            failures.append("not-json: the unreadable .json must be NAMED, not only counted")
        # `R/content/index.md` would fold onto `R/content` — but it is an ASSET, never a node.
        case("content-asset", {"R/content.md": "#", "R/content/index.md": "#",
                                "content.json": node_json, "content/index.json": node_json}, 0)
        case("readme-and-lock", {"README.md": "#", "readme.json": '{"x": 1}', "manifest.lock": "{}"}, 0)
        case("other-ext", {"S.md": "#", "S.tsx": "x", "S.png": "x"}, 0)
        case("same-stem-other-folder", {"T/U.md": "#", "V/U.json": node_json}, 0)

        # The FileSystem adapter's JSON + index.md split: merged THERE, a collision everywhere else.
        case("fs-split-in-a-node-repo", {"S2.json": node_json, "S2/index.md": "#"}, 1)
        case("fs-split-on-the-adapter", {"S3.json": node_json, "S3/index.md": "#"}, 0, fs=True)
        case("fs-split-not-a-licence", {"S4.md": "#", "S4/index.md": "#"}, 1, fs=True)
        case("fs-split-three", {"S5.json": node_json, "S5/index.md": "#", "S5.cs": "//"}, 1, fs=True)

        # --root mode: module sources and tooling scratch are not nodes; a package collision is.
        repo = base / "repo-mode"
        cases.append("repo-mode")
        for rel, text in {"src/M/Foo.cs": "//", "src/M/Foo.json": node_json,
                          ".agents/x/Y.md": "#", ".agents/x/Y.json": node_json,
                          "Pkg/Z.md": "#", "Pkg/Z.json": node_json, "index.md": "#"}.items():
            write(repo, rel, text)
        got, nodes, _ = scan(repo, repo_dirs(repo))
        if len(got) != 1 or "Pkg/Z.json" not in got[0]:
            failures.append(f"repo-mode: expected exactly the Pkg/Z collision, got {got}")
        if nodes != 3:
            failures.append(f"repo-mode: expected 3 node files (Pkg/Z.md, Pkg/Z.json, index.md), saw {nodes}")

        # The vacuity guard: an empty tree is RED, never a clean zero.
        empty = base / "empty"
        empty.mkdir()
        with contextlib.redirect_stdout(io.StringIO()):   # its ::error:: line is the EXPECTED outcome here
            vacuous = report("self-test empty", [], 0, [], 1)
        if vacuous != 1 or scan(empty, [empty])[1] != 0:
            failures.append("an empty tree passed")

    for f in failures:
        print(f"✗ {f}")
    print(f"check-node-path-collisions --self-test: {'FAIL' if failures else 'ok'} "
          f"({len(cases)} cases + vacuity guard; {len(failures)} failure(s))")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--root", type=Path)
    ap.add_argument("--tree", type=Path, action="append", default=[])
    ap.add_argument("--filesystem-layout", action="store_true",
                    help="the --tree is served by FileSystemStorageAdapter, which merges X.json + X/index.md")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    if args.filesystem_layout and not args.tree:
        ap.error("--filesystem-layout describes a --tree; a node repo's packages are installed, never merged")
    if args.tree and args.root:
        ap.error("--tree is its own mode; do not combine it with --root")
    if args.tree:
        rc = 0
        for tree in args.tree:
            tree = tree.resolve()
            if not tree.is_dir():
                print(f"::error::--tree {tree} is not a directory")
                rc = 1
                continue
            findings, nodes, unreadable = scan(tree, [tree], args.filesystem_layout)
            rc |= report(str(tree), findings, nodes, unreadable, 1)
        return rc
    if not args.root:
        ap.error("give --root (a node repo) or one or more --tree")
    root = args.root.resolve()
    dirs = repo_dirs(root)
    findings, nodes, unreadable = scan(root, dirs)
    return report(str(root), findings, nodes, unreadable, len(dirs))


if __name__ == "__main__":
    sys.exit(main())

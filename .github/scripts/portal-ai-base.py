#!/usr/bin/env python3
"""portal-ai-base.py — names the portal-ai BASE image by its CONTENT, and holds its runtime to the
platform's target framework.

WHY. Every portal-ai and control image is a FRAMEWORK-DEPENDENT publish (`--no-self-contained`)
layered on `memex-portal-ai-base`, the one hand-authored image (deploy/base-images/portal-ai). Its
runtime is whatever `ARG DOTNET_VERSION` said when the base was last BUILT — and the base was built
by a hand-dispatched workflow and selected by the moving tag `:latest`. The v4 line (#8135) moved
the Dockerfile to `aspnet:11.0` and the platform to `net11.0`; nobody dispatched the base workflow,
so `:latest` still held the `aspnet:10.0` base from 2026-09-16, and CD run 43592014876 layered a
net11.0 app on a .NET 10 runtime. The host refused to start before Main ran ("You must install or
update .NET to run this application … Microsoft.NETCore.App, version '11.0.0'"), and the control
image acceptance leg (a) saw a container that had already exited.

That is two defects in one name: (1) a TREE's base was resolved by a tag that does not change when
the tree's Dockerfile does, so the build used bytes the tree no longer describes; and (2) the
delivery lines (policy `major-lines-run-separately`) shared that one tag, so rebuilding `:latest`
for v4 would have broken every v3 build the same way in the other direction.

THE RULE. The base an image layers on is named by the content of the directory that builds it:
`memex-portal-ai-base:src-<sha256 of deploy/base-images/portal-ai, 16 hex>`. A Dockerfile change is
a new tag, CD builds it when it is absent, and each line resolves exactly the base its own tree
describes. And the Dockerfile's `ARG DOTNET_VERSION` must equal the platform's `<TargetFramework>`
(Directory.Build.props), so a tree that moves one without the other is RED before any image builds.

  portal-ai-base.py tag [--root <repo root>]
        # the content tag for this tree's base (one line, no network)
  portal-ai-base.py check [--root <repo root>]
        # the PR gate: runtime == target framework, and no workflow layers on a non-content base tag
  portal-ai-base.py --self-test
"""
from __future__ import annotations

import hashlib
import re
import sys
import tempfile
from pathlib import Path

BASE_DIR = Path("deploy/base-images/portal-ai")
BUILD_PROPS = Path("Directory.Build.props")
WORKFLOWS = Path(".github/workflows")
TAG_PREFIX = "src-"
TAG_HEX = 16
BASE_REPOSITORY = "memex-portal-ai-base"

ARG_RE = re.compile(r"^\s*ARG\s+DOTNET_VERSION\s*=\s*(\S+)\s*$", re.MULTILINE)
TFM_RE = re.compile(r"<TargetFramework>\s*net(\d+\.\d+)\s*</TargetFramework>")
# Every reference to the base image in a workflow, with its tag (or an expression standing for it).
BASE_REF_RE = re.compile(re.escape(BASE_REPOSITORY) + r":([^\s\"'\\]+)")


class BaseError(ValueError):
    """The tree cannot answer the question — always RED, never a default."""


def content_tag(root: Path) -> str:
    """`src-<hex>` over every file under the base directory: relative path and bytes, sorted."""
    base = root / BASE_DIR
    files = sorted(p for p in base.rglob("*") if p.is_file()) if base.is_dir() else []
    if not files:
        raise BaseError(f"{BASE_DIR} holds no files under {root} — there is no base to name")
    h = hashlib.sha256()
    for p in files:
        rel = p.relative_to(base).as_posix().encode()
        data = p.read_bytes()
        h.update(len(rel).to_bytes(8, "big") + rel + len(data).to_bytes(8, "big") + data)
    return TAG_PREFIX + h.hexdigest()[:TAG_HEX]


def dockerfile_runtime(root: Path) -> str:
    path = root / BASE_DIR / "Dockerfile"
    if not path.is_file():
        raise BaseError(f"{path} is missing")
    m = ARG_RE.findall(path.read_text(encoding="utf-8"))
    if len(m) != 1:
        raise BaseError(f"{path} must declare exactly one `ARG DOTNET_VERSION=<major>.<minor>` (found {len(m)})")
    return m[0]


def platform_framework(root: Path) -> str:
    path = root / BUILD_PROPS
    if not path.is_file():
        raise BaseError(f"{path} is missing")
    m = TFM_RE.findall(path.read_text(encoding="utf-8"))
    if len(set(m)) != 1:
        raise BaseError(f"{path} must declare exactly one <TargetFramework>net<major>.<minor></TargetFramework> (found {sorted(set(m))})")
    return m[0]


def workflow_findings(root: Path) -> list[str]:
    """Every workflow line that LAYERS an image on the base by anything but its content tag.

    The consuming position is `ContainerBaseImage=` — the SDK container publish's base. The content
    tag reaches it only as an expression (a job output the ensure step wrote), so a LITERAL tag
    there is the defect: `:latest` is exactly the shape that shipped a .NET 10 runtime under a
    net11.0 app. Lines that WRITE or inspect a tag (the hand runbook keeps `:latest` for consumers
    outside this repository) are not consumption and are not judged here."""
    findings = []
    wf = root / WORKFLOWS
    for path in sorted(list(wf.glob("*.yml")) + list(wf.glob("*.yaml"))) if wf.is_dir() else []:
        for n, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if line.lstrip().startswith("#") or "ContainerBaseImage" not in line:
                continue
            for tag in BASE_REF_RE.findall(line):
                if tag.startswith("${{") or tag.startswith("$"):
                    continue
                findings.append(f"{path.relative_to(root).as_posix()}:{n}: layers on `{BASE_REPOSITORY}:{tag}` — a literal tag does not change when deploy/base-images/portal-ai does; resolve the content tag (portal-ai-base.py tag) instead")
    return findings


def check(root: Path) -> list[str]:
    problems = []
    try:
        runtime, tfm = dockerfile_runtime(root), platform_framework(root)
        if runtime != tfm:
            problems.append(f"{BASE_DIR}/Dockerfile runs aspnet:{runtime} but the platform targets net{tfm} — a framework-dependent net{tfm} host cannot start on a {runtime} runtime; move both together")
        content_tag(root)
    except BaseError as e:
        problems.append(str(e))
    problems.extend(workflow_findings(root))
    return problems


def _fixture(tmp: Path, runtime: str, tfm: str, workflow: str = "") -> Path:
    (tmp / BASE_DIR).mkdir(parents=True)
    (tmp / BASE_DIR / "Dockerfile").write_text(f"ARG DOTNET_VERSION={runtime}\nFROM mcr.microsoft.com/dotnet/aspnet:${{DOTNET_VERSION}}\n")
    (tmp / BASE_DIR / "fonts-local.conf").write_text("<fontconfig/>\n")
    (tmp / BUILD_PROPS).write_text(f"<Project><PropertyGroup><TargetFramework>net{tfm}</TargetFramework></PropertyGroup></Project>\n")
    (tmp / WORKFLOWS).mkdir(parents=True)
    (tmp / WORKFLOWS / "cd.yml").write_text(workflow)
    return tmp


def self_test() -> int:
    failures = []

    def expect(name: str, cond: bool) -> None:
        print(("ok   " if cond else "FAIL ") + name)
        if not cond:
            failures.append(name)

    good_wf = "      -p:ContainerBaseImage=${{ env.ACR }}/memex-portal-ai-base:${{ needs.portal-ai-base.outputs.tag }}\n"
    with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b, tempfile.TemporaryDirectory() as c:
        v3 = _fixture(Path(a), "10.0", "10.0", good_wf)
        v4 = _fixture(Path(b), "11.0", "11.0", good_wf)
        v4b = _fixture(Path(c), "11.0", "11.0", good_wf)
        expect("the content tag has the src-<16 hex> shape", re.fullmatch(r"src-[0-9a-f]{16}", content_tag(v4)) is not None)
        expect("the same base content names the same tag", content_tag(v4) == content_tag(v4b))
        # The incident: the v4 tree moved its Dockerfile to 11.0 and still resolved the base built
        # from 10.0. Under a content tag the two trees can never name the same base.
        expect("a Dockerfile runtime change is a NEW tag (v3 base ≠ v4 base)", content_tag(v3) != content_tag(v4))
        (v4b / BASE_DIR / "fonts-local.conf").write_text("<fontconfig><changed/></fontconfig>\n")
        expect("any file in the build context changes the tag", content_tag(v4) != content_tag(v4b))
        expect("a matching tree with a content-tag workflow passes", check(v4) == [] and check(v3) == [])

    with tempfile.TemporaryDirectory() as a:
        skew = _fixture(Path(a), "10.0", "11.0", good_wf)
        p = check(skew)
        expect("a net11.0 platform on an aspnet:10.0 base is RED", any("cannot start" in x for x in p))

    with tempfile.TemporaryDirectory() as a:
        latest = _fixture(Path(a), "11.0", "11.0", "      -p:ContainerBaseImage=${{ env.ACR }}/memex-portal-ai-base:latest\n")
        p = check(latest)
        expect("a workflow layering on memex-portal-ai-base:latest is RED", any(":latest" in x for x in p))
    with tempfile.TemporaryDirectory() as a:
        written = _fixture(Path(a), "11.0", "11.0", good_wf + "  # memex-portal-ai-base:latest was the old shape\n"
                           "          az acr manifest show -r meshweaver -n memex-portal-ai-base:latest\n")
        expect("a comment or a non-consuming line naming :latest is not a finding", check(written) == [])

    with tempfile.TemporaryDirectory() as a:
        root = Path(a)
        (root / BUILD_PROPS).write_text("<Project/>\n")
        p = check(root)
        expect("a tree with no base directory is RED, never a default", any("Dockerfile is missing" in x or "no files" in x for x in p))
        try:
            content_tag(root)
            expect("content_tag refuses an absent base directory", False)
        except BaseError:
            expect("content_tag refuses an absent base directory", True)

    print(f"{'FAILED' if failures else 'PASSED'}: {len(failures)} failure(s)")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if argv[1:] == ["--self-test"]:
        return self_test()
    if len(argv) < 2 or argv[1] not in ("tag", "check"):
        print(__doc__, file=sys.stderr)
        return 2
    root = Path(".")
    if "--root" in argv:
        root = Path(argv[argv.index("--root") + 1])
    root = root.resolve()
    if argv[1] == "tag":
        try:
            print(content_tag(root))
        except BaseError as e:
            print(f"::error::{e}", file=sys.stderr)
            return 1
        return 0
    problems = check(root)
    for p in problems:
        print(f"::error::{p}")
    if not problems:
        print(f"OK: {BASE_DIR} runs aspnet:{dockerfile_runtime(root)} = the platform's net{platform_framework(root)}; its content tag is {content_tag(root)}; no workflow layers on a literal base tag")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

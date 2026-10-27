#!/usr/bin/env python3
"""Every sibling script a packaged script loads is packaged with it.

An image that ships `.github/scripts/*` (today: `deploy/build-host/Dockerfile`) copies a FLAT list of
scripts into one directory, and its `<Dockerfile>.dockerignore` allow-lists exactly those files. A
packaged script that loads a sibling by name — `module-build-key.py` loads `node-repo-scope.py` for
its linked-source matcher; `build-run.py` runs `module-build-key.py` and `ci-artifact-store.py` —
finds it in the repository, where every test runs, and NOT in the image, where it is used: every
call there is a FileNotFoundError, and nothing before the image's first real use can see it.

The rule, followed TRANSITIVELY from every copied script: a quoted literal naming a file that exists
beside it in `.github/scripts/` (`"node-repo-scope.py"`, `'ci-artifact-store.py'`) must be
  1. in the Dockerfile's `COPY .github/scripts/…` list, AND
  2. allow-listed (`!.github/scripts/<name>`) in the Dockerfile's own `.dockerignore`,
because a file the COPY names and the context excludes fails the build, and one the context admits
but no COPY names never reaches the image. A name that is NOT a sibling (a caller repository's
`scripts/affected-modules.py`) is not demanded — it is not this image's file.

  check-image-script-siblings.py [--root .] [--dockerfile deploy/build-host/Dockerfile]
  check-image-script-siblings.py --self-test
"""
from __future__ import annotations

import argparse
import re
import sys
import tempfile
from pathlib import Path

SCRIPTS = ".github/scripts"
DEFAULT_DOCKERFILES = ("deploy/build-host/Dockerfile",)
_LITERAL = re.compile(r"[\"']([A-Za-z0-9_.-]+\.(?:py|sh))[\"']")


def copied_scripts(dockerfile_text: str) -> set[str]:
    """Basenames of `.github/scripts/*` sources in the Dockerfile's COPY instructions."""
    out: set[str] = set()
    joined = re.sub(r"\\\n", " ", dockerfile_text)
    for line in joined.splitlines():
        tokens = line.split()
        if not tokens or tokens[0].upper() != "COPY":
            continue
        for src in [t for t in tokens[1:-1] if not t.startswith("--")]:
            if src.startswith(SCRIPTS + "/"):
                out.add(src[len(SCRIPTS) + 1:])
    return out


def allowed_scripts(dockerignore_text: str) -> set[str]:
    out: set[str] = set()
    for line in dockerignore_text.splitlines():
        line = line.strip()
        if line.startswith("!" + SCRIPTS + "/"):
            out.add(line[len(SCRIPTS) + 2:])
    return out


def check(root: Path, dockerfile: str) -> list[str]:
    df = root / dockerfile
    ignore = df.with_name(df.name + ".dockerignore")
    if not df.is_file():
        return [f"{dockerfile} does not exist — the guard has nothing to check"]
    if not ignore.is_file():
        return [f"{ignore.relative_to(root)} does not exist — the image's context allow-list cannot be read"]
    copied = copied_scripts(df.read_text(encoding="utf-8"))
    allowed = allowed_scripts(ignore.read_text(encoding="utf-8"))
    scripts_dir = root / SCRIPTS
    problems: list[str] = []
    if not copied:
        return [f"{dockerfile} COPYs no {SCRIPTS}/ file — nothing to check is a broken reading, not a pass"]
    seen: set[str] = set()
    queue = sorted(copied)
    while queue:
        name = queue.pop(0)
        if name in seen:
            continue
        seen.add(name)
        path = scripts_dir / name
        if not path.is_file():
            problems.append(f"{dockerfile} copies {SCRIPTS}/{name}, which does not exist")
            continue
        for ref in sorted(set(_LITERAL.findall(path.read_text(encoding="utf-8", errors="replace")))):
            if ref == name or not (scripts_dir / ref).is_file():
                continue  # itself, or not a sibling here (a caller repository's script)
            if ref not in copied:
                problems.append(f"{name} loads sibling {ref}, but {dockerfile} does not COPY "
                                f"{SCRIPTS}/{ref} — in the image every load is a FileNotFoundError")
            if ref not in allowed:
                problems.append(f"{name} loads sibling {ref}, but {ignore.relative_to(root)} does not "
                                f"allow-list !{SCRIPTS}/{ref} — the COPY would fail the image build")
            queue.append(ref)
    return problems


def self_test() -> int:
    failures: list[str] = []
    ran = 0

    def case(name: str, ok: bool, detail: str = "") -> None:
        nonlocal ran
        ran += 1
        print(f"  {'✓' if ok else '✗'} {name}{'' if ok else ': ' + detail}")
        if not ok:
            failures.append(name)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        s = root / SCRIPTS
        s.mkdir(parents=True)
        (root / "img").mkdir()
        (s / "runner.py").write_text('KEY = "key.py"\nCALLER = "scripts/affected-modules.py"\n', encoding="utf-8")
        (s / "key.py").write_text('spec = with_name("matcher.py")\n', encoding="utf-8")
        (s / "matcher.py").write_text('# leaf\nOTHER = "not-a-sibling.py"\n', encoding="utf-8")

        def image(copy: list[str], allow: list[str]) -> list[str]:
            (root / "img" / "Dockerfile").write_text(
                "FROM x\nCOPY " + " \\\n  ".join(f"{SCRIPTS}/{c}" for c in copy) + " /opt/x/\n", encoding="utf-8")
            (root / "img" / "Dockerfile.dockerignore").write_text(
                "*\n" + "".join(f"!{SCRIPTS}/{a}\n" for a in allow), encoding="utf-8")
            return check(root, "img/Dockerfile")

        full = ["runner.py", "key.py", "matcher.py"]
        got = image(full, full)
        case("every sibling copied and allow-listed ⇒ clean (negative control)", got == [], str(got))
        got = image(["runner.py", "key.py"], full)
        case("a TRANSITIVE sibling (runner → key → matcher) not COPYed ⇒ red, naming it",
             any("matcher.py" in p and "does not COPY" in p for p in got), str(got))
        got = image(full, ["runner.py", "key.py"])
        case("a sibling COPYed but not allow-listed in the .dockerignore ⇒ red",
             any("matcher.py" in p and "allow-list" in p for p in got), str(got))
        got = image(["runner.py"], ["runner.py"])
        case("a direct sibling missing ⇒ red", any("key.py" in p for p in got), str(got))
        got = image(full, full)
        case("a quoted name that is NOT a file in .github/scripts is not demanded",
             not any("affected-modules" in p or "not-a-sibling" in p for p in got), str(got))
        got = image(full + ["gone.py"], full + ["gone.py"])
        case("a COPY of a script that does not exist ⇒ red", any("gone.py" in p for p in got), str(got))
        (root / "img" / "Dockerfile").write_text("FROM x\nCOPY artifacts/x /opt/x\n", encoding="utf-8")
        case("a Dockerfile copying no script is a broken reading, not a pass",
             bool(check(root, "img/Dockerfile")))

    real_root = Path(__file__).resolve().parents[2]
    for dockerfile in DEFAULT_DOCKERFILES:
        if (real_root / dockerfile).is_file():
            got = check(real_root, dockerfile)
            case(f"the real {dockerfile} is clean", got == [], "; ".join(got))

    if failures:
        print(f"\n::error title=check-image-script-siblings self-test failed::{len(failures)} case(s)")
        return 1
    print(f"\n✓ check-image-script-siblings self-test: {ran} cases green — transitive and direct siblings, "
          "COPY and .dockerignore both, non-siblings not demanded, and the real image.")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--root", default=".")
    p.add_argument("--dockerfile", action="append", default=None)
    p.add_argument("--self-test", action="store_true")
    a = p.parse_args()
    if a.self_test:
        return self_test()
    root = Path(a.root).resolve()
    problems: list[str] = []
    files = a.dockerfile or list(DEFAULT_DOCKERFILES)
    for dockerfile in files:
        problems += check(root, dockerfile)
    for prob in problems:
        print(f"::error title=image script sibling missing::{prob}")
    print(f"check-image-script-siblings: {len(files)} image(s) checked, {len(problems)} violation(s), root={root}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Licence gate: fail RED when a NuGet dependency carries a licence that is not
compatible with shipping MeshWeaver under Apache-2.0 / MIT.

MeshWeaver is dual-licensed Apache-2.0 / MIT and is distributed as a
network-served product. That makes two whole licence families unacceptable:

  * copyleft (AGPL / GPL / LGPL) - viral for a network-served product, and
  * pay-to-use (a "community" tier that becomes a paid licence above a revenue
    threshold, or a maintenance fee attached to the published binary).

Both families are easy to add by accident: a single `<PackageVersion>` line is
all it takes, the compiler never complains, and nothing else in CI looks at
licence metadata. This gate is what makes that regression loud.

It resolves the licence for every package in the RESTORED dependency graph -
direct AND transitive - from the .nuspec in the local NuGet cache, because a
copyleft dependency that arrives transitively binds us exactly as hard as one
we declared ourselves.

Usage:
    dotnet restore MeshWeaver.slnx
    python3 .github/scripts/check-licenses.py            # gate: exit 1 on violation
    python3 .github/scripts/check-licenses.py --report   # print the full table

This script NEVER skips. It has no "if the cache is missing, pass" branch: an
unresolvable licence is reported as a violation, because a gate that goes green
on absent evidence is worse than no gate at all (AGENTS.md: "A gate NEVER tests
its own inputs - no skip-trapdoors").
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# ---------------------------------------------------------------------------
# Allowlist: SPDX expressions compatible with shipping under Apache-2.0 / MIT.
# Permissive only. Adding an entry here is a licensing decision - not a way to
# make the build green.
# ---------------------------------------------------------------------------
ALLOWED_LICENSES = {
    "MIT",
    "MIT-0",
    "Apache-2.0",
    "BSD-2-Clause",
    "BSD-3-Clause",
    "BSD",  # licence FILE that is recognisably BSD but names no clause count
    "ISC",
    "0BSD",
    "Unlicense",
    "public-domain",
    "MS-PL",
    "PostgreSQL",
    "MICROSOFT-NET-LIBRARY",  # ms-net-library: permissive redistribution
}

# Compound expressions we accept because at least one disjunct is allowed.
ALLOWED_COMPOUND = {
    "MS-PL OR Apache-2.0",
    "Apache-2.0 OR MIT",
    "MIT OR Apache-2.0",
}

# ---------------------------------------------------------------------------
# Exceptions: packages allowed DESPITE unresolvable or non-SPDX metadata.
# Every entry needs a reason. This list is deliberately short and reviewed.
# ---------------------------------------------------------------------------
EXCEPTIONS = {
    # --- Known-incompatible, removal in flight. These are DEBT, not approvals. ---
    "questpdf": (
        "VIOLATION, added 2026-08-11, removal tracked by #1230 and BLOCKED on it. "
        "Dual-licensed: the Community tier is free only below a revenue threshold, "
        "above which a paid commercial licence is required. It is not deletable today "
        "for a hard reason: the intended replacement is the headless-Chromium renderer, "
        "and Chromium is deliberately NOT in the deployed base image "
        "(deploy/base-images/portal-ai/Dockerfile), so removing QuestPDF now would "
        "leave the portal with NO PDF renderer at all. Sequence: #1222 -> #1234 -> a "
        "base-image decision on shipping Chromium -> this removal. Delete this entry "
        "with it; until then the exception is dated debt, not an approval."
    ),
    # --- Permissive upstream, metadata simply absent from the package. ---
    "uglytoad.pdfpig": (
        "Apache-2.0 upstream (github.com/UglyToad/PdfPig). The 1.7.0-custom-* "
        "build on nuget.org ships no <license> element, so the expression "
        "cannot be resolved from metadata; the source licence is Apache-2.0."
    ),
    "uglytoad.pdfpig.core": ("Apache-2.0 upstream, same custom build as UglyToad.PdfPig."),
    "uglytoad.pdfpig.fonts": ("Apache-2.0 upstream, same custom build as UglyToad.PdfPig."),
    "uglytoad.pdfpig.tokenization": ("Apache-2.0 upstream, same custom build as UglyToad.PdfPig."),
    "uglytoad.pdfpig.tokens": ("Apache-2.0 upstream, same custom build as UglyToad.PdfPig."),
}

# ---------------------------------------------------------------------------
# Licence-body classifiers, for packages that ship a licence FILE rather than an
# SPDX expression. Ordered: the disqualifying patterns are tested first so a
# copyleft or pay-to-use notice can never be mistaken for the permissive grant
# that such files usually also contain.
# ---------------------------------------------------------------------------
DENY_PATTERNS = [
    ("AGPL-3.0", ("gnu affero general public license", "affero general public")),
    ("GPL", ("gnu general public license",)),
    ("LGPL", ("gnu lesser general public license",)),
    (
        "OSMF-maintenance-fee",
        ("open source maintenance fee", "in exchange for a maintenance fee"),
    ),
    (
        "dual-commercial",
        (
            "community license",
            "professional license",
            "enterprise license",
            "commercial license is required",
        ),
    ),
]

ALLOW_PATTERNS = [
    ("MIT", ("permission is hereby granted, free of charge", "mit license")),
    ("Apache-2.0", ("apache license", "www.apache.org/licenses/license-2.0")),
    ("BSD", ("redistribution and use in source and binary forms",)),
    ("MS-PL", ("microsoft public license", "ms-pl")),
    ("MICROSOFT-NET-LIBRARY", ("microsoft .net library",)),
    ("public-domain", ("public domain",)),
]

# licenseUrl hosts that identify a known-permissive first-party licence. Legacy
# packages predate the SPDX <license> element and carry only a URL.
LICENSE_URL_MAP = [
    ("github.com/dotnet/", "MIT"),
    ("github.com/aspnet/", "Apache-2.0"),
    ("apache.org/licenses/license-2.0", "Apache-2.0"),
    ("opensource.org/licenses/mit", "MIT"),
    ("licenses.nuget.org/mit", "MIT"),
]


def nuget_cache() -> str:
    return os.environ.get(
        "NUGET_PACKAGES", os.path.join(os.path.expanduser("~"), ".nuget", "packages")
    )


def classify_body(body: str) -> str:
    """Classify a licence FILE body. Deny patterns win over allow patterns."""
    low = body.lower()
    for name, needles in DENY_PATTERNS:
        if any(n in low for n in needles):
            return name
    for name, needles in ALLOW_PATTERNS:
        if any(n in low for n in needles):
            return name
    return "UNKNOWN"


def resolve_license(pid: str, version: str) -> str:
    """Resolve a package's licence from the .nuspec in the local NuGet cache."""
    pdir = os.path.join(nuget_cache(), pid.lower())
    if not os.path.isdir(pdir):
        return "UNRESOLVED(not-restored)"
    candidates = [version] + sorted(os.listdir(pdir), reverse=True)
    for ver in candidates:
        nuspec = os.path.join(pdir, ver, pid.lower() + ".nuspec")
        if not os.path.isfile(nuspec):
            continue
        try:
            meta = ET.parse(nuspec).getroot().find(".//{*}metadata")
        except ET.ParseError:
            continue
        if meta is None:
            continue
        lic = meta.find("{*}license")
        if lic is not None and lic.text:
            text = lic.text.strip()
            if lic.get("type") == "file":
                path = os.path.join(pdir, ver, text.replace("\\", "/"))
                body = ""
                if os.path.isfile(path):
                    with open(path, encoding="utf-8", errors="ignore") as handle:
                        body = handle.read()
                return classify_body(body)
            return text
        url = meta.find("{*}licenseUrl")
        if url is not None and url.text:
            low = url.text.strip().lower()
            for needle, resolved in LICENSE_URL_MAP:
                if needle in low:
                    return resolved
            return "UNRESOLVED(url-only)"
        return "UNRESOLVED(no-license-element)"
    return "UNRESOLVED(no-nuspec)"


def declared_packages() -> dict[str, tuple[str, str]]:
    """Packages declared in central package management."""
    path = os.path.join(REPO_ROOT, "Directory.Packages.props")
    with open(path, encoding="utf-8") as handle:
        content = handle.read()
    found = re.findall(r'<PackageVersion\s+Include="([^"]+)"\s+Version="([^"]+)"', content)
    return {pid.lower(): (pid, ver) for pid, ver in found}


def restored_packages() -> dict[str, tuple[str, str]]:
    """Every package in the restored graph - direct and transitive."""
    packages: dict[str, tuple[str, str]] = {}
    pattern = os.path.join(REPO_ROOT, "**", "obj", "project.assets.json")
    for assets in glob.glob(pattern, recursive=True):
        # Skip nested agent worktrees (.claude/worktrees/*), which carry their own
        # copy of the tree. Tested RELATIVE to the repo root, because the repo root
        # itself may legitimately sit under a .claude/worktrees path.
        relative = os.path.relpath(assets, REPO_ROOT)
        if relative.startswith(".claude" + os.sep):
            continue
        try:
            with open(assets, encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, json.JSONDecodeError):
            continue
        for target in data.get("targets", {}).values():
            for key, meta in target.items():
                if meta.get("type") != "package":
                    continue
                pid, _, ver = key.partition("/")
                packages[pid.lower()] = (pid, ver)
    return packages


def is_allowed(license_id: str) -> bool:
    if license_id in ALLOWED_LICENSES or license_id in ALLOWED_COMPOUND:
        return True
    # Accept an SPDX OR-expression when any disjunct is allowed.
    if " OR " in license_id:
        return any(part.strip().strip("()") in ALLOWED_LICENSES for part in license_id.split(" OR "))
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", action="store_true", help="print the full licence table")
    args = parser.parse_args()

    declared = declared_packages()
    restored = restored_packages()

    if not restored:
        print(
            "LICENCE GATE FAILED: no project.assets.json found. Run "
            "`dotnet restore MeshWeaver.slnx` before this gate - it resolves licences "
            "from the restored graph and must never pass on absent evidence.",
            file=sys.stderr,
        )
        return 1

    everything: dict[str, tuple[str, str]] = dict(restored)
    for key, value in declared.items():
        everything.setdefault(key, value)

    rows = []
    for key in sorted(everything):
        pid, ver = everything[key]
        license_id = resolve_license(pid, ver)
        if key in declared and key in restored:
            scope = "direct"
        elif key in declared:
            scope = "declared-unused"
        else:
            scope = "transitive"
        rows.append((pid, ver, license_id, scope, key))

    # A violation is a package that actually SHIPS - i.e. one NuGet resolved into a
    # project's dependency graph. A `<PackageVersion>` line that no project
    # references restores nothing, is downloaded by nobody and is redistributed in
    # no image, so it carries no licence obligation; it is dead configuration, and
    # reported as such rather than failed on. (itext7 was exactly this: an AGPL
    # declaration that had never once been resolved.)
    violations = [
        row
        for row in rows
        if row[3] != "declared-unused"
        and not is_allowed(row[2])
        and row[4] not in EXCEPTIONS
    ]
    dead = [row for row in rows if row[3] == "declared-unused"]

    if args.report:
        width = max(len(r[0]) for r in rows) + 2
        for pid, ver, license_id, scope, key in rows:
            if scope == "declared-unused":
                flag = ". "
            elif key in EXCEPTIONS:
                flag = "~ "
            elif not is_allowed(license_id):
                flag = "X "
            else:
                flag = "  "
            print(f"{flag}{pid.ljust(width)}{ver.ljust(22)}{license_id.ljust(30)}{scope}")
        shipping = len(rows) - len(dead)
        print(
            f"\n{len(rows)} packages ({shipping} shipping, {len(dead)} declared-unused), "
            f"{len(violations)} violations"
        )
        print("Legend: X violation   ~ documented exception   . declared but never restored")

    if violations:
        print("\nLICENCE GATE FAILED - dependencies incompatible with Apache-2.0 / MIT:\n", file=sys.stderr)
        for pid, ver, license_id, scope, _ in violations:
            print(f"  {pid} {ver} [{scope}] -> {license_id}", file=sys.stderr)
        print(
            "\nMeshWeaver ships under Apache-2.0 / MIT. A copyleft (AGPL/GPL/LGPL) or "
            "pay-to-use dependency is a licensing defect, not a preference.\n"
            "Remove the package, replace it with a permissively-licensed one, or - if "
            "the metadata is simply missing and the upstream licence IS permissive - add "
            "it to EXCEPTIONS in this script WITH a reason.",
            file=sys.stderr,
        )
        return 1

    # A green run still prints the outstanding exceptions. An exception is tracked
    # debt, not an approval, and a gate that hides its own carve-outs on success
    # lets them become permanent silently.
    shipping = len(rows) - len(dead)
    carried = sorted(key for _, _, _, scope, key in rows if key in EXCEPTIONS and scope != "declared-unused")
    print(f"Licence gate passed: {shipping} shipping packages checked ({len(dead)} declared-unused ignored).")
    if carried:
        print(f"\n{len(carried)} documented exception(s) still carried - each is tracked debt, not an approval:")
        for key in carried:
            print(f"  - {key}: {EXCEPTIONS[key]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

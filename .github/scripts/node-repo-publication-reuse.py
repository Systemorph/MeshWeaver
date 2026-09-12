#!/usr/bin/env python3
"""Reuse unchanged gate dependencies from an attested successful publication.

The source selector proves an entry unaffected (test=false). The baseline resolver proves the
toolchain inputs equal. This helper verifies run identity and artifact availability; absence
keeps the normal build leg. It uses the module lane's existing reuse/receipt path, not a builder.
"""
import argparse
import json
import os
import re
import subprocess
import sys


def annotate(entries, run, sha, artifacts):
    if (run.get("status") != "completed" or run.get("conclusion") != "success"
            or run.get("event") != "push" or run.get("head_branch") != "main"
            or run.get("head_sha") != sha or not re.fullmatch(r"[0-9a-f]{40}", sha)):
        return entries
    live = {a["name"] for a in artifacts if not a.get("expired")}
    result = []
    for entry in entries:
        name = f"module-bundle-{entry['module']}"
        if entry.get("test") is False and not entry.get("ledger") and name in live:
            entry = {**entry, "reuse": {"runId": str(run["id"]), "name": name,
                                        "source": run["html_url"]}}
        result.append(entry)
    return result


def self_test():
    entries = [{"module": "Floor", "test": False}, {"module": "Changed", "test": True}]
    run = dict(status="completed", conclusion="success", event="push", head_branch="main",
               head_sha="a" * 40, id=10, html_url="https://example.test/runs/10")
    artifacts = [{"name": "module-bundle-Floor", "expired": False}]
    got = annotate(entries, run, "a" * 40, artifacts)
    assert got[0]["reuse"]["runId"] == "10" and "reuse" not in got[1]
    for change in [dict(conclusion="failure"), dict(event="pull_request"), dict(status="in_progress"),
                   dict(head_branch="other"), dict(head_sha="b" * 40)]:
        assert annotate(entries, {**run, **change}, "a" * 40, artifacts) == entries
    assert annotate(entries, run, "a" * 40, []) == entries
    assert annotate(entries, run, "a" * 40, [{**artifacts[0], "expired": True}]) == entries
    assert annotate([{**entries[0], "ledger": {"decision": "build"}}], run, "a" * 40, artifacts)[0].get("reuse") is None
    print("publication reuse: 9 assertions passed")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--matrix")
    p.add_argument("--run", default="")
    p.add_argument("--sha", default="")
    p.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    p.add_argument("--self-test", action="store_true")
    a = p.parse_args()
    if a.self_test:
        self_test()
        return 0
    entries = json.loads(a.matrix)
    if a.run and a.sha and any(e.get("test") is False for e in entries):
        try:
            if not a.run.isdigit() or not re.fullmatch(r"[\w.-]+/[\w.-]+", a.repo):
                raise ValueError("invalid run or repository")
            def api(path, *args):
                return json.loads(subprocess.run(["gh", "api", path, *args], capture_output=True,
                                                 text=True, timeout=60, check=True).stdout)
            run = api(f"repos/{a.repo}/actions/runs/{a.run}")
            pages = api(f"repos/{a.repo}/actions/runs/{a.run}/artifacts?per_page=100", "--paginate", "--slurp")
            entries = annotate(entries, run, a.sha, [art for page in pages for art in page["artifacts"]])
        except (OSError, ValueError, KeyError, subprocess.SubprocessError):
            print("::warning::publication artifacts unavailable; retaining normal builds", file=sys.stderr)
    build = [e for e in entries if not e.get("reuse") and e.get("ledger", {}).get("decision") != "reuse"]
    print(f"publication reuse: {sum(bool(e.get('reuse')) for e in entries)} unchanged gate dependencies", file=sys.stderr)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"modules={json.dumps(entries)}\nbuild={json.dumps(build)}\n")
    print(json.dumps(entries))
    return 0


if __name__ == "__main__":
    sys.exit(main())

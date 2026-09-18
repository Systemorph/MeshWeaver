#!/usr/bin/env python3
"""
The DESIRED half of check-chart-drift.sh — render the chart from the sources a drift check may
hold, and PROVE the one input it may not hold cannot reach the comparison.

🚨 WHY THIS EXISTS. `Chart Drift` ran 39 times between 2026-08-15 and 2026-09-17 and failed 39
times, never once producing a verdict (MeshWeaver#4640). The last 12 days of that were a single
cause, and it is structural rather than a bug in anybody's code:

  * A record-driven deploy renders the chart from THREE value sources — the chart's own
    `values.yaml`, the committed secret-free overlay, and the Key Vault values half that carries
    the connection strings (`helm-values-<release>`, layered by `hosting-deploy --vault`).
  * A drift check in a PUBLIC repository may hold exactly TWO of them. The third is secrets; the
    whole reason the overlays were moved to the private Systemorph/Memex repo is that publishing
    them would leak credentials.
  * The chart REFUSES to render from that subset, and the refusal is correct — it is #3780's
    guard, which exists because a `helm upgrade` fed the record's render WITHOUT the vault half
    manufactured `ConnectionStrings__orleans: Host=memex-postgres-service;…` from the chart's
    in-cluster default and killed every new pod on the control instance at silo start, twice.

So the check asked the chart a question the chart is right to refuse. Nothing here weakens that
refusal: `check-chart-invariants.sh` still asserts, on every pull request, that the #3780 render
fails. What this script does instead is supply the missing input as an OBVIOUSLY FAKE placeholder
and then demonstrate — by measurement, every run — that the objects the comparison reads do not
depend on it.

THE INDEPENDENCE PROOF, and why a grep would not do. The check compares four objects (see
`COMPARED_OBJECTS`). A placeholder is safe only if none of them is a function of it. Searching the
render for the placeholder string would answer a weaker question: a value can reach an object
DERIVED — hashed, base64'd, truncated — and a search for the literal would come back clean while
the object still moved. So this renders the chart TWICE with two DIFFERENT placeholders and
requires the compared objects to be IDENTICAL. Anything that is a function of the placeholder,
by any derivation, differs between the two renders and is reported by JSON path.

Measured 2026-09-18 against both production overlays, on helm v3.16.3 (the CI binary) and v4.2.4:
exactly ONE path differs, and it is excluded BY NAME below. Every other byte of all four objects
is identical.

🚨 THE EXCLUSION IS DEFAULT-DENY AND IT IS THE POINT. The proof starts from "every byte of the four
objects must match" and subtracts only what is named in `EXPECTED_PLACEHOLDER_DEPENDENCIES`, each
with the reason it cannot carry a verdict. A chart change that makes any other field a function of
a secret reddens this check on the next run, naming the field — instead of silently widening what
the placeholder touches. Do not add an entry to that list to make a red go away: a new dependency
means the compared set now reads a secret, and that is a finding about the CHECK, not noise.

WHAT IS INJECTED, AND WHY THAT AND NOTHING ELSE. Only `secrets.<half>.ConnectionStrings__orleans`,
and only when the chart's own precondition for the #3780 refusal holds. Its HOST is not invented:
it is read from `config.<half>.MEMEX_HOST` / `MEMEX_PORT`, the same committed, secret-free input
the chart itself uses to decide what `wait-for-postgres` waits for when the connection string comes
from outside the values (`memex.meshProbeGroup`, templates/_database.tpl). Only the credential
fields are placeholders. That keeps the render SHAPE-identical to the deploy's: with the real host,
`memex.dbProbeTargets` deduplicates the orleans endpoint against the mesh one and renders the same
single probe target the deploy does. A placeholder host would render two.

🚨 AND IT IS A REFUSAL, NEVER AN INVENTION, when the host is not there either. If
`config.<half>.MEMEX_HOST` is absent, blank, or still the chart's in-cluster default, this script
FAILS naming that input — the same answer the chart gives, for the same reason. A drift check that
manufactured a host would be the #3780 defect wearing a checker's colours.

WHAT THIS SCRIPT DOES NOT PROVE. That the WITHHELD Key Vault half does not itself influence the
compared objects. That is a property of how the fleet splits its values, not of this render, and it
is tracked as Systemorph/Memex#295 / #352 — see
src/MeshWeaver.Documentation/Data/Architecture/ChartDriftRenderWithoutSecrets.md, which records
what was measured about it and how to re-measure. This script is deliberately silent about it
rather than implying a coverage it does not have.

  usage: chart-drift-render.py --chart DIR --namespace NS --release NAME --out FILE -f VALUES...
  exit 0 = FILE holds the render, and the compared objects are independent of the placeholder
  exit 1 = the chart could not be rendered, or the placeholder reaches the comparison
"""
import argparse, copy, json, os, subprocess, sys, tempfile

import yaml

# The objects check-chart-drift.sh / chart-drift-compare.py read from the rendered chart. Kept as
# (kind, name) with None meaning "whichever one of this kind the chart renders", exactly as the
# comparator picks the PodDisruptionBudget and the ScaledObject.
COMPARED_OBJECTS = [
    ("ConfigMap", "memex-portal-config"),
    ("Deployment", "memex-portal-deployment"),
    ("PodDisruptionBudget", None),
    ("ScaledObject", None),
]

# Paths inside a compared object that ARE a function of the placeholder and cannot be otherwise.
# Each entry is (kind, json-path-tuple, why). See the default-deny note in the docstring: this list
# shrinks the proof, so every entry is a standing claim that the field carries no verdict.
EXPECTED_PLACEHOLDER_DEPENDENCIES = [
    (
        "Deployment",
        ("spec", "template", "metadata", "annotations", "checksum/secrets"),
        "a sha256 of the rendered Secret, whose only purpose is to roll pods when a secret CHANGES. "
        "It is a digest of values this check does not hold and deliberately never compares, so it "
        "can carry no drift verdict; chart-drift-compare.py reads no pod-template annotation.",
    ),
]

# Two placeholders, differing in every character position that is not structural. They are written
# to say what they are in any output that ever prints one, and they are not credential-shaped.
PLACEHOLDERS = ("RENDER-ONLY-PLACEHOLDER-NOT-A-CREDENTIAL-A",
                "RENDER-ONLY-PLACEHOLDER-NOT-A-CREDENTIAL-B")

# The chart's in-cluster Postgres Service. A release with `postgres.enabled: false` does not render
# it, so naming it is the #3780 defect — the chart refuses it and so does this script.
IN_CLUSTER_DB_SERVICE = "memex-postgres-service"

HALVES = ("memex_portal", "memex_migration")


def err(*lines):
    print(f"::error::{lines[0]}")
    for line in lines[1:]:
        print(f"    {line}")


def deep_merge(base, over):
    """helm's `-f` merge for the narrow reads below: later files win, maps merge, lists replace."""
    if isinstance(base, dict) and isinstance(over, dict):
        out = dict(base)
        for k, v in over.items():
            out[k] = deep_merge(base[k], v) if k in base else v
        return out
    return over


def merged_values(chart, values_files):
    """The chart's own values.yaml plus every -f in order — what helm computes for `.Values`."""
    merged = {}
    for path in [os.path.join(chart, "values.yaml")] + list(values_files):
        if not os.path.exists(path):
            continue
        with open(path) as fh:
            loaded = yaml.safe_load(fh)
        if isinstance(loaded, dict):
            merged = deep_merge(merged, loaded)
    return merged


def dig(values, *path):
    cur = values
    for part in path:
        if not isinstance(cur, dict):
            return None
        cur = cur.get(part)
    return cur


def needs_placeholder(values, half):
    """Exactly the chart's own precondition for the #3780 refusal — never a broader one.

    `memex.orleansConnectionString` fails only when ALL of these hold, so injecting under any
    weaker condition would put a placeholder into a render that did not need one.
    """
    if dig(values, "postgres", "enabled"):
        return False
    # `memex.adoNetClustering` reads the PORTAL's config for BOTH halves — the migration provisions
    # what the silo will use, so it must not ask its own half. Mirrored here deliberately.
    if (dig(values, "config", "memex_portal", "Deployment__Orleans__Clustering") or "Localhost") != "AdoNet":
        return False
    secrets = dig(values, "secrets", half) or {}
    return not secrets.get("ConnectionStrings__orleans") and not secrets.get("ConnectionStrings__memex")


def database_endpoint(values, half):
    """`config.<half>.MEMEX_HOST:MEMEX_PORT`, or a refusal naming the input — never an invention."""
    host = str(dig(values, "config", half, "MEMEX_HOST") or "").strip()
    port = str(dig(values, "config", half, "MEMEX_PORT") or "5432").strip()
    if not host or host == IN_CLUSTER_DB_SERVICE:
        err(
            f"cannot render the chart for a drift comparison: '{half}' runs AdoNet clustering on an "
            f"EXTERNAL database (postgres.enabled is false) and the values carry no connection "
            f"string, so the chart refuses to render (MeshWeaver#3780) — and the one secret-free "
            f"input that would let this check supply a render-only placeholder, "
            f"config.{half}.MEMEX_HOST, is "
            + ("blank or unset." if not host else f"still the in-cluster default {IN_CLUSTER_DB_SERVICE}."),
            "This check does NOT invent a database host; inventing one is the very defect #3780's",
            "refusal exists to stop. The record renders MEMEX_HOST from databaseServer/databaseHost,",
            f"so a missing one means the committed overlay for this release is stale or partial:",
            f"re-render it from the Hosting/Deployment record and commit it, then re-run.",
        )
        return None
    return host, port


def build_placeholder_values(values, placeholder):
    """The render-only third `-f`: connection strings for the halves that need one, nothing else."""
    secrets = {}
    for half in HALVES:
        if not needs_placeholder(values, half):
            continue
        endpoint = database_endpoint(values, half)
        if endpoint is None:
            return None
        host, port = endpoint
        secrets[half] = {
            "ConnectionStrings__orleans":
                f"Host={host};Port={port};Database={placeholder};"
                f"Username={placeholder};Password={placeholder}"
        }
    return {"secrets": secrets} if secrets else {}


def render(chart, namespace, release, values_files, extra, out_path):
    args = ["helm", "template", release, chart, "--namespace", namespace]
    for path in values_files:
        args += ["-f", path]
    if extra:
        args += ["-f", extra]
    proc = subprocess.run(args, capture_output=True, text=True)
    if proc.returncode != 0:
        err("helm template FAILED — cannot determine what the chart describes:")
        for line in (proc.stderr or "").rstrip().splitlines():
            print(f"    {line}")
        return False
    with open(out_path, "w") as fh:
        fh.write(proc.stdout)
    return True


def compared_objects(path):
    """The rendered objects the comparison reads, keyed `Kind/name`."""
    found = {}
    with open(path) as fh:
        for doc in yaml.safe_load_all(fh):
            if not doc:
                continue
            kind = doc.get("kind")
            name = (doc.get("metadata") or {}).get("name")
            for want_kind, want_name in COMPARED_OBJECTS:
                if kind == want_kind and want_name in (None, name):
                    found[f"{kind}/{name}"] = doc
    return found


def strip_expected(kind, obj):
    """Remove the named, justified placeholder dependencies — and nothing else."""
    obj = copy.deepcopy(obj)
    for dep_kind, path, _why in EXPECTED_PLACEHOLDER_DEPENDENCIES:
        if dep_kind != kind:
            continue
        cur = obj
        for part in path[:-1]:
            if not isinstance(cur, dict):
                cur = None
                break
            cur = cur.get(part)
        if isinstance(cur, dict):
            cur.pop(path[-1], None)
    return obj


def differing_paths(left, right, prefix=""):
    if type(left) is not type(right):
        yield prefix, left, right
        return
    if isinstance(left, dict):
        for key in sorted(set(left) | set(right)):
            if key not in left or key not in right:
                yield f"{prefix}.{key}", left.get(key, "<absent>"), right.get(key, "<absent>")
            else:
                yield from differing_paths(left[key], right[key], f"{prefix}.{key}")
    elif isinstance(left, list):
        if len(left) != len(right):
            yield prefix, f"<{len(left)} items>", f"<{len(right)} items>"
        else:
            for i, (a, b) in enumerate(zip(left, right)):
                yield from differing_paths(a, b, f"{prefix}[{i}]")
    elif left != right:
        yield prefix, left, right


def prove_independent(first, second):
    """Every compared object identical across the two placeholder renders, minus the named set."""
    a, b = compared_objects(first), compared_objects(second)
    if not a:
        err("the rendered chart contains none of the objects this check compares "
            f"({', '.join(k for k, _ in COMPARED_OBJECTS)}).",
            "Nothing could be proved independent of the placeholder, and nothing could be compared",
            "against the cluster either. Treating as FAILURE: 'rendered nothing' must never read as",
            "'found no drift'.")
        return False
    if set(a) != set(b):
        err("the set of compared objects CHANGED between two renders that differ only in a "
            "render-only placeholder.",
            f"first render:  {', '.join(sorted(a)) or '(none)'}",
            f"second render: {', '.join(sorted(b)) or '(none)'}",
            "An object whose very existence depends on a secret cannot be compared against the",
            "cluster from a secret-free render. Fix the chart or narrow the placeholder.")
        return False

    offenders = []
    for name in sorted(a):
        kind = name.split("/", 1)[0]
        for path, left, right in differing_paths(strip_expected(kind, a[name]),
                                                 strip_expected(kind, b[name]), name):
            offenders.append((path, left, right))
    if offenders:
        err("a compared object DEPENDS ON the render-only placeholder — this check would report "
            "the placeholder as if it were the chart's intent.",
            "Two renders differing only in that placeholder disagree at:")
        for path, left, right in offenders[:20]:
            print(f"      {path}")
            print(f"        render A: {json.dumps(left)[:160]}")
            print(f"        render B: {json.dumps(right)[:160]}")
        if len(offenders) > 20:
            print(f"      … and {len(offenders) - 20} more")
        print("    Do NOT silence this by adding the path to EXPECTED_PLACEHOLDER_DEPENDENCIES: a")
        print("    compared field that is a function of a secret means this check can no longer")
        print("    render a comparable chart without that secret, which is a finding about the")
        print("    check. See Doc/Architecture/ChartDriftRenderWithoutSecrets.")
        return False

    excluded = ", ".join(".".join(p) for _k, p, _w in EXPECTED_PLACEHOLDER_DEPENDENCIES)
    print(f"Placeholder independence PROVED: {len(a)} compared object(s) "
          f"({', '.join(sorted(a))}) are byte-identical across two renders whose only difference is "
          f"the placeholder, excluding {excluded}.")
    return True


def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--chart", required=True)
    parser.add_argument("--namespace", required=True)
    parser.add_argument("--release", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("-f", "--values", action="append", default=[])
    args = parser.parse_args()

    values = merged_values(args.chart, args.values)
    stub = build_placeholder_values(values, PLACEHOLDERS[0])
    if stub is None:
        return 1

    if not stub:
        # Nothing was injected, so there is nothing to bound. Said out loud, because "no placeholder
        # was needed" and "the placeholder was proved inert" are different amounts of evidence and a
        # reader must be able to tell which one this run produced. This branch cannot hide a
        # failure: the reason a placeholder is ever needed is that helm otherwise REFUSES to render,
        # so a run that wrongly took this path fails at `helm template` rather than passing.
        print("No render-only placeholder was needed: the supplied values already carry every "
              "connection string the chart requires, so this render uses committed values only.")
        return 0 if render(args.chart, args.namespace, args.release, args.values, None, args.out) else 1

    injected = ", ".join(f"secrets.{half}.ConnectionStrings__orleans" for half in sorted(stub["secrets"]))
    print(f"Injecting a render-only placeholder for {injected} — the chart refuses to render "
          f"without it (MeshWeaver#3780) and this check may not hold the real value. Its host comes "
          f"from config.<half>.MEMEX_HOST, not from an invention.")

    with tempfile.TemporaryDirectory() as work:
        renders = []
        for i, placeholder in enumerate(PLACEHOLDERS):
            stub_path = os.path.join(work, f"placeholder-{i}.yaml")
            with open(stub_path, "w") as fh:
                yaml.safe_dump(build_placeholder_values(values, placeholder), fh)
            out = args.out if i == 0 else os.path.join(work, "desired-b.yaml")
            if not render(args.chart, args.namespace, args.release, args.values, stub_path, out):
                return 1
            renders.append(out)
        if not prove_independent(*renders):
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

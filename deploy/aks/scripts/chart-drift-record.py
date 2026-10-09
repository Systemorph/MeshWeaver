#!/usr/bin/env python3
"""
The RECORD half of Chart Drift (MeshWeaver#4685): find the ONE committed Deployment record that owns
<namespace>/<release> and write the facts of it the check reads.

    chart-drift-record.py --records DIR --namespace NS --release NAME --out FILE

DIR is the config repository's `mesh/Deployments/` folder — the same records the overlays under
`deployments/aks/<ns>/` are generated from. A record owns the pair when its content names
`namespace: NS` and `helmRelease: NAME`.

Written, and nothing else of the record:
  * {"record", "imageRepository", "updatePolicy", "updatePattern", "pinnedImageTag"} — the image the
    portal may run (policy `chart-drift-compares-image-to-record`; chart-drift-compare.py);
  * "vaultValuesKeys" — the NAMES of the keys the Key Vault values half feeds the chart's own Secret
    (policy `one-values-half-per-release`; chart-drift-render.py renders a placeholder half of exactly
    that shape). Names only — the record holds no value of them, and neither does this file.

🚨 Exactly ONE record must own the pair. NONE means the namespace runs a release no record declares,
and TWO means the records disagree about who owns it; either way the comparison cannot be decided,
and an undecidable comparison is a FAILURE (exit 1, naming the records), never a skip.
"""
import argparse, json, os, sys

IMAGE_FIELDS = ("imageRepository", "updatePolicy", "updatePattern", "pinnedImageTag")


def content_of(doc):
    c = doc.get("content") if isinstance(doc, dict) else None
    return c if isinstance(c, dict) else {}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--records", required=True)
    ap.add_argument("--namespace", required=True)
    ap.add_argument("--release", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    if not os.path.isdir(a.records):
        print(f"::error::the Deployment records folder '{a.records}' does not exist — the record "
              f"cannot be read, so the running image cannot be tested against it.")
        return 1

    owners, unreadable = [], []
    for name in sorted(os.listdir(a.records)):
        if not name.endswith(".json") or name == "index.json":
            continue
        path = os.path.join(a.records, name)
        try:
            doc = json.load(open(path))
        except Exception as e:  # noqa: BLE001 — named below, never swallowed
            unreadable.append(f"{name} ({e})")
            continue
        c = content_of(doc)
        if (c.get("namespace") or "").strip() == a.namespace and \
           (c.get("helmRelease") or "").strip() == a.release:
            owners.append((f"Deployments/{name[:-5]}", c))

    if unreadable:
        # An unreadable record could be the owner; deciding without it would be deciding on a subset.
        print("::error::these Deployment records could not be parsed, so which record owns "
              f"{a.namespace}/{a.release} cannot be decided: " + "; ".join(unreadable))
        return 1
    if len(owners) != 1:
        which = ", ".join(o[0] for o in owners) or "none"
        print(f"::error::{len(owners)} Deployment record(s) own namespace '{a.namespace}' release "
              f"'{a.release}' ({which}) — exactly one must, or the cluster cannot be tested against "
              f"a record. Fix the records, not this check.")
        return 1

    record, c = owners[0]
    facts = {"record": record}
    for f in IMAGE_FIELDS:
        v = c.get(f)
        facts[f] = v.strip() if isinstance(v, str) and v.strip() else None
    keys = c.get("vaultValuesKeys") or []
    facts["vaultValuesKeys"] = sorted({str(k).strip() for k in keys if str(k).strip()}) \
        if isinstance(keys, list) else []
    with open(a.out, "w") as out:
        json.dump(facts, out, indent=2)
        out.write("\n")
    print(f"record {record}: image repository {facts['imageRepository'] or '—'}, "
          f"policy {facts['updatePolicy'] or '—'}, pattern {facts['updatePattern'] or '—'}, "
          f"pin {facts['pinnedImageTag'] or '—'}; {len(facts['vaultValuesKeys'])} declared "
          f"values-half key(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

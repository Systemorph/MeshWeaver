#!/usr/bin/env python3
"""
The helm VALUES HALF (`helm-values-<release>` in Key Vault) holds EXACTLY the keys its Deployment
record declares in `vaultValuesKeys` — policy `one-values-half-per-release` (MeshWeaver#4685).

    _values_half.py check  <half-file> --keys K1,K2,...          names only; exit 0 exact, 3 mismatch
    _values_half.py filter <half-file> <out.json> --keys K1,...  value-preserving rewrite; exit 0 / 3

WHY. The half was a CAPTURE of whatever the live release carried (`helm get values`, filtered to the
secret families), so it could feed keys nobody declared — a `config` leaf riding in the vault renders
as a `CLUSTER-ONLY` finding that is wrongly CAUSED (Systemorph/Memex#295), and a secret nobody
declared is a secret nobody reviews. The record already says which keys the chart's own Secret is fed
from the vault (`vaultValuesKeys`, the same list HelmValues attributes to `memex-portal-secrets`); the
half is now held to that list in BOTH directions: no undeclared key, no declared key missing.

THE KEY NAMES. A declared key is a SECRET ENV KEY (`ConnectionStrings__memex`, `MEMEX_PASSWORD`,
`Anthropic__ApiKey`). The half stores it under `secrets.memex_portal.<leaf>` (and the migration's
copy under `secrets.memex_migration.<leaf>`), where the leaf is the env key itself except for ONE
chart alias: `memex_postgres_password` renders as `MEMEX_PASSWORD`. `ConnectionStrings__orleans` may
be absent from the half when `ConnectionStrings__memex` is present — the chart derives it by its ONE
rule (`memex.orleansConnectionString`), and hosting-kv-ensure deliberately never writes a second copy.
Anything else in the half — `parameters`, `pgbackrest`, `secrets.memex_postgres`, any other
`secrets.<x>` — names no declared key and is UNDECLARED.

🔒 NO VALUE IS EVER PRINTED. `check` prints key names; `filter` writes values only to the output file
(the caller creates it 0600) and prints names. A value never reaches argv, stdout or stderr.
"""
import json, sys

try:
    import yaml
except ImportError:  # the operator image installs PyYAML; a missing module is a named refusal
    print("::error::_values_half.py needs PyYAML (python3 'yaml' module) — the operator image "
          "installs it (Dockerfile: tdnf PyYAML). Refusing rather than guessing the half's keys.",
          file=sys.stderr)
    sys.exit(2)

HALVES = ("memex_portal", "memex_migration")
ALIAS = {"memex_postgres_password": "MEMEX_PASSWORD"}
LEAF_FOR = {v: k for k, v in ALIAS.items()}
DERIVED = {"ConnectionStrings__orleans": "ConnectionStrings__memex"}


def env_name(leaf):
    return ALIAS.get(leaf, leaf)


def load(path):
    try:
        doc = yaml.safe_load(open(path))   # a JSON object is valid YAML
    except Exception as e:  # noqa: BLE001 — named, fatal
        print(f"::error::the values half cannot be parsed ({type(e).__name__}) — refusing.", file=sys.stderr)
        sys.exit(2)
    if not isinstance(doc, dict):
        print("::error::the values half is not a mapping — refusing.", file=sys.stderr)
        sys.exit(2)
    return doc


def inventory(doc):
    """(fed: {half: {envName}}, undeclared: [path names]) — names only."""
    fed = {h: set() for h in HALVES}
    other = []
    for family, body in doc.items():
        if family != "secrets":
            other.append(str(family))
            continue
        if not isinstance(body, dict):
            other.append("secrets")
            continue
        for half, leaves in body.items():
            if half not in HALVES:
                other.append(f"secrets.{half}")
                continue
            for leaf in (leaves or {}):
                fed[half].add(env_name(str(leaf)))
    return fed, other


def compare(doc, declared):
    fed, other = inventory(doc)
    supplied = set()
    for h in HALVES:
        supplied |= fed[h]
        for derived, source in DERIVED.items():
            if source in fed[h]:
                supplied.add(derived)
    undeclared = sorted({k for h in HALVES for k in fed[h]} - declared) + sorted(other)
    missing = sorted(declared - supplied)
    return undeclared, missing


def parse_keys(argv):
    if "--keys" not in argv:
        print("::error::--keys K1,K2,... (the record's vaultValuesKeys) is required", file=sys.stderr)
        sys.exit(2)
    raw = argv[argv.index("--keys") + 1] if argv.index("--keys") + 1 < len(argv) else ""
    keys = {k.strip() for k in raw.split(",") if k.strip()}
    if not keys:
        print("::error::--keys is empty — a record with no vaultValuesKeys has no values half", file=sys.stderr)
        sys.exit(2)
    return keys


def report(undeclared, missing):
    if undeclared:
        print("undeclared: " + " ".join(undeclared))
    if missing:
        print("missing: " + " ".join(missing))


def main(argv):
    if len(argv) < 2 or argv[1] not in ("check", "filter"):
        print(__doc__.strip().splitlines()[0], file=sys.stderr)
        return 2
    declared = parse_keys(argv)
    doc = load(argv[2])
    undeclared, missing = compare(doc, declared)
    if argv[1] == "check":
        report(undeclared, missing)
        return 0 if not undeclared and not missing else 3
    # filter: keep exactly the declared keys, values untouched; a declared key the half cannot
    # supply cannot be invented, so that is a refusal and nothing is written.
    if missing:
        report([], missing)
        return 3
    out_path = argv[3]
    secrets = doc.get("secrets") or {}
    kept = {}
    for half in HALVES:
        leaves = secrets.get(half) or {}
        if not isinstance(leaves, dict):
            continue
        keep = {leaf: value for leaf, value in leaves.items() if env_name(str(leaf)) in declared}
        if keep:
            kept[half] = keep
    with open(out_path, "w") as out:
        json.dump({"secrets": kept}, out)
    if undeclared:
        print("dropped: " + " ".join(undeclared))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

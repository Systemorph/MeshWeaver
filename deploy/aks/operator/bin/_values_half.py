#!/usr/bin/env python3
"""
The helm VALUES HALF (`helm-values-<release>` in Key Vault) holds EXACTLY the keys its Deployment
record declares in `vaultValuesKeys` — policy `one-values-half-per-release` (MeshWeaver#4685).

    _values_half.py check  <half-file> --keys K1,K2,...          names only; exit 0 exact, 3 mismatch
    _values_half.py filter <half-file> <out.json> --keys K1,...  value-preserving rewrite; exit 0 / 3
    _values_half.py set    <half-file> <values.json> <out.json> --keys K1,... --set K,...
                                                               change NAMED keys, keep everything else; exit 0 / 3
    _values_half.py verify <half-file> <values.json> --set K,... the named keys hold exactly those values; exit 0 / 3

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

SET (the governed key-level writer, `hosting-kv-values-set`). Before it, only a Provision could
write the half, and only when it was ABSENT — so rotating a value that also lives in the half (the
control instance's `control-db-connection` through the record page's secret dialog) left a stale copy
in it that the next Reconcile layered straight back onto the pods. `set` changes exactly the named
keys and nothing else:
  • every named key must be one the record declares (`--keys`), or nothing is written (exit 3);
  • every other byte of meaning in the half is KEPT — other leaves, other halves, other families
    (`parameters`, `pgbackrest`), multi-line values (a PEM block scalar) — the document is re-emitted
    as JSON, which helm reads as YAML, so a block scalar's value survives byte-for-byte;
  • a key is written into every half that already carries its leaf; one no half carries yet goes into
    `memex_portal`, and also into `memex_migration` when the migration reads it too
    (`ConnectionStrings__memex`, `MEMEX_PASSWORD` — the two hosting-kv-ensure composes into both);
  • `ConnectionStrings__orleans` is written only where a half already carries it: otherwise the chart
    DERIVES it from `ConnectionStrings__memex` by its one rule, and writing a first copy would freeze
    that derivation — rotate `ConnectionStrings__memex` instead (refused, named);
  • an empty value is refused: a blank connection string fails at silo start with nothing naming this.
`verify` reads a half back and compares the named leaves with the values file — by equality, printing
names only.

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


# The keys the MIGRATION Job reads from its own half too — hosting-kv-ensure composes exactly these two
# into both `memex_portal` and `memex_migration`, so a first write of either lands in both.
MIGRATION_KEYS = ("ConnectionStrings__memex", "MEMEX_PASSWORD")


def parse_set(argv):
    if "--set" not in argv or argv.index("--set") + 1 >= len(argv):
        print("::error::--set K,... (the keys to change) is required", file=sys.stderr)
        sys.exit(2)
    raw = argv[argv.index("--set") + 1]
    keys = [k.strip() for k in raw.split(",") if k.strip()]
    if not keys:
        print("::error::--set names no key — a write that changes nothing is a bug in the caller", file=sys.stderr)
        sys.exit(2)
    return list(dict.fromkeys(keys))


def load_values(path, keys):
    """{envKey: value} from the 0600 values file; every named key a non-empty string, or exit 3."""
    try:
        values = json.load(open(path))
    except Exception as e:  # noqa: BLE001 — named, fatal
        print(f"::error::the values file cannot be parsed ({type(e).__name__}) — refusing.", file=sys.stderr)
        sys.exit(2)
    if not isinstance(values, dict):
        print("::error::the values file is not an object of {key: value} — refusing.", file=sys.stderr)
        sys.exit(2)
    empty = [k for k in keys if not isinstance(values.get(k), str) or values.get(k) == ""]
    if empty:
        print("novalue: " + " ".join(empty))
        sys.exit(3)
    return values


def targets_for(doc, key):
    """The (half, leaf) pairs a key is written to, or None when it may not be written (derived)."""
    leaf = LEAF_FOR.get(key, key)
    secrets = doc.get("secrets") if isinstance(doc.get("secrets"), dict) else {}
    carrying = [h for h in HALVES if isinstance(secrets.get(h), dict) and leaf in secrets[h]]
    if carrying:
        return [(h, leaf) for h in carrying]
    if key in DERIVED:
        return None
    halves = ["memex_portal"] + (["memex_migration"] if key in MIGRATION_KEYS else [])
    return [(h, leaf) for h in halves]


def set_keys(doc, values, keys):
    """Mutates doc: each key's leaf set in its target halves. Returns [(key, [paths])]."""
    if "secrets" not in doc or doc["secrets"] is None:
        doc["secrets"] = {}
    if not isinstance(doc["secrets"], dict):
        print("::error::the half's `secrets` is not a mapping — refusing to write into it.", file=sys.stderr)
        sys.exit(2)
    written = []
    for key in keys:
        targets = targets_for(doc, key)
        for half, leaf in targets:
            body = doc["secrets"].get(half)
            if body is None:
                body = doc["secrets"][half] = {}
            if not isinstance(body, dict):
                print(f"::error::secrets.{half} is not a mapping — refusing to write into it.", file=sys.stderr)
                sys.exit(2)
            body[leaf] = values[key]
        written.append((key, [f"secrets.{h}.{l}" for h, l in targets]))
    return written


def leaf_values(doc, key):
    leaf = LEAF_FOR.get(key, key)
    secrets = doc.get("secrets") if isinstance(doc.get("secrets"), dict) else {}
    return [secrets[h][leaf] for h in HALVES if isinstance(secrets.get(h), dict) and leaf in secrets[h]]


def main(argv):
    if len(argv) < 2 or argv[1] not in ("check", "filter", "set", "verify"):
        print(__doc__.strip().splitlines()[0], file=sys.stderr)
        return 2
    if argv[1] == "verify":
        keys = parse_set(argv)
        doc = load(argv[2])
        values = load_values(argv[3], keys)
        wrong = [k for k in keys if not leaf_values(doc, k) or any(v != values[k] for v in leaf_values(doc, k))]
        if wrong:
            print("differs: " + " ".join(wrong))
            return 3
        return 0
    declared = parse_keys(argv)
    if argv[1] == "set":
        keys = parse_set(argv)
        undeclared = [k for k in keys if k not in declared]
        if undeclared:
            print("undeclared: " + " ".join(undeclared))
            return 3
        doc = load(argv[2])
        derived = [k for k in keys if targets_for(doc, k) is None]
        if derived:
            print("derived: " + " ".join(f"{k}<-{DERIVED[k]}" for k in derived))
            return 3
        values = load_values(argv[3], keys)
        written = set_keys(doc, values, keys)
        try:
            text = json.dumps(doc)
        except (TypeError, ValueError) as e:
            # A scalar YAML typed as something JSON has no word for (a bare date): re-emitting it as a
            # string would change what helm reads for a key this write never named — refused instead.
            print(f"::error::the half holds a value JSON cannot carry unchanged ({type(e).__name__}) — "
                  "refusing to re-emit it. Nothing was written.", file=sys.stderr)
            return 2
        with open(argv[4], "w") as out:
            out.write(text)
        for key, paths in written:
            print(f"set: {key} -> {','.join(paths)}")
        undeclared_after, missing_after = compare(doc, declared)
        if undeclared_after:
            print("still-undeclared: " + " ".join(undeclared_after))
        if missing_after:
            print("still-missing: " + " ".join(missing_after))
        return 0
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

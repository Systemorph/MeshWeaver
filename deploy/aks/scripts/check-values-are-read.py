#!/usr/bin/env python3
"""Assert that every `config.<component>.<KEY>` a values file sets is READ by a template.

Driven by check-values-are-read.sh (see that file for why this exists). Takes the chart
directory followed by one or more values files, and reports EVERY orphaned key it finds —
never just the first, so one run tells you the whole story.

Exit 0 = every key this run examined is consumed by the component whose section it sits in.
Exit 1 = at least one orphaned key, or too little was examined to report a pass.
"""
import os
import re
import sys

import yaml

# `.Values.config.<component>.<KEY>` — the ONLY shape the chart uses to read a config key.
# check-values-are-read.sh asserts that up front (no `range` over a config section, no
# `index .Values.config …`), so a key absent from this set is read by nothing, full stop.
#
# The optional `)` is not cosmetic: the chart also writes the nil-safe form
# `(.Values.config.memex_portal).Deployment__Orleans__Clustering` (secrets.yaml, the migration
# Job), and a key that appeared ONLY that way would be reported as orphaned — a FALSE ALARM on a
# key that is read perfectly well. A gate that cries wolf gets switched off, so it must match
# every shape the chart actually uses, not the tidiest one.
READ_RE = re.compile(
    r"\.Values\.config\.([A-Za-z_][A-Za-z0-9_]*)\)?\.([A-Za-z_][A-Za-z0-9_]*)")

chart_dir, *values_paths = sys.argv[1:]

read_keys: set[tuple[str, str]] = set()
components_read: set[str] = set()
template_files = 0
for root, _dirs, files in os.walk(os.path.join(chart_dir, "templates")):
    for fname in files:
        if not fname.endswith((".yaml", ".yml", ".tpl")):
            continue
        template_files += 1
        with open(os.path.join(root, fname)) as fh:
            for comp, key in READ_RE.findall(fh.read()):
                read_keys.add((comp, key))
                components_read.add(comp)

if not read_keys:
    print(
        f"::error::no `.Values.config.<component>.<KEY>` reference was found under "
        f"{chart_dir}/templates ({template_files} template file(s) scanned). Either the chart "
        f"moved, or it stopped naming config keys explicitly. Every key would look orphaned, so "
        f"this is a FAILURE rather than a report on evidence that was never gathered."
    )
    sys.exit(1)

findings: list[str] = []
examined = 0

for path in values_paths:
    with open(path) as fh:
        values = yaml.safe_load(fh) or {}
    config = values.get("config")
    if not isinstance(config, dict):
        # A values file may legitimately carry no `config:` section at all (an image-only pin,
        # a secrets-only vault half). Say so — a silent skip is how "checked nothing" starts
        # reading as "found nothing wrong".
        print(f"  note: {path} has no `config:` section — nothing to check in it.")
        continue
    for comp, keys in config.items():
        if not isinstance(keys, dict):
            continue
        if comp not in components_read:
            # The whole SECTION is unknown to the chart. Naming each key inside it would bury
            # the one fact that matters: the component does not exist.
            findings.append(
                f"{path}: `config.{comp}` is a section NO template reads. The chart reads "
                f"config sections {sorted(components_read)}. Every one of its "
                f"{len(keys)} key(s) reaches no container."
            )
            examined += len(keys)
            continue
        for key in keys:
            examined += 1
            if (comp, key) in read_keys:
                continue
            elsewhere = sorted(c for c in components_read if (c, key) in read_keys)
            if elsewhere:
                findings.append(
                    f"{path}: `config.{comp}.{key}` is read by NO template — but "
                    f"`config.{'`, `config.'.join(elsewhere)}.{key}` IS read. This key is "
                    f"MIS-NESTED: it is set under the wrong component, so the container that "
                    f"needs it never sees the value and the ConfigMap renders the key EMPTY "
                    f"(MeshWeaver#2210). Move it to `config.{elsewhere[0]}`."
                )
            else:
                findings.append(
                    f"{path}: `config.{comp}.{key}` is read by NO template, under any "
                    f"component. Setting it changes nothing, silently (MeshWeaver#1925). Either "
                    f"template the key in deploy/helm/templates/, or delete it from the values "
                    f"file — do not leave a value that looks configured and is inert."
                )

if examined == 0:
    print(
        "::error::not one config key was examined across "
        f"{len(values_paths)} values file(s). A run with no evidence must never read as a pass."
    )
    sys.exit(1)

summary_path = os.environ.get("GITHUB_STEP_SUMMARY")


def summarise(line: str) -> None:
    if summary_path:
        with open(summary_path, "a") as fh:
            fh.write(line + "\n")


if findings:
    for f in findings:
        print(f"::error::{f}")
        summarise(f"- ❌ {f}")
    print(
        f"\n{len(findings)} orphaned config key(s) across {examined} examined, "
        f"against {len(read_keys)} keys the chart reads."
    )
    sys.exit(1)

msg = (
    f"All {examined} config key(s) across {len(values_paths)} values file(s) are read by a "
    f"template ({len(read_keys)} readable keys in the chart)."
)
print(msg)
summarise(f"- ✅ {msg}")
sys.exit(0)

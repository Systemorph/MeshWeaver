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
# `index .Values.config …`), so a key absent from this set is read by nothing — with ONE
# exception the .sh proves wired before it says so: config.memex_portal's pass-through
# (PASSTHROUGH_SECTION below), which delivers that section's un-named keys verbatim.
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

# The section whose un-named keys the chart's pass-through delivers verbatim (the portal
# ConfigMap's memex.portalConfigPassThrough), or "" when the caller could not prove that helper is
# wired. Set ONLY by check-values-are-read.sh, which asserts both halves of the wiring first.
PASSTHROUGH_SECTION = os.environ.get("PASSTHROUGH_SECTION", "").strip()

# Every data key NAME the templates render (a YAML key line in non-comment text) — what the
# pass-through compares a case-twin against. The same notion scripts/check-config-key-coverage.py uses.
_HELM_COMMENT = re.compile(r"\{\{-?\s*/\*.*?\*/\s*-?\}\}", re.DOTALL)
_YAML_KEY = re.compile(r"^\s*([A-Za-z_][A-Za-z0-9_.-]*)\s*:")
rendered_names: set[str] = set()
for root, _dirs, files in os.walk(os.path.join(chart_dir, "templates")):
    for fname in files:
        if fname.endswith((".yaml", ".yml")):
            with open(os.path.join(root, fname)) as fh:
                for line in _HELM_COMMENT.sub(" ", fh.read()).splitlines():
                    m = None if line.lstrip().startswith("#") else _YAML_KEY.match(line)
                    if m:
                        rendered_names.add(m.group(1))
_folded_names = {n.lower(): n for n in rendered_names}


def passthrough_verdict(key: str, value: object) -> str | None:
    """Why the portal pass-through does NOT deliver this key, or None when it does.

    Mirrors _portal-config-passthrough.tpl (and check-config-key-coverage.py): blank/null renders
    nothing; a map/list, a case-twin of a rendered key, an invalid ConfigMap key and a
    Modules__Required__N slot outside the literal block are REFUSED by the render.
    """
    if isinstance(value, (dict, list)):
        return "a map/list — the render refuses it (an environment variable carries one string)"
    if value is None or str(value).strip() == "":
        return "its value is blank, so the pass-through renders nothing for it"
    if key.lower() in _folded_names:
        return f"it differs only by case from {_folded_names[key.lower()]}, so the render refuses it"
    if not re.match(r"^[-._a-zA-Z0-9]+$", key):
        return "it is not a valid ConfigMap key, so the render refuses it"
    if key.lower().startswith("modules__required__"):
        return "it is a boot-module slot outside the literal 0..19 block, so the render refuses it"
    return None


findings: list[str] = []
passed_through: list[str] = []
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
            if comp == PASSTHROUGH_SECTION and not elsewhere:
                # Not named by any template. DELIVERED only if the pass-through's own rules let it
                # through; otherwise a finding naming why (a key another section names is still
                # reported as mis-nested below — the pass-through would hand it to the portal, not
                # to the container that reads it).
                why = passthrough_verdict(str(key), keys[key])
                if why is None:
                    passed_through.append(
                        f"{path}: config.{comp}.{key} is named by no template — delivered verbatim "
                        f"by the portal ConfigMap's pass-through.")
                else:
                    findings.append(
                        f"{path}: `config.{comp}.{key}` is named by no template and the portal "
                        f"ConfigMap's pass-through does not deliver it: {why}.")
                continue
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


for p in passed_through:
    print(f"  note: {p}")

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
    (f"All {examined} config key(s) across {len(values_paths)} values file(s) are read by a "
     f"template ({len(read_keys)} readable keys in the chart).")
    if not passed_through else
    (f"All {examined} config key(s) across {len(values_paths)} values file(s) reach a container: "
     f"{examined - len(passed_through)} read by a template ({len(read_keys)} readable keys in the "
     f"chart), {len(passed_through)} named by none and delivered by the portal ConfigMap's pass-through.")
)
print(msg)
summarise(f"- ✅ {msg}")
sys.exit(0)

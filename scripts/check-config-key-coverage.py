#!/usr/bin/env python3
"""Fail when a deployment overlay declares a `config.*` key the Helm chart renders NOWHERE.

THE DEFECT THIS CATCHES (four recurrences: #1925, #1778, #1780, and #2203). The portal ConfigMap
template (`deploy/helm/templates/memex-portal/config.yaml`) names every key it renders EXPLICITLY —
there is no catch-all `range` over a config block. So a key set in an overlay but rendered by no
template reaches NO container, and NOTHING errors: the portal silently runs with configuration that
does not match the reviewed overlay. #2203 was the worst shape — all twelve `OpenRouter__Models__*`
keys were declared and rendered by nothing, and a re-render would have dropped the two models that
were live, leaving no OpenRouter models at all.

MATCH ON THE RENDERED ConfigMap KEY NAME, not the values path. A ConfigMap/Secret data key
`Foo__Bar` may be rendered from ANY values path — e.g.
`Hosting__Operator__Enabled: "{{ (.Values.hostingOperator).enabled }}"` renders the data key
`Hosting__Operator__Enabled` from `.Values.hostingOperator`, NOT from `.Values.config.memex_portal`.
So the key IS delivered, and matching on the `.Values.config.{block}.{KEY}` path would flag it as a
false positive (it did). The correct test is: does the chart render a ConfigMap data key whose NAME
equals the overlay key? A key rendered from somewhere = PASS; a key rendered nowhere = FAIL (the real
#2203 defect — the key name appears in no template at all).

THE CHECK (deliberately narrow, so it is trustworthy rather than noisy):
  * Scope: ONLY the `config.memex_portal`, `config.memex_migration`, `config.memex_postgres`
    blocks of each overlay. Never the whole values tree — persistence/resources/image keys are not
    meant to reach a ConfigMap and would produce a wall of false positives.
  * "Rendered" = the chart templates contain a YAML data key line `<KEY>:` (in NON-COMMENT text) —
    the key name as it lands in the rendered ConfigMap/Secret, whatever values path fills it.
  * One direction only: DECLARED-in-overlay-but-rendered-NOWHERE = FAIL (the silent drop). The
    reverse — a template key with no overlay value — is fine: it renders "" and is inert.
  * Indexed keys (`OpenRouter__Models__0`, `Anthropic__Models__1`, …) are each their own key.

A RATCHETED allow-list (`config-key-coverage.allow`, beside this script's caller) carries any
pre-existing rendered-nowhere keys, so the gate is green on today's `main` while still failing on any
NEW drop (the #2203 class). Each entry is `<env>\tconfig.<block>.<key>` — env-scoped, because a key
can legitimately be dropped in one environment only. The list may only SHRINK: a NEW unlisted miss
FAILS; a listed key that became rendered (debt paid) FAILS until its line is deleted.

Exit 0 when every rendered-nowhere key is on the allow-list; exit 1 naming every NEW miss and every
stale allow-list entry; exit 2 on a broken chart path (never a silent pass).
"""
from __future__ import annotations

import argparse
import glob
import os
import re
import sys

try:
    import yaml
except ImportError:
    sys.exit("check-config-key-coverage: PyYAML is required (pip install pyyaml)")

CONFIG_BLOCKS = ("memex_portal", "memex_migration", "memex_postgres")

# Helm comment blocks `{{/* ... */}}` / `{{- /* ... */ -}}` — may span lines.
_HELM_COMMENT = re.compile(r"\{\{-?\s*/\*.*?\*/\s*-?\}\}", re.DOTALL)
# A YAML key line: leading indent, a key, a colon. Captures the key name.
_YAML_KEY = re.compile(r"^\s*([A-Za-z_][A-Za-z0-9_.-]*)\s*:")


def rendered_key_names(template_dir: str) -> set[str]:
    """Every ConfigMap/Secret data KEY NAME the templates render (comments removed).

    Collects the name from each YAML key line across all templates. Config keys are distinctive
    (.NET `Foo__Bar` / `MEMEX_*` style), so scanning every key line — rather than only those inside
    a `data:`/`stringData:` block, which is unreliable to parse in templated YAML — cannot collide
    with a chart structural key (`name`, `image`, `replicas`) for the keys this gate checks.
    """
    names: set[str] = set()
    pattern = os.path.join(template_dir, "**", "*.yaml")
    for path in sorted(glob.glob(pattern, recursive=True)):
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        text = _HELM_COMMENT.sub(" ", text)
        for line in text.splitlines():
            if line.lstrip().startswith("#"):
                continue
            m = _YAML_KEY.match(line)
            if m:
                names.add(m.group(1))
    return names


def env_of(overlay_path: str) -> str:
    """The environment name — the overlay's parent dir (memex / memex-cloud / atioz)."""
    return os.path.basename(os.path.dirname(os.path.abspath(overlay_path)))


def load_allow(path: str | None) -> set[str]:
    r"""Allow-list entries as `<env>\tconfig.<block>.<key>`. Blank lines and `#` comments ignored."""
    if not path or not os.path.isfile(path):
        return set()
    out: set[str] = set()
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.split("#", 1)[0].strip()
            if line:
                out.add(re.sub(r"\s+", "\t", line, count=1))
    return out


def declared_keys(overlay_path: str) -> dict[str, list[str]]:
    """{block: [keys]} declared under `config.{block}` in one overlay. Missing blocks omitted."""
    with open(overlay_path, encoding="utf-8") as fh:
        data = yaml.safe_load(fh) or {}
    config = data.get("config") or {}
    out: dict[str, list[str]] = {}
    for block in CONFIG_BLOCKS:
        section = config.get(block)
        if isinstance(section, dict):
            out[block] = [str(k) for k in section.keys()]
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--chart", required=True,
                    help="Path to the checked-out MeshWeaver chart root (contains deploy/helm), "
                         "the deploy/helm dir, or the templates dir itself.")
    ap.add_argument("--overlays", nargs="*", default=None,
                    help="Overlay YAML paths. Default: deployments/aks/*/values.*.public.yaml")
    ap.add_argument("--allow", default=None,
                    help="Path to config-key-coverage.allow (pre-existing rendered-nowhere keys). "
                         "Optional; without it EVERY rendered-nowhere key fails.")
    args = ap.parse_args()

    candidates = [
        os.path.join(args.chart, "deploy", "helm", "templates"),
        os.path.join(args.chart, "templates"),
        args.chart,
    ]
    template_dir = next((c for c in candidates if os.path.isdir(c)), None)
    if not template_dir or not glob.glob(os.path.join(template_dir, "**", "*.yaml"), recursive=True):
        return _die(f"no chart templates found under {args.chart!r} "
                    f"(looked for deploy/helm/templates, templates, or the given dir)")

    overlays = args.overlays or sorted(glob.glob("deployments/aks/*/values.*.public.yaml"))
    if not overlays:
        return _die("no overlays found (deployments/aks/*/values.*.public.yaml)")

    rendered = rendered_key_names(template_dir)
    allow = load_allow(args.allow)
    allow_hit: set[str] = set()
    total_declared = 0
    new_misses: list[str] = []
    print(f"config-key-coverage: chart templates = {template_dir}")
    print(f"config-key-coverage: rendered ConfigMap/Secret key names = {len(rendered)}")
    print(f"config-key-coverage: overlays = {len(overlays)}"
          + (f", allow-list = {len(allow)} entr{'y' if len(allow)==1 else 'ies'}" if args.allow else "")
          + "\n")

    for overlay in overlays:
        env = env_of(overlay)
        blocks = declared_keys(overlay)
        declared = sum(len(v) for v in blocks.values())
        total_declared += declared
        misses: list[tuple[str, str, bool]] = []  # (block, key, allowed)
        for block, keys in blocks.items():
            for key in keys:
                if key not in rendered:
                    token = f"{env}\tconfig.{block}.{key}"
                    allowed = token in allow
                    if allowed:
                        allow_hit.add(token)
                    else:
                        new_misses.append(f"{env}: config.{block}.{key}")
                    misses.append((block, key, allowed))
        status = "OK" if not misses else f"rendered-nowhere {len(misses)}"
        print(f"  [{env}] {os.path.basename(overlay)}  (declared {declared})  ->  {status}")
        for block, key, allowed in misses:
            tag = "allowed (pre-existing debt)" if allowed else "NEW - reaches no container"
            print(f"      {'.' if allowed else 'x'} config.{block}.{key}  - {tag}")

    stale = sorted(allow - allow_hit)
    print(f"\nconfig-key-coverage: declared {total_declared} key(s); "
          f"{len(new_misses)} new rendered-nowhere; {len(stale)} stale allow-list entr"
          f"{'y' if len(stale)==1 else 'ies'}.")

    ok = True
    if new_misses:
        ok = False
        print("\nFAIL - these overlay keys reach NO container (no error is raised at deploy):")
        for m in new_misses:
            print(f"  x {m}")
        print("Render each in the chart (add a `<KEY>:` line to a ConfigMap/Secret template), "
              "remove it from the overlay, or - only if it is knowingly inert - add it to "
              "config-key-coverage.allow with a comment saying why.")
    if stale:
        ok = False
        print("\nFAIL - these allow-list entries are now rendered (debt paid). Delete them "
              "(the list may only shrink):")
        for s in stale:
            print(f"  x {s.replace(chr(9), ': ')}")
    if ok:
        print("PASS: every rendered-nowhere overlay key is a known, allow-listed pre-existing entry.")
        return 0
    return 1


def _die(msg: str) -> int:
    print(f"config-key-coverage: {msg}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Assert the observability values produce a configuration the components actually read.

Driven by check-observability-values.sh (see that file for why this exists). Two checks, because
the two components fail in opposite directions and neither check alone is sufficient:

  RENDER   every `<component>.config.<KEY>` a values file sets must arrive, unaltered, in that
           component's rendered configuration. This is the check that matters for PROMTAIL, whose
           chart builds its config from named fields and silently drops anything else.

  BINARY   the rendered Loki configuration is handed to `loki -verify-config`. This is the check
           that matters for LOKI, whose chart merges `loki.config` into its defaults VERBATIM —
           so the render check is vacuous there (a typo'd `ingestor:` reaches the render intact
           and would pass). Only the binary knows that `ingestor` is not a field. Measured
           2026-09-09: the render check passes that typo, the binary rejects it with
           `field ingestor not found in type loki.ConfigWrapper`.

Reports EVERY finding, never just the first, so one run tells you the whole story.

Exit 0 = every leaf examined arrives unaltered AND the rendered Loki config is valid.
Exit 1 = an orphaned/altered key, an invalid config, or too little examined to report a pass.
"""
import base64
import subprocess
import sys

import yaml

# Where each chart component's rendered configuration actually lands, and which check is
# AUTHORITATIVE for it. Both are Secrets in loki-stack and they differ in encoding — `loki` uses
# base64 `data`, `promtail` uses `stringData` — the kind of detail a hand-written check gets wrong
# once and then reports a false pass forever. Resolved by kind+name, never by position.
#
# `verbatim_config` records that the chart merges the component's `config:` through untouched. For
# such a component the render check CANNOT fail on an unknown key, and saying so here is what stops
# the next reader treating its green as coverage it is not.
COMPONENT_DOCS = {
    "loki": {"kind": "Secret", "name": "loki", "key": "loki.yaml", "verbatim_config": True},
    "promtail": {"kind": "Secret", "name": "loki-promtail", "key": "promtail.yaml",
                 "verbatim_config": False},
}

# Below this, a "pass" is not evidence. The values file sets the ingester WAL (3 leaves), the
# compactor's retention switch and the retention period — so a run that examines fewer than five
# leaves has lost sight of its subject and must say so rather than render a tick.
MIN_LEAVES = 5


def leaves(node, prefix=()):
    """Every scalar leaf under a mapping, as (path tuple, value)."""
    if isinstance(node, dict):
        for key, value in node.items():
            yield from leaves(value, prefix + (str(key),))
    elif isinstance(node, list):
        # A list is compared whole: order and content both matter, and half a list reaching the
        # render is not a partial pass.
        yield prefix, node
    else:
        yield prefix, node


def dig(node, path):
    """Follow `path` through the rendered config; returns (found, value)."""
    for segment in path:
        if not isinstance(node, dict) or segment not in node:
            return False, None
        node = node[segment]
    return True, node


def run(command, stdin=None):
    return subprocess.run(command, input=stdin, capture_output=True, text=True)


def render(chart, version, values_paths):
    command = ["helm", "template", "loki", chart, "--namespace", "monitoring"]
    if version:
        command += ["--version", version]
    for path in values_paths:
        command += ["-f", path]
    result = run(command)
    if result.returncode != 0:
        print(f"::error::`{' '.join(command)}` failed — the chart could not be rendered, so NOTHING "
              f"was checked. This is a failure, not a skip.\n{result.stderr.strip()}")
        sys.exit(1)
    return result.stdout


def loki_app_version(chart, version):
    """The image tag whose binary must accept the config — taken from the PINNED chart, never
    hard-coded, so a chart bump moves the validator with the thing it validates."""
    result = run(["helm", "show", "chart", chart] + (["--version", version] if version else []))
    if result.returncode != 0:
        print(f"::error::`helm show chart {chart}` failed — cannot determine which Loki binary to "
              f"validate against, so the binary check would silently not happen.\n{result.stderr.strip()}")
        sys.exit(1)
    metadata = yaml.safe_load(result.stdout) or {}
    app_version = str(metadata.get("appVersion") or "").lstrip("v")
    if not app_version:
        print(f"::error::the pinned chart declares no appVersion — cannot pick a Loki image, and a "
              f"guessed tag would validate against a binary the cluster never runs.")
        sys.exit(1)
    return app_version


def verify_with_binary(config_text, app_version):
    """Hand the rendered config to the real Loki binary. Piped over stdin rather than bind-mounted:
    a mount depends on the host sharing the path with the container runtime (it does not, on a
    default macOS Docker Desktop), and a check that only runs on CI's filesystem is a check nobody
    can reproduce while fixing it."""
    image = f"grafana/loki:{app_version}"
    result = run(
        ["docker", "run", "-i", "--rm", "--entrypoint", "sh", image,
         "-c", "cat > /tmp/rendered.yaml && exec loki -config.file=/tmp/rendered.yaml -verify-config"],
        stdin=config_text)
    output = (result.stdout + result.stderr).strip()
    if result.returncode != 0:
        return [f"the rendered Loki configuration is REJECTED by {image}:\n"
                + "\n".join(f"      {line}" for line in output.splitlines()[-6:])]
    if "config is valid" not in output:
        return [f"{image} exited 0 but never said `config is valid` — the validator did not run, "
                f"so nothing was verified. Output:\n"
                + "\n".join(f"      {line}" for line in output.splitlines()[-6:])]
    return []


def main():
    chart, version, *values_paths = sys.argv[1:]
    if not values_paths:
        print("::error::no values file given — usage: check-observability-values.py CHART VERSION VALUES...")
        return 1

    manifest = render(chart, version, values_paths)
    documents = [d for d in yaml.safe_load_all(manifest) if d]
    if not documents:
        print(f"::error::`helm template {chart}` rendered NOTHING. Every key would look orphaned, "
              f"so this is a FAILURE rather than a report on evidence that was never gathered.")
        return 1

    rendered: dict[str, dict] = {}
    rendered_text: dict[str, str] = {}
    for component, where in COMPONENT_DOCS.items():
        for document in documents:
            if (document.get("kind") != where["kind"]
                    or document.get("metadata", {}).get("name") != where["name"]):
                continue
            data = document.get("data") or {}
            string_data = document.get("stringData") or {}
            if where["key"] in string_data:
                text = string_data[where["key"]]
            elif where["key"] in data:
                text = base64.b64decode(data[where["key"]]).decode()
            else:
                continue
            rendered_text[component] = text
            rendered[component] = yaml.safe_load(text) or {}
            break

    findings: list[str] = []
    examined = 0

    for path in values_paths:
        with open(path) as handle:
            values = yaml.safe_load(handle) or {}
        for component, section in values.items():
            if not isinstance(section, dict) or "config" not in section:
                continue
            if component not in COMPONENT_DOCS:
                findings.append(
                    f"{path}: `{component}.config` is set, but this check does not know where "
                    f"{component}'s rendered configuration lands. Add it to COMPONENT_DOCS — an "
                    f"unknown component must not pass silently.")
                continue
            where = COMPONENT_DOCS[component]
            if component not in rendered:
                findings.append(
                    f"{path}: `{component}.config` is set, but the render contains no "
                    f"{where['kind']}/{where['name']} carrying `{where['key']}`. Either the chart "
                    f"moved or the component is disabled — either way nothing was verified.")
                continue
            for key_path, expected in leaves(section["config"]):
                examined += 1
                found, actual = dig(rendered[component], key_path)
                dotted = ".".join(key_path)
                if not found:
                    findings.append(
                        f"{path}: `{component}.config.{dotted}` reaches NOTHING — the rendered "
                        f"{component} configuration has no `{dotted}`. helm accepts the key and "
                        f"reports success, so this reads as configured while being inert.")
                elif str(actual) != str(expected):
                    findings.append(
                        f"{path}: `{component}.config.{dotted}` is set to {expected!r} but renders "
                        f"as {actual!r} — the chart overrode it.")

    # The binary half. Unconditional: it does not ask whether anyone set `loki.config`, because the
    # defaults the chart supplies are just as capable of being invalid as anything we add.
    if "loki" not in rendered_text:
        findings.append(
            "the render contains no Secret/loki carrying `loki.yaml`, so the Loki configuration "
            "could not be validated against the binary at all.")
    else:
        findings += verify_with_binary(rendered_text["loki"], loki_app_version(chart, version))

    if findings:
        print(f"::error::{len(findings)} observability configuration finding(s):")
        for finding in findings:
            print(f"  - {finding}")
        return 1

    if examined < MIN_LEAVES:
        print(f"::error::only {examined} config leaf/leaves examined (minimum {MIN_LEAVES}). A pass "
              f"on this little is not evidence — the values file lost its `config` sections, or the "
              f"components were renamed.")
        return 1

    verbatim = sorted(c for c, w in COMPONENT_DOCS.items() if w["verbatim_config"] and c in rendered)
    print(f"Verified: {examined} config leaf/leaves reach the rendered configuration of "
          f"{', '.join(sorted(rendered))}, and the rendered Loki configuration is accepted by the "
          f"Loki binary itself (the check that carries {', '.join(verbatim)}, whose chart merges "
          f"`config` through verbatim).")
    return 0


if __name__ == "__main__":
    sys.exit(main())

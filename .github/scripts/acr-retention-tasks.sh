#!/usr/bin/env bash
# acr-retention-tasks.sh — show, VERIFY or re-apply the `meshweaver` ACR retention tasks.
#
# WHY THIS EXISTS (MeshWeaver#3438)
# ---------------------------------
# The registry's purge tasks are CLOUD-ONLY. `az acr task show` returns an `EncodedTask` with
# `contextPath: null` — the YAML is a base64 blob inside an Azure resource, with no source
# repository — and an org-wide code search for `purge-old-images` finds nothing outside this
# repository's doc pages. So the reasoning that keeps two incidents from recurring (Memex#122's
# "would anything recreate this tag?", #3438's "does anything PIN a digest of it?") lives in
# comments that exist in exactly one place, that nobody diffs, and that any `az acr task update`
# from any laptop replaces silently and completely.
#
# `.github/acr-retention/` is the RECORD. Nothing deploys from it: this script is the only thing
# that relates it to the live registry.
#
#   show     print the live definitions, decoded                      (read-only)
#   record   overwrite the record FROM the live registry               (read-only against Azure)
#   verify   diff live against the record; exit 1 on drift            (read-only)
#   apply    push the record onto the registry                        (MUTATES — maintainer only)
#
# 🚨 `verify` is deliberately NOT wired into CI. It needs a credential with `Microsoft.
# ContainerRegistry/registries/tasks/read`, which is a strictly larger grant than the AcrPull the
# pinned-digest lanes hold, and widening a CI credential to watch a file nobody deploys from is the
# wrong trade. Run it by hand when you touch retention. If that judgement is revisited, the lane
# must carry a `preflight` that asserts the credential — never an `if:` that asks whether it is set.
set -uo pipefail

REGISTRY="${MW_ACR_REGISTRY:-meshweaver}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RECORD="$HERE/../acr-retention"
TASKS="purge-old-images purge-old-ci-releases"

usage() {
  echo "usage: acr-retention-tasks.sh {show|record|verify|apply}" >&2
  exit 2
}

live_yaml() {
  # 🚨 NO `|| true` AND NO `2>/dev/null` ON THE FETCH. A swallowed error here would decode to an
  # empty string and read as "the live task is empty", which `verify` would report as drift and
  # `apply` would treat as a task worth overwriting. An unreadable task is a RED, never a verdict.
  local task="$1" encoded
  if ! encoded=$(az acr task show --registry "$REGISTRY" --name "$task" \
                   --query "step.encodedTaskContent" -o tsv); then
    echo "::error::cannot read task '$task' on registry '$REGISTRY'." >&2
    echo "  Nothing was compared. This needs Microsoft.ContainerRegistry/registries/tasks/read;" >&2
    echo "  \`az login\` first, and confirm the subscription holding $REGISTRY.azurecr.io." >&2
    return 1
  fi
  if [ -z "$encoded" ]; then
    echo "::error::task '$task' returned an EMPTY encodedTaskContent." >&2
    echo "  That is not 'the task has no steps' — an EncodedTask always carries one. Refusing to" >&2
    echo "  read an empty answer as a definition." >&2
    return 1
  fi
  printf '%s' "$encoded" | base64 -d
}

cmd_show() {
  local task
  for task in $TASKS; do
    echo "=== $task ==="
    az acr task show --registry "$REGISTRY" --name "$task" \
      --query "{status:status,schedule:trigger.timerTriggers[0].schedule,timeout:timeout,contextPath:step.contextPath}" \
      -o json || return 1
    live_yaml "$task" || return 1
    echo
  done
}

cmd_record() {
  # 🚨 CAPTURE THROUGH THE SAME READER `verify` COMPARES WITH. Recording with a hand-typed
  # `az … | base64 -d > file` and verifying through `$(…)` differ by one trailing newline, and the
  # verifier then reports permanent drift over a byte nobody wrote. Measured 2026-09-07: the first
  # `verify` after the first capture was red for exactly that reason.
  local task
  for task in $TASKS; do
    live_yaml "$task" > "$RECORD/$task.yaml" || return 1
    echo "  recorded $task -> .github/acr-retention/$task.yaml"
  done
  echo "  now update .github/acr-retention/tasks.json if status/schedule/timeout changed."
}

cmd_verify() {
  local task drift=0 checked=0
  for task in $TASKS; do
    local recorded="$RECORD/$task.yaml"
    if [ ! -f "$recorded" ]; then
      echo "::error::no record for task '$task' — expected $recorded" >&2
      drift=1
      continue
    fi
    # 🚨 COMPARE FILE TO FILE, never `$(live_yaml …)` to a file. Command substitution strips
    # TRAILING NEWLINES, and both live definitions end in one — so a `$(…)`-based diff reported
    # permanent drift on a byte nobody had written, on every task, forever. A drift report that is
    # always red is a drift report nobody reads. Measured 2026-09-07, twice, before it was believed.
    local live="${TMPDIR:-/tmp}/acr-retention-live.$$"
    live_yaml "$task" > "$live" || { rm -f "$live"; return 1; }
    checked=$((checked + 1))
    if ! diff -u "$recorded" "$live" > "${live}.diff" 2>&1; then
      echo "::error::task '$task' DRIFTED from .github/acr-retention/$task.yaml:"
      cat "${live}.diff"
      echo "  Someone changed retention in the cloud without updating the record — or the record"
      echo "  was changed without applying it. Decide which is right, then re-run \`apply\` or"
      echo "  re-record with \`record\`. The reasoning in these comments is the only copy there is."
      drift=1
    else
      echo "  $task: matches the record byte for byte."
    fi
    rm -f "$live" "${live}.diff"

    # The window is only half the definition; the SCHEDULE and the enabled/disabled state are the
    # other half, and a task silently disabled is a retention policy that stopped running.
    local want_status want_schedule got_status got_schedule
    want_status=$(python3 -c "import json,sys;d=json.load(open('$RECORD/tasks.json'));print(next(t['status'] for t in d['tasks'] if t['name']=='$task'))")
    want_schedule=$(python3 -c "import json,sys;d=json.load(open('$RECORD/tasks.json'));print(next(t['schedule'] for t in d['tasks'] if t['name']=='$task'))")
    got_status=$(az acr task show --registry "$REGISTRY" --name "$task" --query "status" -o tsv) || return 1
    got_schedule=$(az acr task show --registry "$REGISTRY" --name "$task" \
                     --query "trigger.timerTriggers[0].schedule" -o tsv) || return 1
    if [ "$want_status" != "$got_status" ]; then
      echo "::error::task '$task' status is '$got_status'; the record says '$want_status'."
      drift=1
    fi
    if [ "$want_schedule" != "$got_schedule" ]; then
      echo "::error::task '$task' schedule is '$got_schedule'; the record says '$want_schedule'."
      drift=1
    fi
  done

  # 🚨 THE DENOMINATOR. "No drift" over zero tasks compared is not a pass, and it is exactly what a
  # renamed task or a typo'd registry produces.
  echo "  compared $checked task definition(s) against the record."
  if [ "$checked" -eq 0 ]; then
    echo "::error::ZERO task definitions were compared, so this proved nothing."
    return 1
  fi

  # The lock protection rests on ONE property of these definitions, so assert it rather than
  # trusting the diff to have been read: `acr purge` skips locked manifests unless told otherwise.
  #
  # 🚨 GREP THE `cmd:` LINES, NOT THE FILE. The first draft grepped the whole recorded YAML and
  # fired on the task's OWN COMMENT — which says, correctly, that neither step passes
  # `--include-locked`. A guard that reds on the prose explaining why it is satisfied is a guard
  # that gets muted. Measured 2026-09-07, first run.
  local steps
  steps=$(grep -h -E "^[[:space:]]*-[[:space:]]+cmd:" "$RECORD"/*.yaml)
  if [ -z "$steps" ]; then
    echo "::error::no \`cmd:\` step was found in any recorded task, so nothing was inspected."
    drift=1
  elif printf '%s\n' "$steps" | grep -q -- "--include-locked"; then
    echo "::error::a recorded purge STEP passes --include-locked."
    echo "  That flag deletes LOCKED manifests, which is the entire protection"
    echo "  lock-pinned-digests.py provides. Remove it, or the pinned set is unprotected again."
    drift=1
  else
    local n
    n=$(printf '%s\n' "$steps" | grep -c .)
    echo "  $n purge step(s) inspected; none passes --include-locked, so locked manifests are skipped."
  fi
  return $drift
}

cmd_apply() {
  echo "🚨 This MUTATES shared registry infrastructure every deployment depends on."
  echo "   Registry: $REGISTRY   Tasks: $TASKS"
  echo "   Re-read Doc/Architecture/PinnedImageRetention before continuing."
  printf '   Type the registry name to proceed: '
  local confirm
  read -r confirm
  if [ "$confirm" != "$REGISTRY" ]; then
    echo "aborted."
    return 1
  fi
  local task
  for task in $TASKS; do
    local recorded="$RECORD/$task.yaml"
    local schedule
    schedule=$(python3 -c "import json;d=json.load(open('$RECORD/tasks.json'));print(next(t['schedule'] for t in d['tasks'] if t['name']=='$task'))")
    echo "applying $task (schedule $schedule) from $recorded"
    az acr task update --registry "$REGISTRY" --name "$task" \
      --file "$recorded" --schedule "$schedule" -o none || return 1
    local status
    status=$(python3 -c "import json;d=json.load(open('$RECORD/tasks.json'));print(next(t['status'] for t in d['tasks'] if t['name']=='$task'))")
    az acr task update --registry "$REGISTRY" --name "$task" \
      --status "$status" -o none || return 1
  done
  echo "applied; re-verifying:"
  cmd_verify
}

case "${1:-}" in
  show)   cmd_show ;;
  record) cmd_record ;;
  verify) cmd_verify ;;
  apply)  cmd_apply ;;
  *)      usage ;;
esac

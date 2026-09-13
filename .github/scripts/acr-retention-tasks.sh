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

# The tasks `recordAheadOfRegistry` declares, space-separated; empty when nothing is declared.
# 🚨 NO `2>/dev/null` AND NO `|| true`. A tasks.json this cannot read must stop the caller, not
# answer "nothing is declared" — that is the swallow that turns "the call failed" into "the answer
# is empty", and here it would decide whether a drift is excused. Callers say `|| return 1`.
declared_ahead() {
  if ! python3 -c "
import json
d = json.load(open('$RECORD/tasks.json'))
a = d.get('recordAheadOfRegistry') or {}
print(' '.join(a.get('tasks') or []) if a.get('inForce') is True else '')"; then
    echo "::error::cannot read $RECORD/tasks.json, so it is unknown whether the record is" >&2
    echo "  deliberately ahead of the registry. Refusing to decide either way." >&2
    return 1
  fi
}

cmd_record() {
  # 🚨 `record` OVERWRITES THE RECORD FROM LIVE, so it is the one command that can silently undo a
  # policy the record carries and the registry does not. On 2026-09-13 that was the whole of
  # MeshWeaver#3438's remaining window fix: re-recording would have restored `--ago 7d --keep 10`
  # into a file whose comments explain at length why it must not say that.
  local ahead
  ahead=$(declared_ahead) || return 1
  if [ -n "$ahead" ]; then
    echo "🚨 tasks.json declares recordAheadOfRegistry for: $ahead"
    echo "   Recording OVERWRITES those files from the LIVE task, which discards the recorded"
    echo "   policy and restores whatever the registry currently carries."
    echo "   If that is what you want, say so in the same diff that deletes the declaration."
    printf '   Type OVERWRITE to continue: '
    local confirm
    read -r confirm
    if [ "$confirm" != "OVERWRITE" ]; then
      echo "aborted."
      return 1
    fi
  fi
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
  local task drift=0 checked=0 ahead
  ahead=$(declared_ahead) || return 1
  ahead=" $ahead "
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
      # 🚨 A DECLARED drift is the record being deliberately AHEAD of the registry — the state a
      # policy change lives in between "decided" and "applied". Reporting it as an incident would
      # make `verify` permanently red, and a drift report that is always red is one nobody reads
      # (this file learned that on 2026-09-07 over a trailing newline). It is still PRINTED.
      if [ "$ahead" != "  " ] && [ "${ahead#* $task }" != "$ahead" ]; then
        echo "  $task: drift is DECLARED (tasks.json → recordAheadOfRegistry) — the record is"
        echo "    deliberately ahead of the registry and \`apply\` is what closes it:"
        sed 's/^/      /' "${live}.diff"
      else
        echo "::error::task '$task' DRIFTED from .github/acr-retention/$task.yaml:"
        cat "${live}.diff"
        echo "  Someone changed retention in the cloud without updating the record — or the record"
        echo "  was changed without applying it. Decide which is right, then re-run \`apply\` or"
        echo "  re-record with \`record\`. The reasoning in these comments is the only copy there is."
        drift=1
      fi
    else
      echo "  $task: matches the record byte for byte."
      # 🚨 THE DECLARATION EXPIRES BY BEING CHECKED, never by being remembered. Once `apply` has
      # run, a still-standing `recordAheadOfRegistry` is a stale exemption that would excuse the
      # NEXT real drift on this task — the same failure the instance roster's stale-entry arm
      # exists for.
      if [ "$ahead" != "  " ] && [ "${ahead#* $task }" != "$ahead" ]; then
        echo "::error::tasks.json declares recordAheadOfRegistry for '$task' and there is NO drift."
        echo "  The record has been applied, so the declaration now excuses nothing and would"
        echo "  excuse the next real drift on this task. Delete it from tasks.json."
        drift=1
      fi
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

# 🚨 THE RE-ENABLE INTERLOCK (MeshWeaver#3859, acceptance criterion 1).
#
# `apply` is the ONLY thing in this repository that can turn a destructive schedule back on: it
# pushes `status` out of tasks.json with `az acr task update --status`. Until this function existed
# it did so with no reference to anything — so editing one word in tasks.json (`Disabled` →
# `Enabled`) and running `apply` restored the 03:00 purge with NO protection decision consulted,
# which is the criterion this issue states in as many words: *failed / unavailable / incomplete
# protection collection cannot be followed by deletion*.
#
# This does NOT close #3859. The two clocks are still independent — the lock is an Actions cron at
# 01:00, the purge an ACR timer task at 03:00, and nothing makes the second wait for the first, so
# a nightly deletion is still not downstream of that night's protection verdict. What it closes is
# the ACT of re-enabling, which is the one edge this repository owns. The nightly interlock needs a
# registry-side mechanism and an RBAC grant, both of which #3859 says to choose with the maintainer.
#
# 🚨 It refuses rather than warns, and it names the condition RATHER THAN asking for a flag. A
# prompt answered "yes" is not a decision anyone can audit; `pause.reEnableWhen` is, and lifting
# the pause is a reviewed diff against this record.
assert_pause_permits_enabling() {
  local paused reenable since blocked
  paused=$(python3 -c "import json;d=json.load(open('$RECORD/tasks.json'));print('yes' if (d.get('pause') or {}).get('inForce') is True else 'no')") || return 1
  [ "$paused" = "yes" ] || return 0
  # 🚨 NO `|| true` — a status this cannot read must stop the apply, not be treated as Disabled.
  blocked=$(python3 -c "
import json
d = json.load(open('$RECORD/tasks.json'))
print(' '.join(t['name'] for t in d['tasks'] if str(t.get('status','')).lower() == 'enabled'))") || return 1
  [ -n "$blocked" ] || return 0
  since=$(python3 -c "import json;print((json.load(open('$RECORD/tasks.json')).get('pause') or {}).get('since',''))")
  reenable=$(python3 -c "import json;print((json.load(open('$RECORD/tasks.json')).get('pause') or {}).get('reEnableWhen',''))")
  echo "::error::REFUSING to apply: the record declares an in-force PAUSE (since $since) and asks" >&2
  echo "  to enable: $blocked" >&2
  echo "" >&2
  echo "  re-enable when: $reenable" >&2
  echo "" >&2
  echo "  A pause is the mitigation held in front of an incomplete protection decision, so pushing" >&2
  echo "  an Enabled status while it stands puts deletion back in front of the protection that is" >&2
  echo "  not finished — MeshWeaver#3859's first acceptance criterion. Lift the pause DELIBERATELY:" >&2
  echo "  delete the \`pause\` block from .github/acr-retention/tasks.json in a reviewed diff that" >&2
  echo "  says what satisfied \`reEnableWhen\`, then run this again." >&2
  return 1
}

cmd_apply() {
  # Before the prompt, not after: a refusal the operator reads only after typing the registry name
  # teaches them the prompt is the gate.
  assert_pause_permits_enabling || return 1
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

#!/usr/bin/env bash
# Tests for the hosting operator scripts.
#
# 🚨 WHAT CAN AND CANNOT BE TESTED HERE, stated plainly so nobody reads a green run as more than it
# is. These scripts drive az, kubectl, helm and psql against a live Azure estate; a test run has
# none of those, and mocking them would assert that the mocks agree with themselves. So this suite
# covers the layer that is genuinely testable AND is where the dangerous bugs live:
#
#   • argument handling — a flag silently dropped is a command run against the wrong target
#   • the REFUSALS — every guard that stands between a plan and a destructive act
#   • the ::hosting:: contract — the lines the mesh's OperatorOutput parser reads
#   • run.sh's sequencing — order, first-failure stop, and never treating an empty plan as a no-op
#
# What it does NOT cover: whether `az network dns record-set a add-record` does what we think. That
# is proven by the first real provision, and it is why the rollout is staged.

set -uo pipefail
BIN="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../bin" && pwd)"
export PATH="$BIN:$PATH"

pass=0; fail=0
ok()   { printf '  ok    %s\n' "$1"; pass=$((pass+1)); }
bad()  { printf '  FAIL  %s\n' "$1"; printf '        %s\n' "${2:-}"; fail=$((fail+1)); }

# Assert a command exits non-zero AND its stderr mentions a phrase — a refusal must SAY why.
refuses() {
  local what="$1" phrase="$2"; shift 2
  local out rc
  out="$("$@" 2>&1)"; rc=$?
  if [ "$rc" -eq 0 ]; then bad "$what" "exited 0 — it should have refused"; return; fi
  case "$out" in *"$phrase"*) ok "$what" ;; *) bad "$what" "refused, but never said '${phrase}'. Said: ${out}" ;; esac
}

# Assert a command REFUSED AND STOPPED — non-zero, saying why, having reported nothing.
#
# 🚨 Why the "reported nothing" half matters. `hosting::die` writes to stderr and exits; when the
# guard calling it was (as it once was) inside a command substitution, the MESSAGE still printed
# while the script carried on. A test that only looked for the message passed against a guard that
# did not guard. The absence of any ::hosting:: line is what proves the script stopped before it
# did anything the mesh would record.
refuses_hard() {
  local what="$1" phrase="$2"; shift 2
  local out rc
  out="$("$@" 2>&1)"; rc=$?
  if [ "$rc" -eq 0 ]; then bad "$what" "exited 0 — it should have refused"; return; fi
  case "$out" in *"$phrase"*) ;; *) bad "$what" "refused, but never said '${phrase}'. Said: ${out}"; return ;; esac
  case "$out" in
    *"::hosting::"*) bad "$what" "refused but had already REPORTED something — it did not stop at the guard: ${out}" ;;
    *"+ az"*|*"+ kubectl"*|*"+ helm"*) bad "$what" "refused but had already run a command: ${out}" ;;
    *) ok "$what" ;;
  esac
}

# Assert a command succeeds and emits a given ::hosting:: line.
emits() {
  local what="$1" line="$2"; shift 2
  local out rc
  out="$("$@" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ]; then bad "$what" "exited ${rc}: ${out}"; return; fi
  case "$out" in *"$line"*) ok "$what" ;; *) bad "$what" "no '${line}' in: ${out}" ;; esac
}

echo "── argument handling ─────────────────────────────────────────────"
refuses "kv-ensure needs --vault"          "missing required flag --vault"      hosting-kv-ensure --namespace n
refuses "kv-purge needs --vault"           "missing required flag --vault"      hosting-kv-purge --namespace n
refuses "dns needs a mode"                 "must be 'upsert' or 'delete'"       hosting-dns --zone z --host h
refuses "redirect needs a mode"            "must be 'suspend' or 'restore'"     hosting-redirect --namespace n
refuses "deploy needs --release"           "missing required flag --release"    hosting-deploy --namespace n --database d
refuses "verify needs --host"              "missing required flag --host"       hosting-verify
refuses "unknown flags are not ignored"    "unknown argument"                   hosting-verify --host h --nope 1

echo
echo "── the refusals that stand in front of something destructive ─────"
refuses "kv-purge refuses an EMPTY prefix" "refusing to purge with an EMPTY --prefix" \
  hosting-kv-purge --vault V --prefix "" --namespace n
refuses "dns refuses a host outside its zone" "is not inside zone" \
  env AZ_DNS_RESOURCE_GROUP=rg hosting-dns upsert --zone example.com --host evil.other.com --target 1.2.3.4
refuses "dns refuses a non-IPv4 target"    "is not an IPv4 address" \
  env AZ_DNS_RESOURCE_GROUP=rg hosting-dns upsert --zone example.com --host a.example.com --target not-an-ip
refuses "redirect refuses a relative target" "is not an absolute URL" \
  env HOSTING_DRY_RUN=true hosting-redirect suspend --namespace n --host h.example.com --target /paywall
refuses "deploy refuses a missing values file" "does not exist" \
  env HOSTING_DRY_RUN=true HOSTING_CHART=/tmp hosting-deploy --namespace n --release r --database d --values /nope/values.yaml
refuses "federate refuses without a resource group" "AZ_RESOURCE_GROUP" \
  env -u AZ_RESOURCE_GROUP hosting-federate --identity i --namespace n

echo
echo "── command injection cannot ride in on a name ────────────────────"
refuses_hard "namespace with a shell metacharacter" "is not a plain name" \
  hosting-kv-ensure --vault V --namespace 'n; rm -rf /'
refuses_hard "database with a backtick"             "is not a plain name" \
  env HOSTING_DRY_RUN=true hosting-verify-restore --database 'd`whoami`' --server s
refuses_hard "host with a space"                    "is not a hostname" \
  hosting-verify --host 'a.example.com b'
refuses_hard "store-uri host with a semicolon"      "is not a plain name" \
  env HOSTING_DRY_RUN=true hosting-backup --database 'd;id' --server s --store-uri https://x/y/z --object o

echo
echo "── the ::hosting:: contract the mesh parses ──────────────────────"
emits "dry-run backup announces the object" "::hosting:: object=arch-1" \
  env HOSTING_DRY_RUN=true hosting-backup --database d --server s --store-uri https://x/y/z --object arch-1
emits "dry-run verify-backup does NOT claim verified" "::hosting:: verify=dry-run" \
  env HOSTING_DRY_RUN=true hosting-verify-backup --store-uri https://x/y/z

# 🚨 The single most important assertion in this file: a run that did not read an archive back must
# never emit verified=true. Everything destructive in the plan waits on that line.
out="$(env HOSTING_DRY_RUN=true hosting-verify-backup --store-uri https://x/y/z 2>&1)"
case "$out" in
  *"verified=true"*) bad "a dry run must NEVER claim verified=true" "it did: ${out}" ;;
  *)                 ok  "a dry run never claims verified=true" ;;
esac

echo
echo "── run.sh sequencing ─────────────────────────────────────────────"
plan() { printf '%s' "$1" | base64 | tr -d '\n'; }

refuses "an EMPTY plan is a bug, not a no-op" "HOSTING_PLAN is empty" \
  env HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d HOSTING_PLAN= "$BIN/run.sh"
refuses "a plan that is not base64 fails loudly" "not valid base64" \
  env HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d HOSTING_PLAN='!!!!' "$BIN/run.sh"
refuses "a step with no command is never skipped" "has no command" \
  env HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d HOSTING_PLAN="$(plan 'Lonely step')" "$BIN/run.sh"

# Steps run IN ORDER, and a failure stops the run — the later step must not have run.
marker="$(mktemp)"
out="$(env HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d \
  HOSTING_PLAN="$(plan "First	echo one
Second	false
Third	echo three >> ${marker}")" "$BIN/run.sh" 2>&1)"
rc=$?
[ "$rc" -ne 0 ] && ok "a failing step fails the run" || bad "a failing step fails the run" "exited 0"
case "$out" in *"step 2/3 'Second' failed"*) ok "the failure names the step and its position" ;;
  *) bad "the failure names the step" "said: ${out}" ;; esac
[ ! -s "$marker" ] && ok "the step after a failure never runs" || bad "the step after a failure never runs" "'Third' ran anyway"
rm -f "$marker"

emits "the step marker is emitted per step" "::hosting:: step=First" \
  env HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d HOSTING_PLAN="$(plan 'First	echo one')" "$BIN/run.sh"

# A dry run narrates and mutates nothing.
guard="$(mktemp)"; rm -f "$guard"
out="$(env HOSTING_DRY_RUN=true HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d \
  HOSTING_PLAN="$(plan "Touch	touch ${guard}")" "$BIN/run.sh" 2>&1)"
[ ! -e "$guard" ] && ok "a dry run runs no step" || bad "a dry run runs no step" "the step executed"
case "$out" in *"DRY-RUN would run"*) ok "a dry run narrates what it would do" ;;
  *) bad "a dry run narrates" "said: ${out}" ;; esac

echo
echo "─────────────────────────────────────────────────────────────────"
echo "${pass} passed, ${fail} failed"
[ "$fail" -eq 0 ] || exit 1

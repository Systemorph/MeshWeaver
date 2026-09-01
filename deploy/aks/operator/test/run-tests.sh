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

# Assert a command did NOT stop at a specific guard. It may still fail for want of az/kubectl —
# what matters is that the named refusal is not the reason, i.e. execution got past that guard.
not_refused_by_guard() {
  local what="$1" phrase="$2"; shift 2
  local out
  out="$("$@" 2>&1)"
  case "$out" in
    *"$phrase"*) bad "$what" "stopped at the guard it should have passed: ${out}" ;;
    *) ok "$what" ;;
  esac
}

echo "── argument handling ─────────────────────────────────────────────"
refuses "kv-ensure needs --vault"          "missing required flag --vault"      hosting-kv-ensure --namespace n
refuses "kv-purge needs --vault"           "missing required flag --vault"      hosting-kv-purge --namespace n
refuses "kv-rotate needs --vault"          "missing required flag --vault"      hosting-kv-rotate --namespace n
refuses "kv-rotate needs --namespace"      "missing required flag --namespace"  hosting-kv-rotate --vault V
refuses "pv-purge needs --namespace"       "missing required flag --namespace"  hosting-pv-purge
refuses "pv-purge rejects unknown flags"   "unknown argument"                   hosting-pv-purge --namespace n --nope 1
refuses "dns needs a mode"                 "must be 'upsert' or 'delete'"       hosting-dns --zone z --host h
refuses "redirect needs a mode"            "must be 'suspend' or 'restore'"     hosting-redirect --namespace n
refuses "deploy needs --release"           "missing required flag --release"    hosting-deploy --namespace n --database d
refuses "verify needs --host"              "missing required flag --host"       hosting-verify
refuses "unknown flags are not ignored"    "unknown argument"                   hosting-verify --host h --nope 1

echo
echo "── the refusals that stand in front of something destructive ─────"
refuses "kv-purge refuses an EMPTY prefix" "refusing to purge with an EMPTY --prefix" \
  hosting-kv-purge --vault V --prefix "" --namespace n
# The ambiguity guard: one instance's prefix living UNDER another's means a teardown of the outer
# one enumerates and deletes the inner one's secrets. Runs before any az call, so it is testable here.
refuses "kv-purge refuses a sibling prefix under its own" "is a prefix of sibling instance prefix" \
  hosting-kv-purge --vault V --prefix memex- --namespace n --sibling-prefix memex-dev-
# A sibling that merely SHARES leading characters is not ambiguous and must NOT refuse — this is
# today's real fleet (memex- vs memexcloud-), and a guard that refused it would block every
# memex teardown. Reaches az, which is absent here, so assert only that it got PAST the guard.
not_refused_by_guard "kv-purge allows memex- beside memexcloud-" "is a prefix of sibling" \
  hosting-kv-purge --vault V --prefix memex- --namespace n --sibling-prefix memexcloud-
refuses "dns refuses a host outside its zone" "is not inside zone" \
  env AZ_DNS_RESOURCE_GROUP=rg hosting-dns upsert --zone example.com --host evil.other.com --target 1.2.3.4
refuses "dns refuses a non-IPv4 target"    "is not an IPv4 address" \
  env AZ_DNS_RESOURCE_GROUP=rg hosting-dns upsert --zone example.com --host a.example.com --target not-an-ip
refuses "redirect refuses a relative target" "is not an absolute URL" \
  env HOSTING_DRY_RUN=true hosting-redirect suspend --namespace n --host h.example.com --target /paywall
refuses "deploy refuses a missing values file" "does not exist" \
  env HOSTING_DRY_RUN=true HOSTING_CHART=/tmp hosting-deploy --namespace n --release r --database d --values /nope/values.yaml
# The values file must be the RECORD-rendered one: an empty file and a hand-written file are both
# refused, because either would fall through to chart defaults (ghcr :latest) and report success.
_empty=$(mktemp); : > "$_empty"
refuses "deploy refuses an EMPTY values file" "is EMPTY" \
  env HOSTING_DRY_RUN=true HOSTING_CHART=/tmp hosting-deploy --namespace n --release r --database d --values "$_empty"
_hand=$(mktemp); printf 'replicas:\n  portal: 1\n' > "$_hand"
refuses "deploy refuses a hand-written values file" "does not carry the HelmValues header" \
  env HOSTING_DRY_RUN=true HOSTING_CHART=/tmp hosting-deploy --namespace n --release r --database d --values "$_hand"
_gen=$(mktemp); printf '# GENERATED from the Hosting/Deployment record by HelmValues\nreplicas:\n  portal: 1\n' > "$_gen"
refuses "deploy refuses an unsafe --image" "is not a plain image reference" \
  env HOSTING_DRY_RUN=true HOSTING_CHART=/tmp hosting-deploy --namespace n --release r --database d --values "$_gen" --image 'x;rm -rf /'
refuses "deploy no longer takes --config-file (the catalog rides in the values)" "unknown argument" \
  env HOSTING_DRY_RUN=true HOSTING_CHART=/tmp hosting-deploy --namespace n --release r --database d --values "$_gen" --config-file /tmp/x
rm -f "$_empty" "$_hand" "$_gen"
refuses "federate refuses without a resource group" "AZ_RESOURCE_GROUP" \
  env -u AZ_RESOURCE_GROUP hosting-federate --identity i --namespace n

echo
echo "── the rotated key never leaves the process ──────────────────────"
# 🚨 THE property of hosting-kv-rotate, and the only one whose failure is unrecoverable: a key that
# reaches a job log has been disclosed to everyone who can read Actions, and rotating again does not
# un-disclose it. The script's banner promises it "NEVER PRINTS THE KEY. Not on success, not on
# failure." Promises in comments are what this repo keeps discovering were never true, so assert it.
#
# A dry run reaches the point where a real run would hold the minted key and reports what it WOULD
# do — exactly the window in which a careless `echo` or a `set -x` would leak it. `mwi_` is the
# scheme prefix (InstanceKeys.Generate), so its presence anywhere in the output is the leak.
_rot_out="$(env HOSTING_DRY_RUN=true hosting-kv-rotate --vault V --namespace n --prefix memex- --synced-secret s 2>&1 || true)"
case "$_rot_out" in
  *mwi_*) bad "kv-rotate never prints the minted key" "a 'mwi_' token appeared in its output: ${_rot_out}" ;;
  *)      ok  "kv-rotate never prints the minted key" ;;
esac
# And it must still report the HASH — the one thing that legitimately crosses back to the mesh. A
# script that leaked nothing because it did nothing would pass the check above.
case "$_rot_out" in
  *"::hosting:: key_hash="*|*"would"*) ok "kv-rotate still reports its work in a dry run" ;;
  *) bad "kv-rotate still reports its work in a dry run" "no key_hash and no dry-run report in: ${_rot_out}" ;;
esac
unset _rot_out

echo
echo "── command injection cannot ride in on a name ────────────────────"
refuses_hard "namespace with a shell metacharacter" "is not a plain name" \
  hosting-kv-ensure --vault V --namespace 'n; rm -rf /'
refuses_hard "pv-purge namespace with a metacharacter" "is not a plain name" \
  hosting-pv-purge --namespace 'n; kubectl delete pv --all'
refuses_hard "kv-rotate namespace with a metacharacter" "is not a plain name" \
  hosting-kv-rotate --vault V --namespace 'n; rm -rf /' --synced-secret s
refuses_hard "kv-rotate prefix with a metacharacter"    "is not a plain name" \
  hosting-kv-rotate --vault V --namespace n --prefix 'p`whoami`' --synced-secret s
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
echo "── hosting-audit: what lives only on the cluster ─────────────────"
# The stubs are NOT mocks of helm/kubectl — they answer the audit's read-only calls from fixture
# files so the DETECTION can be asserted without a cluster. What a fixture cannot prove (that a
# real `helm get manifest | kubectl apply --dry-run=client` round-trips YAML) is proven by the
# first real audit, same as every other command here.
STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs" && pwd)"
FIXTURES="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/fixtures/audit" && pwd)"

refuses "audit needs --namespace"           "missing required flag --namespace" hosting-audit --release r
refuses "audit needs --release"             "missing required flag --release"   hosting-audit --namespace n
refuses "audit rejects unknown flags"       "unknown argument"                  hosting-audit --namespace n --release r --nope 1
refuses_hard "audit namespace with a metacharacter" "is not a plain name" \
  hosting-audit --namespace 'n; kubectl delete ns --all' --release r
refuses "audit refuses when the release does not exist" "no helm release" \
  env PATH="$STUBS:$PATH" HOSTING_AUDIT_FIXTURE="$FIXTURES/missing" hosting-audit --namespace memex --release memex
# 🚨 A kind the audit may not LIST is a kind it must not report as clean — Forbidden is fatal and
# names the ClusterRole to widen, it never degrades to "nothing of that kind".
refuses "audit refuses a kind it may not read" "ClusterRole" \
  env PATH="$STUBS:$PATH" HOSTING_AUDIT_FIXTURE="$FIXTURES/forbidden" hosting-audit --namespace memex --release memex

# The drifting namespace: every category detected, by NAME, and no VALUE ever printed.
out="$(env PATH="$STUBS:$PATH" HOSTING_AUDIT_FIXTURE="$FIXTURES/drift" \
  hosting-audit --namespace memex --release memex 2>&1)"; rc=$?
[ "$rc" -eq 0 ] && ok "a drifting audit still exits 0 (drift is a finding, not a failed run)" \
  || bad "a drifting audit exits 0" "exited ${rc}: ${out}"
case "$out" in *"::hosting:: audit_verdict=drift"*) ok "drift is the verdict" ;;
  *) bad "drift is the verdict" "said: ${out}" ;; esac
report="$(printf '%s\n' "$out" | sed -n 's/^::hosting:: audit=//p' | tail -1 | base64 -d 2>/dev/null)"
if [ -z "$report" ]; then
  bad "the report rides an ::hosting:: audit= line" "no decodable audit= line in: ${out}"
else
  ok "the report rides an ::hosting:: audit= line"
  check_report() {  # check_report <what> <jq predicate over the report>
    if printf '%s' "$report" | jq -e "$2" >/dev/null 2>&1; then ok "$1"; else
      bad "$1" "predicate '$2' false for: $(printf '%s' "$report" | jq -c . 2>/dev/null)"; fi
  }
  check_report "env patches are found by name"       '.envLiveOnly | index("PluginCatalog__RegistryToken") and index("EMAIL__CLIENTSECRET")'
  check_report "a manifest-only env is the same edit" '.envManifestOnly == ["RENDERED_ONLY"]'
  check_report "envFrom / volumes / mounts patches"   '(.envFromLiveOnly == ["memex-extra-secret"]) and (.volumesLiveOnly == ["memex-content"]) and (.mountsLiveOnly == ["/mnt/content"])'
  check_report "sidecar containers are found"         '.containersLiveOnly == ["node-gate","python-gate"]'
  check_report "pod-spec patches are found"           '[.podSpecDiffs[].field] | index(".spec.replicas") and index(".spec.template.spec.nodeSelector")'
  check_report "a lifecycle patch is reported with its strings REDACTED" '(.podSpecDiffs | map(select(.field | endswith(".lifecycle")))) as $l | ($l | length) == 1 and ($l[0].live | contains("…")) and (($l[0].live | contains("lifecycle-cmd")) | not)'
  check_report "live-edited ConfigMap keys are found" '.configMaps[0] | (.liveOnlyKeys | index("Portal__ReactAppUrl")) and (.differingKeys == ["Email__Enabled"])'
  check_report "unmanaged objects group by kind"      '.unmanagedObjects | map(select(.kind == "CronJob")) | .[0].names == ["assembly-cache-prune"]'
  check_report "owned/helm/SA-token/CSI objects are excluded" '[.unmanagedObjects[].names[]] | (index("assembly-cache-prune-29123456") or index("sh.helm.release.v1.memex.v42") or index("memex-kv-secrets") or index("memex-portal-sa-token") or index("default")) | not'
  check_report "the hook Job is chart-managed"        '[.unmanagedObjects[].names[]] | index("memex-migration") | not'
  check_report "unmanaged secrets the pod reads"      '.unmanagedSecrets == ["memex-email-secret","memex-extra-secret"]'
  check_report "plain secret-shaped entries, all containers" '.plainSecretEntries | index("env:memex-portal:PluginCatalog__RegistryToken") and index("env:node-gate:GATE_API_KEY") and index("configmap:memex-portal-config/Hosting__ModuleReportSecret")'
  check_report "the audited revision is recorded"     '.helmRevision == 42 and .managedObjectCount == 6'
fi
# 🚨 The most important assertion of the section: names only. The fixture's secret VALUES must
# never appear anywhere in the output — the report lands on a node a page renders.
case "$out" in
  *"must-never-print"*) bad "the audit never prints a value" "a fixture secret VALUE leaked: ${out}" ;;
  *) ok "the audit never prints a value" ;;
esac

# The clean namespace: verdict clean, zero findings, and a kind the cluster does not serve is
# recorded as skipped — never silently absent, never a finding.
out="$(env PATH="$STUBS:$PATH" HOSTING_AUDIT_FIXTURE="$FIXTURES/clean" \
  hosting-audit --namespace memex --release memex 2>&1)"; rc=$?
[ "$rc" -eq 0 ] && ok "a clean audit exits 0" || bad "a clean audit exits 0" "exited ${rc}: ${out}"
case "$out" in *"::hosting:: audit_verdict=clean"*) ok "clean is the verdict when live matches the manifest" ;;
  *) bad "clean is the verdict" "said: ${out}" ;; esac
case "$out" in *"::hosting:: audit_findings=0"*) ok "…with zero findings" ;;
  *) bad "…with zero findings" "said: ${out}" ;; esac
report="$(printf '%s\n' "$out" | sed -n 's/^::hosting:: audit=//p' | tail -1 | base64 -d 2>/dev/null)"
if printf '%s' "$report" | jq -e '.skippedKinds == ["scaledobject"]' >/dev/null 2>&1; then
  ok "an unserved kind is recorded as skipped, not silently absent"
else
  bad "an unserved kind is recorded as skipped" "report: $(printf '%s' "$report" | jq -c .skippedKinds 2>/dev/null)"
fi

# Read-only: a dry run audits for real — same report, same verdict.
emits "a dry run still audits (read-only)" "::hosting:: audit_verdict=clean" \
  env HOSTING_DRY_RUN=true PATH="$STUBS:$PATH" HOSTING_AUDIT_FIXTURE="$FIXTURES/clean" \
  hosting-audit --namespace memex --release memex

echo
echo "─────────────────────────────────────────────────────────────────"
echo "${pass} passed, ${fail} failed"
[ "$fail" -eq 0 ] || exit 1

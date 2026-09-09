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

# ── hosting::do keeps the DATA channel clean ────────────────────────────────────────────────────
# A wrapped mutation can feed a pipe (hosting-deploy: `hosting::do kubectl create namespace …
# -o yaml | kubectl apply -f -`). Its narration must therefore never share stdout with the
# command's output. Measured 2026-09-08 on memex: the narration became line 1 of the manifest and
# kubectl refused it ("yaml: line 2: mapping values are not allowed in this context"), stopping the
# Reconcile at step 1/3 — and a Provision at the same line. Asserted BYTE-FOR-BYTE, not "contains":
# a leading narration line would still contain the manifest.
_manifest=$'apiVersion: v1\nkind: Namespace'
_piped="$(bash -c 'source "$1"; hosting::do printf "%s\n" "$2"' _ "$BIN/_common.sh" "$_manifest" 2>/dev/null)"
if [ "$_piped" = "$_manifest" ]; then ok "hosting::do narrates on stderr — a piped consumer gets only the command's stdout"
else bad "hosting::do narrates on stderr — a piped consumer gets only the command's stdout" "stdout carried: ${_piped}"; fi
_dry="$(HOSTING_DRY_RUN=true bash -c 'source "$1"; hosting::do printf "%s\n" "$2"' _ "$BIN/_common.sh" "$_manifest" 2>/dev/null)"
if [ -z "$_dry" ]; then ok "hosting::do under DRY-RUN writes nothing to stdout either"
else bad "hosting::do under DRY-RUN writes nothing to stdout either" "stdout carried: ${_dry}"; fi
_narrated="$(bash -c 'source "$1"; hosting::do true' _ "$BIN/_common.sh" 2>&1 >/dev/null)"
case "$_narrated" in *"+ true"*) ok "hosting::do still narrates the command (on stderr)" ;;
  *) bad "hosting::do still narrates the command (on stderr)" "stderr was: ${_narrated}" ;; esac

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
refuses "pv-resize needs --namespace"      "missing required flag --namespace"  hosting-pv-resize --claim c --size 1Gi
refuses "pv-resize needs --claim"          "missing required flag --claim"      hosting-pv-resize --namespace n --size 1Gi
refuses "pv-resize needs --size"           "missing required flag --size"       hosting-pv-resize --namespace n --claim c
refuses "pv-resize rejects unknown flags"  "unknown argument"                   hosting-pv-resize --namespace n --claim c --size 1Gi --nope 1
refuses "pv-resize rejects a non-quantity" "is not a whole binary quantity"     hosting-pv-resize --namespace n --claim c --size 128
refuses "pv-resize rejects a decimal unit" "is not a whole binary quantity"     hosting-pv-resize --namespace n --claim c --size 128G
refuses "dns needs a mode"                 "must be 'upsert' or 'delete'"       hosting-dns --zone z --host h
refuses "redirect needs a mode"            "must be 'suspend' or 'restore'"     hosting-redirect --namespace n
refuses "deploy needs --release"           "missing required flag --release"    hosting-deploy --namespace n --database d
refuses "verify needs --host"              "missing required flag --host"       hosting-verify
refuses "unknown flags are not ignored"    "unknown argument"                   hosting-verify --host h --nope 1
refuses "pull-secret needs --namespace"    "missing required flag --namespace"  hosting-pull-secret --registry r.example.test --vault V --secret S
refuses "pull-secret needs --registry"     "missing required flag --registry"   hosting-pull-secret --namespace n --vault V --secret S
refuses "pull-secret needs --vault"        "missing required flag --vault"      hosting-pull-secret --namespace n --registry r.example.test --secret S
refuses "pull-secret needs --secret"       "missing required flag --secret"     hosting-pull-secret --namespace n --registry r.example.test --vault V
refuses "pull-secret rejects unknown flags" "unknown argument"                  hosting-pull-secret --namespace n --registry r.example.test --vault V --secret S --nope 1

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
# script that leaked nothing BECAUSE IT DID NOTHING would sail through the check above, so this arm
# is what makes that one mean something.
#
# 🚨 Deliberately ONLY `::hosting:: key_hash=`. An earlier version also accepted the word "would",
# which a dry run prints unconditionally from its very first step — so that arm could never fail,
# and a regression that stopped emitting the hash entirely (the control plane then never adopts it,
# and the pods restart onto a key the registry does not expect) would have passed. `hosting::say
# key_hash` is outside the `hosting::dry` branch precisely so a rehearsal still reports it; assert
# exactly that.
case "$_rot_out" in
  *"::hosting:: key_hash="*) ok "kv-rotate reports the key hash, even in a dry run" ;;
  *) bad "kv-rotate reports the key hash, even in a dry run" "no '::hosting:: key_hash=' in: ${_rot_out}" ;;
esac
unset _rot_out

echo
echo "── command injection cannot ride in on a name ────────────────────"
refuses_hard "namespace with a shell metacharacter" "is not a plain name" \
  hosting-kv-ensure --vault V --namespace 'n; rm -rf /'
refuses_hard "pv-purge namespace with a metacharacter" "is not a plain name" \
  hosting-pv-purge --namespace 'n; kubectl delete pv --all'
refuses_hard "pv-resize claim with a metacharacter"    "is not a plain name" \
  hosting-pv-resize --namespace n --claim 'c; kubectl delete pvc --all' --size 1Gi
refuses_hard "pv-resize size with a metacharacter"     "is not a whole binary quantity" \
  hosting-pv-resize --namespace n --claim c --size '1Gi"}}}; rm -rf /'
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
refuses_hard "pull-secret namespace with a metacharacter" "is not a plain name" \
  hosting-pull-secret --namespace 'n; rm -rf /' --registry cr.meshweaver.cloud --vault V --secret S
refuses_hard "pull-secret registry with a space"     "is not a hostname" \
  hosting-pull-secret --namespace n --registry 'cr.meshweaver.cloud x' --vault V --secret S
refuses_hard "pull-secret name with a backtick"      "is not a plain name" \
  hosting-pull-secret --namespace n --registry cr.meshweaver.cloud --vault V --secret S --name 'p`whoami`'

echo
echo "── hosting-pull-secret: the namespace first, the key never ──────"
# The plan runs this BEFORE hosting-deploy (Provision) and as the FIRST step of Roll / Reconcile
# (MeshWeaver.Plugins#1514), so the namespace may not exist yet and the command must ensure it —
# idempotently — before it applies anything into it. The stubs record every invocation in order
# and keep the applied manifest, so the read-backs the script verifies with come from what it
# actually applied: a script that applied nothing cannot pass its own verification.
PS_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/pull-secret" && pwd)"
_ps_dir="$(mktemp -d)"; _ps_log="$_ps_dir/calls.log"; : > "$_ps_log"
_ps_out="$(env PATH="$PS_STUBS:$PATH" HOSTING_PULL_SECRET_STUB_LOG="$_ps_log" HOSTING_PULL_SECRET_STUB_DIR="$_ps_dir" \
  hosting-pull-secret --namespace acme --registry cr.meshweaver.cloud --vault Systemorph --secret acme-PluginCatalog-RegistryToken --name registry-pull 2>&1)"; _ps_rc=$?
[ "$_ps_rc" -eq 0 ] && ok "pull-secret succeeds against the stubbed estate" || bad "pull-secret succeeds against the stubbed estate" "exited ${_ps_rc}: ${_ps_out}"
# 🚨 THE property: the key never leaves the process. The stub answers a sentinel `mwi_` key; its
# presence anywhere in the output — a `+ command` echo, a fact, an error — is the leak.
case "$_ps_out" in
  *mwi_*) bad "pull-secret never prints the key" "a 'mwi_' token appeared in its output: ${_ps_out}" ;;
  *)      ok  "pull-secret never prints the key" ;;
esac
# …and the key never travels on a kubectl COMMAND LINE either (argv is readable by every process
# in the pod). The stub logs every invocation verbatim; only an apply's STDIN may carry it.
if grep -v '^  <stdin>' "$_ps_log" | grep -q 'mwi_'; then
  bad "pull-secret never puts the key on a command line" "$(grep -v '^  <stdin>' "$_ps_log" | grep mwi_)"
else
  ok "pull-secret never puts the key on a command line"
fi
# Ordering: the namespace is ensured (read, then created — the stub answers NotFound) BEFORE the
# Secret is applied.
_ns_line="$(grep -n '^kubectl create namespace acme$' "$_ps_log" | head -1 | cut -d: -f1)"
_sec_line="$(grep -n 'kubectl -n acme create secret generic registry-pull' "$_ps_log" | head -1 | cut -d: -f1)"
if [ -n "$_ns_line" ] && [ -n "$_sec_line" ] && [ "$_ns_line" -lt "$_sec_line" ]; then
  ok "pull-secret creates an absent namespace before it applies the Secret"
else
  bad "pull-secret creates an absent namespace before it applies the Secret" "create at line '${_ns_line}', secret at '${_sec_line}' in: $(cat "$_ps_log")"
fi
# 🚨 ENSURE IS NEVER `create --dry-run=client -o yaml | kubectl apply -f -`. apply PATCHES an
# existing namespace and the ClusterRole grants namespaces no patch — measured 2026-09-09 on memex,
# Deployments/memex-reconcile-20260909-pv-capacity, the first run to reach the line after #3757:
# `namespaces "memex" is forbidden: … cannot patch resource "namespaces"`. The only apply in the
# log is the Secret's.
if grep '^  <stdin>' "$_ps_log" | grep -q 'kind: Namespace'; then
  bad "pull-secret never APPLIES a namespace manifest" "$(grep '^  <stdin>' "$_ps_log" | grep 'kind: Namespace')"
else
  ok "pull-secret never APPLIES a namespace manifest (apply = patch, which the role does not grant)"
fi
# Content: the applied Secret is a dockerconfigjson for the registry, with the default username.
if [ -f "$_ps_dir/applied-secret.yaml" ] \
   && grep -q '^type: kubernetes.io/dockerconfigjson' "$_ps_dir/applied-secret.yaml" \
   && sed -n 's/^  .dockerconfigjson: //p' "$_ps_dir/applied-secret.yaml" | base64 -d | grep -q '"cr.meshweaver.cloud":{"username":"instance"'; then
  ok "the applied Secret is a dockerconfigjson for the registry, user 'instance'"
else
  bad "the applied Secret is a dockerconfigjson for the registry" "applied: $(cat "$_ps_dir/applied-secret.yaml" 2>/dev/null)"
fi
case "$_ps_out" in *"::hosting:: pull_secret=registry-pull"*) ok "pull-secret reports the Secret NAME the record must carry" ;;
  *) bad "pull-secret reports the Secret name" "said: ${_ps_out}" ;; esac
case "$_ps_out" in *"::hosting:: pull_secret_verify=true"*) ok "pull-secret reports verified=true only after reading the Secret back" ;;
  *) bad "pull-secret reports verified=true" "said: ${_ps_out}" ;; esac
rm -rf "$_ps_dir"

# An EXISTING namespace (the Reconcile / Roll case — every instance after its first Provision):
# read, left alone, and the Secret still applied into it.
_ps_dir="$(mktemp -d)"; _ps_log="$_ps_dir/calls.log"; : > "$_ps_log"
_ps_out="$(env PATH="$PS_STUBS:$PATH" HOSTING_PULL_SECRET_STUB_LOG="$_ps_log" HOSTING_PULL_SECRET_STUB_DIR="$_ps_dir" HOSTING_PULL_SECRET_STUB_NS_EXISTS=1 \
  hosting-pull-secret --namespace acme --registry cr.meshweaver.cloud --vault Systemorph --secret acme-PluginCatalog-RegistryToken --name registry-pull 2>&1)"; _ps_rc=$?
[ "$_ps_rc" -eq 0 ] && ok "pull-secret succeeds when the namespace already exists" || bad "pull-secret succeeds when the namespace already exists" "exited ${_ps_rc}: ${_ps_out}"
if grep -q '^kubectl get namespace acme' "$_ps_log" && ! grep -q '^kubectl create namespace' "$_ps_log" && ! { grep '^  <stdin>' "$_ps_log" | grep -q 'kind: Namespace'; }; then
  ok "an existing namespace is read and left alone — no create, no apply"
else
  bad "an existing namespace is read and left alone" "$(cat "$_ps_log")"
fi
grep -q 'kubectl -n acme create secret generic registry-pull' "$_ps_log" && ok "…and the Secret is still applied into it" \
  || bad "the Secret is still applied into an existing namespace" "$(cat "$_ps_log")"
rm -rf "$_ps_dir"

# The refusals a stubbed estate can reach: an absent vault object, and a pre-existing Secret of
# another type (which the kubelet would silently not use).
_ps_dir="$(mktemp -d)"; _ps_log="$_ps_dir/calls.log"; : > "$_ps_log"
refuses "pull-secret refuses when the vault object is absent" "could not read" \
  env PATH="$PS_STUBS:$PATH" HOSTING_PULL_SECRET_STUB_LOG="$_ps_log" HOSTING_PULL_SECRET_STUB_DIR="$_ps_dir" HOSTING_PULL_SECRET_STUB_AZ_FAIL=1 \
  hosting-pull-secret --namespace acme --registry cr.meshweaver.cloud --vault Systemorph --secret acme-PluginCatalog-RegistryToken
refuses "pull-secret refuses a Secret of another type" "not kubernetes.io/dockerconfigjson" \
  env PATH="$PS_STUBS:$PATH" HOSTING_PULL_SECRET_STUB_LOG="$_ps_log" HOSTING_PULL_SECRET_STUB_DIR="$_ps_dir" HOSTING_PULL_SECRET_STUB_WRONG_TYPE=1 \
  hosting-pull-secret --namespace acme --registry cr.meshweaver.cloud --vault Systemorph --secret acme-PluginCatalog-RegistryToken
refuses "pull-secret refuses a key that is not a plain token" "is not a plain token" \
  env PATH="$PS_STUBS:$PATH" HOSTING_PULL_SECRET_STUB_LOG="$_ps_log" HOSTING_PULL_SECRET_STUB_DIR="$_ps_dir" HOSTING_PULL_SECRET_STUB_KEY='mwi_has a space' \
  hosting-pull-secret --namespace acme --registry cr.meshweaver.cloud --vault Systemorph --secret acme-PluginCatalog-RegistryToken
rm -rf "$_ps_dir"
# A dry run reads nothing, applies nothing, and still reports the name — never verified=true.
_ps_out="$(env HOSTING_DRY_RUN=true hosting-pull-secret --namespace acme --registry cr.meshweaver.cloud --vault Systemorph --secret S 2>&1)"; _ps_rc=$?
[ "$_ps_rc" -eq 0 ] && ok "a dry-run pull-secret needs no az and no cluster" || bad "a dry-run pull-secret needs no az and no cluster" "exited ${_ps_rc}: ${_ps_out}"
case "$_ps_out" in *"pull_secret_verify=true"*) bad "a dry-run pull-secret never claims verified" "it did: ${_ps_out}" ;;
  *"::hosting:: pull_secret_verify=dry-run"*) ok "a dry-run pull-secret never claims verified" ;;
  *) bad "a dry-run pull-secret reports its verify state" "said: ${_ps_out}" ;; esac
unset _ps_out _ps_rc _ps_dir _ps_log _ns_line _sec_line

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

# ── run.sh signs in to Azure through Workload Identity, once, before the first step ─────────────
# Measured 2026-09-09 01:33Z: the first Provision through the lane died at step 1/14 with az's own
# "Please run 'az login'" — the webhook projects a token and sets the AZURE_* variables, but the CLI
# never reads them by itself. The stub records argv, which is how the test proves the token is
# handed to az as an argument and appears nowhere in run.sh's OUTPUT (a `+ az login …` narration
# would print it — az is deliberately not wrapped in hosting::do).
RUN_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/run" && pwd)"
_rl_dir="$(mktemp -d)"; _rl_log="$_rl_dir/calls.log"; : > "$_rl_log"
printf 'eyJ0b2tlbi1zZW50aW5lbC1ORVZFUi1QUklOVEVEIjp0cnVlfQ' > "$_rl_dir/token"
_rl_out="$(env PATH="$RUN_STUBS:$PATH" HOSTING_RUN_STUB_LOG="$_rl_log" \
  AZURE_FEDERATED_TOKEN_FILE="$_rl_dir/token" AZURE_CLIENT_ID=11111111-2222-3333-4444-555555555555 AZURE_TENANT_ID=tenant-t \
  HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d HOSTING_PLAN="$(plan "First	echo one >> ${_rl_dir}/steps")" "$BIN/run.sh" 2>&1)"; _rl_rc=$?
[ "$_rl_rc" -eq 0 ] && ok "run.sh succeeds with a workload-identity token" || bad "run.sh succeeds with a workload-identity token" "exited ${_rl_rc}: ${_rl_out}"
if grep -q '^az login --service-principal --username 11111111-2222-3333-4444-555555555555 --tenant tenant-t --federated-token eyJ0b2tlbi1zZW50aW5lbC1ORVZFUi1QUklOVEVEIjp0cnVlfQ --allow-no-subscriptions' "$_rl_log"; then
  ok "run.sh signs in as the identity with the projected token"
else
  bad "run.sh signs in as the identity with the projected token" "$(cat "$_rl_log")"
fi
case "$_rl_out" in *eyJ0b2tlbi1zZW50aW5lbC1ORVZFUi1QUklOVEVEIjp0cnVlfQ*) bad "the federated token never appears in the run's output" "it did: ${_rl_out}" ;;
  *) ok "the federated token never appears in the run's output" ;; esac
case "$_rl_out" in *"::hosting:: az_login=true"*) ok "the sign-in is reported to the mesh (az_login=true)" ;;
  *) bad "the sign-in is reported (az_login=true)" "said: ${_rl_out}" ;; esac
# Order: the sign-in precedes the first step's marker in the output.
_rl_login_pos="$(printf '%s' "$_rl_out" | grep -n 'azure     signed in' | head -1 | cut -d: -f1)"
_rl_step_pos="$(printf '%s' "$_rl_out" | grep -n '::hosting:: step=First' | head -1 | cut -d: -f1)"
if [ -n "$_rl_login_pos" ] && [ -n "$_rl_step_pos" ] && [ "$_rl_login_pos" -lt "$_rl_step_pos" ]; then
  ok "the sign-in happens before the first step"
else
  bad "the sign-in happens before the first step" "login at '${_rl_login_pos}', step at '${_rl_step_pos}'"
fi
# A federation mismatch is a refusal that names the subject/issuer to check, before any step runs.
: > "$_rl_log"; rm -f "$_rl_dir/steps"
refuses_hard "a failed sign-in stops the run before any step" "federated credential on the operator identity" \
  env PATH="$RUN_STUBS:$PATH" HOSTING_RUN_STUB_LOG="$_rl_log" HOSTING_RUN_STUB_LOGIN_FAIL=1 \
  AZURE_FEDERATED_TOKEN_FILE="$_rl_dir/token" AZURE_CLIENT_ID=c AZURE_TENANT_ID=t \
  HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d HOSTING_PLAN="$(plan "First	echo one >> ${_rl_dir}/steps")" "$BIN/run.sh"
[ ! -e "$_rl_dir/steps" ] && ok "…and the first step never ran" || bad "the first step never ran after a failed sign-in" "it did"
# A token file that is set but unreadable, and a missing tenant, are named.
refuses "a token variable without the file is named" "is not readable" \
  env PATH="$RUN_STUBS:$PATH" HOSTING_RUN_STUB_LOG="$_rl_log" AZURE_FEDERATED_TOKEN_FILE="$_rl_dir/nope" AZURE_CLIENT_ID=c AZURE_TENANT_ID=t \
  HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d HOSTING_PLAN="$(plan 'First	echo one')" "$BIN/run.sh"
refuses "a missing tenant id is named" "AZURE_TENANT_ID" \
  env -u AZURE_TENANT_ID PATH="$RUN_STUBS:$PATH" HOSTING_RUN_STUB_LOG="$_rl_log" AZURE_FEDERATED_TOKEN_FILE="$_rl_dir/token" AZURE_CLIENT_ID=c \
  HOSTING_ACTION=provision HOSTING_DEPLOYMENT=d HOSTING_PLAN="$(plan 'First	echo one')" "$BIN/run.sh"
# No token at all (a kubectl+helm-only run): not a refusal, but said, and reported as az_login=false.
emits "without a token the run says so and reports az_login=false" "::hosting:: az_login=false" \
  env -u AZURE_FEDERATED_TOKEN_FILE HOSTING_ACTION=reconcile HOSTING_DEPLOYMENT=d HOSTING_PLAN="$(plan 'First	echo one')" "$BIN/run.sh"
rm -rf "$_rl_dir"

echo
echo "── hosting-pv-resize: capacity is a record property ──────────────"
# The stub answers the command's reads from a per-scenario fixture and RECORDS the writes, so
# the decisions — never shrink, never patch a class that cannot expand, patch once, read the
# capacity back — are asserted without a cluster. Whether Azure Files actually grows is proven
# by the first real run; that it grows ONLINE is the reason the fleet's class is azurefile.
PV_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/pv-resize" && pwd)"
PV_FIXTURES="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/fixtures/pv-resize" && pwd)"
pv_resize() {  # pv_resize <scenario> <size> [env…] — runs against a fresh state dir; sets $_pv_out $_pv_rc $_pv_log
  local scenario="$1" size="$2"; shift 2
  _pv_state="$(mktemp -d)"
  _pv_out="$(env "$@" PATH="$PV_STUBS:$PATH" HOSTING_PV_FIXTURE="$PV_FIXTURES/$scenario" HOSTING_PV_STATE="$_pv_state" \
    HOSTING_RESIZE_ATTEMPTS=2 HOSTING_RESIZE_INTERVAL=0 \
    hosting-pv-resize --namespace memex --claim memex-data --size "$size" 2>&1)"; _pv_rc=$?
  _pv_log="$(cat "$_pv_state/log" 2>/dev/null || true)"
  rm -rf "$_pv_state"
}

pv_resize grows 128Gi
[ "$_pv_rc" -eq 0 ] && ok "a smaller claim is grown to the record's size" \
  || bad "a smaller claim is grown" "exited ${_pv_rc}: ${_pv_out}"
case "$_pv_log" in *'patch pvc memex-data --type merge -p {"spec":{"resources":{"requests":{"storage":"128Gi"}}}}'*) ok "the patch carries exactly the requested storage" ;;
  *) bad "the patch carries the requested storage" "kubectl saw: ${_pv_log}" ;; esac
[ "$(printf '%s\n' "$_pv_log" | grep -c 'patch pvc')" = "1" ] && ok "…and is written ONCE" \
  || bad "the patch is written once" "kubectl saw: ${_pv_log}"
case "$_pv_out" in *"::hosting:: pv_capacity=128Gi"*) ok "the capacity reported is the one READ BACK from status, not the request" ;;
  *) bad "the capacity is read back" "said: ${_pv_out}" ;; esac
case "$_pv_out" in *"::hosting:: pv_resized=1"*) ok "…and the run says it resized" ;;
  *) bad "the run says it resized" "said: ${_pv_out}" ;; esac

pv_resize already 128Gi
[ "$_pv_rc" -eq 0 ] && ok "a claim already at the record's size is a successful no-op (idempotent plan step)" \
  || bad "already-at-size is a no-op" "exited ${_pv_rc}: ${_pv_out}"
case "$_pv_log" in *"patch pvc"*) bad "a no-op writes nothing" "kubectl saw: ${_pv_log}" ;; *) ok "a no-op writes nothing" ;; esac
case "$_pv_out" in *"::hosting:: pv_resized=0"*) ok "…and says so" ;; *) bad "the no-op says so" "said: ${_pv_out}" ;; esac

# 🚨 The refusals, each BEFORE any write.
pv_resize already 64Gi
[ "$_pv_rc" -ne 0 ] && ok "a record SMALLER than the claim is refused — a volume cannot shrink" \
  || bad "shrink is refused" "exited 0: ${_pv_out}"
case "$_pv_out" in *"cannot shrink"*"Correct the record"*) ok "…naming the record as the thing to fix" ;;
  *) bad "the shrink refusal names the fix" "said: ${_pv_out}" ;; esac
case "$_pv_log" in *"patch pvc"*) bad "a refused shrink writes nothing" "kubectl saw: ${_pv_log}" ;; *) ok "a refused shrink writes nothing" ;; esac

pv_resize no-expansion 128Gi
[ "$_pv_rc" -ne 0 ] && ok "a class without allowVolumeExpansion is refused" \
  || bad "non-expandable class is refused" "exited 0: ${_pv_out}"
case "$_pv_out" in *"does not allow volume expansion"*) ok "…saying which class and why" ;;
  *) bad "the class refusal says why" "said: ${_pv_out}" ;; esac
case "$_pv_log" in *"patch pvc"*) bad "a refused class writes nothing" "kubectl saw: ${_pv_log}" ;; *) ok "a refused class writes nothing" ;; esac

pv_resize absent 128Gi
[ "$_pv_rc" -ne 0 ] && ok "an absent claim is refused — this command never creates one" \
  || bad "absent claim is refused" "exited 0: ${_pv_out}"
case "$_pv_out" in *"never creates one"*) ok "…and says creation is the chart's job" ;;
  *) bad "the absent refusal points at the chart" "said: ${_pv_out}" ;; esac

pv_resize pending 128Gi
[ "$_pv_rc" -ne 0 ] && ok "an unbound claim is refused — nothing behind it to grow" \
  || bad "unbound claim is refused" "exited 0: ${_pv_out}"

# The two outcomes of waiting that are not "grown": a filesystem resize the next pod completes
# (reported, exit 0), and a provisioner that never acted (stuck, exit non-zero — never a pass).
pv_resize filesystem-pending 128Gi
[ "$_pv_rc" -eq 0 ] && ok "a FileSystemResizePending claim is reported as pending, not as failed" \
  || bad "filesystem-pending is reported" "exited ${_pv_rc}: ${_pv_out}"
case "$_pv_out" in *"::hosting:: pv_resize=filesystem-pending"*) ok "…on its own ::hosting:: line" ;;
  *) bad "filesystem-pending has a machine line" "said: ${_pv_out}" ;; esac

pv_resize stuck 128Gi
[ "$_pv_rc" -ne 0 ] && ok "a claim that never grows is a FAILED step, not a timed-out pass" \
  || bad "a stuck resize fails" "exited 0: ${_pv_out}"
case "$_pv_out" in *"still reports 16Gi after 2 attempt"*) ok "…naming what it last read and how long it waited" ;;
  *) bad "the stuck failure names its reading" "said: ${_pv_out}" ;; esac

# A dry run reads for real, narrates the patch and writes nothing.
pv_resize grows 128Gi HOSTING_DRY_RUN=true
[ "$_pv_rc" -eq 0 ] && ok "a dry run succeeds" || bad "a dry run succeeds" "exited ${_pv_rc}: ${_pv_out}"
case "$_pv_log" in *"patch pvc"*) bad "a dry run writes nothing" "kubectl saw: ${_pv_log}" ;; *) ok "a dry run writes nothing" ;; esac
case "$_pv_out" in *"DRY-RUN would run"*"patch pvc"*) ok "…and narrates the patch it would write" ;;
  *) bad "a dry run narrates the patch" "said: ${_pv_out}" ;; esac

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
  # The record's volumes reach helm as the release's persistence values; a claim below its declared
  # size is a finding of its own. content matches (64Gi = 64Gi) and postgres names no claim, so
  # exactly ONE entry — and it carries the two sizes, never a value of anything.
  check_report "a claim below the record's size is a finding" \
    '.volumeCapacityBelowRecord == [{"volume":"data","claimName":"memex-data","declared":"128Gi","live":"16Gi"}]'
  check_report "…counted in the total"                 '.findingCount == (.envLiveOnly|length) + (.envManifestOnly|length) + (.envFromLiveOnly|length) + (.volumesLiveOnly|length) + (.mountsLiveOnly|length) + (.containersLiveOnly|length) + (.podAnnotationsLiveOnly|length) + (.podSpecDiffs|length) + ([.configMaps[] | (.liveOnlyKeys|length) + (.manifestOnlyKeys|length) + (.differingKeys|length) + (if .missingLive then 1 else 0 end)] | add) + ([.unmanagedObjects[].names | length] | add) + (.unmanagedSecrets|length) + (.plainSecretEntries|length) + (.volumeCapacityBelowRecord|length)'
fi
case "$out" in *"volume below record     data (memex-data): live=16Gi record=128Gi"*) ok "the human summary names the claim and both sizes" ;;
  *) bad "the human summary names the claim" "said: ${out}" ;; esac
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
# The clean fixture declares data at 16Gi and its chart-rendered claim holds 16Gi: at-size is not
# a finding, and the category is PRESENT and empty — a reader must never mistake "absent" for "0".
if printf '%s' "$report" | jq -e '.volumeCapacityBelowRecord == []' >/dev/null 2>&1; then
  ok "a claim at its declared size is not a finding"
else
  bad "a claim at its declared size is not a finding" "report: $(printf '%s' "$report" | jq -c .volumeCapacityBelowRecord 2>/dev/null)"
fi

# Read-only: a dry run audits for real — same report, same verdict.
emits "a dry run still audits (read-only)" "::hosting:: audit_verdict=clean" \
  env HOSTING_DRY_RUN=true PATH="$STUBS:$PATH" HOSTING_AUDIT_FIXTURE="$FIXTURES/clean" \
  hosting-audit --namespace memex --release memex

# ── hosting-deploy adopts what the RECORD renders and the cluster already holds ─────────────────
# Measured 2026-09-09 01:20Z on memex: the first Reconcile to reach helm failed with
#   UPGRADE FAILED: … SecretProviderClass "memex-kv" … exists and cannot be imported into the
#   current release: invalid ownership metadata
# because that object was hand-applied before any release and only the record renders it. The
# stubs play a three-resource estate — one absent, one owned, one live without ownership — and
# record every call in order, so the assertions are about WHAT was stamped and WHEN.
echo
echo "── hosting-deploy: adoption before helm ─────────────────────────"
DP_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/deploy" && pwd)"
DP_FIXTURES="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/fixtures/deploy" && pwd)"
_dp_dir="$(mktemp -d)"; cp -R "$DP_FIXTURES/." "$_dp_dir/"; _dp_log="$_dp_dir/calls.log"; : > "$_dp_log"
_dp_vals="$_dp_dir/values.yaml"; printf '# GENERATED from the Hosting/Deployment record by HelmValues\nreplicas:\n  portal: 1\n' > "$_dp_vals"
_dp_run() { env PATH="$DP_STUBS:$PATH" HOSTING_CHART=/tmp HOSTING_DEPLOY_FIXTURE="$_dp_dir" HOSTING_DEPLOY_STUB_LOG="$_dp_log" \
  hosting-deploy --namespace memex --release memex --database memex --values "$_dp_vals" --image cr.example.test/memex-portal-ai:1 2>&1; }
_dp_out="$(_dp_run)"; _dp_rc=$?
[ "$_dp_rc" -eq 0 ] && ok "deploy succeeds against the stubbed estate" || bad "deploy succeeds against the stubbed estate" "exited ${_dp_rc}: ${_dp_out}"
case "$_dp_out" in *"::hosting:: adopted=1"*) ok "exactly the unowned live resource is adopted (adopted=1)" ;;
  *) bad "exactly the unowned live resource is adopted" "said: ${_dp_out}" ;; esac
case "$_dp_out" in *"adoption  1 adopted, 1 already owned, 1 to be created"*) ok "the three outcomes are counted and logged" ;;
  *) bad "the three outcomes are counted and logged" "said: ${_dp_out}" ;; esac
_spc='secretproviderclass.secrets-store.csi.x-k8s.io/memex-kv'
if grep -q "^kubectl -n memex annotate --overwrite ${_spc} meta.helm.sh/release-name=memex meta.helm.sh/release-namespace=memex" "$_dp_log" \
   && grep -q "^kubectl -n memex label --overwrite ${_spc} app.kubernetes.io/managed-by=Helm" "$_dp_log"; then
  ok "the unowned SecretProviderClass gets this release's ownership annotations and label"
else
  bad "the unowned SecretProviderClass gets ownership" "$(cat "$_dp_log")"
fi
grep -q 'annotate --overwrite configmap/\|annotate --overwrite deployment.apps/' "$_dp_log" \
  && bad "an owned or absent resource is never touched" "$(grep 'annotate' "$_dp_log")" \
  || ok "an owned or absent resource is never touched"
_adopt_line="$(grep -n "annotate --overwrite ${_spc}" "$_dp_log" | head -1 | cut -d: -f1)"
_helm_line="$(grep -n '^helm upgrade memex' "$_dp_log" | head -1 | cut -d: -f1)"
if [ -n "$_adopt_line" ] && [ -n "$_helm_line" ] && [ "$_adopt_line" -lt "$_helm_line" ]; then
  ok "adoption happens BEFORE helm upgrade"
else
  bad "adoption happens BEFORE helm upgrade" "adopt at '${_adopt_line}', helm at '${_helm_line}' in: $(cat "$_dp_log")"
fi
# Idempotent: the stub rewrote the live object with its ownership; a second run adopts nothing.
: > "$_dp_log"; _dp_out="$(_dp_run)"
case "$_dp_out" in *"::hosting:: adopted=0"*) ok "a second run adopts nothing (the estate is now owned)" ;;
  *) bad "a second run adopts nothing" "said: ${_dp_out}" ;; esac
# 🚨 Never a takeover: an object owned by ANOTHER release is a refusal, and helm is never reached.
jq '.metadata.labels["app.kubernetes.io/managed-by"]="Helm" | .metadata.annotations["meta.helm.sh/release-name"]="other" | .metadata.annotations["meta.helm.sh/release-namespace"]="memex"' \
  "$DP_FIXTURES/live/secretproviderclass.secrets-store.csi.x-k8s.io_memex-kv.json" > "$_dp_dir/live/secretproviderclass.secrets-store.csi.x-k8s.io_memex-kv.json"
: > "$_dp_log"; _dp_out="$(_dp_run)"; _dp_rc=$?
if [ "$_dp_rc" -ne 0 ] && printf '%s' "$_dp_out" | grep -q "owned by release 'other'" && ! grep -q '^helm upgrade' "$_dp_log"; then
  ok "an object owned by another release is refused, and helm never runs"
else
  bad "an object owned by another release is refused" "rc=${_dp_rc} out: ${_dp_out} log: $(cat "$_dp_log")"
fi
rm -rf "$_dp_dir"

# ── hosting-deploy APPLIES; it never waits, and never rolls back a good upgrade ─────────────────
# 🚨 Measured 2026-09-09 02:35-02:51Z on memex (#3782). `--atomic --wait --timeout 15m` DESTROYED a
# correct upgrade: revision 44 had landed with the record's full render and was inside its own
# startup gate (the record budgets 10800s for it) when helm's fixed fifteen minutes expired, and
# --atomic reverted it to revision 43. Slow and broken are indistinguishable to a timer; only
# something watching the rollout can tell them apart. helm now applies and the CALLER observes,
# which is why the applied revision has to come back out.
#
# These cases are that decision, pinned. The first is written against the ARGUMENT LIST rather than
# an outcome on purpose: `--atomic` reappearing is a silent regression that no stubbed run can
# fail on, because a stub always "succeeds" — the flag itself is the defect.
echo
echo "── hosting-deploy: applies without waiting, reports the revision ─"
_ap_dir="$(mktemp -d)"; cp -R "$DP_FIXTURES/." "$_ap_dir/"; _ap_log="$_ap_dir/calls.log"; : > "$_ap_log"
_ap_vals="$_ap_dir/values.yaml"; printf '# GENERATED from the Hosting/Deployment record by HelmValues\nreplicas:\n  portal: 1\n' > "$_ap_vals"
_ap_run() { env PATH="$DP_STUBS:$PATH" HOSTING_CHART=/tmp HOSTING_DEPLOY_FIXTURE="$_ap_dir" HOSTING_DEPLOY_STUB_LOG="$_ap_log" "$@" \
  hosting-deploy --namespace memex --release memex --database memex --values "$_ap_vals" --image cr.example.test/memex-portal-ai:1 2>&1; }
_ap_out="$(_ap_run env)"; _ap_rc=$?
_ap_upgrade="$(grep '^helm upgrade' "$_ap_log" | head -1)"
if [ "$_ap_rc" -eq 0 ] && [ -n "$_ap_upgrade" ] \
   && ! printf '%s' "$_ap_upgrade" | grep -q -- '--atomic' \
   && ! printf '%s' "$_ap_upgrade" | grep -q -- '--wait' \
   && ! printf '%s' "$_ap_upgrade" | grep -q -- '--timeout'; then
  ok "helm upgrade carries no --atomic, no --wait and no --timeout"
else
  bad "helm upgrade carries no --atomic/--wait/--timeout" "rc=${_ap_rc} upgrade line: '${_ap_upgrade}' out: ${_ap_out}"
fi
case "$_ap_out" in *"::hosting:: helm_revision=42"*) ok "the applied revision is reported for the caller to observe" ;;
  *) bad "the applied revision is reported" "said: ${_ap_out}" ;; esac
# The revision is READ BACK from helm, not guessed — and read AFTER the apply, or it names the
# revision the upgrade replaced.
_ap_up_line="$(grep -n '^helm upgrade' "$_ap_log" | head -1 | cut -d: -f1)"
_ap_st_line="$(grep -n '^helm status' "$_ap_log" | tail -1 | cut -d: -f1)"
if [ -n "$_ap_up_line" ] && [ -n "$_ap_st_line" ] && [ "$_ap_st_line" -gt "$_ap_up_line" ]; then
  ok "the revision is read back AFTER the apply, never guessed"
else
  bad "the revision is read back after the apply" "upgrade at '${_ap_up_line}', status at '${_ap_st_line}' in: $(cat "$_ap_log")"
fi
# Success here means APPLIED, not rolled out — and it must say so, or a caller reads a tick as a
# finished rollout, which is precisely what #3782 asks never to report.
case "$_ap_out" in *"NOT yet rolled out"*) ok "the log states the rollout has NOT happened yet" ;;
  *) bad "the log states the rollout has not happened yet" "said: ${_ap_out}" ;; esac

# A FAILED upgrade must not claim a rollback that no longer happens, and must name the way forward.
: > "$_ap_log"
_ap_out="$(_ap_run env HOSTING_DEPLOY_STUB_UPGRADE_FAILS=true)"; _ap_rc=$?
if [ "$_ap_rc" -ne 0 ] && printf '%s' "$_ap_out" | grep -q 'was NOT rolled back' \
   && printf '%s' "$_ap_out" | grep -q 'helm history' \
   && ! printf '%s' "$_ap_out" | grep -q 'rolled back by --atomic'; then
  ok "a failed upgrade says it was NOT rolled back and names helm history"
else
  bad "a failed upgrade reports honestly" "rc=${_ap_rc} out: ${_ap_out}"
fi

# 🚨 An apply whose revision cannot be read is a REFUSAL, not a quiet success. Without this the
# script would report a release the caller has no handle on, and "observed" would degrade back to
# "assumed" — the exact regression this change exists to prevent.
printf '{"name":"memex","info":{"status":"deployed"}}\n' > "$_ap_dir/status.json"; : > "$_ap_log"
_ap_out="$(_ap_run env)"; _ap_rc=$?
if [ "$_ap_rc" -ne 0 ] && printf '%s' "$_ap_out" | grep -q 'returned no revision'; then
  ok "an apply with no readable revision is refused, not reported as done"
else
  bad "an apply with no readable revision is refused" "rc=${_ap_rc} out: ${_ap_out}"
fi
rm -rf "$_ap_dir"

# ── hosting-deploy refuses BEFORE helm when the identity cannot write a rendered kind ───────────
# Measured 2026-09-09 01:59Z on memex: helm died on `poddisruptionbudgets.policy is forbidden`
# and its --atomic rollback erred too. The preflight asks `kubectl auth can-i` per rendered kind
# and names every denial in ONE refusal, with helm never reached.
echo
echo "── hosting-deploy: writable-kinds preflight and release state ───"
_pf_dir="$(mktemp -d)"; cp -R "$DP_FIXTURES/." "$_pf_dir/"; _pf_log="$_pf_dir/calls.log"; : > "$_pf_log"
_pf_vals="$_pf_dir/values.yaml"; printf '# GENERATED from the Hosting/Deployment record by HelmValues\nreplicas:\n  portal: 1\n' > "$_pf_vals"
printf 'create secretproviderclass.secrets-store.csi.x-k8s.io\npatch deployment.apps\n' > "$_pf_dir/denied.txt"
_pf_out="$(env PATH="$DP_STUBS:$PATH" HOSTING_CHART=/tmp HOSTING_DEPLOY_FIXTURE="$_pf_dir" HOSTING_DEPLOY_STUB_LOG="$_pf_log" \
  hosting-deploy --namespace memex --release memex --database memex --values "$_pf_vals" --image cr.example.test/memex-portal-ai:1 2>&1)"; _pf_rc=$?
if [ "$_pf_rc" -ne 0 ] && printf '%s' "$_pf_out" | grep -q 'denied:.*create:secretproviderclass.secrets-store.csi.x-k8s.io' \
   && printf '%s' "$_pf_out" | grep -q 'denied:.*patch:deployment.apps' && ! grep -q '^helm upgrade' "$_pf_log"; then
  ok "every denied verb:kind is named in ONE refusal, and helm never runs"
else
  bad "denied kinds are named before helm" "rc=${_pf_rc} out: ${_pf_out} log: $(cat "$_pf_log")"
fi
# A release helm cannot upgrade (pending-*) is refused by name before helm.
rm -f "$_pf_dir/denied.txt"; printf '{"name":"memex","info":{"status":"pending-upgrade"},"version":41}\n' > "$_pf_dir/status.json"; : > "$_pf_log"
_pf_out="$(env PATH="$DP_STUBS:$PATH" HOSTING_CHART=/tmp HOSTING_DEPLOY_FIXTURE="$_pf_dir" HOSTING_DEPLOY_STUB_LOG="$_pf_log" \
  hosting-deploy --namespace memex --release memex --database memex --values "$_pf_vals" --image cr.example.test/memex-portal-ai:1 2>&1)"; _pf_rc=$?
if [ "$_pf_rc" -ne 0 ] && printf '%s' "$_pf_out" | grep -q "is 'pending-upgrade'" && ! grep -q '^helm upgrade' "$_pf_log"; then
  ok "a pending-* release is refused by name, and helm never runs"
else
  bad "a pending-* release is refused by name" "rc=${_pf_rc} out: ${_pf_out}"
fi
rm -rf "$_pf_dir"

# ── every kind the CHART renders is writable by the operator's ClusterRole ─────────────────────
# core #3774 rendered a PodDisruptionBudget; the role could only read them; the next Reconcile of
# memex failed inside helm. A chart change that renders a new kind lands with its grant, or this
# case is red naming the kind and the rule.
ck_out="$(bash "$(dirname -- "${BASH_SOURCE[0]}")/check-chart-kinds-granted.sh" 2>&1)"; ck_rc=$?
if [ "$ck_rc" -eq 0 ]; then
  ok "every kind the chart renders is writable by operator-rbac.yaml ($(printf '%s' "$ck_out" | tail -1 | sed 's/^check-chart-kinds-granted: //'))"
else
  bad "every kind the chart renders is writable by operator-rbac.yaml" "$ck_out"
fi

# ── every kubectl verb+resource in bin/ is GRANTED by the operator's ClusterRole ─────────────────
# The manifest lives three directories away from the scripts and is reviewed separately; twice a
# script reached main without its grant (storageclasses for pv-resize — failed the first Reconcile
# through the fixed operator on memex, 2026-09-09, step 1/6 Forbidden; persistentvolumes for
# pv-purge — never granted). The check names the script, the verb, the resource and the rule to
# add. Its manifest input is REQUIRED: absent (as in an image without deploy/aks/manifests) it
# exits 2 and this case is red, never skipped — mount the manifests dir and set HOSTING_RBAC_MANIFEST.
rbac_out="$(bash "$(dirname -- "${BASH_SOURCE[0]}")/check-rbac-coverage.sh" 2>&1)"; rbac_rc=$?
if [ "$rbac_rc" -eq 0 ]; then
  ok "every kubectl verb+resource in bin/ is granted by operator-rbac.yaml ($(printf '%s' "$rbac_out" | tail -1 | sed 's/^check-rbac-coverage: //'))"
else
  bad "every kubectl verb+resource in bin/ is granted by operator-rbac.yaml" "$rbac_out"
fi

echo
echo "─────────────────────────────────────────────────────────────────"
echo "${pass} passed, ${fail} failed"
[ "$fail" -eq 0 ] || exit 1

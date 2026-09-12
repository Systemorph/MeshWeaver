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
# MeshWeaver#2802 — a rotation that does not know WHICH registry holds the instance is the defect;
# an older plan that does not pass it stops here, before anything is read, minted or stored.
refuses_hard "kv-rotate needs --registry-url" "missing required flag --registry-url" \
  hosting-kv-rotate --vault V --namespace n --synced-secret s
refuses_hard "kv-rotate needs --instance-id"  "missing required flag --instance-id" \
  hosting-kv-rotate --vault V --namespace n --synced-secret s --registry-url https://registry.test
refuses_hard "kv-rotate refuses a registry URL that is not https" "is not an https base URL" \
  hosting-kv-rotate --vault V --namespace n --synced-secret s --registry-url http://registry.test --instance-id memex
refuses_hard "kv-rotate refuses a registry URL carrying a path"   "is not an https base URL" \
  hosting-kv-rotate --vault V --namespace n --synced-secret s --registry-url 'https://registry.test/$(id)' --instance-id memex
refuses_hard "kv-rotate object with a metacharacter"               "is not a plain name" \
  hosting-kv-rotate --vault V --namespace n --synced-secret s --object 'o;id' --registry-url https://registry.test --instance-id memex
refuses "registry-key needs a verb"               "first argument must be 'commit' or 'revoke'" hosting-registry-key
refuses "registry-key commit needs --instance-id" "missing required flag --instance-id" \
  hosting-registry-key commit --registry-url https://registry.test --namespace n --synced-secret s
refuses "registry-key revoke needs --live-secret" "missing required flag --live-secret" \
  hosting-registry-key revoke --registry-url https://registry.test --namespace n --secret s --key k
refuses_hard "registry-key refuses a registry URL that is not https" "is not an https base URL" \
  hosting-registry-key commit --registry-url http://registry.test --namespace n --synced-secret s --instance-id memex
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
_rot_out="$(env HOSTING_DRY_RUN=true hosting-kv-rotate --vault V --namespace n --prefix memex- --synced-secret s \
  --registry-url https://registry.test --instance-id memex 2>&1 || true)"
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
echo "── hosting-signin-app: the client secret is kept, added never rotated, never shown ──"
# MeshWeaver.Plugins#1719 — runbook step 1 registered the Entra app and minted its secret by hand.
# The stub answers Graph and the vault from a state and — like the real az — prints a minted
# secret on STDERR too, so the no-leak arm proves the script discards it. 🚨 The stub is FIRST on
# PATH for every case: a laptop with a real, logged-in az would otherwise read a real vault.
SA_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/signin-app" && pwd)"
sa() {  # sa [env…] -- <args…>
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  _sa_state="$(mktemp -d)"
  _sa_out="$(env "${envs[@]}" PATH="$SA_STUBS:$PATH" HOSTING_SA_STATE="$_sa_state" hosting-signin-app "$@" 2>&1)"; _sa_rc=$?
  _sa_log="$(cat "$_sa_state/az.log" 2>/dev/null || true)"
}
SA_ARGS=(--name acme --host acme.meshweaver.cloud --vault Systemorph --object acme-Authentication-Microsoft-ClientSecret)
SA_ID=66d36350-397d-420f-97b2-ae173fc97d05

# Present + app readable + redirect on it → kept, verified.
sa HOSTING_SA_EXISTING=acme-Authentication-Microsoft-ClientSecret -- "${SA_ARGS[@]}" --client-id $SA_ID
[ "$_sa_rc" -eq 0 ] && ok "a PRESENT client secret is kept and the app verified" || bad "present + verified" "exited ${_sa_rc}: ${_sa_out}"
case "$_sa_log" in *"credential reset"*|*"secret set"*) bad "…nothing minted, nothing written" "az saw: ${_sa_log}" ;; *) ok "…nothing minted, nothing written" ;; esac
case "$_sa_out" in *"::hosting:: signin_secret=kept"*"::hosting:: signin_app_verify=true"*) ok "…reported kept + verify=true" ;; *) bad "kept facts" "said: ${_sa_out}" ;; esac
rm -rf "$_sa_state"

# Present, app unreadable (no Graph right) → kept, verify UNKNOWN, not red, hand command named.
sa HOSTING_SA_EXISTING=acme-Authentication-Microsoft-ClientSecret HOSTING_SA_GRAPH_DENIED=1 -- "${SA_ARGS[@]}" --client-id $SA_ID
[ "$_sa_rc" -eq 0 ] && ok "an identity without Graph read keeps the secret and does NOT fail the provision" || bad "no Graph read is not red" "exited ${_sa_rc}: ${_sa_out}"
case "$_sa_out" in *"::hosting:: signin_app_verify=unknown"*"az ad app show --id ${SA_ID} --query web.redirectUris"*|*"az ad app show --id ${SA_ID} --query web.redirectUris"*"::hosting:: signin_app_verify=unknown"*) ok "…reports verify=unknown and the hand command" ;; *) bad "unknown verify" "said: ${_sa_out}" ;; esac
rm -rf "$_sa_state"

# Present, app readable, redirect MISSING → RED naming az ad app update.
sa HOSTING_SA_EXISTING=acme-Authentication-Microsoft-ClientSecret HOSTING_SA_REDIRECTS="https://other.example.test/signin-microsoft" -- "${SA_ARGS[@]}" --client-id $SA_ID
[ "$_sa_rc" -ne 0 ] && ok "a readable app WITHOUT the redirect URI is a failed step (AADSTS50011 otherwise)" || bad "missing redirect fails" "exited 0: ${_sa_out}"
case "$_sa_out" in *"az ad app update --id ${SA_ID} --web-redirect-uris https://acme.meshweaver.cloud/signin-microsoft"*) ok "…naming the exact hand command" ;; *) bad "names az ad app update" "said: ${_sa_out}" ;; esac
rm -rf "$_sa_state"

# Present, no client id on the record → kept, verify skipped.
sa HOSTING_SA_EXISTING=acme-Authentication-Microsoft-ClientSecret -- "${SA_ARGS[@]}"
[ "$_sa_rc" -eq 0 ] && ok "present with no client id on the record is kept (verify skipped)" || bad "present no id" "exited ${_sa_rc}: ${_sa_out}"
case "$_sa_out" in *"signin_app_verify=skipped"*) ok "…and says so" ;; *) bad "skipped fact" "said: ${_sa_out}" ;; esac
rm -rf "$_sa_state"

# Absent + client id → a credential is ADDED (--append), stored through --file, never printed.
sa -- "${SA_ARGS[@]}" --client-id $SA_ID
[ "$_sa_rc" -eq 0 ] && ok "an ABSENT secret with a named app gets a credential added" || bad "absent + id mints" "exited ${_sa_rc}: ${_sa_out}"
case "$_sa_log" in *"ad app credential reset --id ${SA_ID} --append --display-name acme --years 1"*) ok "…with --append: nothing existing is rotated" ;; *) bad "append" "az saw: ${_sa_log}" ;; esac
[ "$(cat "$_sa_state/set.acme-Authentication-Microsoft-ClientSecret" 2>/dev/null)" = "fake-client-secret-NEVER-PRINTED" ] && ok "…the secret is stored under the object, byte-for-byte, no trailing newline" || bad "secret stored" "vault got: '$(cat "$_sa_state/set.acme-Authentication-Microsoft-ClientSecret" 2>/dev/null)'"
case "$_sa_out" in *NEVER-PRINTED*) bad "signin-app never prints the secret (az's stderr echo is discarded)" "it did: ${_sa_out}" ;; *) ok "signin-app never prints the secret (az's stderr echo is discarded)" ;; esac
case "$_sa_log" in *NEVER-PRINTED*) bad "…and never puts it on an az command line" "az saw: ${_sa_log}" ;; *) ok "…and never puts it on an az command line" ;; esac
case "$_sa_out" in *"::hosting:: signin_secret=created"*"::hosting:: signin_secret_expires=2027-09-12T00:00:00Z"*"::hosting:: signin_app_verify=true"*) ok "…reported created + expiry + verified" ;; *) bad "created facts" "said: ${_sa_out}" ;; esac
rm -rf "$_sa_state"

# Absent + client id, Graph denied → RED with the exact hand commands; nothing written.
sa HOSTING_SA_GRAPH_DENIED=1 -- "${SA_ARGS[@]}" --client-id $SA_ID
[ "$_sa_rc" -ne 0 ] && ok "without Application.ReadWrite.OwnedBy the mint is a RED step" || bad "denied mint is red" "exited 0: ${_sa_out}"
case "$_sa_out" in *"Application.ReadWrite.OwnedBy"*"az ad app credential reset --id ${SA_ID} --append --display-name acme --years 1 --query password -o tsv > <file> 2>/dev/null; az keyvault secret set --vault-name Systemorph --name acme-Authentication-Microsoft-ClientSecret --file <file>"*) ok "…naming the right and the exact hand commands" ;; *) bad "denied message" "said: ${_sa_out}" ;; esac
[ ! -f "$_sa_state/set.acme-Authentication-Microsoft-ClientSecret" ] && ok "…and writes nothing" || bad "denied writes nothing" "it wrote"
rm -rf "$_sa_state"

# Absent + NO client id → RED: registering the app is the hand step, with the runbook's commands.
sa -- "${SA_ARGS[@]}"
[ "$_sa_rc" -ne 0 ] && ok "absent secret and no app on the record is a RED step — registering the app is not automated" || bad "no app is red" "exited 0: ${_sa_out}"
case "$_sa_out" in *"az ad app create --display-name \"acme Portal (acme.meshweaver.cloud)\" --sign-in-audience AzureADMultipleOrgs --web-redirect-uris \"https://acme.meshweaver.cloud/signin-microsoft\""*"set signIn.microsoftClientId"*) ok "…naming the runbook's exact commands and the record field to set" ;; *) bad "register-by-hand message" "said: ${_sa_out}" ;; esac
case "$_sa_log" in *"ad app"*) bad "…without calling Graph" "az saw: ${_sa_log}" ;; *) ok "…without calling Graph" ;; esac
rm -rf "$_sa_state"

# Vault refuses the write after the mint → RED, names the credential to remove, no secret printed.
sa HOSTING_SA_SET_FAIL=1 -- "${SA_ARGS[@]}" --client-id $SA_ID
[ "$_sa_rc" -ne 0 ] && ok "a vault that refuses the write fails the step" || bad "set fail" "exited 0"
case "$_sa_out" in *NEVER-PRINTED*) bad "…without printing the secret on the failure path" "it did: ${_sa_out}" ;; *) ok "…without printing the secret on the failure path" ;; esac
rm -rf "$_sa_state"

refuses_hard "signin-app needs --host"                 "missing required flag --host"   hosting-signin-app --name a --vault V --object o
refuses_hard "signin-app needs --object"               "missing required flag --object" hosting-signin-app --name a --host a.test --vault V
refuses_hard "signin-app refuses a host with a space"  "is not a hostname"              hosting-signin-app --name a --host 'a.test x' --vault V --object o
refuses_hard "signin-app refuses a client id that is not a GUID" "is not an Entra application"  hosting-signin-app --name a --host a.test --vault V --object o --client-id 'x;id'
refuses_hard "signin-app refuses a name with a metacharacter" "is not a plain name"     hosting-signin-app --name 'a`id`' --host a.test --vault V --object o
refuses_hard "signin-app rejects unknown flags"        "unknown argument"               hosting-signin-app --name a --host a.test --vault V --object o --nope 1

# Dry runs: present → kept + verify dry-run; absent + id → would-create; neither Graph nor vault written.
sa HOSTING_DRY_RUN=true HOSTING_SA_EXISTING=acme-Authentication-Microsoft-ClientSecret -- "${SA_ARGS[@]}" --client-id $SA_ID
[ "$_sa_rc" -eq 0 ] && ok "a dry run over a present secret succeeds" || bad "dry present" "exited ${_sa_rc}"
case "$_sa_out" in *"signin_secret=kept"*"signin_app_verify=dry-run"*) ok "…reports kept + verify=dry-run" ;; *) bad "dry present facts" "said: ${_sa_out}" ;; esac
rm -rf "$_sa_state"
sa HOSTING_DRY_RUN=true -- "${SA_ARGS[@]}" --client-id $SA_ID
[ "$_sa_rc" -eq 0 ] && ok "a dry run over an absent secret succeeds" || bad "dry absent" "exited ${_sa_rc}: ${_sa_out}"
case "$_sa_log" in *"ad app"*|*"secret set"*) bad "…and touches neither Graph nor the vault" "az saw: ${_sa_log}" ;; *) ok "…and touches neither Graph nor the vault" ;; esac
case "$_sa_out" in *"signin_secret=would-create"*) ok "…reporting would-create" ;; *) bad "would-create" "said: ${_sa_out}" ;; esac
rm -rf "$_sa_state"
unset _sa_out _sa_rc _sa_log _sa_state

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
echo "── hosting-inline-env-retire: a retired shadow leaves, onto the same value only ──"
# The stub answers the Deployment, Secret and ConfigMap reads from a per-scenario fixture (falling
# back to fixtures/inline-env/base) and RECORDS the patch, so the decisions — refuse mid-rollout,
# refuse a sole source, refuse a shadow the pod would not read, refuse DIFFER, remove from EVERY
# container in one guarded patch, read it back — are asserted without a cluster. Every fixture value
# is an obviously fake placeholder, and the no-leak arms prove none of them is ever printed, patched
# or passed in an argv. What is NOT proven here: that the API server honours a JSON-patch `test` op
# on resourceVersion — that is Kubernetes' contract, and the first real run is what shows it.
#
# 🚨 EVERY invocation below — the argument refusals included — runs with the stub FIRST on PATH. A
# laptop running this suite may carry a real kubectl with a live context, and a guard that failed to
# stop would otherwise read (or patch) whatever cluster that context names.
IE_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/inline-env" && pwd)"
IE_FIXTURES="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/fixtures/inline-env" && pwd)"
IE_TOKEN="PluginCatalog__RegistryToken=memex-portal-keyvault"
_ie_args_state="$(mktemp -d)"
IE_ENV=(env "PATH=$IE_STUBS:$PATH" "HOSTING_IE_FIXTURE=$IE_FIXTURES/base" "HOSTING_IE_BASE=$IE_FIXTURES/base" "HOSTING_IE_STATE=$_ie_args_state")
ie() {  # ie <scenario> "<KEY=source> [KEY=source…]" [env…] — sets $_ie_out $_ie_rc $_ie_log $_ie_patch
  local scenario="$1" pairs="$2" pair
  shift 2
  local retire=()
  for pair in $pairs; do retire+=(--retire "$pair"); done
  _ie_state="$(mktemp -d)"
  _ie_out="$(env "$@" PATH="$IE_STUBS:$PATH" HOSTING_IE_FIXTURE="$IE_FIXTURES/$scenario" HOSTING_IE_BASE="$IE_FIXTURES/base" \
    HOSTING_IE_STATE="$_ie_state" hosting-inline-env-retire --namespace memex "${retire[@]}" 2>&1)"; _ie_rc=$?
  _ie_log="$(cat "$_ie_state/log" 2>/dev/null || true)"
  _ie_patch="$(cat "$_ie_state/patch" 2>/dev/null || true)"
  rm -rf "$_ie_state"
}
ie_no_patch() {  # the run wrote nothing to the Deployment
  case "$_ie_log" in *" patch deployment "*) bad "$1" "kubectl saw a patch: ${_ie_log}" ;; *) ok "$1" ;; esac
}
ie_no_value() {  # no fixture value in the output, the patch or any argv kubectl saw
  case "${_ie_out}${_ie_patch}${_ie_log}" in
    *fake-inline-registry-token*|*fake-chart-secret-token*|*fake-rotated-registry-token*|*registry.example.invalid*)
      bad "$1" "a value appeared — out: ${_ie_out} patch: ${_ie_patch}" ;;
    *) ok "$1" ;;
  esac
}

refuses "inline-env-retire needs --namespace"     "missing required flag --namespace" \
  "${IE_ENV[@]}" hosting-inline-env-retire --retire "$IE_TOKEN"
refuses "inline-env-retire needs a --retire"      "missing required flag --retire" \
  "${IE_ENV[@]}" hosting-inline-env-retire --namespace memex
refuses "inline-env-retire rejects unknown flags" "unknown argument" \
  "${IE_ENV[@]}" hosting-inline-env-retire --namespace memex --retire "$IE_TOKEN" --nope 1
refuses "inline-env-retire needs KEY=<source>"    "is not KEY=<shadowed source>" \
  "${IE_ENV[@]}" hosting-inline-env-retire --namespace memex --retire PluginCatalog__RegistryToken
refuses "inline-env-retire refuses a blank shadow — that entry is the sole source" "SOLE source" \
  "${IE_ENV[@]}" hosting-inline-env-retire --namespace memex --retire PluginCatalog__RegistryToken=
refuses "inline-env-retire refuses a key named twice" "named twice" \
  "${IE_ENV[@]}" hosting-inline-env-retire --namespace memex --retire "$IE_TOKEN" --retire "$IE_TOKEN"
refuses_hard "inline-env-retire key with a metacharacter"    "is not a plain environment-variable name" \
  "${IE_ENV[@]}" hosting-inline-env-retire --namespace memex --retire 'K;kubectl delete deploy --all=s'
refuses_hard "inline-env-retire shadow with a metacharacter" "is not a plain name" \
  "${IE_ENV[@]}" hosting-inline-env-retire --namespace memex --retire 'PluginCatalog__RegistryToken=s;rm -rf /'
refuses_hard "inline-env-retire namespace with a metacharacter" "is not a plain name" \
  "${IE_ENV[@]}" hosting-inline-env-retire --namespace 'memex; kubectl delete ns memex' --retire "$IE_TOKEN"
case "$(cat "$_ie_args_state/log" 2>/dev/null)" in
  "") ok "…and no argument refusal ever reached kubectl" ;;
  *)  bad "no argument refusal reaches kubectl" "kubectl saw: $(cat "$_ie_args_state/log")" ;;
esac
rm -rf "$_ie_args_state"

ie base "$IE_TOKEN"
[ "$_ie_rc" -eq 0 ] && ok "a retired credential EQUAL to the source it shadows is removed" \
  || bad "a retired credential EQUAL to the source it shadows is removed" "exited ${_ie_rc}: ${_ie_out}"
case "$_ie_out" in *"PluginCatalog__RegistryToken  EQUAL (len 31)"*"falls through to secret/memex-portal-keyvault"*)
    ok "…reporting the verdict and a LENGTH, and naming the source it falls through to" ;;
  *) bad "the EQUAL verdict is reported with its length" "said: ${_ie_out}" ;; esac
ie_no_value "…and no value is printed, patched or passed in an argv"
_ie_all=1
for _p in 0/env/1 1/env/1 2/env/2; do
  case "$_ie_patch" in *"{\"op\":\"remove\",\"path\":\"/spec/template/spec/containers/${_p}\"}"*) ;; *) _ie_all=0 ;; esac
done
[ "$_ie_all" = 1 ] && ok "…from EVERY container that carries it — the portal and both gate sidecars" \
  || bad "the key is removed from every container that carries it" "patch: ${_ie_patch}"
[ "$(printf '%s\n' "$_ie_log" | grep -c ' patch deployment ')" = "1" ] && ok "…in ONE patch (one new ReplicaSet, one rollout)" \
  || bad "the removal is one patch" "kubectl saw: ${_ie_log}"
case "$_ie_patch" in '[{"op":"test","path":"/metadata/resourceVersion","value":"918273"}'*)
    ok "…guarded FIRST on the resourceVersion that was measured" ;;
  *) bad "the patch is guarded on the measured resourceVersion" "patch: ${_ie_patch}" ;; esac
case "$_ie_patch" in *'{"op":"test","path":"/spec/template/spec/containers/0/env/1/name","value":"PluginCatalog__RegistryToken"},{"op":"remove","path":"/spec/template/spec/containers/0/env/1"}'*)
    ok "…and each removal on the entry's NAME, immediately before it" ;;
  *) bad "each removal is guarded on the entry's name" "patch: ${_ie_patch}" ;; esac
case "$_ie_patch" in *RegistryUrl*|*ClaudeCode*|*DOTNET_*|*MESH_*) bad "only the retired key is touched" "patch: ${_ie_patch}" ;;
  *) ok "…and nothing but the retired key is touched" ;; esac
case "$_ie_out" in *"::hosting:: inline_env_retired=1"*) ok "…and the count it reports comes after the read-back" ;;
  *) bad "the retired count is reported" "said: ${_ie_out}" ;; esac

# 🚨 The refusals — every one BEFORE any write.
ie differ "$IE_TOKEN"
[ "$_ie_rc" -ne 0 ] && ok "a shadow that DIFFERS is refused — removing the entry would change the value the portal reads" \
  || bad "a DIFFER is refused" "exited 0: ${_ie_out}"
case "$_ie_out" in *"DIFFER (inline 31 bytes, secret/memex-portal-keyvault 39 bytes)"*) ok "…reporting both lengths and the verdict, never a value" ;;
  *) bad "the DIFFER refusal reports lengths" "said: ${_ie_out}" ;; esac
ie_no_patch "…and writes nothing"
ie_no_value "…and prints no value either"

ie base "PluginCatalog__RegistryToken=memex-portal-secrets"
[ "$_ie_rc" -ne 0 ] && ok "a record naming a shadow the pod would NOT fall through to is refused" \
  || bad "a wrong recorded shadow is refused" "exited 0: ${_ie_out}"
case "$_ie_out" in *"would fall through to secret/memex-portal-keyvault"*) ok "…naming the source that actually wins (the LAST envFrom that carries the key)" ;;
  *) bad "the wrong-shadow refusal names the winner" "said: ${_ie_out}" ;; esac
ie_no_patch "…and writes nothing"

ie base "Features__Ai__Clis__ClaudeCode=memex-portal-config"
[ "$_ie_rc" -ne 0 ] && ok "a SOLE-source entry is refused — removing it would blank the key" \
  || bad "a sole source is refused" "exited 0: ${_ie_out}"
case "$_ie_out" in *"SOLE source"*) ok "…saying so" ;; *) bad "the sole-source refusal says so" "said: ${_ie_out}" ;; esac
ie_no_patch "…and writes nothing"

ie base "PluginCatalog__RegistryUrl=memex-portal-config"
[ "$_ie_rc" -ne 0 ] && ok "a key the ConfigMap renders EMPTY is refused as DIFFER, not blanked (#3201's RegistryUrl)" \
  || bad "an empty-rendered key is refused" "exited 0: ${_ie_out}"
case "$_ie_out" in *"DIFFER (inline 32 bytes, configmap/memex-portal-config 0 bytes)"*) ok "…naming both lengths" ;;
  *) bad "the empty-rendered refusal names both lengths" "said: ${_ie_out}" ;; esac
ie_no_patch "…and writes nothing"

ie base "$IE_TOKEN Features__Ai__Clis__ClaudeCode=memex-portal-config"
[ "$_ie_rc" -ne 0 ] && ok "one refused key refuses the whole run" || bad "one refused key refuses the run" "exited 0: ${_ie_out}"
ie_no_patch "…so nothing is retired halfway — not even the key that measured EQUAL"

ie rolling "$IE_TOKEN"
[ "$_ie_rc" -ne 0 ] && ok "a Deployment mid-rollout is refused — the patch would supersede the rollout in flight" \
  || bad "a mid-rollout Deployment is refused" "exited 0: ${_ie_out}"
case "$_ie_out" in *"a rollout is in progress"*"1 updated"*) ok "…naming what is not settled" ;;
  *) bad "the rollout refusal names what is unsettled" "said: ${_ie_out}" ;; esac
case "$_ie_log" in *"get secret"*) bad "…before any Secret is read" "kubectl saw: ${_ie_log}" ;; *) ok "…before any Secret is read" ;; esac
ie_no_patch "…and writes nothing"

ie valuefrom "$IE_TOKEN"
[ "$_ie_rc" -ne 0 ] && ok "a valueFrom reference is refused — it names its own source and is not a shadow" \
  || bad "a valueFrom entry is refused" "exited 0: ${_ie_out}"
case "$_ie_out" in *"valueFrom"*) ok "…saying so" ;; *) bad "the valueFrom refusal says so" "said: ${_ie_out}" ;; esac
ie_no_patch "…and writes nothing"

ie stuck "$IE_TOKEN"
[ "$_ie_rc" -ne 0 ] && ok "a patch the API accepted but that did not take is a FAILED step, not a pass" \
  || bad "an ineffective patch fails" "exited 0: ${_ie_out}"
case "$_ie_out" in *"still carries"*) ok "…naming what is still there (read back)" ;;
  *) bad "the read-back failure names what is left" "said: ${_ie_out}" ;; esac

ie absent "$IE_TOKEN"
[ "$_ie_rc" -eq 0 ] && ok "a key no container carries any more is a successful no-op (idempotent plan step)" \
  || bad "an already-retired key is a no-op" "exited ${_ie_rc}: ${_ie_out}"
ie_no_patch "…that writes nothing, so it rolls nothing"
case "$_ie_out" in *"::hosting:: inline_env_retired=0"*"::hosting:: inline_env_absent=1"*) ok "…and says so" ;;
  *) bad "the no-op says so" "said: ${_ie_out}" ;; esac

# A dry run measures for real, narrates the patch and writes nothing.
ie base "$IE_TOKEN" HOSTING_DRY_RUN=true
[ "$_ie_rc" -eq 0 ] && ok "a dry run succeeds" || bad "a dry run succeeds" "exited ${_ie_rc}: ${_ie_out}"
ie_no_patch "a dry run writes nothing"
case "$_ie_out" in *"DRY-RUN would run"*"patch deployment"*) ok "…narrates the patch it would write" ;;
  *) bad "a dry run narrates the patch" "said: ${_ie_out}" ;; esac
case "$_ie_out" in *"::hosting:: inline_env_retire=dry-run"*) ok "…and never claims a retirement" ;;
  *) bad "a dry run never claims a retirement" "said: ${_ie_out}" ;; esac
ie_no_value "…still printing no value"

echo
echo "── hosting-kv-rotate refuses under an inline shadow ──────────────"
# MeshWeaver#3201 / Plugins#1593: an inline `env:` entry outranks every envFrom, so a rotation that
# lands the new key in the vault and the synced Secret leaves the pods presenting the OLD one — and
# the registry deletes the old key's index entry the moment it adopts the new hash
# (MeshWeaverInstanceService.AdoptKeyHash → DeleteIndex). A 401 storm behind a green rotation. The
# rotation therefore measures the Deployment first, and refuses BEFORE anything is minted. Both a
# kubectl and an az stub lead PATH: the control case really does reach the vault write, and the az
# stub refuses it without recording the argv that carries the minted key.
KVR_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/kv-rotate" && pwd)"
# The stand-in REGISTRY (stubs/registry/curl): the real two-slot rules — current + staged — over a
# state file of key HASHES. The fixture keys are fake placeholders; the stub never logs one.
REG_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/registry" && pwd)"
_sha() { printf '%s' "$1" | sha256sum | cut -c1-64; }
LIVE_KEY_HASH="$(_sha fake-inline-registry-token-0001)"      # memex-portal-keyvault — what the pods present
OUTRANKED_HASH="$(_sha fake-chart-secret-token-OUTRANKED)"   # memex-portal-secrets — the third key
VAULT_KEY_HASH="$(_sha fake-vault-staged-token-0002)"        # a key an earlier rotation put in the vault
reg_state() {  # reg_state <normal|absent|old> [extra "<hash> <instance> <slot>" lines…] — sets $_reg
  _reg="$(mktemp -d)"
  printf '%s\n' "$1" > "$_reg/mode"; shift
  { printf '%s memex current\n%s crm-old current\n' "$LIVE_KEY_HASH" "$OUTRANKED_HASH"
    for line in "$@"; do printf '%s\n' "$line"; done; } > "$_reg/keys"
  : > "$_reg/log"
}
kvr() {  # kvr <inline-env scenario> — sets $_kvr_out $_kvr_rc $_kvr_az $_kvr_reg
  _kvr_state="$(mktemp -d)"
  reg_state normal
  _kvr_out="$(env PATH="$REG_STUBS:$KVR_STUBS:$IE_STUBS:$PATH" HOSTING_IE_FIXTURE="$IE_FIXTURES/$1" HOSTING_IE_BASE="$IE_FIXTURES/base" \
    HOSTING_IE_STATE="$_kvr_state" HOSTING_REG_STATE="$_reg" HOSTING_KV_SYNC_ATTEMPTS=1 HOSTING_KV_SYNC_INTERVAL=0 \
    hosting-kv-rotate --vault V --prefix memex- --namespace memex --synced-secret memex-portal-keyvault \
      --registry-url https://registry.test --instance-id memex 2>&1)"; _kvr_rc=$?
  _kvr_az="$(cat "$_kvr_state/az.log" 2>/dev/null || true)"
  _kvr_reg="$(cat "$_reg/log" 2>/dev/null || true)"
  rm -rf "$_kvr_state" "$_reg"
}
kvr base
[ "$_kvr_rc" -ne 0 ] && ok "a rotation with the key set INLINE on the Deployment is refused" \
  || bad "an inline shadow refuses the rotation" "exited 0: ${_kvr_out}"
case "$_kvr_out" in *"PluginCatalog__RegistryToken is set INLINE on memex-portal, python-gate, node-gate"*"RetireInlineEnv"*)
    ok "…naming every container that carries it and the prior step (RetireInlineEnv)" ;;
  *) bad "the shadow refusal names the containers and the prior step" "said: ${_kvr_out}" ;; esac
case "$_kvr_out" in *"::hosting::"*) bad "…before anything is reported" "said: ${_kvr_out}" ;;
  *) ok "…before anything is reported — no key_hash line" ;; esac
[ -z "$_kvr_reg" ] && ok "…and before the registry is even asked" \
  || bad "an inline shadow refuses before the registry is asked" "registry saw: ${_kvr_reg}"
[ -z "$_kvr_az" ] && ok "…and before anything is minted or stored (no az call at all)" \
  || bad "nothing is stored under a refusal" "az saw: ${_kvr_az}"
kvr absent
case "$_kvr_out" in *"set INLINE"*) bad "CONTROL: the same Deployment WITHOUT the inline entry passes the shadow check" "said: ${_kvr_out}" ;;
  *) ok "CONTROL: the same Deployment WITHOUT the inline entry passes the shadow check" ;; esac
case "$_kvr_az" in "az keyvault secret"*) ok "…and reaches the vault write (the az stub refuses it, so nothing is stored)" ;;
  *) bad "the control reaches the vault write" "az saw: '${_kvr_az}', said: ${_kvr_out}" ;; esac
case "$_kvr_out" in *mwi_*) bad "…without ever printing the minted key" "said: ${_kvr_out}" ;;
  *) ok "…without ever printing the minted key" ;; esac

echo
echo "── the rotation asks the REGISTRY before it mints (MeshWeaver#2802) ──"
# The defect this section pins. The rotation used to mint and STORE the key first and let the control
# plane adopt its hash afterwards — through whatever IInstanceKeyRegistry the control instance's hub
# resolved, which is not the registry (the instances live only in memex.meshweaver.cloud's store). The
# adoption failed AFTER Key Vault held a key nothing accepted, and the Job restarted the pods anyway:
# the next restart presented a key the registry never adopted. Now the registry is asked first, the
# vault is written last, and every failure in between leaves the key the pods present authenticating.
rot() {  # rot <inline-env fixture> [VAR=value …] — a real (non-dry) rotation against the stubs
  local fixture="$1"; shift
  _rot_state="$(mktemp -d)"
  _rot_out="$(env PATH="$REG_STUBS:$KVR_STUBS:$IE_STUBS:$PATH" HOSTING_IE_FIXTURE="$IE_FIXTURES/$fixture" \
    HOSTING_IE_BASE="$IE_FIXTURES/base" HOSTING_IE_STATE="$_rot_state" HOSTING_REG_STATE="$_reg" \
    HOSTING_KV_SYNC_ATTEMPTS=1 HOSTING_KV_SYNC_INTERVAL=0 "$@" \
    hosting-kv-rotate --vault V --object PluginCatalog-RegistryToken --namespace memex \
      --synced-secret memex-portal-keyvault --registry-url https://registry.test --instance-id memex 2>&1)"; _rot_rc=$?
  _rot_az="$(cat "$_rot_state/az.log" 2>/dev/null || true)"
  _rot_reg="$(cat "$_reg/log" 2>/dev/null || true)"
  rm -rf "$_rot_state"
}
never_minted() {  # never_minted <what> — no hash reported, no vault write, nothing staged
  case "$_rot_out" in *"::hosting:: key_hash="*) bad "$1: no key hash is reported" "said: ${_rot_out}" ;;
    *) ok "$1: no key hash is reported" ;; esac
  case "$_rot_az" in *"secret set"*) bad "$1: Key Vault is never written" "az saw: ${_rot_az}" ;;
    *) ok "$1: Key Vault is never written" ;; esac
  case "$_rot_reg" in *"/key/stage"*) bad "$1: nothing is staged" "registry saw: ${_rot_reg}" ;;
    *) ok "$1: nothing is staged" ;; esac
}
no_value_printed() {  # no_value_printed <output> <what>
  case "$1" in *mwi_*|*fake-inline-registry*|*fake-vault-staged*|*fake-chart-secret*|*nobody-holds*)
      bad "$2" "a key value appeared: $1" ;;
    *) ok "$2" ;; esac
}
holds() {  # holds <what> <line> — the registry's key file carries that exact line
  if grep -qx "$2" "$_reg/keys"; then ok "$1"; else bad "$1" "keys: $(tr '\n' ';' < "$_reg/keys")"; fi
}
lacks() {  # lacks <what> <pattern>
  if grep -q "$2" "$_reg/keys"; then bad "$1" "keys: $(tr '\n' ';' < "$_reg/keys")"; else ok "$1"; fi
}

# 🚨 THE CONTROL-INSTANCE CASE: a portal that does not hold the instance answers 401 to its key.
reg_state absent
rot absent
[ "$_rot_rc" -ne 0 ] && ok "a registry that does not hold the instance refuses the rotation" \
  || bad "a registry that does not hold the instance refuses the rotation" "exited 0: ${_rot_out}"
case "$_rot_out" in *"does not accept the key"*"Nothing was minted"*) ok "…saying so, and that nothing was minted" ;;
  *) bad "the no-instance refusal says so" "said: ${_rot_out}" ;; esac
never_minted "no instance at the registry"
rm -rf "$_reg"

reg_state old
rot absent
case "$_rot_out" in *"older than MeshWeaver#2802"*) ok "a registry without the key surface refuses, naming the roll it needs" ;;
  *) bad "a registry without the key surface refuses" "said: ${_rot_out}" ;; esac
never_minted "no key surface"
rm -rf "$_reg"

reg_state normal
printf '%s someone-else current\n' "$LIVE_KEY_HASH" > "$_reg/keys"
rot absent
case "$_rot_out" in *"belongs to instance 'someone-else'"*) ok "a key of ANOTHER instance refuses, naming it" ;;
  *) bad "a key of another instance refuses" "said: ${_rot_out}" ;; esac
never_minted "the wrong instance"
rm -rf "$_reg"

# CONTROL: a registry that holds the instance — the rotation reaches the vault, in the right order.
reg_state normal
rot absent
case "$_rot_reg" in *"GET /api/instances/self current"*"POST /api/instances/self/key/stage current"*"GET /api/instances/self staged"*)
    ok "CONTROL: asks the registry, stages the hash with the current key, proves the new key — in that order" ;;
  *) bad "the registry is asked, then staged, then the new key proven" "registry saw: ${_rot_reg}" ;; esac
case "$_rot_out" in *"::hosting:: key_hash="*"::hosting:: key_staged=1"*) ok "…reports the hash and that it is staged" ;;
  *) bad "the staged hash is reported" "said: ${_rot_out}" ;; esac
case "$_rot_az" in *"az keyvault secret set"*) ok "…and only then reaches the vault write (refused by the stub)" ;;
  *) bad "the vault write comes after the proof" "az saw: ${_rot_az}" ;; esac
case "$_rot_out" in *"Key Vault is unchanged"*) ok "…whose failure says Key Vault is unchanged" ;;
  *) bad "a failed vault write states the vault's state" "said: ${_rot_out}" ;; esac
holds "…while the key the pods present still authenticates" "${LIVE_KEY_HASH} memex current"
if grep -q ' memex staged$' "$_reg/keys"; then ok "…beside the staged one"; else bad "the new key is staged" "keys: $(cat "$_reg/keys")"; fi
no_value_printed "$_rot_out" "…and no key value is ever printed"
rm -rf "$_reg"

# The vault IS written but the Secret never catches up: nothing is retired, and it says so.
reg_state normal
rot absent HOSTING_AZ_ACCEPT_SET=1
case "$_rot_out" in *"Key Vault now holds the NEW key"*"has NOT been retired"*"pods were NOT restarted"*)
    ok "a sync that never arrives fails naming the vault, the registry and the pods' state" ;;
  *) bad "a failed sync states what it left behind" "said: ${_rot_out}" ;; esac
case "$_rot_out" in *"kv_rotated=1"*) bad "…and never claims the rotation" "said: ${_rot_out}" ;;
  *) ok "…and never claims the rotation" ;; esac
holds "…with the previous key still authenticating" "${LIVE_KEY_HASH} memex current"
rm -rf "$_reg"

# A rotation already in flight whose key is IN THE VAULT is RESUMED — never replaced.
reg_state normal "${VAULT_KEY_HASH} memex staged"
rot absent HOSTING_KV_VAULT_VALUE=fake-vault-staged-token-0002
case "$_rot_out" in *"::hosting:: key_resumed=1"*) ok "a key an earlier rotation left in the vault is RESUMED" ;;
  *) bad "an in-flight rotation is resumed" "said: ${_rot_out}" ;; esac
case "$_rot_out" in *"step=Mint a new instance key"*) bad "…without minting another" "said: ${_rot_out}" ;;
  *) ok "…without minting another" ;; esac
case "$_rot_az" in *"secret set"*) bad "…or writing the vault" "az saw: ${_rot_az}" ;; *) ok "…or writing the vault" ;; esac
holds "…leaving the staged key the vault holds staged" "${VAULT_KEY_HASH} memex staged"
rm -rf "$_reg"
reg_state normal "${VAULT_KEY_HASH} memex staged"
rot rotated HOSTING_KV_VAULT_VALUE=fake-vault-staged-token-0002
[ "$_rot_rc" -eq 0 ] && ok "…and once the Secret carries it, the resume completes for the restart and commit to finish" \
  || bad "a resumed rotation whose Secret caught up completes" "exited ${_rot_rc}: ${_rot_out}"
rm -rf "$_reg"

reg_state normal "$(_sha nobody-holds-this) memex staged"
rot absent HOSTING_KV_VAULT_VALUE=fake-vault-staged-token-0002
case "$_rot_out" in *"nobody holds it"*) ok "a staged key in neither the vault nor the Secret is replaced by a new stage" ;;
  *) bad "an orphaned staged key is replaced" "said: ${_rot_out}" ;; esac
lacks "…and stops authenticating" "^$(_sha nobody-holds-this) "
rm -rf "$_reg"

reg_state normal "${VAULT_KEY_HASH} memex staged"
rot absent
case "$_rot_out" in *"could not be read"*) ok "a staged key with an unreadable vault refuses rather than guessing" ;;
  *) bad "an unreadable vault refuses" "said: ${_rot_out}" ;; esac
never_minted "an unreadable vault"
rm -rf "$_reg"

echo
echo "── hosting-registry-key commit: retire the old key only when the new one is proven ──"
regkey() {  # regkey <inline-env fixture> <args…> — sets $_rk_out $_rk_rc $_rk_reg
  local fixture="$1" rk_state; shift
  rk_state="$(mktemp -d)"
  _rk_out="$(env PATH="$REG_STUBS:$IE_STUBS:$PATH" HOSTING_IE_FIXTURE="$IE_FIXTURES/$fixture" HOSTING_IE_BASE="$IE_FIXTURES/base" \
    HOSTING_IE_STATE="$rk_state" HOSTING_REG_STATE="$_reg" hosting-registry-key "$@" 2>&1)"; _rk_rc=$?
  _rk_reg="$(cat "$_reg/log" 2>/dev/null || true)"
  rm -rf "$rk_state"
}
COMMIT=(commit --registry-url https://registry.test --instance-id memex --namespace memex --synced-secret memex-portal-keyvault)

reg_state normal "${VAULT_KEY_HASH} memex staged"
regkey rotated "${COMMIT[@]}"
[ "$_rk_rc" -eq 0 ] && ok "a Secret carrying the staged key commits it" || bad "the staged key commits" "exited ${_rk_rc}: ${_rk_out}"
case "$_rk_out" in *"::hosting:: key_committed=1"*) ok "…and says so" ;; *) bad "a commit is reported" "said: ${_rk_out}" ;; esac
holds "…the new key is now current" "${VAULT_KEY_HASH} memex current"
lacks "…and the previous key no longer authenticates" "^${LIVE_KEY_HASH} "
no_value_printed "$_rk_out" "…printing no key"
rm -rf "$_reg"

reg_state normal "${VAULT_KEY_HASH} memex staged"
regkey absent "${COMMIT[@]}"
case "$_rk_out" in *"still carries the PREVIOUS key"*"Nothing was retired"*) ok "a Secret still carrying the previous key refuses the commit" ;;
  *) bad "a commit refuses when the new key never reached the Secret" "said: ${_rk_out}" ;; esac
case "$_rk_reg" in *"/key/commit"*) bad "…without asking the registry to commit" "registry saw: ${_rk_reg}" ;;
  *) ok "…without asking the registry to commit" ;; esac
holds "…so the key the pods present still authenticates" "${LIVE_KEY_HASH} memex current"
holds "…and the staged one too" "${VAULT_KEY_HASH} memex staged"
rm -rf "$_reg"

reg_state normal
regkey absent "${COMMIT[@]}"
case "$_rk_out" in *"::hosting:: key_committed=already"*) ok "a commit with nothing staged is an idempotent repeat" ;;
  *) bad "a repeated commit is idempotent" "said: ${_rk_out}" ;; esac
rm -rf "$_reg"

reg_state absent
regkey absent "${COMMIT[@]}"
[ "$_rk_rc" -ne 0 ] && ok "a registry that rejects the pods' key fails the commit" || bad "a rejected key fails the commit" "exited 0: ${_rk_out}"
rm -rf "$_reg"

echo
echo "── hosting-registry-key revoke: a key stops authenticating, nobody reads its value ──"
REVOKE=(revoke --registry-url https://registry.test --namespace memex --secret memex-portal-secrets
  --key PluginCatalog__RegistryToken --live-secret memex-portal-keyvault)

reg_state normal
regkey absent "${REVOKE[@]}"
[ "$_rk_rc" -eq 0 ] && ok "the outranked key in the chart Secret is revoked" || bad "the outranked key is revoked" "exited ${_rk_rc}: ${_rk_out}"
case "$_rk_out" in *"::hosting:: revoked_instance=crm-old"*"::hosting:: key_revoked=1"*) ok "…naming the instance it belonged to" ;;
  *) bad "a revocation names the instance" "said: ${_rk_out}" ;; esac
lacks "…and the registry no longer accepts it" "^${OUTRANKED_HASH} "
holds "…while the key the pods present still authenticates" "${LIVE_KEY_HASH} memex current"
case "$_rk_reg" in *"POST /api/instances/self/key/revoke current"*"GET /api/instances/self none"*) ok "…proven by reading it back as refused" ;;
  *) bad "a revocation is read back" "registry saw: ${_rk_reg}" ;; esac
no_value_printed "$_rk_out" "…printing no key"
rm -rf "$_reg"

reg_state normal
regkey absent revoke --registry-url https://registry.test --namespace memex --secret memex-portal-keyvault \
  --key PluginCatalog__RegistryToken --live-secret memex-portal-keyvault
case "$_rk_out" in *"SAME key"*"ROTATED"*) ok "revoking the key the pods present is refused — it is rotated, never revoked" ;;
  *) bad "the live key is never revoked" "said: ${_rk_out}" ;; esac
[ -z "$_rk_reg" ] && ok "…before the registry is asked" || bad "the live-key refusal asks nothing" "registry saw: ${_rk_reg}"
holds "…so it still authenticates" "${LIVE_KEY_HASH} memex current"
rm -rf "$_reg"

# 🚨 An inline env: entry for the key means the pods present a value no Secret here describes:
# both verbs refuse before the registry is asked (Copilot review on Plugins#1683).
reg_state normal
regkey base "${REVOKE[@]}"
case "$_rk_out" in *"set INLINE on memex-portal, python-gate, node-gate"*"Nothing was changed"*)
    ok "a revocation under an inline shadow of the live key is refused, naming every container" ;;
  *) bad "a revocation under an inline shadow is refused" "said: ${_rk_out}" ;; esac
[ -z "$_rk_reg" ] && ok "…before the registry is asked" || bad "the inline refusal asks nothing" "registry saw: ${_rk_reg}"
holds "…so the outranked key still authenticates" "${OUTRANKED_HASH} crm-old current"
rm -rf "$_reg"
reg_state normal "${VAULT_KEY_HASH} memex staged"
regkey base "${COMMIT[@]}"
case "$_rk_out" in *"set INLINE"*"would retire the key the pods actually present"*)
    ok "a commit under an inline shadow is refused" ;;
  *) bad "a commit under an inline shadow is refused" "said: ${_rk_out}" ;; esac
holds "…retiring nothing" "${LIVE_KEY_HASH} memex current"
rm -rf "$_reg"

reg_state normal
grep -v ' crm-old ' "$_reg/keys" > "$_reg/keys.new"; mv "$_reg/keys.new" "$_reg/keys"
regkey absent "${REVOKE[@]}"
case "$_rk_out" in *"::hosting:: key_revoked=already"*) ok "a key the registry already refuses is reported revoked, idempotently" ;;
  *) bad "revoking a dead key is idempotent" "said: ${_rk_out}" ;; esac
rm -rf "$_reg"

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

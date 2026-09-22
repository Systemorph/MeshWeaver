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
# ---------------------------------------------------------------------------
# THE CERTIFICATE, end to end. A new instance is not provisioned until its own host answers over
# its OWN certificate: an ingress without one is served the controller's fallback — another
# instance's certificate — and pearl.meshweaver.cloud spent nine hours in exactly that state on
# 2026-09-15 while its portal was healthy. These assert the two halves that were missing: the TLS
# step refuses a record that asks cert-manager for nothing, and the verify step tells a wrong
# certificate apart from a dead application instead of blaming the pods for both.
# ---------------------------------------------------------------------------
refuses "tls refuses an issuer of 'none'" "asks cert-manager for nothing" \
  hosting-tls --namespace n --host h.example.com --issuer none
refuses "tls needs --namespace"            "missing required flag --namespace" hosting-tls --host h.example.com
refuses "tls rejects unknown flags"        "unknown argument"                  hosting-tls --namespace n --host h.example.com --nope 1

VERIFY_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/verify" && pwd)"
_verify() { env PATH="$VERIFY_STUBS:$PATH" HOSTING_VERIFY_ATTEMPTS=1 "$@" hosting-verify --host instance.example.com; }

# The fallback-certificate shape: the host answers nothing over TLS and serves a certificate for
# ANOTHER host. The refusal must name the certificate and send the reader to the certificate.
refuses "verify names a WRONG certificate rather than blaming the pods" "served the WRONG certificate" \
  _verify HOSTING_VERIFY_STUB_CN=memex.meshweaver.cloud HOSTING_VERIFY_STUB_SANS=memex.meshweaver.cloud
# No certificate at all — DNS or the TLS step, not the application.
refuses "verify says when NO certificate is served" "served NO certificate at all" \
  _verify HOSTING_VERIFY_STUB_NOCERT=1
# TLS is correct and the app is not: the one case where the pods ARE the answer.
refuses "verify blames the application only when TLS is correct" "the APPLICATION did not answer" \
  _verify HOSTING_VERIFY_STUB_CN=instance.example.com HOSTING_VERIFY_STUB_SANS=instance.example.com
# A wildcard covers one label: *.example.com is this host's certificate, not a wrong one.
refuses "verify accepts a wildcard certificate as this host's" "the APPLICATION did not answer" \
  _verify HOSTING_VERIFY_STUB_CN='*.example.com' HOSTING_VERIFY_STUB_SANS='*.example.com'

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
echo "── hosting-kv-ensure: the connection string is composed, kept, never shown ──"
# MeshWeaver.Plugins#1721 — the Provision creates the database but the string the portal reads
# (<prefix>db-connection, mapped as ConnectionStrings__memex) was written by hand, and a declared
# vault object the vault does not hold fails the whole CSI mount (Memex#202). The stub answers the
# vault from a state directory and records every argv, so the decisions — compose only when
# ABSENT, keep when present, read the password by name and never print it — are asserted here.
KVE_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/kv-ensure" && pwd)"
kve() {  # kve [env…] -- <args…> — runs against a fresh state dir; sets $_kve_out $_kve_rc $_kve_log $_kve_state
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  _kve_state="$(mktemp -d)"
  _kve_out="$(env "${envs[@]}" PATH="$KVE_STUBS:$PATH" HOSTING_KVE_STATE="$_kve_state" hosting-kv-ensure "$@" 2>&1)"; _kve_rc=$?
  _kve_log="$(cat "$_kve_state/az.log" 2>/dev/null || true)"
}
KVE_DB=(--db-connection acme-db-connection --db-host pg.postgres.database.azure.com --db-port 5432 --db-user memexadmin --db-name acmedb --db-password-secret memex-postgres-password)

# Absent: composed from the flags and the password read from the vault, written through --file.
kve HOSTING_KVE_EXISTING="acme-Ai-KeyProtection-MasterKey acme-PluginCatalog-RegistryToken" HOSTING_KVE_PASSWORD_OBJECT=memex-postgres-password \
  -- --vault Systemorph --prefix acme- --namespace acme "${KVE_DB[@]}"
[ "$_kve_rc" -eq 0 ] && ok "kv-ensure composes an ABSENT db-connection object" || bad "kv-ensure composes an absent db-connection" "exited ${_kve_rc}: ${_kve_out}"
_kve_written="$(cat "$_kve_state/set.acme-db-connection" 2>/dev/null || true)"
if [ "$_kve_written" = "Host=pg.postgres.database.azure.com;Port=5432;Username=memexadmin;Password=fake-server-password-NEVER-PRINTED;Database=acmedb;SslMode=Require;Trust Server Certificate=true" ]; then
  ok "…in exactly the shape the portal parses (Host;Port;Username;Password;Database;SslMode;Trust Server Certificate)"
else
  bad "the composed string has the portal's shape" "wrote: ${_kve_written}"
fi
case "$_kve_out" in *NEVER-PRINTED*) bad "kv-ensure never prints the server password" "it did: ${_kve_out}" ;; *) ok "kv-ensure never prints the server password" ;; esac
case "$_kve_log" in *NEVER-PRINTED*) bad "…and never puts it on an az command line (argv)" "az saw: ${_kve_log}" ;; *) ok "…and never puts it on an az command line (argv)" ;; esac
case "$_kve_log" in *"secret set --vault-name Systemorph --name acme-db-connection --file "*) ok "the string reaches az through --file" ;; *) bad "the string reaches az through --file" "az saw: ${_kve_log}" ;; esac
case "$_kve_out" in *"::hosting:: kv_db_connection=created"*) ok "the run reports kv_db_connection=created" ;; *) bad "the run reports created" "said: ${_kve_out}" ;; esac
case "$_kve_out" in *"::hosting:: kv_created=1"*) ok "…and counts it among the created objects" ;; *) bad "created count" "said: ${_kve_out}" ;; esac
rm -rf "$_kve_state"

# Present: KEPT — no read of the password, no write — the master-key rule, one object over.
kve HOSTING_KVE_EXISTING="acme-Ai-KeyProtection-MasterKey acme-PluginCatalog-RegistryToken acme-db-connection" HOSTING_KVE_PASSWORD_OBJECT=memex-postgres-password \
  -- --vault Systemorph --prefix acme- --namespace acme "${KVE_DB[@]}"
[ "$_kve_rc" -eq 0 ] && ok "an EXISTING db-connection object is kept (a re-provision is idempotent)" || bad "existing db-connection is kept" "exited ${_kve_rc}: ${_kve_out}"
case "$_kve_log" in *"secret set"*"acme-db-connection"*) bad "a kept object is never rewritten" "az saw: ${_kve_log}" ;; *) ok "a kept object is never rewritten" ;; esac
case "$_kve_log" in *"memex-postgres-password"*) bad "…and the password is not even read" "az saw: ${_kve_log}" ;; *) ok "…and the password is not even read" ;; esac
case "$_kve_out" in *"::hosting:: kv_db_connection=kept"*) ok "the run reports kv_db_connection=kept" ;; *) bad "reports kept" "said: ${_kve_out}" ;; esac
case "$_kve_out" in *"::hosting:: kv_kept=3"*) ok "…and the three present objects are counted" ;; *) bad "kept count" "said: ${_kve_out}" ;; esac
rm -rf "$_kve_state"

# A record that names no db-connection plans none: today's command line, unchanged.
kve HOSTING_KVE_EXISTING="acme-Ai-KeyProtection-MasterKey acme-PluginCatalog-RegistryToken" -- --vault Systemorph --prefix acme- --namespace acme
[ "$_kve_rc" -eq 0 ] && ok "without --db-connection nothing about the database is touched" || bad "without --db-connection" "exited ${_kve_rc}: ${_kve_out}"
case "$_kve_out" in *kv_db_connection*) bad "…and no kv_db_connection fact is reported" "said: ${_kve_out}" ;; *) ok "…and no kv_db_connection fact is reported" ;; esac
rm -rf "$_kve_state"

# The refusals, each naming the hand command that still works.
kve HOSTING_KVE_EXISTING="acme-Ai-KeyProtection-MasterKey acme-PluginCatalog-RegistryToken" HOSTING_KVE_PASSWORD_OBJECT=other \
  -- --vault Systemorph --prefix acme- --namespace acme "${KVE_DB[@]}"
[ "$_kve_rc" -ne 0 ] && ok "an unreadable password object refuses" || bad "unreadable password refuses" "exited 0: ${_kve_out}"
case "$_kve_out" in *"could not read memex-postgres-password"*"az keyvault secret set --vault-name Systemorph --name acme-db-connection --file"*) ok "…naming the object and the exact hand command" ;; *) bad "names the hand command" "said: ${_kve_out}" ;; esac
case "$_kve_log" in *"secret set"*"acme-db-connection"*) bad "…and writes nothing" "az saw: ${_kve_log}" ;; *) ok "…and writes nothing" ;; esac
rm -rf "$_kve_state"
kve HOSTING_KVE_EXISTING="acme-Ai-KeyProtection-MasterKey acme-PluginCatalog-RegistryToken" HOSTING_KVE_PASSWORD_OBJECT=memex-postgres-password \
  -- --vault Systemorph --prefix acme- --namespace acme --db-connection acme-db-connection --db-host pg.postgres.database.azure.com --db-port 5432 --db-user memexadmin --db-name acmedb --db-password-secret ""
[ "$_kve_rc" -ne 0 ] && ok "an absent object with no password secret named refuses" || bad "no password secret refuses" "exited 0: ${_kve_out}"
case "$_kve_out" in *"missing --db-password-secret"*"AZ_POSTGRES_PASSWORD_SECRET"*) ok "…naming the flag and the operator.environment key that supplies it" ;; *) bad "names AZ_POSTGRES_PASSWORD_SECRET" "said: ${_kve_out}" ;; esac
rm -rf "$_kve_state"
kve HOSTING_KVE_EXISTING="acme-Ai-KeyProtection-MasterKey acme-PluginCatalog-RegistryToken" HOSTING_KVE_PASSWORD_OBJECT=memex-postgres-password HOSTING_KVE_SET_FAIL=1 \
  -- --vault Systemorph --prefix acme- --namespace acme "${KVE_DB[@]}"
[ "$_kve_rc" -ne 0 ] && ok "a vault that refuses the write fails the step" || bad "refused write fails" "exited 0: ${_kve_out}"
case "$_kve_out" in *NEVER-PRINTED*) bad "…without printing the password on the failure path" "it did: ${_kve_out}" ;; *) ok "…without printing the password on the failure path" ;; esac
rm -rf "$_kve_state"
refuses_hard "kv-ensure refuses a db-host that is not a hostname" "is not a hostname" \
  hosting-kv-ensure --vault V --namespace n --db-connection c --db-host 'pg;id' --db-port 5432 --db-user u --db-name d --db-password-secret p
refuses_hard "kv-ensure refuses a db-port that is not a number" "is not a port number" \
  hosting-kv-ensure --vault V --namespace n --db-connection c --db-host pg.test --db-port '5432;id' --db-user u --db-name d --db-password-secret p
refuses_hard "kv-ensure refuses a db-connection object with a metacharacter" "is not a plain name" \
  hosting-kv-ensure --vault V --namespace n --db-connection 'c`id`' --db-host pg.test --db-port 5432 --db-user u --db-name d --db-password-secret p

# A dry run reads no password, writes nothing, and says what it would create.
kve HOSTING_DRY_RUN=true HOSTING_KVE_EXISTING="acme-Ai-KeyProtection-MasterKey acme-PluginCatalog-RegistryToken" HOSTING_KVE_PASSWORD_OBJECT=memex-postgres-password \
  -- --vault Systemorph --prefix acme- --namespace acme "${KVE_DB[@]}"
[ "$_kve_rc" -eq 0 ] && ok "a dry-run kv-ensure with --db-connection succeeds" || bad "dry-run kv-ensure" "exited ${_kve_rc}: ${_kve_out}"
case "$_kve_log" in *"memex-postgres-password"*|*"secret set"*) bad "a dry run neither reads the password nor writes" "az saw: ${_kve_log}" ;; *) ok "a dry run neither reads the password nor writes" ;; esac
case "$_kve_out" in *"::hosting:: kv_db_connection=would-create"*) ok "…and reports would-create, never created" ;; *) bad "dry run reports would-create" "said: ${_kve_out}" ;; esac
rm -rf "$_kve_state"
unset _kve_out _kve_rc _kve_log _kve_state _kve_written
echo "── hosting-registry-register: issue the instance key once, prove it, never show it ──"
# MeshWeaver.Plugins#1720 — hosting-kv-ensure REQUIRES <prefix>PluginCatalog-RegistryToken and
# nothing issued it: runbook step 2 was a hand curl + az. The registry stub answers
# /api/instances/register and /api/instances/self; a recorded-argv az stub answers the vault. The
# decisions asserted: present-and-accepted → nothing issued; absent → registered, stored through
# --file, proven; every refusal before a second registration; no key in any output or argv.
RR_CURL="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/registry" && pwd)"
RR_AZ="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/registry-register" && pwd)"
rr() {  # rr <mode> <vault values> [env…] -- <args…>
  local mode="$1" values="$2"; shift 2
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  _rr_reg="$(mktemp -d)"; _rr_kv="$(mktemp -d)"
  printf '%s' "$mode" > "$_rr_reg/mode"; : > "$_rr_reg/keys"; : > "$_rr_reg/log"
  _rr_out="$(env "${envs[@]}" PATH="$RR_CURL:$RR_AZ:$PATH" HOSTING_REG_STATE="$_rr_reg" HOSTING_RRAZ_STATE="$_rr_kv" HOSTING_RRAZ_VALUES="$values" \
    hosting-registry-register "$@" 2>&1)"; _rr_rc=$?
  _rr_reglog="$(cat "$_rr_reg/log" 2>/dev/null || true)"; _rr_azlog="$(cat "$_rr_kv/az.log" 2>/dev/null || true)"
}
rr_done() { rm -rf "$_rr_reg" "$_rr_kv"; }
RR_ARGS=(--registry-url https://registry.test --instance-id acme --home-url https://acme.meshweaver.cloud --vault Systemorph --object acme-PluginCatalog-RegistryToken)

# Absent → registered on the free plan, stored, proven.
rr normal "" -- "${RR_ARGS[@]}"
[ "$_rr_rc" -eq 0 ] && ok "registry-register issues a key for an instance whose vault object is ABSENT" || bad "registry-register issues a key" "exited ${_rr_rc}: ${_rr_out}"
case "$_rr_reglog" in *"REGISTER acme boot=empty"*) ok "…by open registration (empty bootstrap key)" ;; *) bad "open registration" "registry saw: ${_rr_reglog}" ;; esac
[ "$(cat "$_rr_kv/set.acme-PluginCatalog-RegistryToken" 2>/dev/null)" = "mwi_fake-registered-key-NEVER-PRINTED-acme" ] && ok "…the returned mwi_ key is stored under the object, byte-for-byte" || bad "key stored" "vault got: $(cat "$_rr_kv/set.acme-PluginCatalog-RegistryToken" 2>/dev/null)"
case "$_rr_out" in *NEVER-PRINTED*|*mwi_*) bad "registry-register never prints the key" "it did: ${_rr_out}" ;; *) ok "registry-register never prints the key" ;; esac
case "$_rr_azlog" in *NEVER-PRINTED*|*mwi_*) bad "…and never puts it on an az command line" "az saw: ${_rr_azlog}" ;; *) ok "…and never puts it on an az command line" ;; esac
case "$_rr_reglog" in *"GET /api/instances/self current"*) ok "…and PROVES the stored key authenticates before it reports" ;; *) bad "proof" "registry saw: ${_rr_reglog}" ;; esac
case "$_rr_out" in *"::hosting:: registry_registration=registered"*"::hosting:: registry_instance=acme"*"::hosting:: registry_plan=free"*"::hosting:: registry_key_hash="*) ok "the run reports registered / instance / plan / key hash" ;; *) bad "registration facts" "said: ${_rr_out}" ;; esac
case "$_rr_out" in *"::hosting:: key_hash="*) bad "…and never the rotation's key_hash fact (the control plane would adopt it as a rotation)" "said: ${_rr_out}" ;; *) ok "…and never the rotation's key_hash fact" ;; esac
rr_done

# Present and accepted → nothing issued, nothing written.
_rr_pre="$(mktemp -d)"
rr normal "acme-PluginCatalog-RegistryToken=mwi_fake-registered-key-NEVER-PRINTED-acme" -- "${RR_ARGS[@]}"
rr_done
rr_present() {  # a registry that already knows the vault's key as acme's current key
  _rr_reg="$(mktemp -d)"; _rr_kv="$(mktemp -d)"; printf normal > "$_rr_reg/mode"; : > "$_rr_reg/log"
  printf '%s acme current\n' "$(printf '%s' "mwi_fake-registered-key-NEVER-PRINTED-acme" | sha256sum | cut -c1-64)" > "$_rr_reg/keys"
  _rr_out="$(env "$@" PATH="$RR_CURL:$RR_AZ:$PATH" HOSTING_REG_STATE="$_rr_reg" HOSTING_RRAZ_STATE="$_rr_kv" HOSTING_RRAZ_VALUES="acme-PluginCatalog-RegistryToken=mwi_fake-registered-key-NEVER-PRINTED-acme" \
    hosting-registry-register "${RR_ARGS[@]}" 2>&1)"; _rr_rc=$?
  _rr_reglog="$(cat "$_rr_reg/log")"; _rr_azlog="$(cat "$_rr_kv/az.log" 2>/dev/null || true)"
}
rr_present
[ "$_rr_rc" -eq 0 ] && ok "a PRESENT, accepted key is left alone (a re-provision is idempotent)" || bad "present key kept" "exited ${_rr_rc}: ${_rr_out}"
case "$_rr_reglog" in *REGISTER*) bad "…nothing is registered again" "registry saw: ${_rr_reglog}" ;; *) ok "…nothing is registered again" ;; esac
case "$_rr_azlog" in *"secret set"*) bad "…and nothing is written" "az saw: ${_rr_azlog}" ;; *) ok "…and nothing is written" ;; esac
case "$_rr_out" in *"::hosting:: registry_registration=present"*"registry_instance=acme"*) ok "…reported as present" ;; *) bad "present fact" "said: ${_rr_out}" ;; esac
case "$_rr_out" in *NEVER-PRINTED*|*mwi_*) bad "…without printing the held key" "it did: ${_rr_out}" ;; *) ok "…without printing the held key" ;; esac
rr_done

# Present but the registry rejects it → refused, object KEPT, hand fix named.
rr absent "acme-PluginCatalog-RegistryToken=mwi_stale-NEVER-PRINTED" -- "${RR_ARGS[@]}"
[ "$_rr_rc" -ne 0 ] && ok "a present key the registry rejects is a REFUSAL, never a re-registration" || bad "rejected present key refuses" "exited 0: ${_rr_out}"
case "$_rr_out" in *"does not accept"*"re-issue"*"az keyvault secret set --vault-name Systemorph --name acme-PluginCatalog-RegistryToken --file"*) ok "…naming the re-issue path and the hand command" ;; *) bad "names the fix" "said: ${_rr_out}" ;; esac
case "$_rr_reglog" in *REGISTER*) bad "…and registers nothing" "registry saw: ${_rr_reglog}" ;; *) ok "…and registers nothing" ;; esac
case "$_rr_azlog" in *"secret set"*) bad "…and keeps the object" "az saw: ${_rr_azlog}" ;; *) ok "…and keeps the object" ;; esac
rr_done

# Present but belongs to ANOTHER instance → refused.
_rr_reg="$(mktemp -d)"; _rr_kv="$(mktemp -d)"; printf normal > "$_rr_reg/mode"; : > "$_rr_reg/log"
printf '%s other current\n' "$(printf '%s' "mwi_other-NEVER-PRINTED" | sha256sum | cut -c1-64)" > "$_rr_reg/keys"
_rr_out="$(env PATH="$RR_CURL:$RR_AZ:$PATH" HOSTING_REG_STATE="$_rr_reg" HOSTING_RRAZ_STATE="$_rr_kv" HOSTING_RRAZ_VALUES="acme-PluginCatalog-RegistryToken=mwi_other-NEVER-PRINTED" hosting-registry-register "${RR_ARGS[@]}" 2>&1)"; _rr_rc=$?
[ "$_rr_rc" -ne 0 ] && ok "a present key of ANOTHER instance refuses" || bad "other instance's key refuses" "exited 0: ${_rr_out}"
case "$_rr_out" in *"holds the key of instance 'other'"*) ok "…naming whose it is" ;; *) bad "names the owner" "said: ${_rr_out}" ;; esac
rr_done

# Absent, but the id is taken → 409 → refused with the re-issue path; nothing written.
_rr_reg="$(mktemp -d)"; _rr_kv="$(mktemp -d)"; printf normal > "$_rr_reg/mode"; : > "$_rr_reg/log"
printf '%s acme current\n' "$(printf '%s' "mwi_lost-NEVER-PRINTED" | sha256sum | cut -c1-64)" > "$_rr_reg/keys"
_rr_out="$(env PATH="$RR_CURL:$RR_AZ:$PATH" HOSTING_REG_STATE="$_rr_reg" HOSTING_RRAZ_STATE="$_rr_kv" HOSTING_RRAZ_VALUES="" hosting-registry-register "${RR_ARGS[@]}" 2>&1)"; _rr_rc=$?
[ "$_rr_rc" -ne 0 ] && ok "an absent object for an id the registry already holds refuses (409)" || bad "409 refuses" "exited 0: ${_rr_out}"
case "$_rr_out" in *"already registered"*"re-issue"*) ok "…naming the re-issue path — a lost key is re-issued, never a second registration" ;; *) bad "409 message" "said: ${_rr_out}" ;; esac
[ ! -f "$_rr_kv/set.acme-PluginCatalog-RegistryToken" ] && ok "…and nothing is written" || bad "409 writes nothing" "it wrote"
rr_done

# Closed registration without a bootstrap key → refused naming --bootstrap-secret.
rr closed "" -- "${RR_ARGS[@]}"
[ "$_rr_rc" -ne 0 ] && ok "closed open-registration refuses" || bad "closed refuses" "exited 0: ${_rr_out}"
case "$_rr_out" in *"--bootstrap-secret"*) ok "…naming --bootstrap-secret as the way in" ;; *) bad "names bootstrap" "said: ${_rr_out}" ;; esac
rr_done
# …and with a bootstrap key read from the vault: accepted, never printed, never in argv.
_rr_reg="$(mktemp -d)"; _rr_kv="$(mktemp -d)"; printf closed > "$_rr_reg/mode"; : > "$_rr_reg/keys"; : > "$_rr_reg/log"; printf 'mwr_admin-boot-NEVER-PRINTED' > "$_rr_reg/bootstrap"
_rr_out="$(env PATH="$RR_CURL:$RR_AZ:$PATH" HOSTING_REG_STATE="$_rr_reg" HOSTING_RRAZ_STATE="$_rr_kv" HOSTING_RRAZ_VALUES="fleet-Registry-BootstrapKey=mwr_admin-boot-NEVER-PRINTED" hosting-registry-register "${RR_ARGS[@]}" --bootstrap-secret fleet-Registry-BootstrapKey 2>&1)"; _rr_rc=$?
_rr_reglog="$(cat "$_rr_reg/log")"; _rr_azlog="$(cat "$_rr_kv/az.log")"
[ "$_rr_rc" -eq 0 ] && ok "a bootstrap key from the vault registers on a closed registry" || bad "bootstrap registers" "exited ${_rr_rc}: ${_rr_out}"
case "$_rr_reglog" in *"REGISTER acme boot=present"*) ok "…presented in the body" ;; *) bad "bootstrap presented" "registry saw: ${_rr_reglog}" ;; esac
case "${_rr_out}${_rr_azlog}" in *mwr_*) bad "the bootstrap key never appears in output or argv" "seen: ${_rr_out} ${_rr_azlog}" ;; *) ok "the bootstrap key never appears in output or argv" ;; esac
rr_done

# A registry that predates the surface, and one that does not answer.
rr old "" -- "${RR_ARGS[@]}"
[ "$_rr_rc" -ne 0 ] && ok "a registry without the registration surface refuses (404)" || bad "404 refuses" "exited 0"
rr_done

refuses_hard "registry-register needs --registry-url" "missing required flag --registry-url" hosting-registry-register --instance-id a --home-url https://a.test --vault V --object o
refuses_hard "registry-register needs --object"       "missing required flag --object"       hosting-registry-register --registry-url https://r.test --instance-id acme --home-url https://a.test --vault V
refuses_hard "registry-register refuses a non-https registry" "is not an https base URL"    hosting-registry-register --registry-url http://r.test --instance-id acme --home-url https://a.test --vault V --object o
refuses_hard "registry-register refuses a home URL with a path" "is not an https base URL"  hosting-registry-register --registry-url https://r.test --instance-id acme --home-url 'https://a.test/$(id)' --vault V --object o
refuses_hard "registry-register refuses an id the registry would 400" "is not a registry instance id" hosting-registry-register --registry-url https://r.test --instance-id 'Acme' --home-url https://a.test --vault V --object o
refuses_hard "registry-register refuses a double hyphen"  "is not a registry instance id"   hosting-registry-register --registry-url https://r.test --instance-id 'ac--me' --home-url https://a.test --vault V --object o
refuses_hard "registry-register refuses an object with a metacharacter" "is not a plain name" hosting-registry-register --registry-url https://r.test --instance-id acme --home-url https://a.test --vault V --object 'o;id'

# Dry run: reads and registers nothing, reports dry-run.
rr normal "" HOSTING_DRY_RUN=true -- "${RR_ARGS[@]}"
[ "$_rr_rc" -eq 0 ] && ok "a dry-run registry-register succeeds" || bad "dry run" "exited ${_rr_rc}: ${_rr_out}"
case "$_rr_reglog" in *REGISTER*) bad "a dry run registers nothing" "registry saw: ${_rr_reglog}" ;; *) ok "a dry run registers nothing" ;; esac
case "$_rr_out" in *"::hosting:: registry_registration=dry-run"*) ok "…and reports dry-run, never registered" ;; *) bad "dry-run fact" "said: ${_rr_out}" ;; esac
rr_done
rm -rf "$_rr_pre"
unset _rr_out _rr_rc _rr_reglog _rr_azlog _rr_reg _rr_kv _rr_pre

echo "── hosting-kv-copy: a fleet-shared object materialised under the prefix, never shown ──"
# MeshWeaver.Plugins#1723 — a credential several instances hold (the fleet GitHub App's PEM) has to
# exist under EACH holder's prefix, because hosting-kv-purge deletes by prefix on teardown and a
# cross-prefix mapping would let the first teardown take the shared object with it. The copy was a
# hand step; now the record states `copyFrom` and the Provision runs this. The stub records every
# argv and answers the vault from a state, so the decisions — copy only when ABSENT, keep when
# present (drift reported, never rewritten), never print — are asserted here.
KVC_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/kv-copy" && pwd)"
kvc() {  # kvc [env…] -- <args…>
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  _kvc_state="$(mktemp -d)"
  _kvc_out="$(env "${envs[@]}" PATH="$KVC_STUBS:$PATH" HOSTING_KVC_STATE="$_kvc_state" hosting-kv-copy "$@" 2>&1)"; _kvc_rc=$?
  _kvc_log="$(cat "$_kvc_state/az.log" 2>/dev/null || true)"
}
KVC_PEM="-----BEGIN-FAKE-PEM-NEVER-PRINTED-----"

kvc HOSTING_KVC_VALUES="memexsystemorph-GitHub-App-PrivateKey=${KVC_PEM}" -- --vault Systemorph --copy build-GitHub-App-PrivateKey=memexsystemorph-GitHub-App-PrivateKey
[ "$_kvc_rc" -eq 0 ] && ok "kv-copy materialises an ABSENT target from its source" || bad "kv-copy copies an absent target" "exited ${_kvc_rc}: ${_kvc_out}"
[ "$(cat "$_kvc_state/set.build-GitHub-App-PrivateKey" 2>/dev/null)" = "$KVC_PEM" ] && ok "…byte-for-byte" || bad "the copy is byte-identical" "wrote: $(cat "$_kvc_state/set.build-GitHub-App-PrivateKey" 2>/dev/null)"
case "$_kvc_out" in *NEVER-PRINTED*) bad "kv-copy never prints the value" "it did: ${_kvc_out}" ;; *) ok "kv-copy never prints the value" ;; esac
case "$_kvc_log" in *NEVER-PRINTED*) bad "…and never puts it on an az command line" "az saw: ${_kvc_log}" ;; *) ok "…and never puts it on an az command line" ;; esac
case "$_kvc_out" in *"::hosting:: kv_copy_created=1"*"::hosting:: kv_copy_kept=0"*"::hosting:: kv_copy_drift=0"*) ok "the run reports created=1 kept=0 drift=0" ;; *) bad "kv-copy facts" "said: ${_kvc_out}" ;; esac
rm -rf "$_kvc_state"

kvc HOSTING_KVC_VALUES="memexsystemorph-GitHub-App-PrivateKey=${KVC_PEM} build-GitHub-App-PrivateKey=${KVC_PEM}" -- --vault Systemorph --copy build-GitHub-App-PrivateKey=memexsystemorph-GitHub-App-PrivateKey
[ "$_kvc_rc" -eq 0 ] && ok "an EXISTING, matching target is kept (a re-provision is idempotent)" || bad "existing target kept" "exited ${_kvc_rc}: ${_kvc_out}"
case "$_kvc_log" in *"secret set"*) bad "a kept target is never rewritten" "az saw: ${_kvc_log}" ;; *) ok "a kept target is never rewritten" ;; esac
case "$_kvc_out" in *"kv_copy_created=0"*"kv_copy_kept=1"*"kv_copy_drift=0"*) ok "…reported as kept, no drift" ;; *) bad "kept facts" "said: ${_kvc_out}" ;; esac
rm -rf "$_kvc_state"

kvc HOSTING_KVC_VALUES="memexsystemorph-GitHub-App-PrivateKey=${KVC_PEM} build-GitHub-App-PrivateKey=rotated-elsewhere-NEVER-PRINTED" -- --vault Systemorph --copy build-GitHub-App-PrivateKey=memexsystemorph-GitHub-App-PrivateKey
[ "$_kvc_rc" -eq 0 ] && ok "a target that DIFFERS from its source is still kept — drift is a fact, not a failure" || bad "differing target kept" "exited ${_kvc_rc}: ${_kvc_out}"
case "$_kvc_log" in *"secret set"*) bad "…and is not rewritten" "az saw: ${_kvc_log}" ;; *) ok "…and is not rewritten" ;; esac
case "$_kvc_out" in *"kv_copy_drift=1"*) ok "…and the drift is reported (kv_copy_drift=1)" ;; *) bad "drift reported" "said: ${_kvc_out}" ;; esac
case "$_kvc_out" in *NEVER-PRINTED*) bad "…without printing either value" "it did: ${_kvc_out}" ;; *) ok "…without printing either value" ;; esac
rm -rf "$_kvc_state"

kvc HOSTING_KVC_VALUES="a=1 b=2" -- --vault Systemorph --copy x=a --copy y=b
[ "$_kvc_rc" -eq 0 ] && ok "several --copy pairs run in one step" || bad "several pairs" "exited ${_kvc_rc}: ${_kvc_out}"
case "$_kvc_out" in *"kv_copy_created=2"*) ok "…each counted" ;; *) bad "count of two" "said: ${_kvc_out}" ;; esac
rm -rf "$_kvc_state"

kvc HOSTING_KVC_VALUES="" -- --vault Systemorph --copy build-GitHub-App-PrivateKey=memexsystemorph-GitHub-App-PrivateKey
[ "$_kvc_rc" -ne 0 ] && ok "an unreadable source refuses" || bad "unreadable source refuses" "exited 0: ${_kvc_out}"
case "$_kvc_out" in *"could not read memexsystemorph-GitHub-App-PrivateKey"*"az keyvault secret show --vault-name Systemorph --name memexsystemorph-GitHub-App-PrivateKey --query value -o json | jq -j . | az keyvault secret set --vault-name Systemorph --name build-GitHub-App-PrivateKey --file /dev/stdin"*) ok "…naming the source and the exact hand command" ;; *) bad "names the hand command" "said: ${_kvc_out}" ;; esac
rm -rf "$_kvc_state"

kvc HOSTING_KVC_VALUES="memexsystemorph-GitHub-App-PrivateKey=${KVC_PEM}" HOSTING_KVC_SET_FAIL=1 -- --vault Systemorph --copy build-GitHub-App-PrivateKey=memexsystemorph-GitHub-App-PrivateKey
[ "$_kvc_rc" -ne 0 ] && ok "a vault that refuses the write fails the step" || bad "refused write fails" "exited 0: ${_kvc_out}"
case "$_kvc_out" in *NEVER-PRINTED*) bad "…without printing the value on the failure path" "it did: ${_kvc_out}" ;; *) ok "…without printing the value on the failure path" ;; esac
rm -rf "$_kvc_state"

refuses_hard "kv-copy needs --vault"                    "missing required flag --vault" hosting-kv-copy --copy a=b
refuses_hard "kv-copy needs at least one --copy"        "missing required flag --copy"  hosting-kv-copy --vault V
refuses_hard "kv-copy refuses a pair without '='"       "is not <target>=<source>"      hosting-kv-copy --vault V --copy ab
refuses_hard "kv-copy refuses copying an object onto itself" "onto itself"              hosting-kv-copy --vault V --copy a=a
refuses_hard "kv-copy refuses a target with a metacharacter" "is not a plain name"      hosting-kv-copy --vault V --copy 'a;id=b'
refuses_hard "kv-copy refuses a source with a backtick"      "is not a plain name"      hosting-kv-copy --vault V --copy 'a=b`id`'
refuses_hard "kv-copy rejects unknown flags"            "unknown argument"              hosting-kv-copy --vault V --copy a=b --nope 1

kvc HOSTING_DRY_RUN=true HOSTING_KVC_VALUES="memexsystemorph-GitHub-App-PrivateKey=${KVC_PEM}" -- --vault Systemorph --copy build-GitHub-App-PrivateKey=memexsystemorph-GitHub-App-PrivateKey
[ "$_kvc_rc" -eq 0 ] && ok "a dry-run kv-copy succeeds" || bad "dry-run kv-copy" "exited ${_kvc_rc}: ${_kvc_out}"
case "$_kvc_log" in *"--query value"*|*"secret set"*) bad "a dry run reads no value and writes nothing" "az saw: ${_kvc_log}" ;; *) ok "a dry run reads no value and writes nothing" ;; esac
case "$_kvc_out" in *"kv_copy_created=1"*) ok "…and says what it would copy" ;; *) bad "dry run would-copy" "said: ${_kvc_out}" ;; esac
rm -rf "$_kvc_state"
unset _kvc_out _kvc_rc _kvc_log _kvc_state

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
echo "── hosting-image-mirror: the pinned images reach the fleet registry, once, verified ──"
# MeshWeaver.Plugins#1722 — cr.meshweaver.cloud serves only what was pushed to it; a record pinning
# a tag it lacks sent a human to `crane copy` (runbook step 4). The crane stub keeps a two-registry
# world (ref → digest) and records logins BY HOST AND USER only; the az stub answers the publisher
# password and the ACR token with sentinels. Asserted: present → kept (never re-pushed); absent →
# copied and both digests compared; absent upstream → refused naming the fix; no credential in any
# output or argv; a dry run touches nothing.
IM_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/image-mirror" && pwd)"
im() {  # im "<images lines>" [env…] -- <args…>
  local images="$1"; shift
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  _im_state="$(mktemp -d)"; printf '%b' "$images" > "$_im_state/images"
  _im_out="$(env "${envs[@]}" PATH="$IM_STUBS:$PATH" HOSTING_IM_STATE="$_im_state" hosting-image-mirror "$@" 2>&1)"; _im_rc=$?
  _im_log="$(cat "$_im_state/log" 2>/dev/null || true)"; _im_az="$(cat "$_im_state/az.log" 2>/dev/null || true)"
  _im_images="$(cat "$_im_state/images" 2>/dev/null || true)"
}
IM_ARGS=(--registry cr.meshweaver.cloud --repository memex-portal-ai --tag 3.0.0-ci.8411 --also memex-migration --source meshweaver.azurecr.io --vault Systemorph --publisher-secret memexcloud-Registry-PublisherPassword --publisher publisher)
IM_SRC='meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.8411 sha256:f7e11bb9aaaa\nmeshweaver.azurecr.io/memex-migration:3.0.0-ci.8411 sha256:52ecc7cbbbbb\n'

# Both absent → both copied from ACR, digests equal, as the publisher.
im "$IM_SRC" -- "${IM_ARGS[@]}"
[ "$_im_rc" -eq 0 ] && ok "image-mirror copies BOTH images the fleet registry lacks" || bad "image-mirror copies both" "exited ${_im_rc}: ${_im_out}"
case "$_im_images" in *"cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411 sha256:f7e11bb9aaaa"*"cr.meshweaver.cloud/memex-migration:3.0.0-ci.8411 sha256:52ecc7cbbbbb"*) ok "…portal AND migration land at the source's digests (Memex#141: same tag)" ;; *) bad "both landed" "registry holds: ${_im_images}" ;; esac
case "$_im_log" in *"auth login cr.meshweaver.cloud -u publisher --password-stdin"*"auth login meshweaver.azurecr.io -u 00000000-0000-0000-0000-000000000000 --password-stdin"*) ok "…signed in to the fleet registry as the publisher and to ACR with the token, both on stdin" ;; *) bad "logins" "crane saw: ${_im_log}" ;; esac
case "${_im_out}${_im_log}${_im_az}" in *NEVER-PRINTED*) bad "image-mirror never prints the publisher password or the ACR token, nor puts either on argv" "seen in: ${_im_out} ${_im_log} ${_im_az}" ;; *) ok "image-mirror never prints the publisher password or the ACR token, nor puts either on argv" ;; esac
case "$_im_az" in *"acr login --name meshweaver --expose-token"*) ok "the ACR short name is derived from the source host" ;; *) bad "acr name" "az saw: ${_im_az}" ;; esac
case "$_im_out" in *"::hosting:: image_mirrored=2"*"::hosting:: image_present=0"*"::hosting:: image_drift=0"*"::hosting:: image_tag=3.0.0-ci.8411"*) ok "the run reports mirrored=2 present=0 drift=0 and the tag" ;; *) bad "mirror facts" "said: ${_im_out}" ;; esac
rm -rf "$_im_state"

# Both present → kept, ACR never asked, nothing copied.
im "${IM_SRC}cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411 sha256:f7e11bb9aaaa\ncr.meshweaver.cloud/memex-migration:3.0.0-ci.8411 sha256:52ecc7cbbbbb\n" -- "${IM_ARGS[@]}"
[ "$_im_rc" -eq 0 ] && ok "images already in the fleet registry are kept (a re-provision copies nothing)" || bad "present kept" "exited ${_im_rc}: ${_im_out}"
case "$_im_log" in *" copy "*) bad "…nothing is re-pushed" "crane saw: ${_im_log}" ;; *) ok "…nothing is re-pushed" ;; esac
case "$_im_az" in *"acr login"*) bad "…and ACR is not even asked" "az saw: ${_im_az}" ;; *) ok "…and ACR is not even asked" ;; esac
case "$_im_out" in *"image_mirrored=0"*"image_present=2"*) ok "…reported as present=2" ;; *) bad "present facts" "said: ${_im_out}" ;; esac
rm -rf "$_im_state"

# Portal present, migration absent → only the migration is copied; the present portal is compared
# with ACR and a differing digest is reported as drift, never re-pushed.
im "${IM_SRC}cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411 sha256:OLDOLDOLD\n" -- "${IM_ARGS[@]}"
[ "$_im_rc" -eq 0 ] && ok "a partial set copies only what is missing" || bad "partial" "exited ${_im_rc}: ${_im_out}"
case "$_im_log" in *"copy meshweaver.azurecr.io/memex-migration:3.0.0-ci.8411 cr.meshweaver.cloud/memex-migration:3.0.0-ci.8411"*) ok "…the migration is copied" ;; *) bad "migration copied" "crane saw: ${_im_log}" ;; esac
case "$_im_log" in *"copy meshweaver.azurecr.io/memex-portal-ai"*) bad "…the present portal is NOT re-pushed even though it differs" "crane saw: ${_im_log}" ;; *) ok "…the present portal is NOT re-pushed even though it differs" ;; esac
case "$_im_out" in *"image_mirrored=1"*"image_present=1"*"image_drift=1"*) ok "…and the difference is reported as drift=1" ;; *) bad "drift fact" "said: ${_im_out}" ;; esac
rm -rf "$_im_state"

# Absent upstream too → refused naming the sealed-set fix; nothing copied.
im "" -- "${IM_ARGS[@]}"
[ "$_im_rc" -ne 0 ] && ok "a tag absent from ACR as well is a REFUSAL — no copy can put it anywhere" || bad "absent upstream refuses" "exited 0: ${_im_out}"
case "$_im_out" in *"does not exist"*"SEALED set"*) ok "…naming the fix: pin a sealed set's tag" ;; *) bad "names the fix" "said: ${_im_out}" ;; esac
case "$_im_log" in *" copy "*) bad "…and copies nothing" "crane saw: ${_im_log}" ;; *) ok "…and copies nothing" ;; esac
rm -rf "$_im_state"

# The identity cannot log in to ACR → refused naming AcrPull and the hand commands.
im "$IM_SRC" HOSTING_IM_ACR_DENIED=1 -- "${IM_ARGS[@]}"
[ "$_im_rc" -ne 0 ] && ok "no AcrPull on the source is a RED step" || bad "acr denied" "exited 0: ${_im_out}"
case "$_im_out" in *"AcrPull"*"crane copy meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.8411 cr.meshweaver.cloud/memex-portal-ai:3.0.0-ci.8411"*) ok "…naming the grant and the exact crane copy commands" ;; *) bad "acr message" "said: ${_im_out}" ;; esac
rm -rf "$_im_state"

# The publisher password cannot be read → refused before anything.
im "$IM_SRC" HOSTING_IM_PUBLISHER_ABSENT=1 -- "${IM_ARGS[@]}"
[ "$_im_rc" -ne 0 ] && ok "an unreadable publisher password refuses" || bad "publisher absent" "exited 0"
case "$_im_log" in *"auth login"*) bad "…before any login" "crane saw: ${_im_log}" ;; *) ok "…before any login" ;; esac
rm -rf "$_im_state"

# The fleet registry rejects the publisher password → refused naming the bcrypt hash on the record.
im "$IM_SRC" HOSTING_IM_LOGIN_FAIL=cr.meshweaver.cloud -- "${IM_ARGS[@]}"
[ "$_im_rc" -ne 0 ] && ok "a publisher password the registry rejects refuses" || bad "login fail" "exited 0"
case "$_im_out" in *"publisherPasswordBcrypt"*) ok "…naming the record field it must match" ;; *) bad "bcrypt hint" "said: ${_im_out}" ;; esac
rm -rf "$_im_state"

# A copy that does not land as the same digest is a failure, not a pass.
im "$IM_SRC" HOSTING_IM_COPY_FAIL=1 -- "${IM_ARGS[@]}"
[ "$_im_rc" -ne 0 ] && ok "a failed copy fails the step" || bad "copy fail" "exited 0"
rm -rf "$_im_state"

refuses_hard "image-mirror needs --registry"   "missing required flag --registry"   hosting-image-mirror --repository r --tag t --vault V --publisher-secret p
refuses_hard "image-mirror needs --tag"        "missing required flag --tag"        hosting-image-mirror --registry cr.test --repository r --vault V --publisher-secret p
refuses_hard "image-mirror needs --publisher-secret" "missing required flag --publisher-secret" hosting-image-mirror --registry cr.test --repository r --tag t --vault V
refuses_hard "image-mirror refuses a non-ACR source" "is not an Azure Container Registry host" hosting-image-mirror --registry cr.test --repository r --tag t --vault V --publisher-secret p --source ghcr.io
refuses_hard "image-mirror refuses a repository with a metacharacter" "is not a repository path" hosting-image-mirror --registry cr.test --repository 'r;id' --tag t --vault V --publisher-secret p
refuses_hard "image-mirror refuses a tag with a metacharacter" "is not a plain name" hosting-image-mirror --registry cr.test --repository r --tag 't`id`' --vault V --publisher-secret p
refuses_hard "image-mirror refuses registry == source" "nothing to mirror" hosting-image-mirror --registry meshweaver.azurecr.io --repository r --tag t --vault V --publisher-secret p
refuses_hard "image-mirror rejects unknown flags" "unknown argument" hosting-image-mirror --registry cr.test --repository r --tag t --vault V --publisher-secret p --nope 1

# Dry run: no vault, no login, no copy; says what it would do.
im "$IM_SRC" HOSTING_DRY_RUN=true -- "${IM_ARGS[@]}"
[ "$_im_rc" -eq 0 ] && ok "a dry-run image-mirror succeeds" || bad "dry run" "exited ${_im_rc}: ${_im_out}"
[ -z "$_im_log" ] && [ -z "$_im_az" ] && ok "…touching neither registry nor vault" || bad "dry run touches nothing" "crane: ${_im_log} az: ${_im_az}"
case "$_im_out" in *"::hosting:: image_mirror=dry-run"*) ok "…and reports dry-run" ;; *) bad "dry-run fact" "said: ${_im_out}" ;; esac
rm -rf "$_im_state"
unset _im_out _im_rc _im_log _im_az _im_images _im_state

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

# ── hosting-deploy layers the Key Vault VALUES HALF first, when the record declares one ─────────
# 🚨 MeshWeaver#3780, measured twice on the control instance (helm revisions 44 and 55). The chart's
# own Secrets render from `secrets.<half>.*`, which on an external database live ONLY in the vault
# half `helm-values-<release>` — and this script fed helm the record's secret-free render alone.
# helm replaces a Secret wholesale, so `ConnectionStrings__orleans` became the chart's in-cluster
# default and every new pod died at silo start. The config repo's lane had layered
# `-f vault-values.yaml -f <overlay>` all along. These cases pin the shape that agrees with it:
# the half is read from the vault the record names, layered FIRST so the render wins, never
# echoed, REQUIRED where asked for (an absent half is a refusal before helm, naming capture), and
# absent altogether when the record declares none (a provisioned instance's shape).
echo
echo "── hosting-deploy: the Key Vault values half (#3780) ────────────"
_vh_dir="$(mktemp -d)"; cp -R "$DP_FIXTURES/." "$_vh_dir/"; _vh_log="$_vh_dir/calls.log"; : > "$_vh_log"
mkdir -p "$_vh_dir/vault"
printf 'secrets:\n  memex_portal:\n    ConnectionStrings__orleans: "Host=pg.example.test;Password=SENTINEL-NEVER-PRINTED"\n' > "$_vh_dir/vault/helm-values-memex"
_vh_vals="$_vh_dir/values.yaml"; printf '# GENERATED from the Hosting/Deployment record by HelmValues\nreplicas:\n  portal: 2\n' > "$_vh_vals"
_vh_run() { env PATH="$DP_STUBS:$PATH" HOSTING_CHART=/tmp HOSTING_DEPLOY_FIXTURE="$_vh_dir" HOSTING_DEPLOY_STUB_LOG="$_vh_log" \
  hosting-deploy --namespace memex --release memex --database memex --values "$_vh_vals" --image cr.example.test/memex-portal-ai:1 "$@" 2>&1; }
_vh_out="$(_vh_run --vault kv-test)"; _vh_rc=$?
[ "$_vh_rc" -eq 0 ] && ok "deploy succeeds when the vault holds the values half" || bad "deploy succeeds when the vault holds the values half" "exited ${_vh_rc}: ${_vh_out}"
grep -q '^az keyvault secret download --vault-name kv-test --name helm-values-memex --file ' "$_vh_log" \
  && ok "the half is read from the vault the record names, under helm-values-<release>" \
  || bad "the half is read from the vault the record names" "$(cat "$_vh_log")"
# ORDER IS THE CONTRACT: vault half first, the record's render LAST — on the upgrade AND on the
# adoption render, or the two would disagree about what the release is.
_vh_layers() { sed -n 's/.* -f \([^ ]*\) -f \([^ ]*\).*/\1 \2/p' <<< "$1"; }
_vh_up="$(_vh_layers "$(grep '^helm upgrade' "$_vh_log" | head -1)")"
_vh_tp="$(_vh_layers "$(grep '^helm template' "$_vh_log" | head -1)")"
_vh_first="${_vh_up%% *}"; _vh_second="${_vh_up##* }"
if [ -n "$_vh_up" ] && [ "$_vh_first" != "$_vh_vals" ] && [ "$_vh_second" = "$_vh_vals" ]; then
  ok "helm upgrade layers the vault half FIRST and the record's render LAST"
else
  bad "helm upgrade layers the vault half first and the record last" "layers: '${_vh_up}' in: $(cat "$_vh_log")"
fi
[ -n "$_vh_tp" ] && [ "$_vh_tp" = "$_vh_up" ] \
  && ok "the adoption render sees the same two layers in the same order" \
  || bad "the adoption render sees the same two layers" "template: '${_vh_tp}' upgrade: '${_vh_up}'"
case "$_vh_out" in *SENTINEL-NEVER-PRINTED*) bad "the values half is never echoed" "the secret value reached the log: ${_vh_out}" ;;
  *) ok "the values half is never echoed" ;; esac
case "$_vh_out" in *"::hosting:: vault_values_bytes="*) ok "only the half's SIZE is reported (::hosting:: vault_values_bytes=)" ;;
  *) bad "only the half's size is reported" "said: ${_vh_out}" ;; esac
[ -n "$_vh_first" ] && [ ! -e "$_vh_first" ] \
  && ok "the half's temp file is removed when the script exits" \
  || bad "the half's temp file is removed on exit" "'${_vh_first}' still exists"
# 🚨 Declared but absent is a REFUSAL, before helm, naming the way forward — never a deploy that
# quietly falls through to the chart's defaults, which is the #3780 boot failure itself.
rm -f "$_vh_dir/vault/helm-values-memex"; : > "$_vh_log"
_vh_out="$(_vh_run --vault kv-test)"; _vh_rc=$?
if [ "$_vh_rc" -ne 0 ] && printf '%s' "$_vh_out" | grep -q 'helm-values-memex' \
   && printf '%s' "$_vh_out" | grep -q 'capture' && printf '%s' "$_vh_out" | grep -q '3780' \
   && ! grep -q '^helm ' "$_vh_log"; then
  ok "a declared half the vault does not hold is refused before helm, naming capture and #3780"
else
  bad "a declared half the vault does not hold is refused before helm" "rc=${_vh_rc} out: ${_vh_out} log: $(cat "$_vh_log")"
fi
: > "$_vh_dir/vault/helm-values-memex"; : > "$_vh_log"
_vh_out="$(_vh_run --vault kv-test)"; _vh_rc=$?
if [ "$_vh_rc" -ne 0 ] && printf '%s' "$_vh_out" | grep -q 'EMPTY' && ! grep -q '^helm ' "$_vh_log"; then
  ok "an EMPTY half is refused too — a capture that wrote nothing is not 'no secrets'"
else
  bad "an empty half is refused" "rc=${_vh_rc} out: ${_vh_out}"
fi
# A record that declares NO vault half (vaultValuesKeys empty — every provisioned instance) reads
# nothing from any vault, and the record is the only layer.
: > "$_vh_log"
_vh_out="$(_vh_run)"; _vh_rc=$?
_vh_fs="$(grep '^helm upgrade' "$_vh_log" | head -1 | grep -o ' -f ' | wc -l | tr -d ' ')"
if [ "$_vh_rc" -eq 0 ] && ! grep -q '^az ' "$_vh_log" && [ "$_vh_fs" = "1" ]; then
  ok "without --vault nothing is read from any vault and the record is the only layer"
else
  bad "without --vault the record is the only layer" "rc=${_vh_rc} -f count=${_vh_fs} log: $(cat "$_vh_log")"
fi
refuses_hard "a vault name that is not a plain name is refused before anything runs" "not a plain name" \
  env HOSTING_DRY_RUN=true HOSTING_CHART=/tmp hosting-deploy --namespace memex --release memex --database memex --values "$_vh_vals" --vault 'kv;rm -rf /'
rm -rf "$_vh_dir"

# ── hosting-deploy keeps the RUNNING image when the values carry no portal.image KEY ────────────
# 🚨 Systemorph/Memex#458, measured on pearl 2026-09-21 11:25Z. The record names an imagePullSecret
# and pins no tag, so HelmValues rendered `portal:` + `  imagePullSecret:` and NO image. The old
# test (`grep -q '^portal:'`) took that block as "the values carry an image", skipped the keep-running
# read, and helm fell through to the chart default ghcr :latest (3.0.0-rc13, 2026-08-31) — a
# Reconcile, documented never to move the image, rolled the instance back three weeks. These cases
# pin the KEY as the question: a pull-Secret-only block keeps the running image; a real portal.image
# is left to the values; `image:` under ANOTHER block does not count; and with nothing running and
# no image anywhere, the first-install refusal still stands before helm.
echo
echo "── hosting-deploy: keeps the running image unless portal.image is set (Memex#458) ──"
_ki_dir="$(mktemp -d)"; cp -R "$DP_FIXTURES/." "$_ki_dir/"; _ki_log="$_ki_dir/calls.log"; : > "$_ki_log"
_ki_vals="$_ki_dir/values.yaml"
_ki_running="cr.example.test/memex-portal-ai:3.0.0-ci.9101"
printf '%s' "$_ki_running" > "$_ki_dir/running-image"
_ki_run() { env PATH="$DP_STUBS:$PATH" HOSTING_CHART=/tmp HOSTING_DEPLOY_FIXTURE="$_ki_dir" HOSTING_DEPLOY_STUB_LOG="$_ki_log" \
  hosting-deploy --namespace memex --release memex --database memex --values "$_ki_vals" 2>&1; }
# pearl's shape: the pull Secret alone under portal:
printf '# GENERATED from the Hosting/Deployment record by HelmValues\nportal:\n  imagePullSecret: "registry-pull"\nselfUpdate:\n  registry: "cr.example.test"\n' > "$_ki_vals"
_ki_out="$(_ki_run)"; _ki_rc=$?
_ki_up="$(grep '^helm upgrade' "$_ki_log" | head -1)"
if [ "$_ki_rc" -eq 0 ] && printf '%s' "$_ki_up" | grep -q -- "--set portal.image=${_ki_running}" \
   && printf '%s' "$_ki_up" | grep -q -- "--set migration.image=cr.example.test/memex-migration:3.0.0-ci.9101" \
   && printf '%s' "$_ki_out" | grep -q "keeping the running ${_ki_running}"; then
  ok "a portal: block carrying only imagePullSecret keeps the RUNNING image (portal + migration)"
else
  bad "a pull-Secret-only portal: block keeps the running image" "rc=${_ki_rc} upgrade: '${_ki_up}' out: ${_ki_out}"
fi
# `image:` under a DIFFERENT top-level block (migration:) is not portal.image.
printf '# GENERATED from the Hosting/Deployment record by HelmValues\nportal:\n  imagePullSecret: "registry-pull"\nmigration:\n  image: "cr.example.test/memex-migration:1"\n' > "$_ki_vals"; : > "$_ki_log"
_ki_out="$(_ki_run)"; _ki_rc=$?
_ki_up="$(grep '^helm upgrade' "$_ki_log" | head -1)"
[ "$_ki_rc" -eq 0 ] && printf '%s' "$_ki_up" | grep -q -- "--set portal.image=${_ki_running}" \
  && ok "an image: under another block (migration:) does not count as portal.image" \
  || bad "an image: under another block does not count as portal.image" "rc=${_ki_rc} upgrade: '${_ki_up}'"
# An EMPTY portal.image is no image either.
printf '# GENERATED from the Hosting/Deployment record by HelmValues\nportal:\n  image: ""\n  imagePullSecret: "registry-pull"\n' > "$_ki_vals"; : > "$_ki_log"
_ki_out="$(_ki_run)"; _ki_rc=$?
_ki_up="$(grep '^helm upgrade' "$_ki_log" | head -1)"
[ "$_ki_rc" -eq 0 ] && printf '%s' "$_ki_up" | grep -q -- "--set portal.image=${_ki_running}" \
  && ok "an empty portal.image (\"\") keeps the running image" \
  || bad "an empty portal.image keeps the running image" "rc=${_ki_rc} upgrade: '${_ki_up}'"
# A record that DOES render portal.image (a pinned tag) is left to the values: no override, no read.
printf '# GENERATED from the Hosting/Deployment record by HelmValues\nportal:\n  image: "cr.example.test/memex-portal-ai:7"\n  imagePullSecret: "registry-pull"\nmigration:\n  image: "cr.example.test/memex-migration:7"\n' > "$_ki_vals"; : > "$_ki_log"
_ki_out="$(_ki_run)"; _ki_rc=$?
_ki_up="$(grep '^helm upgrade' "$_ki_log" | head -1)"
if [ "$_ki_rc" -eq 0 ] && [ -n "$_ki_up" ] && ! printf '%s' "$_ki_up" | grep -q -- '--set portal.image=' \
   && ! grep -q 'get deploy memex-portal-deployment' "$_ki_log"; then
  ok "a rendered portal.image is left to the values — no --set override, no running-image read"
else
  bad "a rendered portal.image is left to the values" "rc=${_ki_rc} upgrade: '${_ki_up}' log: $(cat "$_ki_log")"
fi
# Nothing running and no image anywhere: still the first-install refusal, before helm.
printf '# GENERATED from the Hosting/Deployment record by HelmValues\nportal:\n  imagePullSecret: "registry-pull"\n' > "$_ki_vals"; rm -f "$_ki_dir/running-image"; : > "$_ki_log"
_ki_out="$(_ki_run)"; _ki_rc=$?
if [ "$_ki_rc" -ne 0 ] && printf '%s' "$_ki_out" | grep -q 'no image to deploy' && ! grep -q '^helm upgrade' "$_ki_log"; then
  ok "nothing running and no portal.image is refused before helm — never the chart default"
else
  bad "nothing running and no portal.image is refused before helm" "rc=${_ki_rc} out: ${_ki_out} log: $(cat "$_ki_log")"
fi
rm -rf "$_ki_dir"

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

echo
echo "── hosting-kv-set: pasted values reach the vault through --file, never argv, never a log ──"
# The onboarding secret step (MeshWeaver.Plugins): a person pastes a value on the control instance,
# the mesh hands it to the Job as HOSTING_SECRETS (base64 JSON), and this step writes it. The stubs
# record every argv (az) and answer a synced Secret (kubectl), so the decisions — write through
# --file, every named object or nothing, never print, wait by hash — are asserted here.
KVS_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/kv-set" && pwd)"
kvs() {  # kvs [env…] -- <args…> — runs against a fresh state dir; sets $_kvs_out $_kvs_rc $_kvs_log $_kvs_state
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  _kvs_state="$(mktemp -d)"
  _kvs_out="$(env "${envs[@]}" PATH="$KVS_STUBS:$PATH" HOSTING_KVS_STATE="$_kvs_state" HOSTING_KV_SYNC_ATTEMPTS=2 HOSTING_KV_SYNC_INTERVAL=0 hosting-kv-set "$@" 2>&1)"; _kvs_rc=$?
  _kvs_log="$(cat "$_kvs_state/az.log" 2>/dev/null || true)"
}
KVS_SECRET="pasted-client-secret-NEVER-PRINTED"
KVS_JSON="$(printf '{"acme-Authentication-Microsoft-ClientSecret":"%s","acme-Email-ClientSecret":"second-value-NEVER-PRINTED"}' "$KVS_SECRET" | base64 | tr -d '\n')"

# Two objects, both values present → both written through --file, read back, reported by name.
kvs HOSTING_SECRETS="$KVS_JSON" -- --vault Systemorph --object acme-Authentication-Microsoft-ClientSecret --object acme-Email-ClientSecret
[ "$_kvs_rc" -eq 0 ] && ok "kv-set writes every named object" || bad "kv-set writes" "exited ${_kvs_rc}: ${_kvs_out}"
[ "$(cat "$_kvs_state/set.acme-Authentication-Microsoft-ClientSecret" 2>/dev/null)" = "$KVS_SECRET" ] && ok "…byte-for-byte, no trailing newline" || bad "value stored" "vault got: '$(cat "$_kvs_state/set.acme-Authentication-Microsoft-ClientSecret" 2>/dev/null)'"
[ "$(cat "$_kvs_state/set.acme-Email-ClientSecret" 2>/dev/null)" = "second-value-NEVER-PRINTED" ] && ok "…the second object too" || bad "second value" "vault got: '$(cat "$_kvs_state/set.acme-Email-ClientSecret" 2>/dev/null)'"
case "$_kvs_out" in *NEVER-PRINTED*) bad "kv-set never prints a value" "it did: ${_kvs_out}" ;; *) ok "kv-set never prints a value" ;; esac
case "$_kvs_log" in *NEVER-PRINTED*) bad "…and never puts one on an az command line" "az saw: ${_kvs_log}" ;; *) ok "…and never puts one on an az command line" ;; esac
case "$_kvs_log" in *"--file"*) ok "…the write goes through --file" ;; *) bad "through --file" "az saw: ${_kvs_log}" ;; esac
case "$_kvs_out" in *"::hosting:: kv_set=acme-Authentication-Microsoft-ClientSecret:"*"::hosting:: kv_set=acme-Email-ClientSecret:"*"::hosting:: kv_set_count=2"*) ok "…reported by NAME with a hash prefix, and the count" ;; *) bad "kv_set facts" "said: ${_kvs_out}" ;; esac
rm -rf "$_kvs_state"

# A named object with NO value → nothing written at all, the missing one named.
kvs HOSTING_SECRETS="$KVS_JSON" -- --vault Systemorph --object acme-Authentication-Microsoft-ClientSecret --object acme-Missing
[ "$_kvs_rc" -ne 0 ] && ok "an object the request carries no value for refuses" || bad "missing value refuses" "exited 0: ${_kvs_out}"
case "$_kvs_out" in *"acme-Missing"*"Nothing was written"*) ok "…naming it, and stating nothing was written" ;; *) bad "missing message" "said: ${_kvs_out}" ;; esac
[ ! -f "$_kvs_state/set.acme-Authentication-Microsoft-ClientSecret" ] && ok "…and the present one was NOT written either (all or nothing)" || bad "all or nothing" "it wrote the present one"
rm -rf "$_kvs_state"

# An empty value is a missing value.
kvs HOSTING_SECRETS="$(printf '{"acme-X":""}' | base64 | tr -d '\n')" -- --vault Systemorph --object acme-X
[ "$_kvs_rc" -ne 0 ] && ok "an EMPTY value refuses like a missing one" || bad "empty refuses" "exited 0"
rm -rf "$_kvs_state"

# No HOSTING_SECRETS at all → refuse, naming the contract.
kvs -- --vault Systemorph --object acme-X
[ "$_kvs_rc" -ne 0 ] && ok "no HOSTING_SECRETS is a refusal, not a no-op" || bad "no env refuses" "exited 0"
case "$_kvs_out" in *"HOSTING_SECRETS is empty"*) ok "…naming the variable" ;; *) bad "env message" "said: ${_kvs_out}" ;; esac
rm -rf "$_kvs_state"

# Not base64 / not an object → refuse.
kvs HOSTING_SECRETS='not base64!' -- --vault Systemorph --object acme-X
[ "$_kvs_rc" -ne 0 ] && ok "garbage HOSTING_SECRETS refuses" || bad "garbage refuses" "exited 0"
rm -rf "$_kvs_state"
kvs HOSTING_SECRETS="$(printf '["a"]' | base64 | tr -d '\n')" -- --vault Systemorph --object acme-X
[ "$_kvs_rc" -ne 0 ] && ok "a JSON array (not an object) refuses" || bad "array refuses" "exited 0"
rm -rf "$_kvs_state"

# The vault refuses the write → RED, names the object and the role, prints nothing.
kvs HOSTING_SECRETS="$KVS_JSON" HOSTING_KVS_SET_FAIL=acme-Authentication-Microsoft-ClientSecret -- --vault Systemorph --object acme-Authentication-Microsoft-ClientSecret
[ "$_kvs_rc" -ne 0 ] && ok "a vault that refuses the write fails the step" || bad "set fail" "exited 0"
case "$_kvs_out" in *"Key Vault Secrets Officer"*) ok "…naming the role the operator identity needs" ;; *) bad "role named" "said: ${_kvs_out}" ;; esac
case "$_kvs_out" in *NEVER-PRINTED*) bad "…without printing the value on the failure path" "it did: ${_kvs_out}" ;; *) ok "…without printing the value on the failure path" ;; esac
rm -rf "$_kvs_state"

# --wait: the synced Secret carries the new value → done; carries a stale one → RED after the attempts.
kvs HOSTING_SECRETS="$KVS_JSON" HOSTING_KVS_SYNCED="$KVS_SECRET" -- --vault Systemorph --object acme-Authentication-Microsoft-ClientSecret --namespace acme --wait acme-Authentication-Microsoft-ClientSecret=acme-portal-keyvault/Authentication__Microsoft__ClientSecret
[ "$_kvs_rc" -eq 0 ] && ok "kv-set waits for the synced Secret and returns when it carries the value" || bad "wait ok" "exited ${_kvs_rc}: ${_kvs_out}"
case "$_kvs_out" in *"::hosting:: kv_synced=acme-Authentication-Microsoft-ClientSecret"*) ok "…reporting the sync" ;; *) bad "kv_synced fact" "said: ${_kvs_out}" ;; esac
case "$(cat "$_kvs_state/kubectl.log")" in *"-n acme get secret acme-portal-keyvault"*) ok "…by reading the named Secret in the namespace" ;; *) bad "kubectl read" "kubectl saw: $(cat "$_kvs_state/kubectl.log")" ;; esac
rm -rf "$_kvs_state"
kvs HOSTING_SECRETS="$KVS_JSON" HOSTING_KVS_SYNCED="stale" -- --vault Systemorph --object acme-Authentication-Microsoft-ClientSecret --namespace acme --wait acme-Authentication-Microsoft-ClientSecret=acme-portal-keyvault/Authentication__Microsoft__ClientSecret
[ "$_kvs_rc" -ne 0 ] && ok "a Secret that never picks the value up is a RED step (the vault holds it, the pods do not)" || bad "wait stale" "exited 0"
case "$_kvs_out" in *"The vault HOLDS the new value"*) ok "…saying exactly what state that leaves" ;; *) bad "stale message" "said: ${_kvs_out}" ;; esac
[ -f "$_kvs_state/set.acme-Authentication-Microsoft-ClientSecret" ] && ok "…and the value WAS written before the wait" || bad "written before wait" "it was not"
rm -rf "$_kvs_state"

# Dry run: narrates, writes nothing, needs no HOSTING_SECRETS.
kvs HOSTING_DRY_RUN=true -- --vault Systemorph --object acme-X --namespace acme --wait acme-X=s/k
[ "$_kvs_rc" -eq 0 ] && ok "a dry run needs no values and succeeds" || bad "dry run" "exited ${_kvs_rc}: ${_kvs_out}"
case "$_kvs_out" in *"would set acme-X"*"::hosting:: kv_set_count=1"*) ok "…narrating the object and the count" ;; *) bad "dry facts" "said: ${_kvs_out}" ;; esac
[ -z "$_kvs_log" ] && ok "…and az saw nothing" || bad "dry az" "az saw: ${_kvs_log}"
rm -rf "$_kvs_state"

refuses_hard "kv-set needs --vault"                       "missing required flag --vault"  hosting-kv-set --object o
refuses_hard "kv-set needs --object"                      "missing required flag --object" hosting-kv-set --vault V
refuses_hard "kv-set refuses an object with a metacharacter" "is not a plain name"        hosting-kv-set --vault V --object 'o;id'
refuses_hard "kv-set refuses a vault with a metacharacter"   "is not a plain name"        hosting-kv-set --vault 'V`id`' --object o
refuses_hard "kv-set refuses a malformed --wait"          "is not <object>=<syncedSecret>/<configKey>" hosting-kv-set --vault V --object o --namespace n --wait 'o=broken'
refuses_hard "kv-set refuses --wait without --namespace"  "--wait needs --namespace"       hosting-kv-set --vault V --object o --wait o=s/k
refuses_hard "kv-set refuses a --wait for an object it does not set" "which no --object writes" hosting-kv-set --vault V --object o --namespace n --wait other=s/k
refuses_hard "kv-set rejects unknown flags"               "unknown argument"               hosting-kv-set --vault V --object o --nope 1
unset _kvs_out _kvs_rc _kvs_log _kvs_state KVS_JSON KVS_SECRET

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

# ── hosting-db-release: DENIED and ABSENT are different answers (MeshWeaver#4722) ────────────────
#
# 🚨 The defect these pin. The platform-layer preflight reads two CLUSTER-SCOPED things — the CNPG
# CRD and whether a `workload=db` node pool exists. The probes were written `2>/dev/null`, which
# discards the reason, so a Forbidden (the operator's ClusterRole lacking the grant) came out as
# "a node pool labelled workload=db" being MISSING: the operator announcing a platform layer is
# ABSENT when it was merely NOT PERMITTED TO LOOK, sending the reader off to provision a pool that
# already exists. Same shape as the `Not found` that closed #1391 and was re-filed as #3883.
#
# The grant exists now, so these do not guard today's cluster — they guard the FUTURE one. An RBAC
# change that takes the permission away must make the script say "refused", never "absent".
DBR_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/db-release" && pwd)"
DBR_CHART="$(mktemp -d)"

# <forbid> <absent> — run the command with the stub in front of PATH, in a subshell so the exported
# knobs cannot leak into any later test.
dbr() {
  ( export PATH="$DBR_STUBS:$PATH" HOSTING_DB_CHART="$DBR_CHART" \
           HOSTING_DB_STUB_FORBID="$1" HOSTING_DB_STUB_ABSENT="$2"
    hosting-db-release --namespace pearl --release pearl-db --database pearl )
}

refuses_hard "a Forbidden on nodes is REFUSED, not an absent node pool" \
  "REFUSED, not absent" dbr "nodes" ""
refuses_hard "a Forbidden on the CNPG CRD is REFUSED, not an absent operator" \
  "REFUSED, not absent" dbr "crd" ""
refuses_hard "an EMPTY node list is still ABSENT — the discrimination cuts both ways" \
  "lacks the database platform layer" dbr "" "nodes"
refuses_hard "an absent CRD is still ABSENT" \
  "lacks the database platform layer" dbr "" "crd"

# 🚨 THE CONTROL THAT MATTERS, and the one the phrase checks above cannot make: a refusal must not
# ALSO claim the layer is absent. Reporting both would restore the very confusion — the reader still
# goes and provisions a node pool — while every "does it say REFUSED" assertion stayed green.
_dbr_out="$(dbr "nodes" "" 2>&1)"
case "$_dbr_out" in
  *"lacks the database platform layer"*)
    bad "a Forbidden never also claims the platform layer is absent" "it said BOTH: ${_dbr_out}" ;;
  *"REFUSED, not absent"*)
    ok  "a Forbidden never also claims the platform layer is absent" ;;
  *)
    bad "a Forbidden never also claims the platform layer is absent" "said neither: ${_dbr_out}" ;;
esac

# And the happy path reaches PAST the preflight — otherwise every assertion above would pass on a
# command that refuses unconditionally, which is the "guard that checked nothing" shape.
_dbr_ok="$(dbr "" "" 2>&1)"
case "$_dbr_ok" in
  *"REFUSED, not absent"*|*"lacks the database platform layer"*)
    bad "a healthy platform layer passes the preflight" "it refused: ${_dbr_ok}" ;;
  *) ok "a healthy platform layer passes the preflight" ;;
esac

# ── the SAME defect forty lines below the fix: the credentials Secret (MeshWeaver#4722) ─────────
#
# 🚨 WHY THESE EXIST. #4436 fixed the three platform-layer probes above and left this read alone:
#   pw_len="$(kubectl … get secret "${release}-app" -o jsonpath='{.data.password}' 2>/dev/null | wc -c …)"
# — the same `2>/dev/null`, in the same file, inside a PIPE where no `||` could have caught it.
# Measured on main before this change, ALL THREE of Forbidden, absent-Secret and
# present-but-no-password-key produced ONE sentence, byte for byte:
#   "its credentials Secret pearl-db-app carries no password … Read the operator's log in
#    cnpg-system."
# So a missing ClusterRole grant sent the reader to CloudNativePG's log, and the operator stated
# the CONTENTS of a Secret it had never read. Three states, three sentences, or this is red.
_dbs_out() { ( export PATH="$DBR_STUBS:$PATH" HOSTING_DB_CHART="$DBR_CHART" \
                      HOSTING_DB_STUB_FORBID="$1" HOSTING_DB_STUB_ABSENT="$2"
               hosting-db-release --namespace pearl --release pearl-db --database pearl ) 2>&1; }

_dbs="$(_dbs_out "secret" "")"
case "$_dbs" in
  *"carries no password"*|*"carries NO password key"*)
    bad "a Forbidden on the credentials Secret is REFUSED, not an empty password" "it stated the Secret's contents: ${_dbs}" ;;
  *"REFUSED, not absent"*) ok "a Forbidden on the credentials Secret is REFUSED, not an empty password" ;;
  *) bad "a Forbidden on the credentials Secret is REFUSED, not an empty password" "said neither: ${_dbs}" ;;
esac

_dbs="$(_dbs_out "" "secret")"
case "$_dbs" in
  *"REFUSED"*) bad "an ABSENT credentials Secret is ABSENT, not refused" "the discrimination points the wrong way: ${_dbs}" ;;
  *"is ABSENT in pearl"*) ok "an ABSENT credentials Secret is ABSENT, not refused" ;;
  *) bad "an ABSENT credentials Secret is ABSENT, not refused" "said neither: ${_dbs}" ;;
esac

# 🚨 THE CONTROL ON THE OTHER SIDE — the state the old sentence was actually ABOUT must keep its
# own answer. A discrimination that renamed every case would pass both assertions above while
# losing the one reading that was correct all along.
_dbs="$(_dbs_out "" "password")"
case "$_dbs" in
  *"carries NO password key"*) ok "a Secret that EXISTS with no password key still says so — the reading that was right all along" ;;
  *) bad "a Secret that EXISTS with no password key still says so" "said: ${_dbs}" ;;
esac

# …and the whole command still SUCCEEDS when everything is there, reporting the two facts the mesh
# reads. Without this every assertion above would pass on a command that refuses unconditionally.
_dbs="$(_dbs_out "" "")"; _dbs_rc=$?
case "$_dbs" in
  *"::hosting:: db_release=pearl-db"*)
    [ "$_dbs_rc" -eq 0 ] && ok "a healthy database release reports db_release and exits 0" \
      || bad "a healthy database release reports db_release and exits 0" "rc=${_dbs_rc}: ${_dbs}" ;;
  *) bad "a healthy database release reports db_release and exits 0" "never reported it: ${_dbs}" ;;
esac

# ── hosting::probe itself: the primitive every one of those sites now depends on ─────────────────
# It moved out of hosting-db-release into _common.sh because a discrimination only one script can
# reach is one the next script will not make — which is exactly how the five sites above survived
# the fix that named them. Three answers, pinned directly.
_probe_case() {  # <expected rc> <what> <cmd…>
  local want="$1" what="$2"; shift 2
  ( set +u; . "$(dirname -- "${BASH_SOURCE[0]}")/../bin/_common.sh" 2>/dev/null
    hosting::probe "$@" ); local rc=$?
  [ "$rc" = "$want" ] && ok "hosting::probe: $what" || bad "hosting::probe: $what" "returned ${rc}, expected ${want}"
}
_probe_case 0 "a read that answers is PRESENT"                  any    printf 'x'
_probe_case 2 "a Forbidden on stderr is REFUSED (2)"            any    bash -c 'echo "Error from server (Forbidden): nodes is forbidden" >&2; exit 1'
_probe_case 1 "any other failure is ABSENT (1)"                 any    bash -c 'echo "Error from server (NotFound): x not found" >&2; exit 1'
_probe_case 1 "exit 0 with no output is ABSENT under 'output'"  output true
_probe_case 0 "exit 0 with no output is PRESENT under 'any'"    any    true

# ── every kubectl READ in bin/ that discards stderr is DECLARED, with its reason ─────────────────
# The static half of the same defect. #4436 fixed three probes by hand and nothing compared the fix
# against its subject, so two more reads in that file and three in other commands kept collapsing
# REFUSED into ABSENT. Undeclared is red; a declaration whose call is gone is stale and red.
sd_out="$(bash "$(dirname -- "${BASH_SOURCE[0]}")/check-stderr-discarded.sh" 2>&1)"; sd_rc=$?
if [ "$sd_rc" -eq 0 ]; then
  ok "every kubectl read in bin/ that discards stderr is declared ($(printf '%s' "$sd_out" | tail -1 | sed 's/^check-stderr-discarded: //'))"
else
  bad "every kubectl read in bin/ that discards stderr is declared" "$sd_out"
fi

# ── hosting-migrate: the Roll's migration runs as its OWN Job, and nothing moves until it succeeds ──
# 🚨 Systemorph/Memex#458/#460. A Roll was `kubectl set image` alone; across a db_version bump the new
# pod died on DbVersionGate behind old pods answering 200 (memex-cloud 2026-09-19, 8411 → 8955). The
# step runs the release's OWN migration Job (read from `helm get manifest`) with only the migration
# image moved, and its exit code is the gate the Roll's set-image sits behind. These cases pin: the
# Job is the release's (pull Secret, budget, envFrom, wait-for-postgres kept), BOTH the rehearsal and
# the migration container move to the target, succeeded is the only success, a failure / a vanished
# Job / a Job past its budget is a non-zero exit naming it, a Forbidden is REFUSED not absent, a
# release with no migration Job refuses, and a re-run per tag is idempotent.
echo
echo "── hosting-migrate: the migration runs as its own Job before the image moves ──"
MG_STUBS="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/stubs/migrate" && pwd)"
MG_FIXTURES="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/fixtures/migrate" && pwd)"
_mg_img="cr.example.test/memex-migration:3.0.0-ci.9101"
_mg_job="memex-migration-roll-3-0-0-ci-9101"
_mg_new() { _mg_dir="$(mktemp -d)"; cp -R "$MG_FIXTURES/." "$_mg_dir/"; _mg_log="$_mg_dir/calls.log"; : > "$_mg_log"; }
_mg_run() { env PATH="$MG_STUBS:$PATH" HOSTING_MIGRATE_FIXTURE="$_mg_dir" HOSTING_MIGRATE_STUB_LOG="$_mg_log" \
  HOSTING_MIGRATE_INTERVAL=0 HOSTING_MIGRATE_GRACE=0 "$@" \
  hosting-migrate --namespace pearl --release pearl --image "$_mg_img" 2>&1; }

# absent → created from the release's Job, retargeted, waited on, completed
_mg_new; echo 2 > "$_mg_dir/polls"; echo succeeded > "$_mg_dir/outcome"
_mg_out="$(_mg_run env)"; _mg_rc=$?
if [ "$_mg_rc" -eq 0 ] && printf '%s' "$_mg_out" | grep -q "::hosting:: migration_job=${_mg_job}" \
   && printf '%s' "$_mg_out" | grep -q '::hosting:: migration=completed' \
   && printf '%s' "$_mg_out" | grep -q 'Database migration completed. Version: 57'; then
  ok "an absent Job is created, waited on, and only then reported completed"
else
  bad "an absent Job is created, waited on and reported completed" "rc=${_mg_rc} out: ${_mg_out}"
fi
_mg_c="$_mg_dir/created.json"
if [ -f "$_mg_c" ] \
   && [ "$(jq -r '.metadata.name' "$_mg_c")" = "$_mg_job" ] \
   && [ "$(jq -r '.spec.template.spec.containers[0].image' "$_mg_c")" = "$_mg_img" ] \
   && [ "$(jq -r '.spec.template.spec.initContainers[] | select(.name=="memex-migration-rehearsal") | .image' "$_mg_c")" = "$_mg_img" ] \
   && [ "$(jq -r '.spec.template.spec.initContainers[] | select(.name=="wait-for-postgres") | .image' "$_mg_c")" = "busybox:1.36" ]; then
  ok "BOTH migration containers (rehearsal + run) move to the target; wait-for-postgres keeps its image"
else
  bad "the migration containers move to the target and nothing else does" "$(cat "$_mg_c" 2>/dev/null)"
fi
if [ "$(jq -r '.spec.template.spec.imagePullSecrets[0].name' "$_mg_c")" = "registry-pull" ] \
   && [ "$(jq -r '.spec.activeDeadlineSeconds' "$_mg_c")" = "660" ] \
   && [ "$(jq -r '.spec.template.spec.containers[0].envFrom[1].secretRef.name' "$_mg_c")" = "memex-migration-secrets" ] \
   && [ "$(jq -r '.metadata.labels["app.kubernetes.io/component"]' "$_mg_c")" = "memex-migration" ]; then
  ok "the Job is the release's own: pull Secret, budget, envFrom and labels carried over"
else
  bad "the Job is the release's own" "$(cat "$_mg_c")"
fi
_mg_get="$(grep -n '^kubectl -n pearl get job' "$_mg_log" | tail -1 | cut -d: -f1)"
_mg_create="$(grep -n '^kubectl -n pearl create -f -' "$_mg_log" | head -1 | cut -d: -f1)"
[ -n "$_mg_create" ] && [ -n "$_mg_get" ] && [ "$_mg_get" -gt "$_mg_create" ] \
  && ok "the Job's status is read AFTER it was created (the wait is real)" \
  || bad "the Job's status is read after it was created" "$(cat "$_mg_log")"
rm -rf "$_mg_dir"

# already succeeded for this tag → reported, never re-run
_mg_new; echo succeeded > "$_mg_dir/job-state"
_mg_out="$(_mg_run env)"; _mg_rc=$?
if [ "$_mg_rc" -eq 0 ] && printf '%s' "$_mg_out" | grep -q '::hosting:: migration=completed' && ! grep -q ' create -f -' "$_mg_log"; then
  ok "a Job that already SUCCEEDED for this tag is reported, not run again"
else
  bad "an already-succeeded Job is not run again" "rc=${_mg_rc} log: $(cat "$_mg_log")"
fi
rm -rf "$_mg_dir"

# failed before → deleted and run again
_mg_new; echo failed > "$_mg_dir/job-state"; echo succeeded > "$_mg_dir/outcome"
_mg_out="$(_mg_run env)"; _mg_rc=$?
_mg_del="$(grep -n "delete job ${_mg_job}" "$_mg_log" | head -1 | cut -d: -f1)"
_mg_create="$(grep -n ' create -f -' "$_mg_log" | head -1 | cut -d: -f1)"
if [ "$_mg_rc" -eq 0 ] && [ -n "$_mg_del" ] && [ -n "$_mg_create" ] && [ "$_mg_del" -lt "$_mg_create" ]; then
  ok "a Job that FAILED before is deleted and run again — the last failure is not this run's verdict"
else
  bad "a previously failed Job is deleted and re-run" "rc=${_mg_rc} log: $(cat "$_mg_log")"
fi
rm -rf "$_mg_dir"

# the Job fails → non-zero, names the Job, says the image must not move, never "completed"
_mg_new; echo 1 > "$_mg_dir/polls"; echo failed > "$_mg_dir/outcome"
_mg_out="$(_mg_run env)"; _mg_rc=$?
if [ "$_mg_rc" -ne 0 ] && printf '%s' "$_mg_out" | grep -q "${_mg_job} FAILED" \
   && printf '%s' "$_mg_out" | grep -q 'must not either' && ! printf '%s' "$_mg_out" | grep -q 'migration=completed'; then
  ok "a FAILED migration exits non-zero, names the Job, and never reports completed"
else
  bad "a failed migration is a failed step" "rc=${_mg_rc} out: ${_mg_out}"
fi
rm -rf "$_mg_dir"

# never finishes → stops at the Job's own budget, non-zero
_mg_new; echo hang > "$_mg_dir/outcome"
jq '(.items[] | select(.kind=="Job") | .spec.activeDeadlineSeconds) = 3' "$MG_FIXTURES/rendered.json" > "$_mg_dir/rendered.json"
_mg_out="$(_mg_run env)"; _mg_rc=$?
if [ "$_mg_rc" -ne 0 ] && printf '%s' "$_mg_out" | grep -q 'has not completed after' && ! printf '%s' "$_mg_out" | grep -q 'migration=completed'; then
  ok "a migration still running past its own budget is a failed step, not a pass"
else
  bad "a migration past its budget fails" "rc=${_mg_rc} out: ${_mg_out}"
fi
rm -rf "$_mg_dir"

# REFUSED is not ABSENT: a Forbidden on the Job read stops before anything is created
_mg_new; echo forbidden > "$_mg_dir/job-state"
_mg_out="$(_mg_run env)"; _mg_rc=$?
if [ "$_mg_rc" -ne 0 ] && printf '%s' "$_mg_out" | grep -q 'REFUSED, not absent' && ! grep -q ' create -f -' "$_mg_log"; then
  ok "a Forbidden on the Job read is REFUSED (not absent) and nothing is created"
else
  bad "a Forbidden job read is refused" "rc=${_mg_rc} out: ${_mg_out} log: $(cat "$_mg_log")"
fi
rm -rf "$_mg_dir"

# a release that renders no migration Job → refusal, nothing created
_mg_new; jq '.items |= map(select(.kind != "Job"))' "$MG_FIXTURES/rendered.json" > "$_mg_dir/rendered.json"
_mg_out="$(_mg_run env)"; _mg_rc=$?
if [ "$_mg_rc" -ne 0 ] && printf '%s' "$_mg_out" | grep -q 'renders no migration Job' && ! grep -q ' create -f -' "$_mg_log"; then
  ok "a release with no migration Job is a refusal — the image must not move without one"
else
  bad "a release with no migration Job refuses" "rc=${_mg_rc} out: ${_mg_out}"
fi
rm -rf "$_mg_dir"

# no release at all → refusal naming helm's answer
_mg_new; rm -f "$_mg_dir/manifest.yaml"
_mg_out="$(_mg_run env)"; _mg_rc=$?
[ "$_mg_rc" -ne 0 ] && printf '%s' "$_mg_out" | grep -q 'has no readable manifest' \
  && ok "a release helm cannot find is a refusal naming helm's answer" \
  || bad "a missing release refuses" "rc=${_mg_rc} out: ${_mg_out}"
rm -rf "$_mg_dir"

# dry run: reads, narrates the create, creates nothing, waits for nothing
_mg_new
_mg_out="$(_mg_run env HOSTING_DRY_RUN=true)"; _mg_rc=$?
if [ "$_mg_rc" -eq 0 ] && printf '%s' "$_mg_out" | grep -q 'DRY-RUN would run: kubectl -n pearl create -f -' \
   && [ ! -f "$_mg_dir/created.json" ] && ! printf '%s' "$_mg_out" | grep -q 'migration=completed'; then
  ok "a dry run narrates the Job, creates nothing and claims no migration"
else
  bad "a dry run creates nothing" "rc=${_mg_rc} out: ${_mg_out}"
fi
rm -rf "$_mg_dir"

refuses_hard "hosting-migrate refuses a PORTAL image — only memex-migration runs as the migration" "not a plain memex-migration image reference" \
  env HOSTING_DRY_RUN=true hosting-migrate --namespace pearl --release pearl --image cr.example.test/memex-portal-ai:3.0.0-ci.9101
refuses_hard "hosting-migrate refuses an image reference with a metacharacter" "not a plain memex-migration image reference" \
  env HOSTING_DRY_RUN=true hosting-migrate --namespace pearl --release pearl --image 'cr.example.test/memex-migration:1;rm -rf /'
refuses_hard "hosting-migrate needs --release" "missing required flag --release" \
  env HOSTING_DRY_RUN=true hosting-migrate --namespace pearl --image "$_mg_img"

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

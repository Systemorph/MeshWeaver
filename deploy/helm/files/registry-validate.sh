#!/bin/sh
# registry-validate — docker_auth's `ext_auth` hook for the fleet registry (cr.meshweaver.cloud).
#
# Contract (cesanta/docker_auth 1.14.0, auth_server/authn/ext_auth.go): stdin is "<user> <password>"
# with NO trailing newline (so `read` returns non-zero at EOF while still assigning — hence no
# `set -e` and no test of read's status); exit 0 = authenticated, 1 = denied, 2 = no match (try the
# next authenticator), 3 = error. On exit 0, stdout may carry a JSON object whose `labels` key is
# a map of label name → list of values; the ACL then matches `${labels:<name>}` placeholders
# against every value (auth_server/authz/acl.go).
#
# The PASSWORD is a MeshWeaver instance key. It is presented ONCE, as a bearer, to the portal's
# key→token exchange (`POST /api/instances/token`, the SyncTokenPayloads contract) with an empty
# body: a 200 authenticates and its `scope` — the caller's CURRENT licence entries, as the grant
# holds them (`Plugins/*`, `Reinsurance/UWDeepfield`, `Plugins/*@pro`) — becomes the labels the
# ACL authorizes plugin repositories with. 🚨 This is the only place the scope can be learned:
# docker_auth's `ext_authz` hook receives the marshalled request (account, type, name, actions,
# labels) and NOT the credential, so it cannot ask memex anything; and a label may never carry
# the key itself, because docker_auth logs every token's labels at Info (server.go, "New token
# for … %+v"). A scope is not a secret.
#
# Labels emitted, both in the registry's lowercase name grammar:
#   package  one per licence entry — `<source>/<package>`, or `<source>/*` for a whole-source
#            entry (plan-scoped or not): the ACL grants `pull` on `plugins/${labels:package}`,
#            i.e. on the bundle repositories `plugins/<source>/<package>`.
#   source   one per PLAN-LESS whole-source entry — `<source>`: the ACL grants `pull` on
#            `plugins/${labels:source}`, the publication INDEX. A plan-scoped `Source/*@plan`
#            licenses that source's packages by tier and never the publication whole (which
#            carries every plan's bundles), so it contributes no `source` label — same rule as
#            PluginGrant.AllowsWholeSource.
# Which package a plan-scoped entry covers is decided where the package's tier is known — the
# catalog and the landing — not here; the edge grants the source's bundle repositories and
# nothing above them.
#
# 401/403 from the exchange denies. Anything that is not a definite 200 or a definite 401/403 —
# DNS failure, timeout, a 5xx, a redirect chain ending elsewhere, an unparseable body — is exit 3
# (ERROR), never a pass and never a quiet deny that would read as "wrong key" to the caller.
# Busybox only (Alpine): wget -S prints the status line to stderr, the body goes to a file.
#
#   MEMEX_VALIDATION_URL   the exchange URL (registry.validationUrl)   required
read -r user password
if [ -z "$password" ]; then
  exit 1
fi
url="${MEMEX_VALIDATION_URL:-}"
if [ -z "$url" ]; then
  echo "registry-validate: MEMEX_VALIDATION_URL is not set" >&2
  exit 3
fi
body=$(mktemp) || exit 3
trap 'rm -f "$body"' EXIT
# An empty JSON body is a valid request (~0.2 s) — NOT the plugin catalog, which renders in full:
# measured 14–18 s for a valid key, past the 10 s cap.
status=$(wget -T 10 -qS -O "$body" --post-data='{}' --header='Content-Type: application/json' \
  --header="Authorization: Bearer $password" "$url" 2>&1 \
  | sed -n 's/^ *HTTP\/[0-9.]* \([0-9][0-9][0-9]\).*/\1/p' | tail -n 1)
case "$status" in
  200) ;;
  401|403) exit 1 ;;
  *) echo "registry-validate: no definite answer from $url for account '$user' (status '$status')" >&2; exit 3 ;;
esac

# The exchange answers System.Text.Json Web defaults: compact, camelCase, `"scope":["…","…"]`.
# A scope entry is `Source/Package[@plan]`; neither half can carry `"`, `,` or `]`.
if ! grep -q '"scope":\[' "$body"; then
  echo "registry-validate: the exchange answered 200 without a scope array for account '$user'" >&2
  exit 3
fi
scope=$(sed -n 's/.*"scope":\[\([^]]*\)\].*/\1/p' "$body" | tr ',' '\n' | tr -d '" ')
packages=""
sources=""
for entry in $scope; do
  plan=""
  case "$entry" in
    *@*) plan="${entry##*@}"; entry="${entry%@*}" ;;
  esac
  case "$entry" in
    */*) source="${entry%%/*}"; package="${entry#*/}" ;;
    *) source="$entry"; package="*" ;;
  esac
  source=$(printf '%s' "$source" | tr '[:upper:]' '[:lower:]')
  package=$(printf '%s' "$package" | tr '[:upper:]' '[:lower:]')
  # Only what the registry's name grammar can spell becomes a label; anything else is dropped,
  # which denies rather than matching something unintended.
  case "$source" in *[!a-z0-9._-]*|'') continue ;; esac
  case "$package" in '*') ;; *[!a-z0-9._-]*|'') continue ;; esac
  packages="$packages\"$source/$package\","
  if [ "$package" = '*' ] && [ -z "$plan" ]; then
    sources="$sources\"$source\","
  fi
done
labels=""
if [ -n "$packages" ]; then
  labels="\"package\":[${packages%,}]"
fi
if [ -n "$sources" ]; then
  labels="${labels:+$labels,}\"source\":[${sources%,}]"
fi
printf '{"labels":{%s}}\n' "$labels"
exit 0

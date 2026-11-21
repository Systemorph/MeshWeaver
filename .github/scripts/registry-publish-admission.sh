#!/usr/bin/env bash
#
# registry-publish-admission.sh admit|complete <step>
# registry-publish-admission.sh governs <image ref>...
#
# The CD half of the fleet registry's maintenance lease (MeshWeaver#7530, owner decision A;
# Doc/Architecture/FleetRegistryRetention → "Publishing under the lease"). A CD step that WRITES a
# lease-governed registry — a mirror push, a pointer move, a release retag — is admitted on the
# control instance BEFORE it writes and reports completion AFTER, through one signed call to
# control's `/api/registry-maintenance/admission` (MeshWeaver.Plugins `RegistryPublishAdmission`).
# The admission is recorded on the same lease row the image-applying instance actions are admitted
# on, so a cleanup can never start while a publish is writing, and a publish can never write while a
# cleanup — its manifest DELETEs or its read-only collect window — holds the lease.
#
#   governs   prints the lease-governed registry hosts among the refs' hosts (one per line), and
#             nothing when none is. Governed = the record says the registry's retention rule is
#             `derived-protected-set` (.github/acr-retention/instances.json) — DECLARED, never a list
#             in this script.
#   admit     asks to be admitted. 200 admitted ⇒ exit 0. 409 (a cleanup holds the lease) ⇒ the step
#             WAITS, re-asking every MW_REGISTRY_ADMISSION_POLL_SECONDS (60), for at most
#             MW_REGISTRY_ADMISSION_WAIT_SECONDS (1200, capped at 1800 so the job's 45-minute cut is
#             never the thing that ends it) — then RED, naming the holder. Every other answer is RED
#             by name, and nothing is written: control unreachable, a signature control refuses, a
#             route that does not exist, a store that could not record the admission.
#   complete  reports that the write finished. A failure is a WARNING, never a red: an admission that
#             was never completed keeps holding maintenance back until its bound (60 min), which is
#             the safe direction — the push itself already landed and was verified.
#
# 🚨 THERE IS NO SKIP. A governed write without the secret is RED (`CONTROL_WEBHOOK_SECRET` is the
# credential the CD lanes already hand control its builds with), and so is an unreadable record or
# control URL. A publish that cannot be admitted does not happen.
#
# Environment (never argv — the secret must not land in `ps` output):
#   CONTROL_WEBHOOK_SECRET  the HMAC control verifies X-Hub-Signature-256 against (Hosting:PlatformWebhookSecret)
#   GITHUB_REPOSITORY, GITHUB_RUN_ID, GITHUB_RUN_ATTEMPT  the publisher and run the admission is keyed by
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RECORD="${MW_ACR_RETENTION_DIR:-$HERE/../acr-retention}/instances.json"
CONTROL_RECORD="${MW_CONTROL_INSTANCE_FILE:-$HERE/../control-instance.json}"
ROUTE="/api/registry-maintenance/admission"

op="${1:-}"
shift || true

governed_hosts() {
  python3 - "$RECORD" <<'PY'
import json, sys
try:
    doc = json.load(open(sys.argv[1], encoding="utf-8"))
except (OSError, ValueError) as e:
    sys.exit(f"cannot read the registry record {sys.argv[1]}: {e}")
regs = doc.get("registries")
if not isinstance(regs, dict):
    sys.exit(f"{sys.argv[1]} carries no `registries` table")
for host, entry in sorted(regs.items()):
    if isinstance(entry, dict) and isinstance(entry.get("retention"), dict) \
            and entry["retention"].get("rule") == "derived-protected-set":
        print(host)
PY
}

host_of() {
  # Docker's rule: the first segment is a HOST only when it carries a dot, a colon, or is localhost.
  local ref="$1" first="${1%%/*}"
  if [ "$first" != "$ref" ] && { [[ "$first" == *.* ]] || [[ "$first" == *:* ]] || [ "$first" = localhost ]; }; then
    printf '%s\n' "$first" | tr '[:upper:]' '[:lower:]'
  else
    printf 'docker.io\n'
  fi
}

case "$op" in
  governs)
    governed=$(governed_hosts) || { echo "::error::registry-publish-admission: the registry record could not be read, so whether a write is lease-governed is unknown — refusing to write." >&2; exit 1; }
    for ref in "$@"; do
      h=$(host_of "$ref")
      if printf '%s\n' "$governed" | grep -Fxq "$h"; then printf '%s\n' "$h"; fi
    done | sort -u
    exit 0 ;;
  admit|complete) ;;
  *) echo "::error::registry-publish-admission: usage: registry-publish-admission.sh admit|complete <step> | governs <image ref>..."; exit 2 ;;
esac

step="${1:-}"
registry="${2:-}"
[ -n "$step" ] && [ -n "$registry" ] || { echo "::error::registry-publish-admission $op: usage: registry-publish-admission.sh $op <step> <registry host>"; exit 2; }
for v in CONTROL_WEBHOOK_SECRET GITHUB_REPOSITORY GITHUB_RUN_ID; do
  [ -n "${!v:-}" ] || { echo "::error::registry-publish-admission $op: \$$v is empty — a write to $registry (a lease-governed registry) cannot be admitted, so it does not happen. Hand the step \`CONTROL_WEBHOOK_SECRET: \${{ secrets.CONTROL_WEBHOOK_SECRET }}\`."; exit 1; }
done
base=$(python3 -c 'import json,sys; u=json.load(open(sys.argv[1]))["url"]; assert u.startswith("https://"), u; print(u.rstrip("/"))' "$CONTROL_RECORD") \
  || { echo "::error::registry-publish-admission: cannot read an https url from $CONTROL_RECORD — the control instance is unknown, so nothing is admitted."; exit 1; }
URL="$base$ROUTE"
run="${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT:-1}"

post() {
  # One signed POST. Prints the HTTP code on stdout; the body lands in $resp.
  local body sig
  body=$(OP="$op" PUB="$GITHUB_REPOSITORY" RUN="$run" STEP="$step" REG="$registry" python3 -c '
import json, os, datetime
print(json.dumps({"event": "registry-publish-admission", "op": os.environ["OP"], "publisher": os.environ["PUB"],
                  "run": os.environ["RUN"], "step": os.environ["STEP"], "registry": os.environ["REG"],
                  "at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")},
                 separators=(",", ":")))')
  # The HMAC is keyed from the ENVIRONMENT inside python3, never an argv (post-self-update-announcement.sh).
  sig=$(BODY="$body" SECRET="$CONTROL_WEBHOOK_SECRET" python3 -c 'import hashlib, hmac, os; print("sha256=" + hmac.new(os.environ["SECRET"].encode(), os.environ["BODY"].encode(), hashlib.sha256).hexdigest())')
  local code
  code=$(curl -sS -o "$resp" -w '%{http_code}' --connect-timeout 10 --max-time 60 \
    -X POST "$URL" -H 'Content-Type: application/json' -H "X-Hub-Signature-256: $sig" --data "$body") || code=000
  printf '%s\n' "${code:-000}"
}

resp="${RUNNER_TEMP:-/tmp}/registry-admission-resp-$$"
: > "$resp"
trap 'rm -f "$resp"' EXIT

if [ "$op" = complete ]; then
  code=$(post)
  if [ "${code:0:1}" = 2 ]; then
    echo "registry-publish-admission: $step completed on $URL (HTTP $code) — it no longer holds registry maintenance back."
  else
    echo "::warning::registry-publish-admission: control did not record the completion of $step (HTTP $code: $(tr -d '\n' < "$resp" | cut -c1-300)). Its admission keeps holding registry maintenance back until its 60-minute bound — the safe direction; the write itself already landed."
  fi
  exit 0
fi

wait_s="${MW_REGISTRY_ADMISSION_WAIT_SECONDS:-1200}"
poll_s="${MW_REGISTRY_ADMISSION_POLL_SECONDS:-60}"
[ "$wait_s" -le 1800 ] || wait_s=1800
deadline=$(( $(date +%s) + wait_s ))
while :; do
  code=$(post)
  answer=$(tr -d '\n' < "$resp" | cut -c1-600)
  case "$code" in
    200)
      echo "registry-publish-admission: $step ADMITTED to write $registry under the registry-maintenance lease ($URL): $answer"
      exit 0 ;;
    409)
      if [ $(( $(date +%s) + poll_s )) -gt "$deadline" ]; then
        echo "::error::registry-publish-admission: a registry cleanup still holds the maintenance lease after ${wait_s}s, so $step did NOT write $registry: $answer — re-run this job once the lease is released (the cleanup's read-only window or its DELETEs are in progress; nothing is lost by waiting)."
        exit 1
      fi
      echo "registry-publish-admission: $registry is under maintenance — waiting ${poll_s}s and asking again (until $(date -u -d "@$deadline" +%H:%M:%SZ 2>/dev/null || echo "+${wait_s}s")): $answer"
      sleep "$poll_s" ;;
    000) echo "::error::registry-publish-admission: control could not be reached at $URL — $step was NOT admitted, so it does not write $registry. This is never skipped: a publish nobody admitted could race a cleanup."; exit 1 ;;
    401) echo "::error::registry-publish-admission: control REFUSED the signature (401) — secrets.CONTROL_WEBHOOK_SECRET and control's Hosting:PlatformWebhookSecret are not byte-identical. $step was NOT admitted: $answer"; exit 1 ;;
    404) echo "::error::registry-publish-admission: control has no $ROUTE (404) — the control instance does not run the build that admits publishes yet. $step was NOT admitted."; exit 1 ;;
    *)   echo "::error::registry-publish-admission: control did not admit $step (HTTP $code): $answer — nothing was written to $registry."; exit 1 ;;
  esac
done

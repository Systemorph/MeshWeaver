#!/usr/bin/env bash
# Fail a test shard whose logs show a node's content degrading to an untyped JsonElement.
#
# 🚨 WHY THIS IS A GATE AND NOT A LOG LINE.
#
# A content type that is not registered on the hub reading it does NOT fail. The polymorphic
# converter cannot resolve the `$type`, degrades the value to a raw JsonElement, and everything
# downstream reads it as absent: `node.Content is MyType` misses, a view renders empty, a reactive
# wait never completes. No exception, no NACK, nothing to grep for after the fact.
#
# Contrast the message-payload case, which is LOUD: an unregistered inbound payload type raises
# "type 'X' is not registered in this hub's TypeRegistry" with a NACK policy attached. That
# asymmetry — loud for payloads, silent for content — is the whole reason this file exists.
#
# 🚨 WHAT CHANGED, AND WHY THE WARNING IS NOW LOAD-BEARING (MeshWeaver#3056).
#
# `MessageService.Post` used to log at Debug via `JsonSerializer.Serialize(ret, …)`. That serialize
# walked every posted payload through ObjectPolymorphicConverter.Write → typeRegistry.GetOrAddType,
# so EVERY posted type got registered as a side effect of LOGGING. #3056 removed that line (an OOM
# fix — the allocation was the failure), which was correct: logging must not register types.
#
# But it was also a net. It masked every place that relied on "this type is registered because it
# was once posted". With the net gone those places surface as this warning — which nothing was
# checking. Measured the same day: MeshWeaver.Plugins' NodeOperations validator test began passing
# a node whose Content "stayed an untyped JsonElement", the validator's `is` missed, it returned
# Valid, and a version-downgrade guard silently stopped guarding. It read as a validator BYPASS.
#
# Prod content types are covered by ContentTypeRegistrationSweep (static definitions, swept at boot)
# and by WithContentType running on instance-hub activation (dynamic types). This gate covers what
# neither does: catching a NEW gap the moment a test run first exhibits it.
set -euo pipefail

PHRASE='stayed an untyped JsonElement'
DIR="${1:?usage: check-untyped-content.sh <collected-logs-dir>}"
scan_err="$(mktemp)"
trap 'rm -f "$scan_err"' EXIT

if [ ! -d "$DIR" ]; then
  echo "::error::$DIR does not exist — this gate had nothing to scan. That is a FAILED sweep, not a clean one: it must never be possible to pass by scanning nothing."
  exit 1
fi

# 🚨 Deliberately NOT failing on an empty directory. A shard whose tests wrote no logs is a real,
# healthy state here (log files are collected from bin/*/test-logs, which not every project emits),
# so "no files" cannot be treated as evidence either way. The control arm for THIS gate is not the
# file count — it is UntypedContentDegradationGate, which fails the build if the phrase stops
# existing in the source that emits it, or if this script stops grepping the identical phrase.
# Without that test, a reworded log message would silently retire this gate.
# 🚨 SEPARATE "found nothing" FROM "the scan failed". grep exits 0 on a match, 1 on no match, and
# 2+ on a real error (unreadable file, bad path, I/O). The first version of this line was
# `$(grep … 2>/dev/null || true)`, which collapses all three into an empty string — so a scan that
# ERRORED reported "no degradation" and passed the gate. That is precisely the silent pass this file
# exists to prevent, committed inside the file that prevents it. Caught by the repo's own
# `CI's own shell` gate, which flags a captured command substitution that swallows stderr and status.
set +e
matches=$(grep -rl "$PHRASE" "$DIR" 2>"$scan_err")
scan_rc=$?
set -e

if [ "$scan_rc" -gt 1 ]; then
  echo "::error::the scan itself FAILED (grep exit $scan_rc) — this gate reached no verdict about $DIR."
  echo "Treat this as a failed sweep, not a clean one: an unreadable log is not an absent degradation."
  sed 's/^/  /' "$scan_err" 2>/dev/null | head -20
  exit 1
fi

if [ -z "$matches" ]; then
  echo "No content-type degradation in $DIR (scan exit $scan_rc)."
  exit 0
fi

echo "::error::A node's content degraded to an untyped JsonElement — a content type was not registered on the hub that read it."
echo ""
echo "This does NOT throw. The value reads as absent: an 'is MyType' check misses, a view renders"
echo "empty, a reactive wait never completes. It is caught here or not at all."
echo ""
echo "Occurrences:"
# Trim to the node path — the whole line carries a serialised payload and drowns the signal.
grep -rh "$PHRASE" "$DIR" 2>/dev/null \
  | sed -E 's/.*Content for ([^ ]+) stayed.*/  \1/' \
  | sort | uniq -c | sort -rn | head -20
echo ""
echo "Files: $(printf '%s' "$matches" | tr '\n' ' ')"
echo ""
echo "FIX: register the content type where it is READ — WithContentType<T>() on the hub's data"
echo "source, or WithType(typeof(T), nameof(T)). Do NOT paper over it with .ContentAs<T>() at the"
echo "call site: AGENTS.md is explicit that deserialising close to where the type IS registered"
echo "comes first, and .As<T>() on a payload read where the type was never registered hides a"
echo "routing mistake rather than fixing it."
exit 1

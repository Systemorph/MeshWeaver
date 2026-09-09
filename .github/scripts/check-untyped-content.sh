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
#
# 🚨 WHY THIS GATE COULD NOT FIRE FOR ITS FIRST FIVE DAYS (MeshWeaver#3625), AND WHAT KEYS IT NOW.
#
# The scan was correct and its input could not arrive. Both producers logged the degradation as a
# Warning with NO exception object, and the only sink that feeds `collected-logs/` takes a record
# IF AND ONLY IF `exception is not null && logLevel >= Warning`
# (XUnitFileLogger.Log / LoggingBuilderExtensions.Log -> TestTraceLog.AppendFault). So the phrase
# below could reach the scanned directory by construction NEVER. Measured on a run that really did
# degrade content: 817 trace records naming the test class, ZERO occurrences of the phrase it
# emitted. A gate whose input cannot arrive is indistinguishable from a gate that passes — the exact
# thing AGENTS.md forbids, committed inside a gate written to prevent it.
#
# Two things changed, and the second is the one that stops it recurring:
#
#   1. The degradation warnings now carry `MeshNodeContentDegradedException` — an exception object
#      that is constructed and never thrown, whose whole job is to satisfy the sink's predicate.
#      Reaching a sink is a property of the CALL, not of the level or the wording.
#
#   2. 🚨 The gate keys on that TYPE NAME first, and on the prose phrase only as a second net.
#      A message is a DESCRIPTION of the event; the type is the event's IDENTITY, bound by the
#      compiler at every construction site. That gives the coupling two independent bindings
#      instead of one — a rename is a compile-wide change, AND UntypedContentDegradationGate pins
#      this script's MARKER to nameof(MeshNodeContentDegradedException). The phrase is kept because
#      it costs nothing and catches a third seam whose wording ("stays", not "stayed") the phrase
#      grep never matched anyway.
#
# The reachability half — that a degradation record actually satisfies the sink predicate — is
# asserted by UntypedContentDegradationReachesTheTraceSinkTest, which drives the PRODUCTION emitter
# and evaluates the sink's own condition against the captured record. Modelled on
# EmitDeadAttributionReachesTheTraceSinkTest, which exists for the same trap one diagnostic over.
#
# 🚨 WHY THE KEY MOVED FROM THE EVENT TO THE VERDICT (MeshWeaver#3645).
#
# For its next stretch this gate could fire, and fired on the WRONG POPULATION. It keyed on
# MeshNodeContentDegradedException, which is emitted at the INSTANT of a read — and at that instant
# nothing can know whether the type registers a moment later. That is the ordinary state during a
# portal boot: a NodeType's runtime compile lands after the first readers have been served, and
# MeshWeaver#2952 exists precisely to re-type those readers when the registration arrives. The
# sibling seam one layer down (MeshNodeTypeSource) already said so in its own message — "content
# stays an untyped JsonElement FOR NOW … (a) … has not registered it YET, which is TRANSIENT … or
# (b) no declaration will ever claim this discriminator" — and, passing no exception, reddened
# nothing. One event, two descriptions, opposite CI consequences.
#
# Measured on #3628: two of LateContentTypeRegistrationTest's three cases degrade and RECOVER inside
# a single test — the recovery IS the assertion — and both produced a gate-reddening record. So a
# hit meant "content was unreadable at a read", never "content is unreadable".
#
# The denominator is now the FINAL state, and the platform already had the machinery for it.
# ContentDegradationRegistry keeps what a replica could not type; MeshNodeStreamCache.Dispose calls
# Unresolved(), which RE-ASKS the mesh-wide content-type registry per entry — both routes
# TryRecoverForNodeType takes, two pure map lookups, no content needed — and logs one
# MeshNodeContentUnresolvedException per node type that is STILL unresolvable. A boot that read
# before its compile landed leaves nothing behind; a discriminator no declaration will ever claim
# leaves exactly one record, naming the node type.
#
# So the marker below is the VERDICT, not the event. The per-read
# MeshNodeContentDegradedException records stay in the logs and stay useful — they are how you find
# WHICH read degraded once the verdict tells you a type never recovered — but they no longer red a
# shard on their own.
set -euo pipefail

# The verdict's IDENTITY. Primary key: compiler-bound, and pinned to this string by
# UntypedContentDegradationGate via nameof(...).
MARKER='MeshNodeContentUnresolvedException'
# The verdict's DESCRIPTION. Secondary net, kept deliberately: a per-test file log
# (MESHWEAVER_TEST_FILE_LOGS, on in node-repo-module-pack.yml) carries the formatted message with
# no exception attached at all. 🚨 It must match the FINAL report only — the per-read warnings say
# "stayed an untyped JsonElement", which is the transient population this gate no longer reds on.
PHRASE='was NEVER resolvable on this replica'
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
# 🚨 EXCLUDE THE BINARY ARTEFACTS BY NAME — and deliberately NOT with grep's own `-I`.
#
# `collected-logs/` is not all text: the workflow copies native mini dumps (`/tmp/coredumps/*.dmp`)
# and, for a host that died on a SIGNAL, a `symbols-<project>/` directory of `.dll` + `.pdb`. A core
# dump is a snapshot of the process's memory, so it can contain the marker string simply because the
# process once formatted it — a match there is not a log record and would be a FALSE RED (and, in
# the Occurrences list, an unparseable `Binary file … matches` line that the sed path-extraction
# below cannot reduce).
#
# `-I` would also fix that, and it is the wrong instrument: it decides by CONTENT, so a text log
# that happens to contain one NUL byte becomes invisible to the scan — silently, with the gate
# reporting "no degradation". That is a pass-by-scanning-nothing, which is the one outcome this file
# exists to make impossible; a false red is loud and investigable, a false green is not. Excluding
# by NAME can only ever skip a file whose extension is listed right here, in the open.
EXCLUDES=(--exclude='*.dmp' --exclude='*.dll' --exclude='*.pdb')

set +e
matches=$(grep -rl "${EXCLUDES[@]}" -e "$MARKER" -e "$PHRASE" "$DIR" 2>"$scan_err")
scan_rc=$?
set -e

if [ "$scan_rc" -gt 1 ]; then
  echo "::error::the scan itself FAILED (grep exit $scan_rc) — this gate reached no verdict about $DIR."
  echo "Treat this as a failed sweep, not a clean one: an unreadable log is not an absent degradation."
  sed 's/^/  /' "$scan_err" 2>/dev/null | head -20
  exit 1
fi

if [ -z "$matches" ]; then
  echo "No unresolved content type in $DIR (scan exit $scan_rc) — a transient degradation that later recovered is not a hit."
  exit 0
fi

echo "::error::A node's content was NEVER resolvable — a content type was not registered on the hub that read it, and still was not when the mesh ended."
echo ""
echo "This does NOT throw. The value reads as absent: an 'is MyType' check misses, a view renders"
echo "empty, a reactive wait never completes. It is caught here or not at all."
echo ""
echo "This is the VERDICT, not the event: the content-type registry was re-asked at teardown and"
echo "answered no, so the transient boot race (a NodeType read before its own compile landed) is"
echo "already excluded. Search the same logs for MeshNodeContentDegradedException to see WHICH"
echo "reads degraded on it."
echo ""
echo "Occurrences:"
# Reduce to the NODE TYPE — the verdict is per node type, not per node. Both record shapes name
# it: the log message says "content for nodeType <X> was NEVER…", the exception says
# "content for nodeType '<X>' (discriminator …)".
grep -rh "${EXCLUDES[@]}" -e "$MARKER" -e "$PHRASE" "$DIR" 2>/dev/null \
  | sed -E -e "s/.*content for nodeType '([^']*)'.*/  \1/" \
           -e "s/.*content for nodeType ([^ ]+) was NEVER.*/  \1/" \
  | sort | uniq -c | sort -rn | head -20
echo ""
echo "Files: $(printf '%s' "$matches" | tr '\n' ' ')"
echo ""
echo "FIX: register the content type where it is READ — WithContentType<T>() on the hub's data"
echo "source, or WithType(typeof(T), nameof(T)). Do NOT paper over it with .ContentAs<T>() at the"
echo "call site: AGENTS.md is explicit that deserialising close to where the type IS registered"
echo "comes first, and .As<T>() on a payload read where the type was never registered hides a"
echo "routing mistake rather than fixing it."
echo ""
echo "🚨 IF THIS IS YOUR TEST'S OWN FIXTURE — a node deliberately seeded with content the build"
echo "cannot read, to prove a write REFUSES it — then the fixture is what needs changing, not this"
echo "gate, and there is no allow-list on purpose: an exemption here would be a permanently green"
echo "check wearing a reason. Model 'present but unreadable as T' the way a running mesh actually"
echo "produces it: seed a value of a DIFFERENT, REGISTERED type. The stream cache then types it"
echo "happily (nothing degraded — so no record, and no hit here), while ContentAs<T>/As<T> still"
echo "answers null, because it recovers a foreign runtime type ONLY when the short name matches."
echo "That is the production case — a same-named record from another collectible assembly, or a"
echo "foreign type — so the fixture gets closer to the defect, not further from it. Malformed JSON"
echo "with no resolvable \$type models a DIFFERENT defect: content nothing can read, which is what"
echo "this gate exists to report."
exit 1

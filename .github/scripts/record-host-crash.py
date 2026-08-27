#!/usr/bin/env python3
"""Turn a test host that DIED into a failed test result inside its own .trx.

Why this exists (issue #2495)
-----------------------------
Pass/fail evidence and liveness evidence used to live in two different channels:

  * the ``.trx`` — what the host managed to *stream* before it stopped;
  * the ``[CI] <name> exit=<n>`` marker — whether the host *survived*.

Every reporter in the pipeline reads only the first. So a host that ran three tests,
reported them green and then took a SIGSEGV produced a trx saying ``3 passed`` and a
summary line, a per-shard check and a consolidated check all announcing a pass over a
process that had crashed. That is exactly what ``MeshWeaver.Content.Test`` did with
``exit=139``, and it is the failure mode this repo keeps being bitten by: a green signal
measuring the wrong thing.

The exit-marker gate does still red the shard — but it runs LAST and reports a number,
while everything a human or an agent actually reads has already said "passed". Adding a
seventh place that also checks the marker would not fix that; the next reporter added
would inherit the same blind spot.

So the fix is to make the evidence single-channel: a crash becomes a first-class
``<UnitTestResult outcome="Failed">`` in the very file every reporter already parses.
After that, no summary CAN report a pass over a crashed host, because the data it
summarises contains the failure.

Usage
-----
    record-host-crash.py <trx-path> <label> <exit-code> <classification>

``<trx-path>`` need not exist: a host killed at the wall-clock cap writes no trx at all,
and that case must produce evidence too — a complete one-result trx is written instead.

Exits 0 when the crash was recorded, 1 (with a ``::error::``) when it could not be.
A diagnostic that cannot fail is not a diagnostic, so this never degrades quietly.
"""

import os
import sys
import uuid
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
UNIT_TEST_TYPE = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b"
TEST_LIST_ID = "8c84fa94-04c1-424b-9868-57a2d4851a1d"

# TRX is schema-ordered; vstest and the GitHub reporters both reject/ignore out-of-order
# children. Rebuild the document in this order after inserting.
ELEMENT_ORDER = ["Times", "TestSettings", "Results", "TestDefinitions", "TestEntries",
                 "TestLists", "ResultSummary"]


def q(tag):
    return f"{{{NS}}}{tag}"


def now():
    return datetime.now(timezone.utc).isoformat()


def build_empty_run(label):
    ts = now()
    run = ET.Element(q("TestRun"), {"id": str(uuid.uuid4()),
                                    "name": f"{label} (host crash)",
                                    "runUser": os.environ.get("USER", "ci")})
    ET.SubElement(run, q("Times"), {"creation": ts, "queuing": ts, "start": ts, "finish": ts})
    ET.SubElement(run, q("TestSettings"), {"name": "default", "id": str(uuid.uuid4())})
    ET.SubElement(run, q("Results"))
    ET.SubElement(run, q("TestDefinitions"))
    ET.SubElement(run, q("TestEntries"))
    lists = ET.SubElement(run, q("TestLists"))
    ET.SubElement(lists, q("TestList"), {"name": "Results Not in a List", "id": TEST_LIST_ID})
    summary = ET.SubElement(run, q("ResultSummary"), {"outcome": "Failed"})
    ET.SubElement(summary, q("Counters"), {k: "0" for k in COUNTER_KEYS})
    return run


COUNTER_KEYS = ["total", "executed", "passed", "failed", "error", "timeout", "aborted",
                "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted",
                "disconnected", "warning", "completed", "inProgress", "pending"]


def ensure(parent, tag, attrib=None):
    found = parent.find(q(tag))
    if found is None:
        found = ET.SubElement(parent, q(tag), attrib or {})
    return found


def reorder(run):
    children = list(run)
    run[:] = sorted(children, key=lambda c: ELEMENT_ORDER.index(c.tag.split("}")[1])
                    if c.tag.split("}")[1] in ELEMENT_ORDER else len(ELEMENT_ORDER))


def record(trx_path, label, exit_code, classification):
    ET.register_namespace("", NS)

    existing_results = 0
    if os.path.isfile(trx_path) and os.path.getsize(trx_path) > 0:
        try:
            run = ET.parse(trx_path).getroot()
            existing_results = len(run.findall(f"{q('Results')}/{q('UnitTestResult')}"))
        except ET.ParseError as exc:
            # A truncated trx is itself crash evidence — do not silently discard the fact
            # that the host died mid-write. Start a fresh document that SAYS so.
            classification += f" | the host's own trx was unparseable ({exc}) — it died mid-write"
            run = build_empty_run(label)
    else:
        classification += " | the host wrote no trx at all"
        run = build_empty_run(label)

    test_id = str(uuid.uuid4())
    test_name = f"{label}.HOST_CRASHED"
    storage = os.path.abspath(trx_path)
    ts = now()

    results = ensure(run, "Results")
    result = ET.Element(q("UnitTestResult"), {
        "testName": test_name,
        "outcome": "Failed",
        "testType": UNIT_TEST_TYPE,
        "testListId": TEST_LIST_ID,
        "testId": test_id,
        "executionId": test_id,
        "computerName": os.environ.get("RUNNER_NAME", "unknown"),
        "duration": "00:00:00.0000000",
        "startTime": ts,
        "endTime": ts,
    })
    output = ET.SubElement(result, q("Output"))
    error = ET.SubElement(output, q("ErrorInfo"))
    ET.SubElement(error, q("Message")).text = (
        f"The test host for {label} exited {exit_code} — it did NOT complete.\n"
        f"{classification}\n\n"
        f"{existing_results} test result(s) were streamed before it stopped. Those results are "
        f"real, but they are NOT the run: every test that was in flight or not yet started is "
        f"missing from this file entirely, so the counts here are a floor, not a verdict.\n\n"
        f"This entry is synthesized by .github/scripts/record-host-crash.py so that the crash "
        f"lives in the same evidence every reporter reads. Without it the trx said "
        f"'{existing_results} passed, 0 failed' and every summary repeated that over a dead "
        f"process (issue #2495)."
    )
    ET.SubElement(error, q("StackTrace")).text = (
        f"exit={exit_code}\nat <test host process for {label}>"
    )
    results.append(result)

    definitions = ensure(run, "TestDefinitions")
    unit_test = ET.SubElement(definitions, q("UnitTest"),
                              {"name": test_name, "id": test_id, "storage": storage})
    ET.SubElement(unit_test, q("Execution"), {"id": test_id})
    ET.SubElement(unit_test, q("TestMethod"), {
        "codeBase": storage,
        "className": label,
        "name": "HOST_CRASHED",
        "adapterTypeName": "record-host-crash",
    })

    entries = ensure(run, "TestEntries")
    ET.SubElement(entries, q("TestEntry"),
                  {"testListId": TEST_LIST_ID, "testId": test_id, "executionId": test_id})

    lists = ensure(run, "TestLists")
    if lists.find(q("TestList")) is None:
        ET.SubElement(lists, q("TestList"), {"name": "Results Not in a List", "id": TEST_LIST_ID})

    summary = ensure(run, "ResultSummary", {"outcome": "Failed"})
    summary.set("outcome", "Failed")
    counters = ensure(summary, "Counters", {k: "0" for k in COUNTER_KEYS})
    for key in ("total", "executed", "failed"):
        counters.set(key, str(int(counters.get(key, "0") or "0") + 1))

    reorder(run)
    ET.ElementTree(run).write(trx_path, encoding="utf-8", xml_declaration=True)
    return test_name


def main(argv):
    if len(argv) != 5:
        print("::error::record-host-crash.py <trx-path> <label> <exit-code> <classification>",
              file=sys.stderr)
        return 1
    _, trx_path, label, exit_code, classification = argv
    try:
        name = record(trx_path, label, exit_code, classification)
    except Exception as exc:  # noqa: BLE001 — the failure must be loud, whatever it is
        print(f"::error::could not record the crash of {label} (exit={exit_code}) into "
              f"{trx_path}: {exc}. The trx therefore still reports whatever the host managed "
              f"to stream, which would let a summary announce a pass over a dead process — "
              f"the exact defect issue #2495 exists to remove. Failing rather than degrading.",
              file=sys.stderr)
        return 1
    print(f"recorded host crash as a failed trx result: {name} (exit={exit_code})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

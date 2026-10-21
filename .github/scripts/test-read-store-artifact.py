#!/usr/bin/env python3
"""Executes the REAL printer embedded in `.github/workflows/read-store-artifact.yml` over fixtures.

The lane cannot run on a pull request (it needs the in-cluster share), so nothing else ever runs its
Python before a reader depends on it. This takes the `run:` heredoc text out of the workflow file
verbatim and runs it against artifact trees built here:

- a log whose early minute holds a long record must not starve the END of the run (MeshWeaver#7035,
  review thread 4338716927: minute 39 went unprinted although all forty minutes fit the cap);
- a minute whose only record is longer than its share prints that record CUT, never nothing;
- a cut run's live results (no `end` record) name the failure and the attempt in flight;
- a Playwright 1.5x+ report embedded as `<template id="playwrightReportBase64">` is recognised.
"""
import base64
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest
import zipfile
from pathlib import Path

WORKFLOW = Path(__file__).resolve().parents[1] / "workflows" / "read-store-artifact.yml"


def printer_source():
    text = WORKFLOW.read_text()
    opener = "python3 - <<'PY'\n"
    start = text.index(opener) + len(opener)
    end = text.index("\n          PY\n", start)
    return textwrap.dedent(text[start:end]) + "\n"


SOURCE = printer_source()


def run_printer(root, limit=2000, log_pattern=r"(?i)\bfail", focus=""):
    with tempfile.NamedTemporaryFile("w", suffix=".py", delete=False) as script:
        script.write(SOURCE)
    env = dict(os.environ, READ_ROOT=str(root), READ_MAX_LINES=str(limit), READ_LOG_PATTERN=log_pattern,
               READ_FOCUS_PATTERN=focus, READ_STORE_ID="test-store", READ_REPOSITORY="o/r",
               READ_RUN_ID="1", READ_PATTERN="*")
    try:
        done = subprocess.run([sys.executable, "-I", script.name], env=env, capture_output=True, text=True)
    finally:
        os.unlink(script.name)
    if done.returncode != 0:
        raise AssertionError(f"printer exited {done.returncode}\n{done.stdout}\n{done.stderr}")
    return done.stdout


def write_log(root, records):
    """records: (minute, second, line_count) — one `fail:` header plus indented continuations."""
    folder = Path(root) / "art" / "mesh-diagnostics"
    folder.mkdir(parents=True)
    out = []
    for minute, second, count in records:
        ts = f"2026-10-21T13:{minute:02d}:{second:02d}.000000000Z"
        out.append(f"portal-1  | {ts} fail: MeshWeaver.X[0]")
        out.extend(f"portal-1  | {ts}       continuation {k}" for k in range(count - 1))
    (folder / "portal.log").write_text("\n".join(out) + "\n")


def minutes_printed(text):
    return {int(m) for m in re.findall(r"2026-10-21T13:(\d\d):\d\d\.\d+Z fail:", text)}


def log_lines_printed(text):
    return [line for line in text.splitlines() if line.startswith("  portal-1")]


class LogWindow(unittest.TestCase):
    def test_a_long_early_record_does_not_starve_the_end_of_the_run(self):
        # 2 + 39 * 50 = 1952 lines: every minute fits under 2000 if minute 0 takes its SHORT record.
        with tempfile.TemporaryDirectory() as root:
            write_log(root, [(0, 1, 100), (0, 2, 2)] + [(m, 1, 50) for m in range(1, 40)])
            text = run_printer(root)
        self.assertEqual(minutes_printed(text), set(range(40)))
        self.assertLessEqual(len(log_lines_printed(text)), 2000)

    def test_a_record_longer_than_its_minutes_share_is_cut_not_dropped(self):
        # 40 minutes of one 80-line record each: the share is 50, so each prints cut to 50.
        with tempfile.TemporaryDirectory() as root:
            write_log(root, [(m, 1, 80) for m in range(40)])
            text = run_printer(root)
        self.assertEqual(minutes_printed(text), set(range(40)))
        self.assertLessEqual(len(log_lines_printed(text)), 2000)
        self.assertEqual(text.count("cut to its minute's share"), 40)

    def test_under_the_cap_every_selected_record_prints_whole(self):
        with tempfile.TemporaryDirectory() as root:
            write_log(root, [(m, 1, 3) for m in range(10)])
            text = run_printer(root)
        self.assertEqual(len(log_lines_printed(text)), 30)
        self.assertNotIn("omitted", text)


class LiveResults(unittest.TestCase):
    def test_a_cut_run_names_its_failure_and_the_attempt_in_flight(self):
        def rec(event, **fields):
            return json.dumps({"kind": "live-results", "v": 1, "event": event, "at": "2026-10-21T13:00:00.000Z", **fields})
        with tempfile.TemporaryDirectory() as root:
            folder = Path(root) / "art" / "e2e" / "live-results"
            folder.mkdir(parents=True)
            failing = dict(id="a", retry=0, file="x.spec.ts", line=3, title=["chromium", "x.spec.ts", "the cell answers"],
                           startTime="2026-10-21T13:00:01.000Z")
            stuck = dict(id="b", retry=0, file="x.spec.ts", line=9, title=["chromium", "x.spec.ts", "the next cell"],
                         startTime="2026-10-21T13:00:05.000Z")
            (folder / "results.jsonl").write_text("\n".join([
                rec("begin", shard="2/3", total=2),
                rec("testBegin", **failing),
                rec("testEnd", **failing, duration=1200, status="failed", expectedStatus="passed",
                    errors=[{"message": "Error: the cell answered 1, the exercise wants 2"}]),
                rec("testBegin", **stuck),
                '{"kind":"live-results","v":1,"event":"testE',  # the line the kill cut in half
            ]) + "\n")
            text = run_printer(root)
        self.assertIn("NO end record — the run was CUT", text)
        self.assertIn("the cell answered 1, the exercise wants 2", text)
        self.assertIn("IN FLIGHT AT THE CUT chromium › x.spec.ts › the next cell", text)


class HtmlReport(unittest.TestCase):
    def test_the_template_embedding_of_a_current_report_is_recognised(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("report.json", json.dumps({"stats": {"total": 1, "unexpected": 1}}))
            archive.writestr("abc.json", json.dumps({"fileName": "x.spec.ts", "tests": [{
                "title": "the cell answers", "path": [], "outcome": "unexpected", "location": {"line": 3},
                "results": [{"retry": 0, "status": "failed", "duration": 10,
                             "errors": [{"message": "Error: template-embedded failure text"}]}]}]}))
        data = base64.b64encode(buffer.getvalue()).decode()
        with tempfile.TemporaryDirectory() as root:
            folder = Path(root) / "art" / "e2e" / "playwright-report"
            folder.mkdir(parents=True)
            (folder / "index.html").write_text(
                f'<html><body></body></html>\n<template id="playwrightReportBase64">data:application/zip;base64,{data}</template>')
            text = run_printer(root)
        self.assertIn("##### Playwright report", text)
        self.assertIn("template-embedded failure text", text)
        self.assertNotIn("no Playwright HTML report", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)

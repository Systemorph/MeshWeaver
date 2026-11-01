"""Regression coverage for declaration gates reading corrected PR metadata."""

import base64
import copy
import email.message
import http.client
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
import urllib.error
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "reader", Path(__file__).with_name("read-current-pr-body.py"))
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)


class CurrentPrBodyTest(unittest.TestCase):
    def setUp(self):
        self.pr = {"number": 3844, "head": {"sha": "a" * 40},
                   "base": {"repo": {"full_name": "Systemorph/MeshWeaver"}},
                   "body": "old body without the required declaration"}
        self.event = {"pull_request": copy.deepcopy(self.pr)}

    def read(self, current):
        def api(path):
            self.assertEqual(path, "repos/Systemorph/MeshWeaver/pulls/3844")
            return current
        return reader.read_body(self.event, "Systemorph/MeshWeaver", api)

    def test_corrected_body_replaces_frozen_event_and_preserves_literal_text(self):
        self.pr["body"] = "Mirror-sync: tracked in #3844\n`touch /tmp/unwanted` $(echo nope)\n"
        self.assertEqual(self.read(self.pr), self.pr["body"])
        self.assertNotEqual(self.pr["body"], self.event["pull_request"]["body"])

    def test_deleted_declaration_is_not_reused_from_event(self):
        self.event["pull_request"]["body"] = "Mirror-sync: tracked in #3844"
        for body in (None, ""):
            with self.subTest(body=body):
                self.pr["body"] = body
                self.assertEqual(self.read(self.pr), "")

    def test_new_head_cannot_authorize_an_obsolete_run(self):
        self.pr["head"]["sha"] = "b" * 40
        with self.assertRaisesRegex(ValueError, "head changed"):
            self.read(self.pr)

    def test_wrong_pr_and_wrong_repository_are_refused(self):
        for field in ("number", "repository"):
            with self.subTest(field=field):
                current = copy.deepcopy(self.pr)
                if field == "number":
                    current["number"] += 1
                else:
                    current["base"]["repo"]["full_name"] = "Other/Repo"
                with self.assertRaisesRegex(ValueError, "different pull request"):
                    self.read(current)

    def test_missing_or_malformed_body_is_not_an_empty_success(self):
        del self.pr["body"]
        with self.assertRaises(KeyError):
            self.read(self.pr)
        self.pr["body"] = {"unreadable": True}
        with self.assertRaises(ValueError):
            self.read(self.pr)

    def test_failed_rest_read_never_falls_back_to_event(self):
        def fail(_):
            raise reader.ApiRefused("HTTP 401 for GET repos/Systemorph/MeshWeaver/pulls/3844")
        with self.assertRaises(reader.ApiRefused):
            reader.read_body(self.event, "Systemorph/MeshWeaver", fail)

    def test_failed_refresh_removes_previous_output_and_exits_red(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "body.md"
            output.write_text("Mirror-sync: stale declaration")
            with patch("sys.argv", ["read-current-pr-body.py", "--output", str(output)]), \
                 patch.dict("os.environ", {"GITHUB_EVENT_PATH": directory + "/absent.json"}), \
                 patch("builtins.print"):
                self.assertEqual(reader.main(), 1)
            self.assertFalse(output.exists())


def http_error(status, headers=None, body=""):
    message = email.message.Message()
    for key, value in (headers or {}).items():
        message[key] = value
    return urllib.error.HTTPError("https://api.github.com/x", status, "error", message,
                                  io.BytesIO(body.encode("utf-8")))


class FailingBody(io.BytesIO):
    """An error body whose read fails mid-connection (reset, timeout, short read)."""

    def __init__(self, failure):
        super().__init__(b"")
        self.failure = failure

    def read(self, *args):
        raise self.failure


def unreadable_http_error(status, failure, headers=None):
    message = email.message.Message()
    for key, value in (headers or {}).items():
        message[key] = value
    return urllib.error.HTTPError("https://api.github.com/x", status, "error", message,
                                  FailingBody(failure))


class Truncated(io.BytesIO):
    """A response whose connection closed before the full Content-Length body arrived."""

    def read(self, *args):
        raise http.client.IncompleteRead(b'{"numb', 40)


class FakeApi:
    """A scripted GitHub: each call pops the next answer (an HTTPError, an OSError or a dict)."""

    def __init__(self, *answers):
        self.answers = list(answers)
        self.calls = 0
        self.now = 1_000_000.0
        self.slept = []

    def urlopen(self, request, timeout):
        self.calls += 1
        answer = self.answers.pop(0)
        if isinstance(answer, BaseException):
            raise answer
        if isinstance(answer, Truncated):
            return answer
        return io.BytesIO(json.dumps(answer).encode("utf-8"))

    def sleep(self, seconds):
        self.slept.append(seconds)
        self.now += seconds

    def clock(self):
        return self.now

    def get(self, budget=240):
        return reader.api_get("repos/o/r/pulls/1", token="t", base_url="https://api.example",
                              budget_seconds=budget, urlopen=self.urlopen,
                              sleep=self.sleep, clock=self.clock)


class Download(io.BytesIO):
    """A 200 artifact body; `headers` carries its Content-Length (may advertise more than sent)."""

    def __init__(self, data, content_length=None):
        super().__init__(data)
        self.headers = email.message.Message()
        self.headers["Content-Length"] = str(len(data) if content_length is None else content_length)


class DownloadApi(FakeApi):
    def urlopen(self, request, timeout):
        self.calls += 1
        answer = self.answers.pop(0)
        if isinstance(answer, BaseException):
            raise answer
        return answer

    def download(self, dest, budget=240):
        return reader.api_get("repos/o/r/actions/artifacts/1/zip", token="t",
                              base_url="https://api.example", budget_seconds=budget,
                              urlopen=self.urlopen, sleep=self.sleep, clock=self.clock, dest=dest)


class DownloadTest(unittest.TestCase):
    """`dest`: a short body is a transport failure; a local storage failure is never one."""

    def test_complete_body_is_written(self):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "a.zip"
            api = DownloadApi(Download(b"PK" * 1000))
            self.assertIsNone(api.download(dest))
            self.assertEqual((dest.read_bytes(), api.calls), (b"PK" * 1000, 1))

    def test_early_eof_short_of_content_length_is_retried_then_no_verdict(self):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "a.zip"
            # The connection closed after 10 of 2000 advertised bytes: read() returns b"" at EOF.
            api = DownloadApi(Download(b"PK" * 5, content_length=2000), Download(b"PK" * 1000))
            self.assertIsNone(api.download(dest))
            self.assertEqual((dest.read_bytes(), api.calls), (b"PK" * 1000, 2))
            always = DownloadApi(*[Download(b"PK", content_length=2000) for _ in range(50)])
            with self.assertRaises(reader.ApiUnavailable) as caught:
                always.download(dest, budget=30)
            self.assertIn("transport failure: IncompleteRead", caught.exception.sentence())

    def test_body_without_content_length_is_accepted_as_sent(self):
        # Negative control: with nothing advertised there is nothing to be short of.
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "a.zip"
            body = Download(b"PK")
            del body.headers["Content-Length"]
            self.assertIsNone(DownloadApi(body).download(dest))
            self.assertEqual(dest.read_bytes(), b"PK")

    def test_local_write_failure_surfaces_at_once_never_as_github_unavailable(self):
        real_open = open

        def failing_open(path, mode="r", *args, **kwargs):
            handle = real_open(path, mode, *args, **kwargs)
            if "w" in mode and str(path).endswith("a.zip") and failing_open.armed:
                def write(_):
                    raise OSError(28, "No space left on device")
                handle.write = write
            failing_open.armed = True  # the up-front probe open succeeds; the streaming one fails
            return handle
        failing_open.armed = False

        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "a.zip"
            api = DownloadApi(Download(b"PK" * 1000), Download(b"PK" * 1000))
            with patch("builtins.open", failing_open):
                with self.assertRaises(reader.DestinationError) as caught:
                    api.download(dest)
            self.assertEqual((api.calls, api.slept), (1, []))
            self.assertIn("No space left on device", str(caught.exception))
            self.assertNotIsInstance(caught.exception, OSError)

    def test_unopenable_destination_surfaces_at_once(self):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "missing-dir" / "a.zip"
            api = DownloadApi(Download(b"PK"))
            with self.assertRaises(reader.DestinationError):
                api.download(dest)
            self.assertEqual(api.calls, 0)


RATE_LIMIT_BODY = '{"message":"API rate limit exceeded for installation ID 1."}'


class ApiBudgetTest(unittest.TestCase):
    """Negative controls: an unavailable API is NO VERDICT, never a finding and never a pass."""

    def test_200_is_returned_without_retry(self):
        api = FakeApi({"number": 1})
        self.assertEqual(api.get(), {"number": 1})
        self.assertEqual((api.calls, api.slept), (1, []))

    def test_installation_rate_limit_past_the_budget_is_no_verdict_naming_the_reset(self):
        reset = 1_000_000 + 1800
        api = FakeApi(http_error(403, {"x-ratelimit-remaining": "0", "x-ratelimit-reset": str(reset)},
                                 RATE_LIMIT_BODY))
        with self.assertRaises(reader.ApiUnavailable) as caught:
            api.get()
        # A wait the budget cannot cover is not slept at all.
        self.assertEqual((api.calls, api.slept), (1, []))
        sentence = caught.exception.sentence()
        self.assertIn("NO VERDICT — GitHub API rate-limited until 1970-01-12T14:16:40Z", sentence)
        self.assertIn("re-run after it", sentence)

    def test_rate_limit_inside_the_budget_is_waited_out_honouring_retry_after(self):
        api = FakeApi(http_error(403, {"retry-after": "7"}, RATE_LIMIT_BODY), {"number": 1})
        self.assertEqual(api.get(), {"number": 1})
        self.assertEqual(api.slept, [7])

    def test_rate_limit_text_alone_without_headers_is_still_rate_limit(self):
        api = FakeApi(http_error(403, {}, RATE_LIMIT_BODY), {"number": 1})
        self.assertEqual(api.get(), {"number": 1})
        self.assertEqual(api.calls, 2)

    def test_5xx_and_transport_failures_are_retried_then_no_verdict(self):
        api = FakeApi(http_error(502), OSError("reset by peer"), {"number": 1})
        self.assertEqual(api.get(), {"number": 1})
        self.assertEqual(api.calls, 3)
        always = FakeApi(*[http_error(503)] * 50)
        with self.assertRaises(reader.ApiUnavailable) as caught:
            always.get(budget=30)
        self.assertLessEqual(sum(always.slept), 30)
        self.assertIn("NO VERDICT — GitHub API unavailable (HTTP 503", caught.exception.sentence())

    def test_truncated_response_is_transient_then_no_verdict_never_a_traceback(self):
        api = FakeApi(Truncated(), {"number": 1})
        self.assertEqual(api.get(), {"number": 1})
        self.assertEqual(api.calls, 2)
        always = FakeApi(*[Truncated() for _ in range(50)])
        with self.assertRaises(reader.ApiUnavailable) as caught:
            always.get(budget=30)
        self.assertIn("transport failure: IncompleteRead", caught.exception.sentence())

    def test_error_body_read_failure_is_transient_then_no_verdict_never_a_traceback(self):
        # #7457: the error body is read over the same connection; a reset/timeout there must not
        # escape the HTTPError handler (exit 1, "defect") — it is an outage, so NO VERDICT.
        for status in (503, 502, 429, 403):
            for failure in (TimeoutError("read timed out"), ConnectionResetError("reset"),
                            http.client.IncompleteRead(b"{", 40)):
                with self.subTest(status=status, failure=type(failure).__name__):
                    api = FakeApi(unreadable_http_error(status, failure), {"number": 1})
                    self.assertEqual(api.get(), {"number": 1})
                    self.assertEqual(api.calls, 2)
                    always = FakeApi(*[unreadable_http_error(status, failure) for _ in range(50)])
                    with self.assertRaises(reader.ApiUnavailable) as caught:
                        always.get(budget=30)
                    self.assertIn(f"HTTP {status}", caught.exception.sentence())
                    self.assertIn("error body unreadable", caught.exception.sentence())

    def test_error_body_read_failure_keeps_deliberate_refusals_immediate(self):
        # Negative control: 401/404 are refusals whatever their body says, so an unreadable body
        # changes nothing — refused at once, no retry.
        for status in (401, 404):
            with self.subTest(status=status):
                api = FakeApi(unreadable_http_error(status, TimeoutError("read timed out")))
                with self.assertRaises(reader.ApiRefused):
                    api.get()
                self.assertEqual(api.calls, 1)

    def test_deliberate_refusals_fail_at_once_without_retry(self):
        for status, body in ((404, '{"message":"Not Found"}'), (401, "{}"),
                             (403, '{"message":"Resource not accessible by integration"}')):
            with self.subTest(status=status):
                api = FakeApi(http_error(status, {"x-ratelimit-remaining": "4000"}, body))
                with self.assertRaises(reader.ApiRefused):
                    api.get()
                self.assertEqual(api.calls, 1)

    def test_main_exits_no_verdict_red_and_removes_previous_output(self):
        with tempfile.TemporaryDirectory() as directory:
            event = Path(directory) / "event.json"
            event.write_text(json.dumps({"pull_request": {
                "number": 3844, "head": {"sha": "a" * 40},
                "base": {"repo": {"full_name": "Systemorph/MeshWeaver"}}}}))
            output = Path(directory) / "body.md"
            output.write_text("Mirror-sync: stale declaration")
            printed = io.StringIO()

            def limited(path):
                raise reader.ApiUnavailable(path, "HTTP 403 rate limit", 1_000_000)
            with patch("sys.argv", ["read-current-pr-body.py", "--output", str(output)]), \
                 patch.dict("os.environ", {"GITHUB_EVENT_PATH": str(event),
                                           "GITHUB_REPOSITORY": "Systemorph/MeshWeaver"}), \
                 patch.object(reader, "read_pr", limited), patch("sys.stdout", printed):
                self.assertEqual(reader.main(), reader.NO_VERDICT_EXIT)
            self.assertFalse(output.exists())
            self.assertIn("NO VERDICT — GitHub API rate-limited until", printed.getvalue())
            self.assertNotIn("Could not read the current PR body", printed.getvalue())


class SharedBodyTest(unittest.TestCase):
    """One read per run attempt — and never a stale body on a re-run of failed jobs."""

    def setUp(self):
        self.event = {"pull_request": {"head": {"sha": "a" * 40}}}
        self.env = {"SHARED_PR_BODY_B64": base64.b64encode("Pairs-with: none — ü".encode()).decode(),
                    "SHARED_PR_BODY_ATTEMPT": "1", "SHARED_PR_BODY_HEAD": "a" * 40,
                    "GITHUB_RUN_ATTEMPT": "1"}

    def test_same_attempt_same_head_is_reused(self):
        self.assertEqual(reader.shared_body(self.event, self.env), ("Pairs-with: none — ü", None))

    def test_empty_shared_body_is_a_real_empty_body(self):
        self.env["SHARED_PR_BODY_B64"] = ""
        self.assertEqual(reader.shared_body(self.event, self.env), ("", None))

    def test_rerun_of_failed_jobs_reads_the_current_body_instead(self):
        self.env["GITHUB_RUN_ATTEMPT"] = "2"
        body, why = reader.shared_body(self.event, self.env)
        self.assertIsNone(body)
        self.assertIn("re-run of failed jobs", why)

    def test_other_head_missing_or_corrupt_read_is_not_reused(self):
        for change in ({"SHARED_PR_BODY_HEAD": "b" * 40}, {"SHARED_PR_BODY_ATTEMPT": ""},
                       {"SHARED_PR_BODY_B64": "not base64!"}):
            with self.subTest(change=change):
                body, why = reader.shared_body(self.event, {**self.env, **change})
                self.assertIsNone(body)
                self.assertTrue(why)


if __name__ == "__main__":
    unittest.main()

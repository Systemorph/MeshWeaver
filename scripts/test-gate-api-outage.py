"""An API OUTAGE is NO VERDICT in every gate that asks GitHub something, never a missing grant (MeshWeaver#7448).

The three resolvers below used to answer every 403 with "the token is valid but lacks <grant> here,
OR IS RATE-LIMITED" — a red that reads as a finding about the pull request when the cause was the
repository's installation budget running dry (2026-10-27, 20:37-21:14Z). Each now routes through
`scripts/read-current-pr-body.py`'s `api_get`. These cases drive them through a FAKE transport —
`urllib.request.urlopen` and `build_opener` are both replaced, so the same cases run against the
old code (which called `urlopen` directly) and fail there — and nothing reaches GitHub.
"""

import email.message
import importlib.util
import io
from pathlib import Path
import sys
import unittest
import urllib.error
import urllib.request
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def _load(name, rel):
    spec = importlib.util.spec_from_file_location(name, ROOT / rel)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


pair = _load("check_cross_repo_pair", "scripts/check-cross-repo-pair.py")
allow = _load("transitional_allow_under_test", "scripts/transitional_allow.py")
rules = _load("check_shared_rules", ".github/scripts/check-shared-rules.py")

RATE_BODY = '{"message":"API rate limit exceeded for installation ID 12345."}'
PLAIN_403 = '{"message":"Resource not accessible by integration"}'


def http_error(code, body, **headers):
    h = email.message.Message()
    for k, v in headers.items():
        h[k.replace("_", "-")] = str(v)
    return urllib.error.HTTPError("https://api.github.com/x", code, "x", h, io.BytesIO(body.encode()))


class _Answer:
    def __init__(self, text):
        self.text, self.status = text, 200

    def read(self, *a):
        return self.text.encode()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class FakeTransport:
    """Scripted answers; patches every way the code under test can open a URL."""

    def __init__(self, *answers):
        self.answers, self.calls = list(answers), 0

    def open(self, req, timeout=None, *a, **k):
        self.calls += 1
        answer = self.answers.pop(0) if len(self.answers) > 1 else self.answers[0]
        if isinstance(answer, BaseException):
            raise answer
        return answer

    def __enter__(self):
        opener = type("Opener", (), {"open": lambda _self, req, timeout=None: self.open(req, timeout)})()
        self._patches = [patch.object(urllib.request, "urlopen", self.open),
                         patch.object(urllib.request, "build_opener", lambda *a, **k: opener),
                         # no real waiting: a reset an hour away is past any budget, so nothing sleeps
                         patch("time.sleep", lambda s: None)]
        for p in self._patches:
            p.start()
        return self

    def __exit__(self, *exc):
        for p in self._patches:
            p.stop()
        return False


def rate_limited_far():
    import time
    return http_error(403, RATE_BODY, x_ratelimit_remaining=0, x_ratelimit_reset=int(time.time()) + 3600)


class OutageIsNoVerdict(unittest.TestCase):
    def assert_no_verdict(self, message):
        self.assertIn("NO VERDICT", message)
        self.assertIn("rate-limited until", message)
        self.assertNotIn("lacks", message)

    def test_cross_repo_pair_counterpart_read(self):
        with FakeTransport(rate_limited_far()):
            with self.assertRaises(pair.PairResolutionError) as caught:
                pair._api("https://api.github.com/repos/o/r/pulls/1", "t")
        self.assert_no_verdict(str(caught.exception))

    def test_transitional_allow_reference_read(self):
        with FakeTransport(rate_limited_far()):
            with self.assertRaises(allow.AllowResolutionError) as caught:
                allow._api("https://api.github.com/repos/o/r/issues/1", "t")
        self.assert_no_verdict(str(caught.exception))

    def test_shared_rules_agents_md_read(self):
        with FakeTransport(rate_limited_far()):
            with self.assertRaises(rules.Problem) as caught:
                rules.fetch_agents_md("o/r", "t")
        self.assert_no_verdict(str(caught.exception))

    def test_rate_limit_inside_the_budget_is_waited_out(self):
        with FakeTransport(http_error(403, RATE_BODY, retry_after=1), _Answer('{"merged_at": null}')) as t:
            self.assertEqual(pair._api("https://api.github.com/repos/o/r/pulls/1", "t"), {"merged_at": None})
        self.assertEqual(t.calls, 2)

    def test_agents_md_is_read_raw(self):
        with FakeTransport(_Answer("# AGENTS.md\n")):
            self.assertEqual(rules.fetch_agents_md("o/r", "t"), "# AGENTS.md\n")


class LedgerLaneHasNoOutageBlindProbe(unittest.TestCase):
    """The ci-failure lane must reach the ledger's outage-aware client with nothing in front of it
    that asks GitHub through a client that cannot tell a rate limit from a missing grant. A shell
    probe of `/issues` did exactly that: a rate-limited 403 printed "it must be the caller's
    GITHUB_TOKEN … granting issues: write" and exited before the ledger could file or say NO VERDICT."""

    def test_no_step_calls_the_github_api_outside_the_ledger(self):
        text = (ROOT / ".github/workflows/node-repo-ci-failure.yml").read_text(encoding="utf-8")
        calls = [n for n, line in enumerate(text.splitlines(), 1)
                 if "api.github.com" in line and not line.lstrip().startswith("#")]
        self.assertEqual(calls, [], "node-repo-ci-failure.yml calls api.github.com outside the ledger "
                                    f"at line(s) {calls}: a rate limit there is read as a missing grant")

    def test_a_prepared_event_is_delivered_when_the_ledger_step_fails_after_writing_it(self):
        """The ledger files the red and writes the event, THEN exits 3 when the run's jobs could not
        be listed (#7604 review). A step `if:` without a status function is implicitly
        `success() && …`, so the signed POST and its verification were skipped and the control
        portal never heard of a red the ledger had already filed."""
        lines = (ROOT / ".github/workflows/node-repo-ci-failure.yml").read_text(encoding="utf-8").splitlines()
        conditions = [line.split("if:", 1)[1].strip() for line in lines
                      if line.lstrip().startswith("if:") and "steps.ledger.outputs.post" in line]
        self.assertEqual(len(conditions), 2, f"expected the POST and the verification steps: {conditions}")
        for condition in conditions:
            self.assertIn("!cancelled()", condition,
                          f"`{condition}` skips delivery whenever the ledger step exited non-zero "
                          "after writing the event")


class RefusalStaysAFinding(unittest.TestCase):
    """NEGATIVE CONTROL: a real permission 403 is still the finding that names its grant, unretried."""

    def test_each_resolver_names_the_grant_on_a_plain_403(self):
        cases = [
            (lambda: pair._api("https://api.github.com/repos/o/r/pulls/1", "t"), pair.PairResolutionError),
            (lambda: allow._api("https://api.github.com/repos/o/r/issues/1", "t"), allow.AllowResolutionError),
            (lambda: rules.fetch_agents_md("o/r", "t"), rules.Problem),
        ]
        for call, error in cases:
            with self.subTest(error=error.__name__):
                with FakeTransport(http_error(403, PLAIN_403, x_ratelimit_remaining=4000)) as t:
                    with self.assertRaises(error) as caught:
                        call()
                self.assertIn("lacks", str(caught.exception))
                self.assertNotIn("NO VERDICT", str(caught.exception))
                self.assertEqual(t.calls, 1)


if __name__ == "__main__":
    unittest.main()

"""Caller authentication for s1-extract: a projected ServiceAccount token, validated by TokenReview.

The only callers are in-cluster workloads (the instance that owns the CRM). Each sends a projected
ServiceAccount token minted for the audience ``s1-extract``; this service asks the Kubernetes API
whether it is valid (``authentication.k8s.io/v1`` TokenReview, which needs the
``system:auth-delegator`` ClusterRole) and then checks two things itself: the audience, and that the
ServiceAccount is on the allow-list. No shared secret, nothing to rotate, no credential in a value file.

Pure except for :func:`kube_token_review`, which is injected so the decision is testable offline.
"""
from __future__ import annotations

import hashlib
import json
import os
import ssl
import time
import urllib.request
from dataclasses import dataclass
from typing import Callable

SA_DIR = "/var/run/secrets/kubernetes.io/serviceaccount"
CACHE_S = 60.0


@dataclass(frozen=True)
class Verdict:
    allowed: bool
    who: str = ""
    why: str = ""


def decide(review: dict, audience: str, allowed: frozenset[str]) -> Verdict:
    """The decision on a TokenReview answer. Pure."""
    status = (review or {}).get("status") or {}
    if not status.get("authenticated"):
        return Verdict(False, why="token not authenticated")
    user = (status.get("user") or {}).get("username", "")
    if audience not in (status.get("audiences") or []):
        return Verdict(False, who=user, why=f"token audience is not {audience!r}")
    if user not in allowed:
        return Verdict(False, who=user, why="service account not on the allow-list")
    return Verdict(True, who=user)


def kube_token_review(token: str, audience: str) -> dict:
    """POST a TokenReview to the in-cluster API server, as this pod's own ServiceAccount."""
    host = os.environ["KUBERNETES_SERVICE_HOST"]
    port = os.environ.get("KUBERNETES_SERVICE_PORT", "443")
    with open(f"{SA_DIR}/token", encoding="utf-8") as fh:
        own = fh.read().strip()
    ctx = ssl.create_default_context(cafile=f"{SA_DIR}/ca.crt")
    body = json.dumps({"apiVersion": "authentication.k8s.io/v1", "kind": "TokenReview",
                       "spec": {"token": token, "audiences": [audience]}}).encode()
    req = urllib.request.Request(f"https://{host}:{port}/apis/authentication.k8s.io/v1/tokenreviews",
                                 data=body, method="POST",
                                 headers={"Authorization": f"Bearer {own}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req, context=ctx, timeout=10) as resp:
        return json.loads(resp.read())


class Authenticator:
    """Bearer → Verdict, with a short cache keyed by the token's hash (never the token)."""

    def __init__(self, audience: str, allowed: frozenset[str],
                 review: Callable[[str, str], dict] = kube_token_review, clock: Callable[[], float] = time.monotonic):
        self.audience, self.allowed, self.review, self.clock = audience, allowed, review, clock
        self._cache: dict[str, tuple[float, Verdict]] = {}

    def check(self, authorization: str | None) -> Verdict:
        if not authorization or not authorization.startswith("Bearer "):
            return Verdict(False, why="no bearer token")
        token = authorization[len("Bearer "):].strip()
        key = hashlib.sha256(token.encode()).hexdigest()
        now = self.clock()
        hit = self._cache.get(key)
        if hit and now - hit[0] < CACHE_S:
            return hit[1]
        try:
            verdict = decide(self.review(token, self.audience), self.audience, self.allowed)
        except Exception as exc:  # the API server is unreachable: refuse, never allow
            return Verdict(False, why=f"token review failed ({type(exc).__name__})")
        self._cache[key] = (now, verdict)
        if len(self._cache) > 1024:
            self._cache = {k: v for k, v in self._cache.items() if now - v[0] < CACHE_S}
        return verdict

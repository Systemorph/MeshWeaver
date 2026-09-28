"""Offline tests: the auth decision, request limits, and the HTTP surface with a fake model.

    S1_NO_AUTOSTART=1 python3 -m pytest -q deploy/s1-extract/app   (or: python3 app/test_server.py)
"""
import os
import sys

os.environ["S1_NO_AUTOSTART"] = "1"
sys.path.insert(0, os.path.dirname(__file__))

from fastapi.testclient import TestClient  # noqa: E402

import auth  # noqa: E402
import server  # noqa: E402

SA = "system:serviceaccount:crm-ns:portal"


def review(authenticated=True, user=SA, audiences=("s1-extract",)):
    return {"status": {"authenticated": authenticated, "user": {"username": user}, "audiences": list(audiences)}}


def test_decide():
    allowed = frozenset({SA})
    assert auth.decide(review(), "s1-extract", allowed).allowed
    assert not auth.decide(review(authenticated=False), "s1-extract", allowed).allowed
    assert "audience" in auth.decide(review(audiences=("other",)), "s1-extract", allowed).why
    assert "allow-list" in auth.decide(review(user="system:serviceaccount:x:y"), "s1-extract", allowed).why


def test_authenticator_caches_and_fails_closed():
    calls = []

    def fake(token, aud):
        calls.append(token)
        return review()

    a = auth.Authenticator("s1-extract", frozenset({SA}), review=fake, clock=lambda: 0.0)
    assert a.check("Bearer t1").allowed and a.check("Bearer t1").allowed and calls == ["t1"]
    assert not a.check(None).allowed and not a.check("Basic x").allowed

    def boom(token, aud):
        raise OSError("api down")

    assert not auth.Authenticator("s1-extract", frozenset({SA}), review=boom).check("Bearer t").allowed


def test_validate_limits():
    ok = server.ExtractRequest(texts=["a"])
    assert server.validate(ok) is None
    assert server.validate(server.ExtractRequest(texts=[])) == "texts is empty"
    assert "limit" in server.validate(server.ExtractRequest(texts=["a"] * (server.MAX_TEXTS + 1)))
    assert "longer" in server.validate(server.ExtractRequest(texts=["a" * (server.MAX_CHARS + 1)]))
    assert "threshold" in server.validate(server.ExtractRequest(texts=["a"], threshold=1.5))


class FakeModel:
    def inference(self, texts, labels, threshold=0.5, batch_size=8):
        return [[{"start": 0, "end": 7, "label": "organization", "score": 0.91234, "text": "Contoso"}] for _ in texts]


def client(authn=None, loaded=True):
    ex = server.Extractor(load=lambda: FakeModel())
    if loaded:
        ex.model = FakeModel()
    return TestClient(server.build_app(ex, authn))


def test_http_surface():
    c = client()
    assert c.get("/healthz").status_code == 200
    r = c.post("/v1/extract", json={"texts": ["Contoso signed"]})
    assert r.status_code == 200 and r.json()["results"][0][0]["label"] == "organization"
    assert c.post("/v1/extract", json={"texts": []}).status_code == 422
    assert client(loaded=False).get("/readyz").status_code == 503


def test_http_refuses_without_a_valid_token():
    a = auth.Authenticator("s1-extract", frozenset({SA}), review=lambda t, aud: review(user="system:serviceaccount:x:y"))
    c = client(authn=a)
    assert c.post("/v1/extract", json={"texts": ["x"]}).status_code == 401
    assert c.post("/v1/extract", json={"texts": ["x"]}, headers={"Authorization": "Bearer t"}).status_code == 403


def test_no_auth_needs_the_second_switch(monkeypatch=None):
    os.environ["S1_ALLOW_NO_AUTH"] = ""
    server.AUTH_MODE = "none"
    try:
        server.authenticator_from_env()
        raise AssertionError("S1_AUTH=none without S1_ALLOW_NO_AUTH must refuse")
    except SystemExit:
        pass
    finally:
        server.AUTH_MODE = "tokenreview"


if __name__ == "__main__":
    fails = 0
    for name, fn in list(globals().items()):
        if name.startswith("test_"):
            try:
                fn()
                print("✓", name)
            except Exception as exc:  # noqa: BLE001
                fails += 1
                print("✗", name, exc)
    sys.exit(1 if fails else 0)

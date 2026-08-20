"""The spoken context: sticky threads, switching, the session cookie."""
import asyncio
import time

from memex_voice_gateway.router import BrainRouter
from memex_voice_gateway.session import SpokenSession


class FakeMesh:
    def __init__(self):
        self.started, self.submitted = [], []
        self.counter = 0

    async def ask(self, text): return "h"
    async def await_reply(self, handle, budget_s): return None
    async def close(self): pass

    async def delegate(self, text, agent=None):
        self.counter += 1
        self.started.append((text, agent))
        return f"u/_Thread/t{self.counter}"

    async def submit(self, path, text, agent=None):
        self.submitted.append((path, text, agent))
        return path


def test_context_sticks_until_switched_or_closed():
    async def scenario():
        mesh = FakeMesh()
        router = BrainRouter({"memex": mesh}, "memex")
        # First delegation opens the thread and SETS the context.
        h1 = await router.delegate_task("Wetter morgen in Zürich")
        assert h1 == "memex::u/_Thread/t1" and len(mesh.started) == 1
        # The follow-up POSTS INTO the same thread — no new thread.
        h2 = await router.delegate_task("Und übermorgen?")
        assert h2 == h1 and len(mesh.started) == 1
        assert mesh.submitted[0][0] == "u/_Thread/t1"
        # A different explicit agent gets its own thread; context switches to it.
        h3 = await router.delegate_task("Check my inbox", "ExecutiveAssistant")
        assert h3 == "memex::u/_Thread/t2"
        # …and now follow-ups continue THERE.
        h4 = await router.delegate_task("Reply to the first one")
        assert h4 == h3
        # "neues Thema" clears the context: the next delegation opens thread 3.
        assert await router.handle_command("Neues Thema") == "Okay, new topic."
        h5 = await router.delegate_task("Plan my week")
        assert h5 == "memex::u/_Thread/t3"

    asyncio.run(scenario())


def test_spoken_switch_and_post_by_topic():
    async def scenario():
        mesh = FakeMesh()
        router = BrainRouter({"memex": mesh}, "memex")
        weather = await router.delegate_task("Wetter morgen in Zürich")
        await router.handle_command("Neues Thema")
        mail = await router.delegate_task("Fasse meine Mails zusammen", "ExecutiveAssistant")
        assert weather != mail
        # Switch back by topic — context returns to the weather thread.
        out = await router.handle_command("Wechsle zum Thread über Wetter")
        assert "Wetter" in out
        assert (await router.delegate_task("Regnet es?")) == weather
        # Post into the OTHER thread by topic without switching a delegation there first.
        out = await router.handle_command("Im Thread über Mails: bitte die von Anna zuerst")
        assert "Mails" in out
        assert mesh.submitted[-1][0] == mail.partition("::")[2]
        # …and the answer is queued for announcement.
        pending = router.drain_delegations()
        assert pending and pending[0][0] == mail
        # Listing names both threads.
        listed = await router.handle_command("Welche Threads sind offen?")
        assert "Wetter" in listed and "Mails" in listed

    asyncio.run(scenario())


def test_session_cookie_roundtrip_and_expiry(tmp_path):
    store = SpokenSession(str(tmp_path / "session.json"), ttl_hours=8)
    store.save(portal="memex", context={"portal": "memex", "path": "u/_Thread/t1",
                                        "task": "Wetter", "agent": ""},
               threads=[{"portal": "memex", "path": "u/_Thread/t1", "task": "Wetter",
                         "agent": "", "last": time.time()}])
    state = store.load()
    assert state["portal"] == "memex" and state["context"]["path"] == "u/_Thread/t1"

    async def scenario():
        mesh = FakeMesh()
        router = BrainRouter({"memex": mesh}, "memex")
        router.restore(state)
        # The restored context is live: a delegation posts into the persisted thread.
        assert (await router.delegate_task("Und heute?")) == "memex::u/_Thread/t1"
        assert mesh.submitted and not mesh.started

    asyncio.run(scenario())
    # An expired cookie loads as a clean session.
    expired = SpokenSession(str(tmp_path / "expired.json"), ttl_hours=-1)
    expired.save(portal="memex", context=None, threads=[])
    assert expired.load() == {}


def test_mesh_tool_passthrough_gates_destructive():
    class ToolMesh(FakeMesh):
        def __init__(self):
            super().__init__()
            self.calls = []
        async def call(self, tool, arguments):
            self.calls.append((tool, arguments))
            return "ok"

    async def scenario():
        mesh = ToolMesh()
        router = BrainRouter({"memex": mesh}, "memex")
        assert await router.run_tool("mesh_tool", {"tool": "render_area",
                                                   "arguments": {"path": "@X"}}) == "ok"
        out = await router.run_tool("mesh_tool", {"tool": "delete",
                                                  "arguments": {"path": "@X"}})
        assert "destructive" in out and ("delete", {"path": "@X"}) not in mesh.calls
        router.allow_destructive = True
        assert await router.run_tool("mesh_tool", {"tool": "delete",
                                                   "arguments": {"path": "@X"}}) == "ok"

    asyncio.run(scenario())

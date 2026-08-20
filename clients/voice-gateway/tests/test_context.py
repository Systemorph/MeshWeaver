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


def test_mailbox_signal_then_read_aloud():
    async def scenario():
        mesh = FakeMesh()
        router = BrainRouter({"memex": mesh}, "memex")
        handle = await router.delegate_task("Wetter morgen in Zürich")
        await router.handle_command("Neues Thema")
        # The answer lands: only a short READY signal is spoken; the text waits.
        signal = router.deliver_answer(handle, "Wetter morgen in Zürich",
                                       "Morgen wird es sonnig bei 24 Grad.")
        assert "ready" in signal.lower() or "bereit" in signal.lower()
        # …and the answered thread is pinned back as the context for follow-ups.
        assert (await router.delegate_task("Und übermorgen?")) == handle
        # "vorlesen" plays the stored answer, attributed to its question.
        out = await router.handle_command("Vorlesen")
        assert "sonnig" in out and "Wetter morgen" in out
        # The mailbox is empty afterwards.
        assert await router.handle_command("Lies vor") == router._phrases["nothing_new"]

    asyncio.run(scenario())


def test_email_routes_deterministically_to_executive_assistant():
    async def scenario():
        mesh = FakeMesh()
        router = BrainRouter({"memex": mesh}, "memex",
                             agent_homes={"ExecutiveAssistant": "memex"})
        out = await router.handle_command("Kannst du meine Mails checken?")
        assert out is not None and "memex" in out
        assert mesh.started == [("Kannst du meine Mails checken?", "ExecutiveAssistant")]
        # The answer is queued for the mailbox/announce path.
        assert router.drain_delegations()
        # Unrelated sentences pass through to the brain untouched.
        assert await router.handle_command("Wie spät ist es?") is None

    asyncio.run(scenario())


def test_system_prompt_syncs_from_mesh_or_deposits():
    import json as _json

    class PromptMesh(FakeMesh):
        def __init__(self, body=None):
            super().__init__()
            self.namespace = "rbuergi"
            self.body = body
            self.created = []
        async def call(self, tool, arguments):
            if tool == "get":
                if self.body is None:
                    raise RuntimeError("not found")
                return _json.dumps({"content": {"$type": "MarkdownContent", "body": self.body}})
            if tool == "create":
                self.created.append(_json.loads(arguments["node"]))
                return "{}"
            raise AssertionError(tool)

    async def scenario():
        # A prompt node in the mesh WINS over the built-in.
        mesh = PromptMesh(body="Du bist Memex, kurz und knapp.")
        router = BrainRouter({"memex": mesh}, "memex")
        assert await router.sync_system_prompt("BUILTIN") == "Du bist Memex, kurz und knapp."
        # No node → the built-in is DEPOSITED for the user to edit, and used as-is.
        mesh2 = PromptMesh(body=None)
        router2 = BrainRouter({"memex": mesh2}, "memex")
        assert await router2.sync_system_prompt("BUILTIN") == "BUILTIN"
        deposited = mesh2.created[0]
        assert deposited["namespace"] == "rbuergi/Voice" and deposited["id"] == "Prompt"
        assert deposited["content"]["body"] == "BUILTIN"

    asyncio.run(scenario())

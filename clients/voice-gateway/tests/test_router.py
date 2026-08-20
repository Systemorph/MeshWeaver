import asyncio

from memex_voice_gateway.router import BrainRouter, parse_switch_command


def test_parse_switch_variants():
    assert parse_switch_command("Wechsle zu Systemorph.") == "Systemorph"
    assert parse_switch_command("switch to memex") == "memex"
    assert parse_switch_command("Verbinde mit lokal!") == "lokal"
    assert parse_switch_command("gang uf systemorph") == "systemorph"
    assert parse_switch_command("Wie viele Kantone hat die Schweiz?") is None
    assert parse_switch_command("Wechsle zu was auch immer du willst bitte jetzt") \
        == "was auch immer du willst bitte jetzt"


class FakeBrain:
    def __init__(self, tag):
        self.tag = tag
        self.asked = []

    async def ask(self, text):
        self.asked.append(text)
        return f"h-{self.tag}"

    async def await_reply(self, handle, budget_s):
        return f"{self.tag} answered {handle}"

    async def close(self):
        pass


def test_switch_and_routing_with_stamped_handles():
    async def scenario():
        memex, local = FakeBrain("memex"), FakeBrain("lokal")
        router = BrainRouter({"memex": memex, "lokal": local}, "memex")

        assert await router.handle_command("Wie spät ist es?") is None
        handle = await router.ask("frage eins")
        assert handle == "memex::h-memex"

        assert await router.handle_command("wechsle zu lokal") == "Connected to lokal."
        assert router.active == "lokal"
        await router.ask("frage zwei")
        assert local.asked == ["frage zwei"]

        # a handle from BEFORE the switch still polls the brain that produced it
        assert await router.await_reply(handle, 1.0) == "memex answered h-memex"

        unknown = await router.handle_command("wechsle zu atlantis")
        assert "atlantis" in unknown and "memex" in unknown

    asyncio.run(scenario())


def test_resolve_is_forgiving():
    router = BrainRouter({"systemorph": FakeBrain("s"), "memex": FakeBrain("m")}, "memex")
    assert router.resolve("Systemorph") == "systemorph"
    assert router.resolve("system") == "systemorph"
    assert router.resolve("nothing") is None


def test_delegation_to_a_standard_agent():
    async def scenario():
        class MeshBrain(FakeBrain):
            def __init__(self):
                super().__init__("memex")
                self.delegated = []

            async def delegate(self, text, agent=None):
                self.delegated.append((agent, text))
                return "u/_Thread/task-1"

        mesh, local = MeshBrain(), FakeBrain("lokal")
        router = BrainRouter({"lokal": local, "memex": mesh}, "lokal")
        reply = await router.handle_command("Frag den Researcher nach dem Wetter von morgen")
        assert "Researcher" in reply and "memex" in reply
        assert mesh.delegated == [("Researcher", "dem Wetter von morgen")]
        assert await router.handle_command("Fragen kostet nichts") is None  # not a delegation
        assert router.describe_hold("memex::x").startswith("Submitted to memex")
        assert router.describe_hold("lokal::x") == "Let me check. One moment please."

    asyncio.run(scenario())


def test_run_tool_dispatches_to_mesh_and_home():
    class FakeMesh:
        def __init__(self):
            self.calls = []
        async def ask(self, text): return "h"
        async def await_reply(self, handle, budget_s): return None
        async def delegate(self, text, agent=None): return "u/_Thread/t1"
        async def call(self, tool, arguments):
            self.calls.append((tool, arguments))
            return f"{tool}-result"
        async def close(self): pass

    class FakeHome:
        async def run(self, args): return f"home:{args['action']}"
        async def close(self): pass

    async def scenario():
        mesh = FakeMesh()
        router = BrainRouter({"memex": mesh}, "memex", home=FakeHome())
        assert await router.run_tool("search_mesh", {"query": "kantone"}) == "search-result"
        assert await router.run_tool("get_node", {"path": "@Edu/Guide"}) == "get-result"
        assert await router.run_tool("home_assistant", {"action": "get_state"}) == "home:get_state"
        assert mesh.calls == [("search", {"query": "kantone", "limit": 8}),
                              ("get", {"path": "@Edu/Guide"})]
        bare = BrainRouter({"memex": mesh}, "memex")
        assert "not configured" in await bare.run_tool("home_assistant", {"action": "get_state"})
        await router.close()

    asyncio.run(scenario())


def test_delegate_routes_agent_to_its_home_portal():
    class FakeMesh:
        def __init__(self):
            self.delegated = []
        async def ask(self, text): return "h"
        async def await_reply(self, handle, budget_s): return None
        async def delegate(self, text, agent=None):
            self.delegated.append((text, agent))
            return "u/_Thread/t1"
        async def close(self): pass

    async def scenario():
        cloud, mac = FakeMesh(), FakeMesh()
        router = BrainRouter({"memex": cloud, "mac": mac}, "memex",
                             agent_homes={"ExecutiveAssistant": "mac"})
        # Email agent goes HOME to the local mesh, even while the cloud is active.
        handle = await router.delegate_task("Check my inbox", "ExecutiveAssistant")
        assert handle.startswith("mac::") and mac.delegated and not cloud.delegated
        # Unpinned agents keep going to the active mesh.
        handle = await router.delegate_task("Research quantum", "Researcher")
        assert handle.startswith("memex::") and cloud.delegated

    asyncio.run(scenario())


def test_music_commands_play_and_are_honest_about_songs():
    class FakeBrainOnly:
        async def ask(self, text): return "h"
        async def await_reply(self, handle, budget_s): return None
        async def close(self): pass

    async def scenario():
        played = []
        router = BrainRouter({"lokal": FakeBrainOnly()}, "lokal",
                             phrases={"radio_on": "Hier kommt {station}.",
                                      "song_hint": "Einzelne Lieder kann ich noch nicht — dafür kommt {station}."})
        async def player(url): played.append(url)
        router.player = player
        # A generic music wish plays the default station.
        out = await router.handle_command("Kannst du für mich Musik spielen?")
        assert out == "Hier kommt Energy Zürich." and len(played) == 1
        # A NAMED song gets radio plus the honest sentence — never an invented promise.
        out = await router.handle_command("Ich möchte, dass du mir ein Lied spielst und es sollte Komet heißen.")
        assert "noch nicht" in out and len(played) == 2
        # A named station is honored.
        out = await router.handle_command("Spiel Radio SRF 3")
        assert "SRF 3" in out and played[-1].endswith("drs3/mp3_128")
        # "Musik aus" ends quietly through the interrupt path.
        assert await router.handle_command("Musik aus") == ""
        # Without a player wired, music requests fall through to the brain.
        bare = BrainRouter({"lokal": FakeBrainOnly()}, "lokal")
        assert await bare.handle_command("Spiel Musik") is None

    asyncio.run(scenario())

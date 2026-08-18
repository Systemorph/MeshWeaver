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

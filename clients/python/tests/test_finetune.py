"""The fine-tuning loop: docs → chat-format records → a file kept in content → train with progress
streamed back onto a run node. The heavy LoRA trainer is injected as a stub, so these pin the whole
mesh orchestration without torch — the same duck-typing test_mesh uses."""
from typing import Any

import pytest

from meshweaver.content import text_from_node
from meshweaver.examples.finetune import (
    SYSTEM_PROMPT,
    collect,
    from_jsonl,
    to_jsonl,
    train,
    training_data_node,
    training_records,
)

DOC_BODY = (
    "MeshWeaver is an actor-model data mesh.\n\n"
    "## Message routing\n\n"
    "Every hub has an address; deliveries route by target. " + "Routing detail. " * 10 + "\n\n"
    "## Layout areas\n\n"
    "Views are reactive UiControl trees rendered in Blazor. " + "Layout detail. " * 10 + "\n"
)


def _doc(path: str, title: str, body: str = DOC_BODY) -> dict[str, Any]:
    return {"path": path, "name": title,
            "content": {"$type": "MarkdownContent", "title": title, "content": body}}


class FakeMesh:
    """Answers search/get and records every write — the mesh side of the loop."""

    def __init__(self, nodes: dict[str, dict[str, Any]]):
        self.nodes = nodes
        self.upserts: list[dict[str, Any]] = []
        self.patches: list[tuple[str, dict[str, Any]]] = []

    async def search(self, query: str, limit: int = 50) -> list[dict[str, Any]]:
        return [{"path": p} for p in self.nodes]

    async def get(self, path: str) -> dict[str, Any]:
        return self.nodes[path]

    async def create_or_update(self, node: dict[str, Any]) -> dict[str, Any]:
        self.upserts.append(node)
        return {"status": "ok"}

    async def patch(self, path: str, fields: dict[str, Any]) -> None:
        self.patches.append((path, fields))


# ---- docs -> records (pure) ------------------------------------------------------------------------

def test_training_records_distill_page_and_sections():
    records = training_records([_doc("Doc/Routing", "Message Routing")])
    questions = [r["messages"][1]["content"] for r in records]
    assert "Explain Message Routing in MeshWeaver." in questions
    assert any("Message routing" in q for q in questions)      # one record per ## section
    assert any("Layout areas" in q for q in questions)
    for r in records:
        roles = [m["role"] for m in r["messages"]]
        assert roles == ["system", "user", "assistant"]
        assert r["messages"][0]["content"] == SYSTEM_PROMPT
        assert r["messages"][2]["content"]                     # the docs text is the answer


def test_training_records_skip_nodes_without_text():
    assert training_records([{"path": "Doc/Empty", "name": "Empty", "content": {}}]) == []


def test_jsonl_round_trips_through_the_content_node():
    records = training_records([_doc("Doc/Routing", "Message Routing")])
    node = training_data_node("PythonDemo/FineTune/TrainingData", to_jsonl(records),
                              "namespace:Doc", len(records))
    assert node["namespace"] == "PythonDemo/FineTune"
    assert node["id"] == "TrainingData"
    # The JSONL survives the trip INTO the markdown fence and back out.
    assert from_jsonl(text_from_node(node)) == records


# ---- collect: mesh -> records -> a file kept in content --------------------------------------------

async def test_collect_reads_docs_and_upserts_the_training_file():
    mesh = FakeMesh({"Doc/Routing": _doc("Doc/Routing", "Message Routing"),
                     "Doc/Layout": _doc("Doc/Layout", "Layout Areas")})
    result = await collect(mesh, query="namespace:Doc", target="PythonDemo/FineTune/TrainingData")
    assert result["documents"] == 2
    assert result["records"] >= 4                              # 1 page + 2 sections per doc
    (node,) = mesh.upserts
    assert node["namespace"] + "/" + node["id"] == "PythonDemo/FineTune/TrainingData"
    assert len(from_jsonl(text_from_node(node))) == result["records"]


async def test_collect_with_no_usable_docs_raises():
    mesh = FakeMesh({"Doc/Empty": {"path": "Doc/Empty", "name": "Empty", "content": {}}})
    with pytest.raises(RuntimeError):
        await collect(mesh, query="namespace:Doc")


# ---- train: the file in content -> tuner -> progress back onto the run node ------------------------

async def test_train_pulls_the_file_streams_progress_and_reports_success():
    records = training_records([_doc("Doc/Routing", "Message Routing")])
    data = training_data_node("PythonDemo/FineTune/TrainingData", to_jsonl(records),
                              "namespace:Doc", len(records))
    mesh = FakeMesh({"PythonDemo/FineTune/TrainingData": data})
    seen: dict[str, Any] = {}

    def fake_tuner(recs: list[dict[str, Any]], *, on_progress: Any, **hp: Any) -> dict[str, Any]:
        seen["records"] = recs
        seen["hp"] = hp
        on_progress("step 10: loss 2.5000")                    # from the trainer thread
        on_progress("step 20: loss 1.2500")
        return {"train_loss": 1.25, "steps": 20, "output_dir": hp["output_dir"]}

    result = await train(mesh, run_path="PythonDemo/FineTune/Runs/test", tuner=fake_tuner)

    assert seen["records"] == records                          # decoded from the content file
    assert seen["hp"]["model"]
    assert result["run_path"] == "PythonDemo/FineTune/Runs/test"
    (run,) = mesh.upserts                                      # the run node was created first
    assert run["namespace"] + "/" + run["id"] == "PythonDemo/FineTune/Runs/test"
    # Progress and the final verdict were streamed onto the run node (python -> mesh).
    bodies = [f["content"]["content"] for p, f in mesh.patches if p == "PythonDemo/FineTune/Runs/test"]
    assert any("step 10: loss 2.5000" in b for b in bodies)
    assert "Succeeded" in bodies[-1] and "1.2500" in bodies[-1]


async def test_train_failure_lands_on_the_run_node_and_raises():
    records = training_records([_doc("Doc/Routing", "Message Routing")])
    data = training_data_node("PythonDemo/FineTune/TrainingData", to_jsonl(records),
                              "namespace:Doc", len(records))
    mesh = FakeMesh({"PythonDemo/FineTune/TrainingData": data})

    def broken_tuner(recs: list[dict[str, Any]], **hp: Any) -> dict[str, Any]:
        raise RuntimeError("CUDA out of memory")

    with pytest.raises(RuntimeError, match="CUDA out of memory"):
        await train(mesh, run_path="PythonDemo/FineTune/Runs/test", tuner=broken_tuner)
    assert "Failed" in mesh.patches[-1][1]["content"]["content"]

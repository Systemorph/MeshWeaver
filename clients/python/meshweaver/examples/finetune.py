"""Fine-tune a language model on MeshWeaver's own documentation — the dataset lives IN the mesh.

The full loop, each leg a mesh round trip (see ``Doc/DataMesh/PythonFineTuning``):

1. ``collect`` — **mesh → Python → mesh**: query the documentation nodes, distill every page into
   chat-format instruction records ("teach the model MeshWeaver"), and write the JSONL back to the
   mesh as **a file kept in content** (``PythonDemo/FineTune/TrainingData``) — reviewable, versioned,
   hand-editable like any node.
2. ``train`` — **mesh → Python → mesh**: pull that training file from the mesh, LoRA-fine-tune a
   causal LM (``transformers`` + ``peft``, the optional ``[finetune]`` extras), and stream per-step
   progress back onto a run node — so the portal watches the training live, exactly like any other
   activity.

Run it::

    pip install -e ".[finetune]"
    python -m meshweaver.examples.finetune collect --url https://memex.meshweaver.cloud --token mw_…
    python -m meshweaver.examples.finetune train   --url https://memex.meshweaver.cloud --token mw_…

Everything except :func:`lora_fine_tune` is dependency-free and unit-tested
(``tests/test_finetune.py``); the heavy trainer is injectable so the mesh orchestration is testable
without ``torch``.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import time
from typing import Any, Callable, Iterable, Optional

from ..content import text_from_node
from ..mesh import Mesh

DEFAULT_QUERY = "namespace:Doc nodeType:Markdown"
DEFAULT_DATA_PATH = "PythonDemo/FineTune/TrainingData"
DEFAULT_MODEL = "Qwen/Qwen2.5-0.5B-Instruct"

SYSTEM_PROMPT = (
    "You are the MeshWeaver assistant. Answer questions about the MeshWeaver data mesh "
    "platform precisely, grounded in its documentation."
)

#: Cap per-answer length so one giant page doesn't dominate the dataset (cut at a paragraph edge).
MAX_ANSWER_CHARS = 4000


# --- docs -> training records (pure, tested) --------------------------------------------------------

def training_records(nodes: Iterable[Any]) -> list[dict[str, Any]]:
    """Distill documentation nodes into chat-format instruction records.

    Deterministic — no generator model in the loop: each page yields one "explain the page" record,
    plus one record per ``##`` section ("how does <section> work?"). The assistant turn is the
    documentation text itself, so the tuned model learns to answer the way the docs do."""
    records: list[dict[str, Any]] = []
    for node in nodes:
        title, text = _title_and_text(node)
        if not title or not text:
            continue
        records.append(_chat(f"Explain {title} in MeshWeaver.", _truncate(text)))
        for heading, body in _sections(text):
            records.append(_chat(f"In MeshWeaver's {title}: how does {heading} work?", _truncate(body)))
    return records


def _title_and_text(node: Any) -> tuple[Optional[str], Optional[str]]:
    """A doc node's display title and markdown body, tolerant of MeshNode / dict shapes."""
    content = getattr(node, "content", None) if not isinstance(node, dict) else (node.get("content") or node.get("Content"))
    name = getattr(node, "name", None) if not isinstance(node, dict) else (node.get("name") or node.get("Name"))
    if isinstance(content, dict):
        title = content.get("title") or content.get("Title") or name
        text = content.get("content") or content.get("Content")
        return title, text if isinstance(text, str) else None
    return name, content if isinstance(content, str) else None


def _sections(markdown: str) -> list[tuple[str, str]]:
    """(heading, body) per ``##`` section — skips bodies too short to teach anything."""
    sections: list[tuple[str, str]] = []
    heading: Optional[str] = None
    body: list[str] = []
    for line in markdown.splitlines() + ["## "]:  # sentinel flushes the last section
        if line.startswith("## "):
            text = "\n".join(body).strip()
            if heading and len(text) >= 80:
                sections.append((heading, text))
            heading, body = line[3:].strip() or None, []
        else:
            body.append(line)
    return sections


def _chat(question: str, answer: str) -> dict[str, Any]:
    return {"messages": [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": question},
        {"role": "assistant", "content": answer},
    ]}


def _truncate(text: str, limit: int = MAX_ANSWER_CHARS) -> str:
    text = text.strip()
    if len(text) <= limit:
        return text
    cut = text.rfind("\n\n", 0, limit)
    return text[: cut if cut > 0 else limit].strip()


def to_jsonl(records: Iterable[dict[str, Any]]) -> str:
    return "\n".join(json.dumps(r, ensure_ascii=False) for r in records)


def from_jsonl(text: str) -> list[dict[str, Any]]:
    return [json.loads(line) for line in text.splitlines() if line.strip()]


# --- the training file kept in content --------------------------------------------------------------

def training_data_node(path: str, jsonl: str, query: str, count: int) -> dict[str, Any]:
    """The mesh node that KEEPS the training set in content: a Markdown node documenting itself,
    with the JSONL in a fenced block (``text_from_node`` reads it back out)."""
    namespace, _, node_id = path.rpartition("/")
    body = (
        f"# {node_id}\n\n"
        f"Instruction-tuning dataset distilled from the MeshWeaver documentation "
        f"(query: `{query}`) — **{count} chat-format records**, consumed by "
        f"`python -m meshweaver.examples.finetune train`.\n\n"
        f"```jsonl\n{jsonl}\n```\n"
    )
    return {
        "id": node_id,
        "namespace": namespace,
        "name": "MeshWeaver training data",
        "nodeType": "Markdown",
        "description": f"Fine-tuning dataset built from the docs ({count} records).",
        # MarkdownContent is the Markdown NodeType's registered content shape (the portal rejects
        # unregistered $type discriminators — they would persist as untyped blobs).
        "content": {"$type": "MarkdownContent", "content": body},
    }


def run_node(path: str, data_path: str, model: str, count: int) -> dict[str, Any]:
    """The run node the trainer streams progress onto — watch it live in the portal."""
    namespace, _, node_id = path.rpartition("/")
    return {
        "id": node_id,
        "namespace": namespace,
        "name": f"Fine-tune {node_id}",
        "nodeType": "Markdown",
        "description": f"LoRA fine-tune of {model} on {data_path} ({count} records).",
        "content": {"$type": "MarkdownContent", "content": ""},
    }


# --- collect: docs -> dataset -> a file in content --------------------------------------------------

async def collect(mesh: Any, query: str = DEFAULT_QUERY, target: str = DEFAULT_DATA_PATH,
                  limit: int = 200) -> dict[str, Any]:
    """mesh → Python → mesh: build the training set from the docs and keep it in content."""
    hits = await mesh.search(query, limit=limit)
    nodes = []
    for hit in hits:
        path = hit.get("path") or hit.get("Path")
        if path:
            nodes.append(await mesh.get(path))  # full content — search hits are summaries
    records = training_records(nodes)
    if not records:
        raise RuntimeError(f"query {query!r} yielded no usable documentation pages")
    await mesh.create_or_update(training_data_node(target, to_jsonl(records), query, len(records)))
    return {"documents": len(nodes), "records": len(records), "path": target}


# --- train: the file in content -> LoRA -> progress back onto the mesh ------------------------------

async def train(mesh: Any, data_path: str = DEFAULT_DATA_PATH, run_path: Optional[str] = None,
                model: str = DEFAULT_MODEL, epochs: int = 3, learning_rate: float = 2e-4,
                output_dir: str = "./meshweaver-lora",
                tuner: Optional[Callable[..., dict[str, Any]]] = None) -> dict[str, Any]:
    """Pull the training file from the mesh, fine-tune, and stream progress back to a run node.

    ``tuner`` defaults to :func:`lora_fine_tune`; tests inject a stub so the mesh orchestration is
    provable without torch. The tuner runs on a worker thread (it blocks for minutes); progress
    callbacks marshal back onto the event loop to patch the run node."""
    run_path = run_path or f"PythonDemo/FineTune/Runs/{time.strftime('%Y%m%d-%H%M%S')}"
    node = await mesh.get(data_path)
    records = from_jsonl(text_from_node(node))

    await mesh.create_or_update(run_node(run_path, data_path, model, len(records)))
    lines = [
        "# Fine-tune run", "",
        f"- **Data:** `{data_path}` — {len(records)} records",
        f"- **Model:** `{model}` (LoRA)",
        f"- **Status:** Running", "",
        "## Progress", "",
    ]

    async def report(line: str) -> None:
        lines.append(line)
        await mesh.patch(run_path, {"content": {"content": "\n".join(lines)}})

    loop = asyncio.get_running_loop()

    def on_progress(line: str) -> None:  # called from the trainer thread
        asyncio.run_coroutine_threadsafe(report(f"- {line}"), loop).result()

    tune = tuner or lora_fine_tune
    try:
        metrics = await asyncio.to_thread(
            tune, records, model=model, epochs=epochs, learning_rate=learning_rate,
            output_dir=output_dir, on_progress=on_progress)
    except Exception as ex:
        await report(f"\n**Failed:** {ex}")
        raise

    await report(f"\n**Succeeded** — final training loss `{metrics['train_loss']:.4f}` "
                 f"over {metrics['steps']} steps; adapter saved to `{metrics['output_dir']}`.")
    return {**metrics, "run_path": run_path}


def lora_fine_tune(records: list[dict[str, Any]], *, model: str = DEFAULT_MODEL, epochs: int = 3,
                   learning_rate: float = 2e-4, output_dir: str = "./meshweaver-lora",
                   on_progress: Callable[[str], None] = lambda line: None) -> dict[str, Any]:
    """LoRA fine-tuning of a causal LM on the chat records — the only function needing the heavy deps."""
    try:
        from datasets import Dataset
        from peft import LoraConfig, get_peft_model
        from transformers import (AutoModelForCausalLM, AutoTokenizer, DataCollatorForLanguageModeling,
                                  Trainer, TrainerCallback, TrainingArguments)
    except ImportError as ex:  # keep the mesh loop importable without torch
        raise RuntimeError(
            'fine-tuning needs the optional extras: pip install -e ".[finetune]"') from ex

    tokenizer = AutoTokenizer.from_pretrained(model)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    texts = [_render_chat(tokenizer, r["messages"]) for r in records]
    dataset = Dataset.from_dict({"text": texts}).map(
        lambda batch: tokenizer(batch["text"], truncation=True, max_length=1024),
        batched=True, remove_columns=["text"])

    lm = get_peft_model(
        AutoModelForCausalLM.from_pretrained(model),
        LoraConfig(task_type="CAUSAL_LM", r=16, lora_alpha=32, target_modules="all-linear"))

    class Report(TrainerCallback):
        def on_log(self, args: Any, state: Any, control: Any, logs: Any = None, **kw: Any) -> None:
            if logs and "loss" in logs:
                on_progress(f"step {state.global_step}: loss {logs['loss']:.4f}")

    trainer = Trainer(
        model=lm,
        args=TrainingArguments(output_dir=output_dir, num_train_epochs=epochs,
                               learning_rate=learning_rate, per_device_train_batch_size=2,
                               logging_steps=10, save_strategy="no", report_to=[]),
        train_dataset=dataset,
        data_collator=DataCollatorForLanguageModeling(tokenizer, mlm=False),
        callbacks=[Report()])
    outcome = trainer.train()
    lm.save_pretrained(output_dir)
    tokenizer.save_pretrained(output_dir)
    return {"train_loss": float(outcome.training_loss), "steps": int(outcome.global_step),
            "output_dir": output_dir}


def _render_chat(tokenizer: Any, messages: list[dict[str, str]]) -> str:
    if getattr(tokenizer, "chat_template", None):
        return tokenizer.apply_chat_template(messages, tokenize=False)
    return "\n".join(f"<|{m['role']}|>\n{m['content']}" for m in messages) + (tokenizer.eos_token or "")


# --- CLI ---------------------------------------------------------------------------------------------

def main() -> None:
    p = argparse.ArgumentParser(prog="meshweaver.examples.finetune",
                                description="Fine-tune a model on MeshWeaver docs kept in the mesh.")
    sub = p.add_subparsers(dest="mode", required=True)

    c = sub.add_parser("collect", help="docs -> chat-format JSONL -> a file kept in content")
    c.add_argument("--url", required=True)
    c.add_argument("--token", default=None)
    c.add_argument("--query", default=DEFAULT_QUERY)
    c.add_argument("--target", default=DEFAULT_DATA_PATH)
    c.add_argument("--limit", type=int, default=200)

    t = sub.add_parser("train", help="the file in content -> LoRA fine-tune -> progress on a run node")
    t.add_argument("--url", required=True)
    t.add_argument("--token", default=None)
    t.add_argument("--data", default=DEFAULT_DATA_PATH)
    t.add_argument("--run", default=None, help="run-node path (default PythonDemo/FineTune/Runs/<stamp>)")
    t.add_argument("--model", default=DEFAULT_MODEL)
    t.add_argument("--epochs", type=int, default=3)
    t.add_argument("--learning-rate", type=float, default=2e-4)
    t.add_argument("--output-dir", default="./meshweaver-lora")

    args = p.parse_args()

    async def run() -> dict[str, Any]:
        async with await Mesh.connect(args.url, token=args.token) as mesh:
            if args.mode == "collect":
                return await collect(mesh, query=args.query, target=args.target, limit=args.limit)
            return await train(mesh, data_path=args.data, run_path=args.run, model=args.model,
                               epochs=args.epochs, learning_rate=args.learning_rate,
                               output_dir=args.output_dir)

    print(json.dumps(asyncio.run(run()), indent=2))


if __name__ == "__main__":
    main()

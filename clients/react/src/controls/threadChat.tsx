// ThreadChat — the React port of the Blazor portal's ThreadChatView (src/MeshWeaver.Blazor.Portal/
// Chat/ThreadChatView.razor): the full chat experience over a THREAD NODE's live stream.
//
// Data flow mirrors Blazor exactly — no parallel protocol:
//   - The view watches the thread node (MeshOps.watch(threadPath)) and renders its Thread content:
//     `messages` (ordered ids), `pendingUserMessages` (queued payloads keyed by id), `status`
//     (Idle | StartingExecution | Executing | Cancelled | Done), `executionStatus`, `streamingText`,
//     `streamingToolCalls`, `composer` (the sticky agent/model selection).
//   - Each message id is a SATELLITE CELL at {threadPath}/{id} — one watch per id (the twin of
//     Blazor's SyncMessageSubscriptions). A cell's content is a ThreadMessage
//     (role/text/status/toolCalls/...). Until the cell exists, the bubble renders from the pending
//     payload (queued state).
//   - Submission goes through the canonical surface (the client twin of HubThreadExtensions):
//     no thread yet → MeshOps.startThread (ONE CreateNodeRequest, seeded pendingUserMessages);
//     existing thread → MeshOps.submitMessage (RFC 7396 merge-patch appending userMessageIds +
//     pendingUserMessages). Stop → patch { content: { requestedStatus: "Cancelled" } } — the
//     control-plane flip the owning hub's watcher reacts to.
//
// The composer is gated exactly like Blazor (ThreadChatView.razor line ~865):
//   disabled = whitespace-only text || thread.IsExecuting (StartingExecution | Executing).

import { useEffect, useMemo, useRef, useState } from "react";
import type { KeyboardEvent, ReactNode } from "react";
import { Button, Dropdown, Option, Spinner, Text, Textarea, Tooltip } from "@fluentui/react-components";
import { Send20Filled, Stop20Regular } from "@fluentui/react-icons";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";
import type { Json, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { useMeshOps, type MeshNodeState, type MeshOps, type ThreadSubmitOptions } from "../live/meshOps.js";
import { controlStyle } from "../render/style.js";
import { str } from "./common.js";

// ---- wire shapes (camelCase — SerializationExtensions serialises with camelCase + string enums) ---

interface ToolCallJson {
  name?: string;
  displayName?: string;
  arguments?: string;
  result?: string;
  isSuccess?: boolean;
  status?: string; // Streaming | Success | Failed | Cancelled
}

interface ThreadMessageJson {
  role?: string; // "user" | "assistant"
  text?: string;
  authorName?: string;
  agentName?: string;
  modelName?: string;
  status?: string; // Queued | Submitted | Streaming | Completed | Cancelled | Error
  toolCalls?: ToolCallJson[];
  timestamp?: string;
}

interface ThreadJson {
  messages?: string[];
  userMessageIds?: string[];
  pendingUserMessages?: Record<string, ThreadMessageJson>;
  composer?: { agentName?: string; modelName?: string; harness?: string; contextPath?: string };
  status?: string; // Idle | StartingExecution | Executing | Cancelled | Done
  executionStatus?: string;
  executionStartedAt?: string;
  activeMessageId?: string;
  streamingText?: string;
  streamingToolCalls?: ToolCallJson[];
  createdBy?: string;
}

// ---- node-stream plumbing (the grpc-web watch primitive, via the MeshOps context) ---------------

/** Drive an async-iterable node watch into a callback; returns the stop function. */
function watchInto(ops: MeshOps, path: string, onState: (n: MeshNodeState) => void): () => void {
  let live = true;
  const it = ops.watch(path)[Symbol.asyncIterator]();
  void (async () => {
    try {
      while (live) {
        const r = await it.next();
        if (r.done) break;
        if (live && r.value) onState(r.value);
      }
    } catch {
      // A missing satellite surfaces as a stream error (the cache's DeliveryFailure). The bubble
      // keeps rendering from the pending payload — never crash the view (Blazor marks it missing).
    }
  })();
  return () => {
    live = false;
    void Promise.resolve(it.return?.()).catch(() => undefined);
  };
}

/** The live state of one node (null until the first emission / when unset). */
function useNodeState(ops: MeshOps | null, path: string | null): MeshNodeState | null {
  const [node, setNode] = useState<MeshNodeState | null>(null);
  useEffect(() => {
    setNode(null);
    if (!ops || !path) return;
    return watchInto(ops, path, setNode);
  }, [ops, path]);
  return node;
}

/** One watch per message cell at {threadPath}/{id} — Blazor's SyncMessageSubscriptions. */
function useMessageCells(
  ops: MeshOps | null,
  threadPath: string | null,
  ids: readonly string[],
): Record<string, ThreadMessageJson> {
  const [cells, setCells] = useState<Record<string, ThreadMessageJson>>({});
  const watchers = useRef(new Map<string, () => void>());
  const paths = threadPath ? ids.map((id) => `${threadPath}/${id}`) : [];
  const pathsKey = paths.join("\n");
  useEffect(() => {
    if (!ops) return;
    const want = new Set(paths);
    for (const [p, stop] of watchers.current) {
      if (!want.has(p)) {
        stop();
        watchers.current.delete(p);
      }
    }
    for (const p of paths) {
      if (watchers.current.has(p)) continue;
      watchers.current.set(
        p,
        watchInto(ops, p, (node) => setCells((c) => ({ ...c, [p]: (node.content ?? {}) as ThreadMessageJson }))),
      );
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, pathsKey]);
  useEffect(
    () => () => {
      for (const stop of watchers.current.values()) stop();
      watchers.current.clear();
    },
    [],
  );
  return cells;
}

/** Agent/model options from the mesh (nodeType:Agent / nodeType:Model), when the ops expose search. */
function useSearchOptions(ops: MeshOps | null, query: string): { path: string; name: string }[] {
  const [options, setOptions] = useState<{ path: string; name: string }[]>([]);
  useEffect(() => {
    if (!ops?.search) return;
    let live = true;
    ops
      .search(query)
      .then((rs) => {
        if (!live) return;
        setOptions(
          rs.map((r) => ({ path: str(r.path), name: str(r.name) || str(r.path) })).filter((o) => o.path.length > 0),
        );
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, [ops, query]);
  return options;
}

// ---- rendering ----------------------------------------------------------------------------------

function ToolCallView({ call }: { call: ToolCallJson }): ReactNode {
  const failed = call.isSuccess === false || call.status === "Failed";
  const running = call.status === "Streaming";
  const icon = running ? "●" : failed ? "✗" : "✓";
  return (
    <details style={{ fontSize: 12, margin: "2px 0", color: "var(--colorNeutralForeground3)" }}>
      <summary style={{ cursor: "pointer" }}>
        <span style={{ color: failed ? "var(--colorStatusDangerForeground1)" : undefined }}>{icon}</span>{" "}
        {str(call.displayName) || str(call.name)}
      </summary>
      {call.arguments ? (
        <pre style={{ whiteSpace: "pre-wrap", margin: "4px 0", fontFamily: "var(--fontFamilyMonospace)" }}>
          {str(call.arguments)}
        </pre>
      ) : null}
      {call.result ? (
        <pre style={{ whiteSpace: "pre-wrap", margin: "4px 0", fontFamily: "var(--fontFamilyMonospace)" }}>
          {str(call.result)}
        </pre>
      ) : null}
    </details>
  );
}

function MessageBubble({ msg, queued }: { msg: ThreadMessageJson; queued: boolean }): ReactNode {
  const mine = /user/i.test(str(msg.role) || "user");
  const isError = msg.status === "Error";
  const streaming = msg.status === "Streaming";
  const author = str(msg.authorName) || (mine ? "" : str(msg.agentName));
  return (
    <div style={{ display: "flex", justifyContent: mine ? "flex-end" : "flex-start" }}>
      <div
        data-role={mine ? "user" : "assistant"}
        style={{
          maxWidth: "80%",
          padding: "8px 12px",
          borderRadius: 12,
          opacity: queued ? 0.7 : 1,
          border: isError ? "1px solid var(--colorStatusDangerBorder1)" : undefined,
          background: isError
            ? "var(--colorStatusDangerBackground1)"
            : mine
              ? "var(--colorBrandBackground2)"
              : "var(--colorNeutralBackground3)",
        }}
      >
        {author ? (
          <Text size={200} weight="semibold" block style={{ color: "var(--colorNeutralForeground3)" }}>
            {author}
            {msg.modelName ? ` · ${str(msg.modelName)}` : ""}
          </Text>
        ) : null}
        {(msg.toolCalls ?? []).map((c, i) => (
          <ToolCallView key={i} call={c} />
        ))}
        <div className="mw-markdown" style={{ fontSize: 14 }}>
          <Markdown remarkPlugins={[remarkGfm]}>{str(msg.text)}</Markdown>
        </div>
        {queued ? (
          <Text size={100} style={{ color: "var(--colorNeutralForeground3)" }}>
            queued…
          </Text>
        ) : streaming ? (
          <Text size={100} style={{ color: "var(--colorNeutralForeground3)" }}>
            streaming…
          </Text>
        ) : null}
      </div>
    </div>
  );
}

/** The exec bar — visible while the thread executes: status line, streaming preview, tools, Stop. */
function ExecutionBar({
  thread,
  onStop,
}: {
  thread: ThreadJson;
  onStop: () => void;
}): ReactNode {
  const preview = str(thread.streamingText);
  return (
    <div
      role="status"
      style={{
        display: "flex",
        flexDirection: "column",
        gap: 4,
        padding: "8px 12px",
        borderRadius: 8,
        background: "var(--colorNeutralBackground2)",
        border: "1px solid var(--colorNeutralStroke2)",
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
        <Spinner size="extra-tiny" />
        <Text size={200} weight="semibold" style={{ flex: 1 }}>
          ✦ {str(thread.executionStatus) || "Working…"}
        </Text>
        <Button size="small" appearance="subtle" icon={<Stop20Regular />} onClick={onStop}>
          Stop
        </Button>
      </div>
      {preview ? (
        <Text size={200} style={{ color: "var(--colorNeutralForeground3)", whiteSpace: "pre-wrap" }}>
          {preview.length > 300 ? `${preview.slice(0, 300)}…` : preview}
        </Text>
      ) : null}
      {(thread.streamingToolCalls ?? []).length > 0 ? (
        <div style={{ display: "flex", flexWrap: "wrap", gap: 6 }}>
          {(thread.streamingToolCalls ?? []).map((c, i) => (
            <Text key={i} size={100} style={{ color: "var(--colorNeutralForeground3)" }}>
              {c.status === "Streaming" ? "●" : c.isSuccess === false || c.status === "Failed" ? "✗" : "✓"}{" "}
              {str(c.displayName) || str(c.name)}
            </Text>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function SelectorDropdown({
  label,
  value,
  options,
  onSelect,
}: {
  label: string;
  value: string;
  options: { path: string; name: string }[];
  onSelect: (path: string) => void;
}): ReactNode {
  if (options.length === 0) return null;
  const selected = options.find((o) => o.path === value);
  return (
    <Dropdown
      aria-label={label}
      placeholder={label}
      size="small"
      value={selected?.name ?? (value || "")}
      selectedOptions={value ? [value] : []}
      onOptionSelect={(_, d) => onSelect(str(d.optionValue))}
      style={{ minWidth: 120 }}
    >
      {options.map((o) => (
        <Option key={o.path} value={o.path} text={o.name}>
          {o.name}
        </Option>
      ))}
    </Dropdown>
  );
}

export function ThreadChatView({ control }: { control: UiControl }): ReactNode {
  const ops = useMeshOps();
  const vm = useResolve(control.threadViewModel) as Json;
  const boundPath =
    str(useResolve(control.threadPath)) || (vm && typeof vm === "object" ? str(vm.threadPath) : "");
  const initialContext = str(useResolve(control.initialContext));
  const hideEmptyState = !!useResolve(control.hideEmptyState);
  const showFullHeader = !!useResolve(control.showFullHeader);

  // Once submit creates the thread, all later sends drain through it (Blazor's onCreated sets
  // threadPath the moment the node exists — message 2+ must never re-StartThread).
  const [createdPath, setCreatedPath] = useState<string | null>(null);
  const threadPath = createdPath ?? (boundPath || null);

  const threadNode = useNodeState(ops, threadPath);
  const thread = (threadNode?.content ?? {}) as ThreadJson;
  const isExecuting = thread.status === "StartingExecution" || thread.status === "Executing";

  const ids = useMemo(() => (Array.isArray(thread.messages) ? thread.messages.map(String) : []), [thread.messages]);
  const pending = (thread.pendingUserMessages ?? {}) as Record<string, ThreadMessageJson>;
  const cells = useMessageCells(ops, threadPath, ids);

  // Ordered bubbles: the messages list (cell content, falling back to the pending payload), then
  // any queued messages the watcher hasn't drained into `messages` yet.
  const items = [
    ...ids.map((id) => {
      const cell = threadPath ? cells[`${threadPath}/${id}`] : undefined;
      return { id, msg: cell ?? pending[id], queued: !cell && !!pending[id] };
    }),
    ...Object.keys(pending)
      .filter((id) => !ids.includes(id))
      .map((id) => ({ id, msg: pending[id], queued: true })),
  ].filter((x): x is { id: string; msg: ThreadMessageJson; queued: boolean } => x.msg != null);

  // Composer state — the selection defaults to the thread's embedded composer (the single source of
  // the round's selection); an explicit pick overrides and folds back into the composer on submit.
  const [text, setText] = useState("");
  const [agent, setAgent] = useState<string | null>(null);
  const [model, setModel] = useState<string | null>(null);
  const [sendError, setSendError] = useState<string | null>(null);
  const composer = thread.composer ?? {};
  const effectiveAgent = agent ?? str(composer.agentName);
  const effectiveModel = model ?? str(composer.modelName);
  const agentOptions = useSearchOptions(ops, "nodeType:Agent");
  const modelOptions = useSearchOptions(ops, "nodeType:Model");

  // Auto-scroll to the latest bubble (Blazor's ChatMessageList behavior).
  const endRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    endRef.current?.scrollIntoView?.({ behavior: "smooth", block: "end" });
  }, [items.length, thread.streamingText]);

  const namespacePath = initialContext;
  const canSend = !!ops && text.trim().length > 0 && !isExecuting && (!!threadPath || !!namespacePath);

  const send = () => {
    if (!canSend || !ops) return;
    const userText = text.trim();
    setText("");
    setSendError(null);
    const opts: ThreadSubmitOptions = {};
    if (effectiveAgent) opts.agentName = effectiveAgent;
    if (effectiveModel) opts.modelName = effectiveModel;
    const submitted: Promise<unknown> = threadPath
      ? ops.submitMessage(threadPath, userText, opts)
      : ops
          .startThread(namespacePath, userText, { ...opts, contextPath: initialContext || undefined })
          .then((r) => setCreatedPath(r.path));
    submitted.catch((err) => {
      // Mirror Blazor's onError: SURFACE the failure and restore the typed text — a silent reset is
      // the "message vanished, no idea why" symptom.
      setSendError(err instanceof Error ? err.message : String(err));
      setText(userText);
    });
  };

  const stop = () => {
    if (ops && threadPath) ops.patch(threadPath, { content: { requestedStatus: "Cancelled" } });
  };

  const onComposerKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  };

  if (!ops) {
    return (
      <div style={{ padding: 16, color: "var(--colorNeutralForeground3)", ...controlStyle(control) }}>
        <Text size={200}>Thread chat needs a live mesh connection — wrap the app in a MeshOpsProvider.</Text>
      </div>
    );
  }

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        gap: 8,
        minHeight: 0,
        height: "100%",
        ...controlStyle(control),
      }}
    >
      {showFullHeader && threadNode?.name ? (
        <Text size={500} weight="semibold">
          {str(threadNode.name)}
        </Text>
      ) : null}

      <div style={{ flex: 1, minHeight: 0, overflowY: "auto", display: "flex", flexDirection: "column", gap: 8 }}>
        {items.length === 0 && !isExecuting && !hideEmptyState ? (
          <div style={{ textAlign: "center", padding: 24, color: "var(--colorNeutralForeground3)" }}>
            <Text size={400} block>
              💬
            </Text>
            <Text size={200}>No messages yet — ask anything below.</Text>
          </div>
        ) : null}
        {items.map((x) => (
          <MessageBubble key={x.id} msg={x.msg} queued={x.queued} />
        ))}
        {isExecuting ? <ExecutionBar thread={thread} onStop={stop} /> : null}
        <div ref={endRef} />
      </div>

      {sendError ? (
        <Text size={200} role="alert" style={{ color: "var(--colorStatusDangerForeground1)" }}>
          Couldn't send your message: {sendError}
        </Text>
      ) : null}

      <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
        <Textarea
          aria-label="Message"
          placeholder="Type a message…"
          value={text}
          disabled={isExecuting}
          onChange={(_, d) => setText(d.value)}
          onKeyDown={onComposerKeyDown}
          resize="vertical"
        />
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <SelectorDropdown label="Agent" value={effectiveAgent} options={agentOptions} onSelect={setAgent} />
          <SelectorDropdown label="Model" value={effectiveModel} options={modelOptions} onSelect={setModel} />
          <div style={{ flex: 1 }} />
          <Tooltip content="Send" relationship="label">
            <Button appearance="primary" icon={<Send20Filled />} disabled={!canSend} onClick={send}>
              Send
            </Button>
          </Tooltip>
        </div>
      </div>
    </div>
  );
}

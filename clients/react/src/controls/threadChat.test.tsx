// ThreadChat parity with Blazor's ThreadChatView: the message list renders from the THREAD NODE's
// live stream (thread node + per-message satellite cells at {threadPath}/{id}), queued messages come
// from content.pendingUserMessages, the exec bar reflects Thread.status, and the composer is gated
// exactly like Blazor (whitespace-only OR IsExecuting → disabled). Submission drains through the
// canonical MeshOps surface — startThread for a new thread, submitMessage for an existing one; the
// wire shapes those produce (CreateNodeRequest / RFC 7396 PatchDataRequest) are pinned by
// @meshweaver/client-web's threads.test.ts, whose `Mesh` satisfies MeshOps structurally.

import { beforeAll, describe, expect, it } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree } from "../core.js";
import type { AutocompleteSuggestion, MeshNodeState, MeshOps, ThreadSubmitOptions } from "../live/meshOps.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
  if (!(globalThis as any).ResizeObserver)
    (globalThis as any).ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
});

// A MeshOps fake backed by static node snapshots: watch(path) yields the snapshot once and then
// stays open (like a live stream that has emitted its initial state).
class FakeOps implements MeshOps {
  readonly submitCalls: { path: string; text: string; opts?: ThreadSubmitOptions }[] = [];
  readonly startCalls: { ns: string; text: string; opts?: ThreadSubmitOptions }[] = [];
  readonly patchCalls: { path: string; fields: Record<string, unknown> }[] = [];
  readonly autocompleteCalls: { query: string; contextPath?: string }[] = [];
  /** Optional (like the real MeshOps.autocomplete) — a test sets it to open the @-mention dropdown. */
  autocomplete?: (query: string, contextPath?: string) => Promise<AutocompleteSuggestion[]>;
  constructor(private readonly nodes: Record<string, Record<string, unknown>>) {}

  async *watch(path: string): AsyncIterableIterator<MeshNodeState> {
    const content = this.nodes[path];
    if (content === undefined) throw new Error(`no node at ${path}`); // a missing satellite errors the stream
    yield { path, name: path.split("/").pop(), content };
    await new Promise<void>(() => undefined); // stream stays open (aborted via iterator.return on cleanup)
  }

  async startThread(ns: string, text: string, opts?: ThreadSubmitOptions) {
    this.startCalls.push({ ns, text, opts });
    return { path: `${ns}/_Thread/created-1234`, userMessageId: "aaaa1111" };
  }

  async submitMessage(path: string, text: string, opts?: ThreadSubmitOptions) {
    this.submitCalls.push({ path, text, opts });
    return "bbbb2222";
  }

  patch(path: string, fields: Record<string, unknown>): void {
    this.patchCalls.push({ path, fields });
  }
}

const THREAD = "rbuergi/_Thread/t-1";

function chatTree(control: Record<string, unknown> = {}): AreaTree {
  return { data: {}, areas: { main: { $type: "ThreadChat", threadPath: THREAD, ...control } } };
}

function renderChat(ops: MeshOps, control: Record<string, unknown> = {}) {
  return render(<MeshAreaView source={new StaticAreaSource(chatTree(control))} rootArea="main" ops={ops} />);
}

describe("ThreadChat — message list from the thread node's live stream", () => {
  it("renders user/assistant bubbles from Thread.messages via the per-message cells", async () => {
    const ops = new FakeOps({
      [THREAD]: { messages: ["m1", "m2"], status: "Idle" },
      [`${THREAD}/m1`]: { role: "user", text: "Hello there" },
      [`${THREAD}/m2`]: { role: "assistant", text: "Hi! **How** can I help?", agentName: "Coder" },
    });
    renderChat(ops);
    await screen.findByText("Hello there");
    // Markdown-rendered assistant text (the ** renders as <strong>).
    await screen.findByText("How");
    expect(document.querySelector('[data-role="user"]')).toBeTruthy();
    expect(document.querySelector('[data-role="assistant"]')).toBeTruthy();
    expect(screen.getByText(/Coder/)).toBeTruthy();
  });

  it("renders tool calls collapsed with their status glyph", async () => {
    const ops = new FakeOps({
      [THREAD]: { messages: ["m1"], status: "Idle" },
      [`${THREAD}/m1`]: {
        role: "assistant",
        text: "done",
        toolCalls: [
          { name: "search", isSuccess: true, arguments: '{"q":"laptops"}' },
          { name: "update", displayName: "Update node", isSuccess: false, status: "Failed" },
        ],
      },
    });
    renderChat(ops);
    await screen.findByText(/search/);
    expect(screen.getByText(/Update node/)).toBeTruthy();
    const details = document.querySelectorAll("details");
    expect(details.length).toBe(2);
    expect(details[0].open).toBe(false); // collapsed by default, like Blazor
  });

  it("renders queued messages from pendingUserMessages before their cells exist", async () => {
    const ops = new FakeOps({
      [THREAD]: {
        messages: [],
        status: "Idle",
        pendingUserMessages: { p1: { role: "user", text: "still queued" } },
      },
    });
    renderChat(ops);
    await screen.findByText("still queued");
    expect(screen.getByText("queued…")).toBeTruthy();
  });

  it("shows the exec bar (status text + Stop) while the thread executes, and Stop flips requestedStatus", async () => {
    const ops = new FakeOps({
      [THREAD]: {
        messages: [],
        status: "Executing",
        executionStatus: "Calling search…",
        streamingText: "Here is what I found so far",
        streamingToolCalls: [{ name: "search", status: "Streaming" }],
      },
    });
    renderChat(ops);
    await screen.findByText(/Calling search…/);
    expect(screen.getByText(/Here is what I found so far/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /Stop/ }));
    expect(ops.patchCalls).toEqual([{ path: THREAD, fields: { content: { requestedStatus: "Cancelled" } } }]);
  });
});

describe("ThreadChat — composer gating (Blazor: whitespace-only || IsExecuting → disabled)", () => {
  it("Send is disabled for empty text and enables when text is typed", async () => {
    const ops = new FakeOps({ [THREAD]: { messages: [], status: "Idle" } });
    renderChat(ops);
    const send = await screen.findByRole("button", { name: "Send" });
    expect((send as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText("Message"), { target: { value: "hello" } });
    expect((send as HTMLButtonElement).disabled).toBe(false);
    fireEvent.change(screen.getByLabelText("Message"), { target: { value: "   " } });
    expect((send as HTMLButtonElement).disabled).toBe(true);
  });

  it("Send and the textarea are disabled while the thread is executing", async () => {
    const ops = new FakeOps({ [THREAD]: { messages: [], status: "StartingExecution" } });
    renderChat(ops);
    await screen.findByRole("status"); // the exec bar
    expect((screen.getByRole("button", { name: "Send" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByLabelText("Message") as HTMLTextAreaElement).disabled).toBe(true);
  });
});

describe("ThreadChat — submission through the canonical thread surface", () => {
  it("submitMessage drains through the EXISTING thread with the composer's sticky selection", async () => {
    const ops = new FakeOps({
      [THREAD]: { messages: [], status: "Idle", composer: { agentName: "Agent/Coder", modelName: "Model/gpt" } },
    });
    renderChat(ops);
    fireEvent.change(await screen.findByLabelText("Message"), { target: { value: "  and another thing  " } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(ops.submitCalls.length).toBe(1));
    expect(ops.submitCalls[0]).toEqual({
      path: THREAD,
      text: "and another thing",
      opts: { agentName: "Agent/Coder", modelName: "Model/gpt" },
    });
    expect(ops.startCalls).toEqual([]); // never re-StartThread on an existing thread
    expect((screen.getByLabelText("Message") as HTMLTextAreaElement).value).toBe(""); // composer cleared
  });

  it("with no threadPath, Enter starts a thread under the initial context and routes send #2 to it", async () => {
    const ops = new FakeOps({
      "rbuergi/_Thread/created-1234": { messages: [], status: "Idle" },
    });
    renderChat(ops, { threadPath: undefined, initialContext: "rbuergi" });
    const input = await screen.findByLabelText("Message");
    fireEvent.change(input, { target: { value: "Hello from React" } });
    fireEvent.keyDown(input, { key: "Enter" });
    await waitFor(() => expect(ops.startCalls.length).toBe(1));
    expect(ops.startCalls[0].ns).toBe("rbuergi");
    expect(ops.startCalls[0].text).toBe("Hello from React");
    expect(ops.startCalls[0].opts?.contextPath).toBe("rbuergi");
    // The created path becomes the thread — message 2 must submit, not create a second thread.
    fireEvent.change(input, { target: { value: "second message" } });
    fireEvent.keyDown(input, { key: "Enter" });
    await waitFor(() => expect(ops.submitCalls.length).toBe(1));
    expect(ops.submitCalls[0].path).toBe("rbuergi/_Thread/created-1234");
    expect(ops.startCalls.length).toBe(1);
  });

  it("with no threadPath AND no context, Send stays disabled (nowhere to anchor the thread)", async () => {
    const ops = new FakeOps({});
    renderChat(ops, { threadPath: undefined });
    fireEvent.change(await screen.findByLabelText("Message"), { target: { value: "hello" } });
    expect((screen.getByRole("button", { name: "Send" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("surfaces a submit failure and restores the typed text (never a silent drop)", async () => {
    const ops = new FakeOps({ [THREAD]: { messages: [], status: "Idle" } });
    ops.submitMessage = async () => {
      throw new Error("Access denied");
    };
    renderChat(ops);
    const input = await screen.findByLabelText("Message");
    fireEvent.change(input, { target: { value: "will fail" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await screen.findByRole("alert");
    expect(screen.getByRole("alert").textContent).toContain("Access denied");
    expect((input as HTMLTextAreaElement).value).toBe("will fail");
  });
});

// Type `value` into the composer with the caret at its end — the shape the @-token tracker reads
// (onChange consults ev.target.selectionStart). fireEvent applies value then selectionStart.
function typeInto(input: HTMLElement, value: string) {
  fireEvent.change(input, { target: { value, selectionStart: value.length, selectionEnd: value.length } });
}

describe("ThreadChat — @-mention autocomplete (the Blazor MeshNodeAutocomplete parity surface)", () => {
  function withSuggestions(sugg: AutocompleteSuggestion[]) {
    const ops = new FakeOps({ [THREAD]: { messages: [], status: "Idle" } });
    ops.autocomplete = async (query, contextPath) => {
      ops.autocompleteCalls.push({ query, contextPath });
      return sugg;
    };
    return ops;
  }

  it("opens a suggestion dropdown for an @-token, queries ops.autocomplete, and shows the hint", async () => {
    const ops = withSuggestions([
      { label: "Coder", insertText: "@agent/Coder", path: "Agent/Coder" },
    ]);
    renderChat(ops);
    // The composer surfaces the @-reference hint whenever the host exposes autocomplete.
    expect(screen.getByText("Use @ to reference nodes")).toBeTruthy();
    const input = await screen.findByLabelText("Message");
    typeInto(input, "look at @ag");
    await waitFor(() => expect(document.querySelector("[data-mw-autocomplete]")).toBeTruthy());
    // Debounced query fired with the @-token (leading @ kept — same as the search bar's @path branch).
    expect(ops.autocompleteCalls.some((c) => c.query === "@ag")).toBe(true);
    expect(screen.getByText("Coder")).toBeTruthy(); // primary label
    expect(screen.getByText("Agent/Coder")).toBeTruthy(); // secondary line (path)
  });

  it("inserts the suggestion's insertText at the caret, replacing the partial @-token (mouse pick)", async () => {
    const ops = withSuggestions([{ label: "Coder", insertText: "@agent/Coder", path: "Agent/Coder" }]);
    renderChat(ops);
    const input = (await screen.findByLabelText("Message")) as HTMLTextAreaElement;
    typeInto(input, "hi @ag");
    await screen.findByText("Coder");
    // mousedown (not click) — the composer picks on mousedown so the textarea never loses focus first.
    fireEvent.mouseDown(screen.getByText("Coder"));
    expect(input.value).toBe("hi @agent/Coder ");
    expect(document.querySelector("[data-mw-autocomplete]")).toBeNull(); // dropdown dismissed
  });

  it("arrow-navigates and accepts with Enter without submitting the message", async () => {
    const ops = withSuggestions([
      { label: "Coder", insertText: "@agent/Coder" },
      { label: "Planner", insertText: "@agent/Planner" },
    ]);
    renderChat(ops);
    const input = (await screen.findByLabelText("Message")) as HTMLTextAreaElement;
    typeInto(input, "@a");
    await screen.findByText("Planner");
    fireEvent.keyDown(input, { key: "ArrowDown" }); // highlight 0 → 1 (Planner)
    fireEvent.keyDown(input, { key: "Enter" }); // accepts the suggestion, does NOT send
    expect(input.value).toBe("@agent/Planner ");
    expect(ops.submitCalls).toEqual([]);
    expect(ops.startCalls).toEqual([]);
  });

  it("dismisses the dropdown on Escape, and a later Enter then submits normally", async () => {
    const ops = withSuggestions([{ label: "Coder", insertText: "@agent/Coder" }]);
    renderChat(ops);
    const input = (await screen.findByLabelText("Message")) as HTMLTextAreaElement;
    typeInto(input, "hello @ag");
    await screen.findByText("Coder");
    fireEvent.keyDown(input, { key: "Escape" });
    await waitFor(() => expect(document.querySelector("[data-mw-autocomplete]")).toBeNull());
    fireEvent.keyDown(input, { key: "Enter" });
    await waitFor(() => expect(ops.submitCalls.length).toBe(1));
    expect(ops.submitCalls[0].text).toBe("hello @ag");
  });

  it("without ops.autocomplete, typing @ opens no dropdown and shows no hint (graceful degradation)", async () => {
    const ops = new FakeOps({ [THREAD]: { messages: [], status: "Idle" } }); // no autocomplete
    renderChat(ops);
    expect(screen.queryByText("Use @ to reference nodes")).toBeNull();
    const input = await screen.findByLabelText("Message");
    typeInto(input, "@ag");
    // Give the debounce window a chance — nothing should open.
    await new Promise((r) => setTimeout(r, 300));
    expect(document.querySelector("[data-mw-autocomplete]")).toBeNull();
  });
});

describe("ThreadChat — composer selection row (Blazor's harness · agent · model status chips)", () => {
  it("surfaces the composer's default harness/agent/model as chips when no options load", async () => {
    const ops = new FakeOps({
      [THREAD]: {
        messages: [],
        status: "Idle",
        composer: { harness: "MeshWeaver", agentName: "Agent/Coder", modelName: "Provider/OpenAI/gpt-4o" },
      },
    });
    renderChat(ops);
    await screen.findByLabelText("Message");
    // FakeOps exposes no `search`, so the option dropdowns stay empty — the bound selection must still
    // show (Blazor surfaces the default regardless), rendered as last-segment chips.
    expect(screen.getByText("MeshWeaver")).toBeTruthy();
    expect(screen.getByText("Coder")).toBeTruthy();
    expect(screen.getByText("gpt-4o")).toBeTruthy();
  });

  it("defaults the harness chip to MeshWeaver even with no composer selection", async () => {
    const ops = new FakeOps({ [THREAD]: { messages: [], status: "Idle" } });
    renderChat(ops);
    await screen.findByLabelText("Message");
    expect(screen.getByText("MeshWeaver")).toBeTruthy();
  });
});

// Chat-thread submission for the browser/RN client — the in-language port of the canonical .NET
// surface `MeshWeaver.AI.HubThreadExtensions` (hub.StartThread / hub.SubmitMessage) + its pure
// helpers (`ThreadNodeType.BuildThreadNode`, `ThreadInput.CreateUserMessage/ApplyUserInput`).
//
// Same contract as the .NET side:
//   - StartThread  = CreateNodeRequest with a thread node at {namespace}/_Thread/{speakingId},
//     pre-seeded with the first user message in content.pendingUserMessages (the submission
//     watcher dispatches the first round as soon as the thread hub activates).
//   - SubmitMessage = a JSON-merge patch (RFC 7396) on the thread node adding the message id to
//     content.userMessageIds and the payload to content.pendingUserMessages — the client twin of
//     `workspace.GetMeshNodeStream(threadPath).Update(...)`. The owning hub serialises the patch.
//
// The hub serialises with camelCase properties + string enums (SerializationExtensions), so the
// JSON built here is camelCase and enum-valued strings ("Submitted", "ExecutedInput").
// WIRE: annotated where the exact shape must be confirmed against a running mesh (same convention
// as mesh.ts) — capture a sample from the C# round-trip test.

import { newId } from "./envelope";

/** The `_Thread` satellite partition segment (ThreadNodeType.ThreadPartition). */
export const THREAD_PARTITION = "_Thread";

export interface StartThreadOptions {
  agentName?: string;
  modelName?: string;
  harness?: string;
  contextPath?: string;
  contextReference?: string;
  attachments?: string[];
  createdBy?: string;
  authorName?: string;
  /** Pre-chosen speaking id for the thread node; generated from the text when omitted. */
  speakingId?: string;
  /** Point the thread at an existing node (e.g. an inbound Email) as its MainNode. */
  mainNode?: string;
}

export interface SubmitMessageOptions {
  agentName?: string;
  modelName?: string;
  harness?: string;
  contextPath?: string;
  attachments?: string[];
  createdBy?: string;
  authorName?: string;
}

/**
 * Port of ThreadNodeType.GenerateSpeakingId: "Hello, can you help?" → "hello-can-you-help-a3f9".
 * First ~50 chars, lowercased, non-alphanumerics collapsed to hyphens, 4-char unique suffix.
 */
export function generateSpeakingId(messageText: string): string {
  let slug = (messageText.length > 50 ? messageText.slice(0, 50) : messageText)
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  if (slug.length > 40) slug = slug.slice(0, 40).replace(/-+$/g, "");
  const suffix = newId().slice(0, 4);
  return slug.length === 0 ? suffix : `${slug}-${suffix}`;
}

/** Port of ThreadInput.CreateUserMessage — the camelCase JSON of a user ThreadMessage. */
export function createUserMessage(text: string, opts: SubmitMessageOptions = {}): Record<string, unknown> {
  const message: Record<string, unknown> = {
    role: "user",
    text,
    timestamp: new Date().toISOString(),
    // String enums (EnumMemberJsonStringEnumConverter on the hub).
    type: "ExecutedInput",
    status: "Submitted",
  };
  if (opts.authorName) message.authorName = opts.authorName;
  if (opts.createdBy) message.createdBy = opts.createdBy;
  if (opts.agentName) message.agentName = opts.agentName;
  if (opts.modelName) message.modelName = opts.modelName;
  if (opts.harness) message.harness = opts.harness;
  if (opts.contextPath) message.contextPath = opts.contextPath;
  if (opts.attachments?.length) message.attachments = opts.attachments;
  // NOTE: submitterObjectId/-Name are deliberately NOT set client-side — identity is stamped by the
  // server from the bearer token (the server never trusts a client-claimed AccessContext).
  return message;
}

/**
 * Port of ThreadNodeType.BuildThreadNode + HubThreadExtensions.StartThread's seeding: the thread
 * node at {namespacePath}/_Thread/{speakingId}, pre-seeded with the first user message queued in
 * content.pendingUserMessages and the composer carrying the sticky selection.
 * Returns the node plus the generated ids.
 */
export function buildThreadNode(
  namespacePath: string,
  userText: string,
  opts: StartThreadOptions = {},
): { node: Record<string, unknown>; path: string; userMessageId: string } {
  if (!namespacePath) throw new Error("startThread requires namespacePath.");

  const speakingId = opts.speakingId ?? generateSpeakingId(userText);
  // Sub-threads (delegations) already live under a _Thread segment — don't nest another.
  const ns = namespacePath.includes(`/${THREAD_PARTITION}/`)
    ? namespacePath
    : `${namespacePath}/${THREAD_PARTITION}`;

  // 🚫 Ownerless guard (ActivityNodeGuard.IsOwnerless): a bare _Thread/{id} has no partition /
  // per-node hub to route to — fail fast instead of NotFound-storming the router.
  if (ns === THREAD_PARTITION || ns.startsWith(`${THREAD_PARTITION}/`))
    throw new Error(`startThread refused a top-level/ownerless thread under '${ns}'.`);

  const name = userText.length > 60 ? userText.slice(0, 57) + "..." : userText;
  const userMessageId = newId().slice(0, 8);

  // Thread.Composer is the single source of truth for the round's selection — always seeded.
  const composer: Record<string, unknown> = {};
  if (opts.agentName) composer.agentName = opts.agentName;
  if (opts.modelName) composer.modelName = opts.modelName;
  if (opts.harness) composer.harness = opts.harness;
  if (opts.contextPath) composer.contextPath = opts.contextPath;
  if (opts.contextReference) composer.contextReference = opts.contextReference;

  const hasFirstMessage = userText.trim().length > 0;
  const content: Record<string, unknown> = {
    // WIRE: polymorphic content discriminator — the hub serialises Thread content with the short
    // $type name; confirm against a captured CreateNodeRequest from the C# round-trip test.
    $type: "Thread",
    composer,
  };
  if (opts.createdBy) content.createdBy = opts.createdBy;
  if (hasFirstMessage) {
    content.messages = [userMessageId];
    content.userMessageIds = [userMessageId];
    content.pendingUserMessages = {
      [userMessageId]: createUserMessage(userText, opts),
    };
  }

  const path = `${ns}/${speakingId}`;
  const node: Record<string, unknown> = {
    id: speakingId,
    namespace: ns,
    path,
    name,
    nodeType: "Thread",
    mainNode: opts.mainNode ?? namespacePath,
    content,
  };
  return { node, path, userMessageId };
}

/**
 * Port of ThreadInput.ApplyUserInput as a JSON-MERGE PATCH (RFC 7396) against the thread node:
 * appends to userMessageIds (arrays replace wholesale under merge-patch, so the caller supplies the
 * CURRENT ids), adds the pending payload (objects deep-merge, so only the new key is sent), and
 * folds any explicit agent/model/harness selection into the composer.
 */
export function buildSubmitPatch(
  currentUserMessageIds: readonly string[],
  msgId: string,
  message: Record<string, unknown>,
  opts: SubmitMessageOptions = {},
): Record<string, unknown> {
  const content: Record<string, unknown> = {
    userMessageIds: currentUserMessageIds.includes(msgId)
      ? [...currentUserMessageIds]
      : [...currentUserMessageIds, msgId],
    pendingUserMessages: { [msgId]: message },
  };
  const composer: Record<string, unknown> = {};
  if (opts.agentName) composer.agentName = opts.agentName;
  if (opts.modelName) composer.modelName = opts.modelName;
  if (opts.harness) composer.harness = opts.harness;
  if (Object.keys(composer).length > 0) content.composer = composer;
  return { content };
}

/** Ownerless-threadPath guard for submitMessage (the SubmitMessage twin of the StartThread guard). */
export function isOwnerlessThreadPath(threadPath: string): boolean {
  if (!threadPath) return true;
  const segments = threadPath.split("/").filter((s) => s.length > 0);
  // A bare _Thread/{id} (no owner segment before the partition) has no per-node hub to route to.
  return segments[0] === THREAD_PARTITION;
}

using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// Canonical client-side surface for thread operations. All thread mutations
/// — create, submit, resubmit, delete-from, mark-done, record-failure — go
/// through these <see cref="IMessageHub"/> extensions. The extensions write
/// to the thread node via <c>hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(...)</c>
/// (or <see cref="CreateNodeRequest"/> for new-thread lifecycle); the
/// per-thread submission watcher reacts to the resulting state changes.
///
/// <para><b>Tests, GUI, and agents all call these methods.</b> There is no
/// other entry point — no <c>SubmitMessageRequest</c>, no parameter-bag
/// context records, no direct <c>hub.Post</c> shortcuts. If you find yourself
/// needing one, the answer is to extend this surface (or fold the new
/// operation into the existing thread-node state machine).</para>
///
/// <para>All methods are <c>void</c> / fire-and-forget. Callers observe
/// confirmation by subscribing to the thread node's remote stream (the same
/// stream the UI already binds to). The optional <c>onError</c> / <c>onCreated</c>
/// callbacks exist for one-shot signalling (e.g. the chat view's "navigate
/// to the new thread once it's created") and fire exactly once.</para>
/// </summary>
public static class HubThreadExtensions
{
    // ═════════════════════════════════════════════════════════════════════
    // Submitter identity capture (rides on the data, survives async hops)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Snapshots the SUBMITTER's live <see cref="AccessContext"/> at the submit boundary — the
    /// one moment the originating user's identity is reliably on the AsyncLocal
    /// (<c>AccessService.Context</c>) or the per-circuit fallback (<c>CircuitContext</c>). The
    /// captured <c>(ObjectId, Name)</c> is stamped onto the pending <see cref="ThreadMessage"/>
    /// (<see cref="ThreadMessage.SubmitterObjectId"/> / <see cref="ThreadMessage.SubmitterName"/>)
    /// so the round-dispatch watcher can rebuild the identity AFTER every later async boundary
    /// (its own <c>.Subscribe</c> continuation + the AI streaming continuations) has wiped the
    /// AsyncLocal. This is "capture the identity when computing the submit patch" — the data
    /// carries the truth, not the (by-then-null) AsyncLocal.
    ///
    /// <para>Returns <c>(null, null)</c> when no real user identity is live (e.g. a hub-shaped
    /// principal, or no context at all) — the watcher then falls back to the thread owner derived
    /// from the node, NEVER hub-self. A hub-shaped principal is explicitly rejected here so it can
    /// never be persisted as a fake submitter (the <c>CreatedBy=sync/…</c> class of bug).</para>
    /// </summary>
    private static (string? ObjectId, string? Name) CaptureSubmitter(this IMessageHub hub)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var ctx = accessService?.Context ?? accessService?.CircuitContext;
        if (ctx is null || string.IsNullOrEmpty(ctx.ObjectId)
            || AccessService.LooksLikeHubPrincipal(ctx.ObjectId))
            return (null, null);
        return (ctx.ObjectId, string.IsNullOrEmpty(ctx.Name) ? ctx.ObjectId : ctx.Name);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Create + submit (new thread)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new thread under <paramref name="namespacePath"/> and queues
    /// the first user message on it. The thread node is created via
    /// <see cref="CreateNodeRequest"/> (node-lifecycle, not a mutation) and
    /// pre-seeded with <see cref="MeshThread.PendingUserMessages"/> so the
    /// submission watcher dispatches the first round as soon as the thread
    /// hub activates — no second round-trip.
    ///
    /// <para><paramref name="composer"/>, when supplied, is COPIED onto the created
    /// thread as <see cref="MeshThread.Composer"/> — the thread's own data-bound
    /// chat-input state — with the draft + attachments emptied (the draft became the
    /// first message) and the navigation signal cleared. This is how the out-of-thread
    /// composer's selection (harness/agent/model paths) carries into the thread.</para>
    /// </summary>
    public static void StartThread(
        this IMessageHub hub,
        string namespacePath,
        string userText,
        string? agentName = null,
        string? modelName = null,
        string? contextPath = null,
        IReadOnlyList<string>? attachments = null,
        string? createdBy = null,
        string? authorName = null,
        Action<MeshNode>? onCreated = null,
        Action<string>? onError = null,
        string? mainNode = null,
        string? speakingId = null,
        string? harness = null,
        ThreadComposer? composer = null,
        string? contextReference = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(namespacePath))
        {
            onError?.Invoke("StartThread requires namespacePath.");
            return;
        }

        var threadNode = ThreadNodeType.BuildThreadNode(namespacePath, userText, createdBy, speakingId);
        // Optional: point the thread at an existing node (e.g. an inbound Email) as its MainNode,
        // so the agent's context is that node and consumers can navigate thread → source.
        if (!string.IsNullOrEmpty(mainNode))
            threadNode = threadNode with { MainNode = mainNode };

        // 🚫 Fail fast on a top-level / ownerless thread. BuildThreadNode anchors a thread at
        // {namespacePath}/_Thread/{id}; a top-level namespacePath collapses to a bare _Thread/{id}
        // (empty owner) — there is no partition / per-node hub to route to, so the chat view + the
        // submission watcher NotFound-storm the router (the Memex.Client voice bridge could anchor
        // exactly this when handed an empty namespace). Validate the BUILT node against the same
        // structural invariant the create boundary enforces, and route the error to onError — never
        // post a doomed CreateNodeRequest. Reactive: no throw into the void.
        if (ActivityNodeGuard.IsOwnerless(threadNode, out var ownerlessReason))
        {
            onError?.Invoke($"StartThread refused a top-level/ownerless thread: {ownerlessReason}");
            return;
        }

        // 🚫 Nothing-to-do: whitespace-only first message (e.g. the user ran a slash command —
        // the command text is CUT and what remains is empty). Create the thread (the composer
        // selection still carries onto it) but seed NO pending message — there is nothing to
        // submit, so the submission watcher must NOT dispatch a round (which would reach
        // CreateChatClient with no input and storm "No model selected"). The command's side
        // effect (agent/model/harness pick) already happened on the composer.
        var hasFirstMessage = !string.IsNullOrWhiteSpace(userText);
        var firstMessageId = Guid.NewGuid().ToString("N")[..8];

        // Capture the submitter's identity NOW — synchronous, live AsyncLocal — and persist it on the
        // pending message (below) so the round-dispatch watcher can rebuild it AFTER the async boundary
        // wipes the AsyncLocal. See ThreadMessage.SubmitterObjectId / AccessContextPropagation.md.
        var (submitterObjectId, submitterName) = hub.CaptureSubmitter();

        // 🎯 The thread's COMPOSER is the single source of truth for the round's sticky
        // selection (agent / model / harness / context). Seed it from the supplied composer
        // snapshot when present, else from the explicit agent/model/harness/context params —
        // either way the created thread ALWAYS carries a composer so the submission watcher's
        // PlanNextRound can read the selection from Thread.Composer. The draft + attachments
        // are consumed by the first message; the navigate-signal never carries over.
        var seedComposer = (composer ?? new ThreadComposer
            {
                AgentName = agentName,
                ModelName = modelName,
                Harness = harness,
                ContextPath = contextPath,
                ContextReference = contextReference
            })
            with { MessageContent = null, Attachments = null, OpenThreadPath = null };

        var baseThread = (threadNode.ContentAs<MeshThread>(hub.JsonSerializerOptions) ?? new MeshThread()) with
        {
            Composer = seedComposer
        };

        var seededThread = hasFirstMessage
            ? baseThread with
            {
                Messages = ImmutableList.Create(firstMessageId),
                UserMessageIds = ImmutableList.Create(firstMessageId),
                // The pending ThreadMessage records the per-message context + attachments and a
                // historical stamp of the agent/model/harness; the round's SELECTION is read from
                // Thread.Composer (the single source), not from this message.
                PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty
                    .SetItem(firstMessageId, ThreadInput.CreateUserMessage(
                        userText,
                        createdBy: createdBy,
                        authorName: authorName,
                        agentName: agentName,
                        modelName: modelName,
                        contextPath: contextPath,
                        attachments: attachments,
                        harness: harness,
                        submitterObjectId: submitterObjectId,
                        submitterName: submitterName))
            }
            : baseThread; // empty thread — no round
        threadNode = threadNode with { Content = seededThread };

        // Create under the requested namespace. If that partition denies thread creation (the user
        // lacks Thread permission there — e.g. a read-only Doc/* partition), FALL BACK to the user's
        // OWN partition ({createdBy}/_Thread/{id}), keeping MainNode = the original namespace so the
        // thread stays linked to the node the user was viewing and the agent keeps its context.
        AttemptCreate(namespacePath, threadNode, canFallBack: true);

        void AttemptCreate(string targetNamespace, MeshNode node, bool canFallBack)
        {
            var delivery = hub.Post(
                new CreateNodeRequest(node),
                o => o.WithTarget(new Address(targetNamespace)));

            if (delivery == null)
            {
                onError?.Invoke("Hub.Post returned null");
                return;
            }

            hub.Observe((IMessageDelivery)delivery)
                .Subscribe(
                    response =>
                    {
                        if (response.Message is CreateNodeResponse { Success: true } cnr)
                        {
                            onCreated?.Invoke(cnr.Node ?? node);
                            return;
                        }
                        var err = (response.Message as CreateNodeResponse)?.Error ?? "unknown";
                        if (canFallBack && TryBuildUserPartitionFallback(targetNamespace, err, out var fb))
                        {
                            AttemptCreate(createdBy!, fb!, canFallBack: false);
                            return;
                        }
                        onError?.Invoke($"Thread creation failed: {err}");
                    },
                    ex =>
                    {
                        if (canFallBack && TryBuildUserPartitionFallback(targetNamespace, ex.Message, out var fb))
                        {
                            AttemptCreate(createdBy!, fb!, canFallBack: false);
                            return;
                        }
                        onError?.Invoke($"Thread creation failed: {ex.Message}");
                    });
        }

        // True (with the rebuilt node) when the failure is an access denial AND a distinct user
        // partition is available to retry in. Match on the AccessControlPipeline denial shape
        // ("Access denied: … lacks <perm> permission on …").
        bool TryBuildUserPartitionFallback(string targetNamespace, string? error, out MeshNode? fallbackNode)
        {
            fallbackNode = null;
            if (string.IsNullOrEmpty(createdBy)
                || string.IsNullOrEmpty(error)
                || !error.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
                return false;

            // Already in the user's own partition → falling back can't help; surface the error.
            var targetPartition = targetNamespace.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(targetPartition, createdBy, StringComparison.OrdinalIgnoreCase))
                return false;

            // Re-anchor the SAME thread (id + seeded content) under {createdBy}/_Thread/{id}, but keep
            // MainNode pointing at the original namespace so thread→source navigation + agent context hold.
            fallbackNode = ThreadNodeType.BuildThreadNode(createdBy!, userText, createdBy, threadNode.Id) with
            {
                MainNode = string.IsNullOrEmpty(mainNode) ? namespacePath : mainNode,
                Content = seededThread
            };
            return true;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Submit (existing thread)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Submits a user message into an existing thread. Writes
    /// <see cref="MeshThread.PendingUserMessages"/> via
    /// <c>hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(...)</c>.
    /// The per-thread submission watcher drains the queue into a new round.
    /// </summary>
    public static void SubmitMessage(
        this IMessageHub hub,
        string threadPath,
        string userText,
        string? agentName = null,
        string? modelName = null,
        string? contextPath = null,
        IReadOnlyList<string>? attachments = null,
        string? createdBy = null,
        string? authorName = null,
        Action<string>? onError = null,
        string? harness = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath))
        {
            onError?.Invoke("SubmitMessage requires threadPath. Use StartThread for new threads.");
            return;
        }

        // 🚫 Fail fast on a top-level / ownerless threadPath. A bare _Thread/{id} (empty owner) has
        // no partition / per-node hub, so AppendUserInput's cross-hub write NotFound-storms the
        // router. Validate the path against the same structural invariant the create boundary
        // enforces (owned {owner}/_Thread/{id} and sub-thread paths pass; only the bare top-level
        // shape is rejected).
        if (ActivityNodeGuard.IsOwnerless(MeshNode.FromPath(threadPath), out var ownerlessReason))
        {
            onError?.Invoke($"SubmitMessage refused a top-level/ownerless threadPath: {ownerlessReason}");
            return;
        }

        // 🚫 Nothing-to-do: whitespace-only text (e.g. the user ran a slash command — the
        // command text is CUT and what remains is empty). There is nothing to submit, so do
        // NOT append a pending message: enqueuing an empty round would reach CreateChatClient
        // with no input and storm "No model selected". The command's side effect already ran.
        if (string.IsNullOrWhiteSpace(userText))
            return;

        var (submitterObjectId, submitterName) = hub.CaptureSubmitter();
        var userMessage = ThreadInput.CreateUserMessage(
            userText ?? string.Empty,
            createdBy: createdBy,
            authorName: authorName,
            agentName: agentName,
            modelName: modelName,
            contextPath: contextPath,
            attachments: attachments,
            harness: harness,
            submitterObjectId: submitterObjectId,
            submitterName: submitterName);
        try
        {
            ThreadInput.AppendUserInput(hub.GetWorkspace(), threadPath, userMessage);
        }
        catch (Exception ex)
        {
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
            logger?.LogWarning(ex, "[SubmitMessage] AppendUserInput threw for {ThreadPath}", threadPath);
            onError?.Invoke($"SubmitMessage failed: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Submit from the thread's composer (drain + empty, one atomic update)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Submits the thread's own composer (<see cref="MeshThread.Composer"/>) as the next
    /// user message — ONE atomic <c>stream.Update</c> on the thread node that
    /// (a) builds the user message from <paramref name="userText"/> (or, when null, the
    /// composer's persisted <see cref="ThreadComposer.MessageContent"/> draft) carrying the
    /// composer's harness/agent/model selection (picked node paths — normalized at the
    /// execution boundary), (b) queues it via <see cref="ThreadInput.ApplyUserInput"/>
    /// (<see cref="MeshThread.PendingUserMessages"/> + <c>Pending*</c> hints), and
    /// (c) EMPTIES the composer (draft + attachments). The per-thread submission watcher
    /// reacts to the resulting state change and dispatches the round.
    ///
    /// <para>No-op when there is no text to submit. <paramref name="contextPath"/> and
    /// <paramref name="attachments"/> override the composer's persisted values when supplied
    /// (the Blazor chat passes its live nav context + attachment chips).</para>
    /// </summary>
    public static void SubmitComposer(
        this IMessageHub hub,
        string threadPath,
        string? userText = null,
        string? contextPath = null,
        IReadOnlyList<string>? attachments = null,
        string? createdBy = null,
        string? authorName = null,
        Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath))
        {
            onError?.Invoke("SubmitComposer requires threadPath.");
            return;
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
        var msgId = Guid.NewGuid().ToString("N")[..8];
        var (submitterObjectId, submitterName) = hub.CaptureSubmitter();

        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.ContentAs<MeshThread>(hub.JsonSerializerOptions, logger);
            // Existing node whose content can't be recovered → leave it alone, NEVER clobber.
            if (node.Content is not null && t is null)
                return node;
            t ??= new MeshThread();
            var c = t.Composer ?? new ThreadComposer();
            var text = !string.IsNullOrWhiteSpace(userText) ? userText : c.MessageContent;
            if (string.IsNullOrWhiteSpace(text))
                return node; // nothing to submit — no-op (byte-identical node dedupes downstream)

            var message = ThreadInput.CreateUserMessage(
                text!,
                createdBy: createdBy,
                authorName: authorName,
                agentName: c.AgentName,
                modelName: c.ModelName,
                contextPath: contextPath ?? c.ContextPath,
                attachments: attachments ?? c.Attachments,
                harness: c.Harness,
                submitterObjectId: submitterObjectId,
                submitterName: submitterName);

            return node with
            {
                Content = ThreadInput.ApplyUserInput(t, msgId, message) with
                {
                    Composer = c with { MessageContent = null, Attachments = null }
                }
            };
        }).Subscribe(
            _ => { },
            ex =>
            {
                logger?.LogWarning(ex,
                    "SubmitComposer: thread Update failed for {ThreadPath}", threadPath);
                onError?.Invoke($"SubmitComposer failed: {ex.Message}");
            });
    }

    // ═════════════════════════════════════════════════════════════════════
    // Resubmit (truncate after a user message and re-queue it)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Truncates the thread after <paramref name="userMessageId"/> and re-queues
    /// it as a new pending user message. Single <c>stream.Update</c> on the
    /// thread node: drops <c>Messages</c> after the resubmit point, removes the
    /// id from <c>IngestedMessageIds</c>, puts the (optionally edited) user
    /// message back into <c>PendingUserMessages</c>, and resets <c>Status</c>
    /// to <c>Idle</c>. The submission watcher then dispatches the next round
    /// naturally. The user cell's text — a separate node — is updated through
    /// the shared <see cref="IMeshNodeStreamCache"/> when <paramref name="newUserText"/>
    /// is supplied.
    /// </summary>
    public static void ResubmitMessage(
        this IMessageHub hub,
        string threadPath,
        string userMessageId,
        string? newUserText = null,
        string? agentName = null,
        string? modelName = null,
        Action<string>? onError = null,
        string? harness = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath) || string.IsNullOrEmpty(userMessageId))
        {
            onError?.Invoke("ResubmitMessage requires threadPath and userMessageId.");
            return;
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");

        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.ContentAs<MeshThread>(hub.JsonSerializerOptions);
            if (t is null) return node;

            var idx = t.Messages.IndexOf(userMessageId);
            if (idx < 0) return node; // id not in thread — no-op

            var keep = t.Messages.Take(idx).ToImmutableList();
            var trimmedUserIds = t.UserMessageIds
                .Where(uid => keep.Contains(uid) || uid == userMessageId)
                .ToImmutableList();
            if (!trimmedUserIds.Contains(userMessageId))
                trimmedUserIds = trimmedUserIds.Add(userMessageId);

            var ingested = t.IngestedMessageIds.Remove(userMessageId);
            var replayMessage = new ThreadMessage
            {
                Role = "user",
                Text = newUserText ?? "",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput,
                AgentName = agentName,
                ModelName = modelName,
                Harness = harness
            };
            var pending = t.PendingUserMessages.SetItem(userMessageId, replayMessage);

            return node with
            {
                Content = t with
                {
                    Messages = keep,
                    UserMessageIds = trimmedUserIds,
                    IngestedMessageIds = ingested,
                    // The replay ThreadMessage carries the selection (agent/model/harness) —
                    // no thread-level Pending* mirror.
                    PendingUserMessages = pending,
                    Status = ThreadExecutionStatus.Idle,
                    ActiveMessageId = null,
                    ExecutionStartedAt = null
                }
            };
        }).Subscribe(
            _ => { },
            ex =>
            {
                logger?.LogWarning(ex,
                    "ResubmitMessage: thread Update failed for {ThreadPath} message {MessageId}",
                    threadPath, userMessageId);
                onError?.Invoke($"ResubmitMessage failed: {ex.Message}");
            });

        // Cell-text update — the per-message satellite is a SEPARATE node, so
        // it goes through the shared cache rather than the thread-node Update.
        // Independent of the thread Update above; no ordering dependency. Only
        // runs when the caller supplied new text.
        if (!string.IsNullOrEmpty(newUserText))
        {
            var cellPath = $"{threadPath}/{userMessageId}";
            hub.GetMeshNodeStream(cellPath).Update(node =>
            {
                var existing = node.ContentAs<ThreadMessage>(hub.JsonSerializerOptions);
                var nextContent = existing is not null
                    ? existing with { Text = newUserText!, Timestamp = DateTime.UtcNow }
                    : new ThreadMessage
                    {
                        Role = "user",
                        Text = newUserText!,
                        Timestamp = DateTime.UtcNow,
                        Type = ThreadMessageType.ExecutedInput
                    };
                return node with
                {
                    NodeType = node.NodeType ?? ThreadMessageNodeType.NodeType,
                    Content = nextContent
                };
            }).Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "ResubmitMessage: cell-text Update failed for {CellPath}", cellPath));
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // Delete from message (truncate Messages list at the given message)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Truncates <see cref="MeshThread.Messages"/> starting at
    /// <paramref name="atMessageId"/> (exclusive — drops <paramref name="atMessageId"/>
    /// and everything after). Single <c>stream.Update</c> on the thread node;
    /// no watcher indirection.
    /// </summary>
    public static void DeleteFromMessage(
        this IMessageHub hub, string threadPath, string atMessageId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath) || string.IsNullOrEmpty(atMessageId))
            return;

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.ContentAs<MeshThread>(hub.JsonSerializerOptions);
            if (t is null) return node;
            var idx = t.Messages.IndexOf(atMessageId);
            if (idx < 0) return node; // id not in thread — no-op
            return node with
            {
                Content = t with { Messages = t.Messages.Take(idx).ToImmutableList() }
            };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "DeleteFromMessage: Update failed for thread {ThreadPath} message {MessageId}",
                threadPath, atMessageId));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Terminal state — Done / Idle
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Marks the thread <see cref="ThreadExecutionStatus.Done"/> (terminal,
    /// hidden from default catalogs) or re-opens it by flipping back to
    /// <see cref="ThreadExecutionStatus.Idle"/>. Refuses to act while a round
    /// is in flight (the CAS check lives in the Update lambda).
    /// </summary>
    public static void MarkThreadDone(this IMessageHub hub, string threadPath, bool done)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath))
            return;

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
        hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(node =>
        {
            var t = node.ContentAs<MeshThread>(hub.JsonSerializerOptions, logger);
            // Existing node whose content can't be recovered → leave it alone, NEVER clobber.
            if (node.Content is not null && t is null)
                return node;
            t ??= new MeshThread();
            if (t.IsExecuting)
            {
                logger?.LogInformation(
                    "MarkThreadDone: ignored — thread {ThreadPath} is executing (Status={Status})",
                    threadPath, t.Status);
                return node;
            }
            var newStatus = done ? ThreadExecutionStatus.Done : ThreadExecutionStatus.Idle;
            if (t.Status == newStatus) return node;
            return node with { Content = t with { Status = newStatus } };
        }).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "MarkThreadDone: stream.Update failed for thread {ThreadPath} done={Done}",
                threadPath, done));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Record failure (one-shot pending entry)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records a failed submission by creating an error satellite cell and
    /// updating the thread node in one chained operation: <c>CreateNode</c>
    /// for the error cell, then a single <c>stream.Update</c> on the thread
    /// node to append both the user-message id and the error-cell id to
    /// <c>Messages</c> + bookkeeping in <c>UserMessageIds</c> /
    /// <c>IngestedMessageIds</c>. No intent indirection, no watcher.
    /// </summary>
    public static void RecordSubmissionFailure(
        this IMessageHub hub,
        string threadPath,
        string userMessageId,
        string userText,
        string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrEmpty(threadPath))
            return;

        var errorCellId = Guid.NewGuid().ToString("N")[..8];
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.AI.HubThreadExtensions");
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = hub.GetWorkspace();

        var errorCell = new MeshNode(errorCellId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = threadPath,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = $"**Submission failed:** {errorMessage}",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };

        meshService.CreateNode(errorCell)
            .SelectMany(_ => workspace.GetMeshNodeStream(threadPath).Update(node =>
            {
                var t = node.ContentAs<MeshThread>(hub.JsonSerializerOptions, logger);
                // Existing node whose content can't be recovered → leave it alone, NEVER clobber.
                if (node.Content is not null && t is null)
                    return node;
                t ??= new MeshThread();
                var msgs = t.Messages;
                if (!msgs.Contains(userMessageId)) msgs = msgs.Add(userMessageId);
                if (!msgs.Contains(errorCellId)) msgs = msgs.Add(errorCellId);
                var userIds = t.UserMessageIds.Contains(userMessageId)
                    ? t.UserMessageIds
                    : t.UserMessageIds.Add(userMessageId);
                var ingested = t.IngestedMessageIds.Contains(userMessageId)
                    ? t.IngestedMessageIds
                    : t.IngestedMessageIds.Add(userMessageId);
                return node with
                {
                    Content = t with
                    {
                        Messages = msgs,
                        UserMessageIds = userIds,
                        IngestedMessageIds = ingested
                    }
                };
            }))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "RecordSubmissionFailure: CreateNode+Update failed for {ThreadPath} message {MessageId}",
                    threadPath, userMessageId));
    }
}

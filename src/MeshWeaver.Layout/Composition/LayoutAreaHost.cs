using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Composition;

public record LayoutAreaHost : IDisposable
{
    private readonly IUiControlService uiControlService;
    public LayoutAreaReference Reference { get; }
    public ISynchronizationStream<EntityStore> Stream { get; }
    public IMessageHub Hub => Workspace.Hub;
    public IWorkspace Workspace { get; }
    private readonly Dictionary<object, object?> variables = new();

    public T? GetVariable<T>(object key) => variables.TryGetValue(key, out var value) ? (T?)value : default;
    public bool ContainsVariable(object key) => variables.ContainsKey(key);
    public object? SetVariable(object key, object? value) => variables[key] = value;
    public T GetOrAddVariable<T>(object key, Func<T> factory)
    {
        if (!ContainsVariable(key))
        {
            SetVariable(key, factory.Invoke());
        }

        return GetVariable<T>(key) ?? factory();
    }

    public LayoutDefinition LayoutDefinition { get; }
    private readonly ILogger<LayoutAreaHost> logger;

    public LayoutAreaHost(IWorkspace workspace,
        LayoutAreaReference reference,
        IUiControlService uiControlService,
        Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>>? configuration)
    {

        this.uiControlService = uiControlService;
        Workspace = workspace;
        LayoutDefinition = uiControlService.LayoutDefinition;

        // When Area is null/empty, resolve to the default area
        var isDefaultArea = string.IsNullOrEmpty(reference.Area);
        var resolvedArea = isDefaultArea ? ResolveDefaultArea() : reference.Area!;
        var context = new RenderingContext(resolvedArea) { Layout = reference.Layout };

        // Capture the delivery-scoped AccessContext (AsyncLocal only) at construction time.
        // Context returns only the per-request AsyncLocal — it does NOT fall back to
        // circuitContext, which would leak one user's identity to other users when
        // the LayoutAreaHost is cached and reused across requests (e.g., in Orleans grains).
        var accessService = workspace.Hub.ServiceProvider.GetService<AccessService>();
        var capturedAccessContext = accessService?.Context;
        var ctorLogger = workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Layout.LayoutAreaHost");
        ctorLogger?.LogDebug(
            "[LayoutAreaHost.ctor] hub={Hub} area={Area} captured AccessContext: ObjectId={ObjectId} Email={Email} IsVirtual={IsVirtual} (Context={HasContext}, CircuitContext={HasCircuit})",
            workspace.Hub.Address,
            reference.Area ?? "(default)",
            capturedAccessContext?.ObjectId ?? "(null)",
            capturedAccessContext?.Email ?? "(null)",
            capturedAccessContext?.IsVirtual ?? false,
            accessService?.Context != null,
            accessService?.CircuitContext != null);

        configuration ??= c => c;
        // Create stream with deferred initialization to avoid circular dependency
        // where initialization lambda uses 'this' before Stream property is assigned
        Stream = new SynchronizationStream<EntityStore>(
            new(workspace.Hub.Address, reference),
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<EntityStore>(),
            c => configuration.Invoke(c.WithDeferredInitialization())
                .WithInitialization(_ => BuildInitialization(
                    context, isDefaultArea, resolvedArea, accessService, capturedAccessContext, ctorLogger))
                .WithExceptionCallback(FailRendering));
        Reference = reference;
        logger = Stream.Hub.ServiceProvider.GetRequiredService<ILogger<LayoutAreaHost>>();

        // Manually trigger initialization now that Stream property is assigned
        // This resolves the circular dependency where initialization lambda uses 'this'
        Stream.Hub.Post(new InitializeHubRequest());
        Stream.RegisterForDisposal(this);
        Stream.RegisterForDisposal(
            Stream.Hub.Register<ClickedEvent>(
                OnClick,
                delivery => Stream.ClientId.Equals(delivery.Message.StreamId)
            )
        );
        Stream.RegisterForDisposal(
            Stream.Hub.Register<CloseDialogEvent>(
                OnCloseDialog,
                delivery => Stream.ClientId.Equals(delivery.Message.StreamId)
            )
        );
        Stream.RegisterForDisposal(
            Stream.Hub.Register<BlurEvent>(
                OnBlur,
                delivery => Stream.ClientId.Equals(delivery.Message.StreamId)
            )
        );
    }

    /// <summary>
    /// The observable layout-area initialization, fed to the stream's observable
    /// <c>WithInitialization</c>. It emits exactly one value — the base store (an empty
    /// Areas/Data shell plus the "Building layout…" progress marker) — which the init
    /// subscription sets as the stream's Current (a Full). That base frame establishes
    /// Current and opens the init gate; it is the ONLY init <c>SetCurrent</c>, so it can
    /// never overwrite content the renderers deliver afterwards (the old async
    /// <c>RenderAsync</c> issued a SECOND, final SetCurrent that clobbered any control a
    /// generator had already pushed during the init turn — the LinkedIn "area never
    /// renders" bug the <c>InvokeAsync</c> patch worked around).
    /// <para>
    /// On the same subscribe it wires <see cref="LayoutDefinition.Render"/> to deliver
    /// each accumulated render via <see cref="Stream"/>'s update queue (same serialized
    /// action block as <see cref="UpdateData"/> and the live-area <see cref="UpdateArea"/>
    /// path). Routing content through that single queue — rather than a synchronous
    /// SetCurrent — is what preserves <b>data-before-control</b> ordering: a generator
    /// that seeds its DataContext via <c>host.UpdateData(...)</c> (a queued update)
    /// before returning its control has that data update land first, then the control,
    /// so a client <c>UpdatePointer</c> that follows finds its data path reachable.
    /// </para>
    /// <para>The restored <see cref="AccessContext"/> is cleared on teardown.</para>
    /// </summary>
    private IObservable<EntityStore> BuildInitialization(
        RenderingContext context,
        bool isDefaultArea,
        string resolvedArea,
        AccessService? accessService,
        AccessContext? capturedAccessContext,
        ILogger? ctorLogger)
        => Observable.Create<EntityStore>(observer =>
        {
            ctorLogger?.LogDebug(
                "[LayoutAreaHost.WithInitialization] hub={Hub} area={Area} restoring AccessContext: {ObjectId} (was-captured={HadCapture})",
                Hub.Address, context.Area,
                capturedAccessContext?.ObjectId ?? "(null)",
                capturedAccessContext != null);

            // Restore captured AccessContext for the rendering scope.
            if (capturedAccessContext != null)
                accessService?.SetContext(capturedAccessContext);

            // Base store — empty Areas/Data shell plus the "Building layout…" progress
            // marker. Emitted as the single init SetCurrent (a Full) to establish
            // Current and open the gate.
            var baseStore = new EntityStore()
                .Update(LayoutAreaReference.Areas, x => x)
                .Update(LayoutAreaReference.Data, x => x)
                .Update(LayoutAreaReference.Data,
                    coll => coll.SetItem("progress", new { message = "Building layout...", progress = 0 }));

            // When the requested area was null/empty, the default-area indirection
            // (a NamedAreaControl at "" pointing to the resolved area) is a STATIC
            // resolution known at construction — not rendered content — so put it in the
            // base frame. This means the client's very first frame already carries the
            // resolved default area (consumers that read only the first emission, e.g.
            // the Markdown "default area is content not catalog" assertion, depend on it),
            // and it is rendered before any slow area content arrives.
            if (isDefaultArea && !string.IsNullOrEmpty(resolvedArea))
                baseStore = baseStore.Update(LayoutAreaReference.Areas,
                    coll => coll.SetItem(string.Empty, new NamedAreaControl(resolvedArea)));

            observer.OnNext(baseStore);

            // Wire the renderers to deliver content through the stream's serialized
            // update queue (Stream.Update), AFTER the base Full above. Each emission
            // merges its area content + clears progress; the queue ordering keeps any
            // generator-side UpdateData write (also a Stream.Update) ahead of its
            // control. A late/observable generator re-emitting keeps flowing through
            // the same path for the area's whole lifetime.
            var renderSubscription = LayoutDefinition.Render(this, context, baseStore)
                .Subscribe(PushRenderResult, FailRendering);

            // Tear down: dispose the render subscription and clear the restored context
            // so it never leaks into unrelated work on this thread.
            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                renderSubscription.Dispose();
                if (capturedAccessContext != null)
                    accessService?.SetContext(null);
            });
        });

    /// <summary>
    /// Applies one <see cref="LayoutDefinition.Render"/> emission to the stream through
    /// its serialized update queue, emitting the merged result as a <c>Full</c>.
    /// <para>
    /// Running on the stream's single action block — the SAME queue the generators' own
    /// <c>host.UpdateData(...)</c> writes and a container's nested-sub-area renders use —
    /// is what preserves <b>data-before-control</b> ordering: a write issued earlier in
    /// the generator body is queued (and therefore applied) before this render result.
    /// We MERGE onto the live store (never clobber) so those concurrent writes — the
    /// seeded DataContext and any sub-area controls a container delivered via its own
    /// subscriptions — survive. Emitting a <c>Full</c> (rather than a Patch) means the
    /// client's per-area control streams re-evaluate the complete snapshot, which is how
    /// a freshly-built container's nested sub-areas (keys containing '/') reach their
    /// streams regardless of subscription timing (see <c>LayoutExtensions.GetStream</c>).
    /// </para>
    /// </summary>
    private void PushRenderResult(EntityStoreAndUpdates result)
        => Stream.Update(current =>
        {
            var store = current ?? new EntityStore();

            // Merge the render onto the live store (union — preserves concurrent
            // UpdateData writes, the default-area NamedAreaControl seeded in the base
            // frame, and any sub-area controls delivered separately).
            var resultStore = store.Merge(result.Store);

            // Clear progress now content has been rendered.
            resultStore = resultStore.Update(LayoutAreaReference.Data,
                coll => coll.SetItem("progress", new { message = "", progress = 100 }));

            // Emit as a Full: a complete snapshot the client's control streams
            // re-evaluate wholesale, delivering nested sub-areas reliably.
            return new ChangeItem<EntityStore>(resultStore, Stream.StreamId, Stream.Hub.Version);
        }, ex => logger.LogWarning(ex, "Cannot apply render for {Area}", Reference.Area));




    private IMessageDelivery OnClick(IMessageDelivery<ClickedEvent> request)
    {
        if (GetControl(request.Message.Area) is UiControl { ClickAction: not null } control)
            try
            {
                control.ClickAction.Invoke(
                    new(request.Message.Area, request.Message.Payload ?? new object(), Hub, this)
                );
            }
            catch (Exception ex)
            {
                FailRequest(ex, request);
            }
        return request.Processed();
    }

    private IMessageDelivery OnCloseDialog(IMessageDelivery<CloseDialogEvent> request)
    {
        if (GetControl(request.Message.Area) is DialogControl { CloseAction: not null } control)
            InvokeAsync(() => control.CloseAction.Invoke(
                new(request.Message.Area, request.Message.State, request.Message.Payload ?? new object(), Hub, this)
            ), ex => FailRequest(ex, request));
        return request.Processed();
    }

    private IMessageDelivery OnBlur(IMessageDelivery<BlurEvent> request)
    {
        if (GetControl(request.Message.Area) is IFormControl { OnBlur: Func<UiActionContext, Task> blurAction })
            try
            {
                blurAction.Invoke(
                    new(request.Message.Area, request.Message.Payload ?? new object(), Hub, this)
                );
            }
            catch (Exception ex)
            {
                FailRequest(ex, request);
            }
        return request.Processed();
    }

    private Task FailRequest(Exception? exception, IMessageDelivery request)
    {
        logger.LogWarning(exception, "Request failed");
        Hub.Post(new DeliveryFailure(request, exception?.Message), o => o.ResponseFor(request));
        return Task.CompletedTask;
    }

    public object? GetControl(string area) =>
        Stream
            .Current?.Value?.Collections.GetValueOrDefault(LayoutAreaReference.Areas)
            ?.Instances.GetValueOrDefault(area);





    private UiControl? ConvertToControl(object instance)
        => uiControlService.Convert(instance);


    /// <summary>
    /// Maximum layout-area nesting depth. A control tree that references itself
    /// (a container embedding itself, or a layout area whose content embeds its
    /// own area) recurses through <see cref="RenderArea(RenderingContext, object, EntityStore)"/>
    /// → <c>Render</c> → <c>RenderArea</c> synchronously until the stack
    /// overflows — which in .NET is a fatal, UNCATCHABLE fail-fast (exit
    /// 0xC0000409) that takes down the whole server. Real layouts nest a few
    /// dozen levels at most; 100 is far above any legitimate tree and far below
    /// the stack-overflow frame count, so it converts the crash into a visible
    /// error without ever tripping on a valid layout.
    /// </summary>
    internal const int MaxRenderDepth = 100;

    internal EntityStoreAndUpdates RenderArea(RenderingContext context, object? view, EntityStore store)
    {
        if (view == null)
            return new(store, [], Stream.StreamId);

        // 🚨 Recursion guard — bail BEFORE the stack overflows. One buggy /
        // self-referential layout (e.g. a dynamic NodeType whose Overview embeds
        // itself) must NEVER crash the whole server; surface it as a visible
        // error instead. This was the rbuergi/CatBond crash: opening it recursed
        // here until the process fail-fasted (0xC0000409).
        if (context.Depth > MaxRenderDepth)
        {
            logger?.LogError(
                "[LAH-RENDER] Render depth {Depth} exceeded limit {Max} at area={Area} on hub {Hub} — " +
                "aborting to avoid a stack-overflow crash. The layout is almost certainly recursive " +
                "(a control or area that embeds its own area).",
                context.Depth, MaxRenderDepth, context.Area, Stream.Hub.Address);
            var recursionError = new MarkdownControl(
                $"**Layout recursion detected**\n\nRendering of `{context.Area}` was stopped at depth " +
                $"{context.Depth} to protect the server from a stack-overflow crash. This layout appears " +
                $"to embed itself (a control or area that references its own area). Fix the layout so it " +
                $"no longer renders itself recursively.");
            return store.UpdateControl(context.Area, recursionError);
        }

        var control = ConvertToControl(view);
        if (control == null)
            return new(store, [], Stream.StreamId);

        var dataContext = control.DataContext ?? context.DataContext;

        control = control with { DataContext = dataContext, };


        var ret = ((IUiControl)control).Render(this, context, store);
        foreach (var (a, c) in ret.Updates
                     .Where(x => x.Collection == LayoutAreaReference.Areas)
                     .Select(x => (x.Id!.ToString()!, Control: x.Value as UiControl))
                     .Where(x => x.Control != null))
            RegisterForDisposal(a, c!);

        return ret;
    }
    /// <summary>
    /// Reactive render of a top-level area whose generator is produced asynchronously
    /// (<c>Func&lt;…, Task&lt;IObservable&lt;T?&gt;&gt;&gt;</c>). The Task is bridged at the
    /// boundary via <see cref="Observable.FromAsync{TResult}(Func{CancellationToken, Task{TResult}})"/>
    /// (the only sanctioned Task→observable seam) and the resulting view stream is
    /// rendered through <see cref="RenderObservable"/>, so every emission flows through
    /// the layout-area init subscription's <c>SetCurrent</c> rather than a
    /// dropped-during-init <c>Stream.Update</c>.
    /// </summary>
    internal IObservable<EntityStoreAndUpdates> RenderArea<T>(
        RenderingContext context,
        Func<LayoutAreaHost, RenderingContext, CancellationToken, Task<IObservable<T?>>> asyncGenerator,
        EntityStore store)
        => RenderObservable(
            context,
            FromViewBuilder(ct => asyncGenerator.Invoke(this, context, ct))
                .SelectMany(observable => observable.Select(c => (object?)c)),
            store);

    /// <summary>
    /// Core reactive render for a TOP-LEVEL area generator (used by the renderers
    /// registered via <c>WithView</c>/<c>WithRenderer</c>). On each generator emission it
    /// re-renders the control tree onto the store and emits the accumulated
    /// <see cref="EntityStoreAndUpdates"/>; the init subscription sets each as Current
    /// (a Full) — never via <c>Stream.Update</c> on the init turn, so a synchronous
    /// <c>Observable.Return(control)</c> emission is not dropped. Child-area
    /// subscriptions are disposed and re-created on each emission, mirroring
    /// <see cref="UpdateArea"/>'s <c>DisposeChildAreas</c>.
    /// </summary>
    internal IObservable<EntityStoreAndUpdates> RenderObservable(
        RenderingContext context,
        IObservable<object?> generator,
        EntityStore store)
    {
        var cleared = DisposeExistingAreas(store, context);
        return generator
            .DistinctUntilChanged()
            .Scan(cleared, (acc, view) =>
            {
                // Dispose child-area subscriptions from the previous emission before
                // re-rendering (the feeding subscription itself is owned by the init
                // pipeline, not by this area key, so it is left intact).
                var disposed = DisposeChildAreas(acc.Store, context);
                var rendered = RenderArea(context, view, disposed.Store);
                return new EntityStoreAndUpdates(
                    rendered.Store,
                    disposed.Updates.Concat(rendered.Updates),
                    Stream.StreamId);
            })
            .Catch<EntityStoreAndUpdates, Exception>(ex =>
            {
                FailRendering(ex);
                return Observable.Empty<EntityStoreAndUpdates>();
            });
    }

    /// <summary>
    /// Reactive render of a top-level area whose generator is an observable of
    /// controls. Used by the layout renderers registered via <c>WithView</c>, so the
    /// synchronous first emission (the <c>Observable.Return(control)</c> common case)
    /// flows through the init subscription's <c>SetCurrent</c> instead of being dropped
    /// by the init window.
    /// </summary>
    internal IObservable<EntityStoreAndUpdates> RenderAreaObservable(
        RenderingContext context, IObservable<object?> generator, EntityStore store)
        => RenderObservable(context, generator, store);

    /// <summary>
    /// Reactive render of a top-level area whose generator is an observable of
    /// <see cref="ViewDefinition"/>s. Each ViewDefinition is invoked (bridged via
    /// <see cref="Observable.FromAsync{TResult}(Func{CancellationToken, Task{TResult}})"/>)
    /// and the resulting control rendered through <see cref="RenderObservable"/>.
    /// </summary>
    internal IObservable<EntityStoreAndUpdates> RenderAreaObservable(
        RenderingContext context, IObservable<ViewDefinition> generator, EntityStore store)
        => RenderObservable(
            context,
            generator.Select(vd => FromViewBuilder(ct => vd.Invoke(this, context, ct)))
                .Switch()
                .Select(c => (object?)c),
            store);

    /// <summary>
    /// Reactive render of a top-level area that is a single <see cref="ViewDefinition"/>
    /// (a <c>Func&lt;…, Task&lt;UiControl?&gt;&gt;</c>).
    /// </summary>
    internal IObservable<EntityStoreAndUpdates> RenderAreaObservable(
        RenderingContext context, ViewDefinition generator, EntityStore store)
        => RenderObservable(
            context,
            FromViewBuilder(ct => generator.Invoke(this, context, ct)).Select(c => (object?)c),
            store);

    /// <summary>
    /// Reactive render of a top-level area that is a plain control (already known
    /// synchronously). Emits once.
    /// </summary>
    internal IObservable<EntityStoreAndUpdates> RenderAreaObservable(
        RenderingContext context, object? view, EntityStore store)
        => Observable.Return(RenderArea(context, view, store));


    public void UpdateArea(RenderingContext context, object? view)
    {
        Stream.Update(store =>
        {
            var changes = DisposeChildAreas(store ?? new EntityStore(), context);
            var updates = RenderArea(context, view, changes.Store);
            return Stream.ApplyChanges(
                new(
                    updates.Store,
                changes.Updates.Concat(updates.Updates),
                Stream.StreamId
                    )
            );
        }, ex => logger.LogWarning(ex, "Cannot update {Area}", context.Area));
    }

    /// <summary>
    /// Clears all views under the given area immediately,
    /// pushing the removal to the client so stale content disappears
    /// before the new content is ready.
    /// </summary>
    public void ClearArea(RenderingContext context)
    {
        Stream.Update(store =>
        {
            var s = store ?? new EntityStore();
            var changes = RemoveViews(s, context.Area);
            return Stream.ApplyChanges(changes);
        }, ex => logger.LogWarning(ex, "Cannot clear {Area}", context.Area));
    }

    public void SubscribeToDataStream<T>(string id, IObservable<T> stream)
        => RegisterForDisposal(id, stream.Subscribe(x => Update(LayoutAreaReference.Data, coll => coll.SetItem(id, x!))));

    public void Update(string collection, Func<InstanceCollection, InstanceCollection> update)
    {
        Stream.Update(ws =>
            Stream.ApplyChanges((ws ?? new EntityStore()).MergeWithUpdates((ws ?? new EntityStore()).Update(collection, update), Stream.StreamId)),
            ex => logger.LogWarning(ex, "Cannot update {Collection}", collection));
    }

    public void UpdateData(string id, object? data)
    {
        if (data != null)
            Update(LayoutAreaReference.Data, store => store.SetItem(id, data));
    }

    // Per-session "already seeded" set for toggleable edit-state ids (EditorExtensions.
    // MapToToggleableControl). Instance field — NOT a static set — so it dies with this
    // layout-area host and never bleeds across sessions/tests. See NoStaticState.md.
    private readonly HashSet<string> initializedEditStates = new();

    /// <summary>
    /// Marks <paramref name="editStateId"/> as initialized for this layout-area session.
    /// Returns <c>true</c> the first time (caller should seed the initial value), <c>false</c>
    /// on every subsequent render (so a re-render doesn't reset the edit/read toggle).
    /// </summary>
    public bool TryMarkEditStateInitialized(string editStateId)
    {
        lock (initializedEditStates)
            return initializedEditStates.Add(editStateId);
    }


    private readonly ConcurrentDictionary<string, List<IDisposable>> disposablesByArea = new();


    public void RegisterForDisposal(string area, IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(area, _ => new()).Add(disposable);
    }

    /// <summary>
    /// Registers a disposable for the given key, disposing any previously registered disposable with the same key.
    /// Use this when re-creating subscriptions to prevent duplicate/endless emissions.
    /// </summary>
    public void ReplaceDisposable(string key, IDisposable disposable)
    {
        // Dispose existing disposables with this key
        if (disposablesByArea.TryRemove(key, out var existing))
        {
            foreach (var d in existing)
            {
                try { d.Dispose(); }
                catch { /* ignore disposal errors */ }
            }
        }
        // Add the new disposable
        disposablesByArea.GetOrAdd(key, _ => new()).Add(disposable);
    }

    public void RegisterForDisposal(IDisposable disposable)
    {
        disposablesByArea.GetOrAdd(string.Empty, _ => new()).Add(disposable);
    }

    public IObservable<T?> GetDataStream<T>(string id)
        where T : class
        => GetStream<T>(new EntityReference(LayoutAreaReference.Data, id));


    private IObservable<T?> GetStream<T>(EntityReference reference) where T : class
    {
        return Stream
            .Reduce(reference)!
            .Select(Convert<T>)
            .Where(x => x is not null)
            .DistinctUntilChanged();
    }

    private T? Convert<T>(ChangeItem<object> ci) where T : class
    {
        var result = ci.Value;
        if (result is null)
            return null;

        if (result is JsonElement je)
            return je.Deserialize<T?>(Hub.JsonSerializerOptions);
        if (result is JsonNode jn)
            return jn.Deserialize<T?>(Hub.JsonSerializerOptions);

        if (result is T t)
            return t;
        // Check if T is an array
        if (typeof(T).IsArray)
        {
            var elementType = typeof(T).GetElementType()!;
            if (result is Array array)
            {
                var ret = Array.CreateInstance(elementType, array.Length);
                for (var i = 0; i < ret.Length; i++)
                    ret.SetValue(array.GetValue(i), i);
                return (T)(object)ret;
            }
        }
        // Handle other IEnumerable<T> types
        else
        {
            var elementType = typeof(T).GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments().First();

            if (elementType != null)
            {
                if (result is IEnumerable enumerable)
                {
                    var genericListType = typeof(List<>).MakeGenericType(elementType);
                    var list = (IList)Activator.CreateInstance(genericListType)!;

                    foreach (var item in enumerable)
                        list.Add(item);
                    return (T)list;
                }
            }
        }

        throw new NotSupportedException(
            $"Cannot convert instance of type {result.GetType().Name} to Type {typeof(T).FullName}");
    }

    public void Dispose()
    {
        foreach (var disposable in disposablesByArea.ToArray())
            disposable.Value.ForEach(d => d.Dispose());
        disposablesByArea.Clear();
    }

    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback)
    {
        Stream.Hub.InvokeAsync(action, exceptionCallback);
    }

    public void InvokeAsync(Action action, Func<Exception, Task> exceptionCallback) =>
        InvokeAsync(_ =>
        {
            action();
            return Task.CompletedTask;
        }, exceptionCallback);

    private EntityStoreAndUpdates DisposeExistingAreas(EntityStore store, RenderingContext context)
    {
        var contextArea = context.Area;
        foreach (var area in disposablesByArea.Where(x => x.Key.StartsWith(contextArea)).ToArray())
            if (disposablesByArea.TryRemove(area.Key, out var disposables))
                disposables.ForEach(d => d.Dispose());

        return RemoveViews(store, contextArea);
    }

    /// <summary>
    /// Disposes subscriptions for child areas only (not the area itself),
    /// then removes all views under the area from the store.
    /// Used by UpdateArea to avoid disposing the feeding observable subscription.
    /// </summary>
    private EntityStoreAndUpdates DisposeChildAreas(EntityStore store, RenderingContext context)
    {
        var contextArea = context.Area;
        foreach (var area in disposablesByArea.Where(x => x.Key.StartsWith(contextArea) && x.Key != contextArea).ToArray())
            if (disposablesByArea.TryRemove(area.Key, out var disposables))
                disposables.ForEach(d => d.Dispose());

        return RemoveViews(store, contextArea);
    }

    private EntityStoreAndUpdates RemoveViews(EntityStore store, string contextArea)
    {
        var existing =
            store.Collections
                .GetValueOrDefault(LayoutAreaReference.Areas)
                ?.Instances
                .Where(x => ((string)x.Key).StartsWith(contextArea))
                .ToArray();

        if (existing == null)
            return new(store, [], Stream.StreamId);

        return new(store.Update(LayoutAreaReference.Areas,
            i => i with { Instances = i.Instances.RemoveRange(existing.Select(x => x.Key)) }), existing.Select(i =>
            new EntityUpdate(LayoutAreaReference.Areas, i.Key, null) { OldValue = i.Value }), Stream.StreamId);
    }


    internal EntityStoreAndUpdates RenderArea<T>(RenderingContext context, ViewStream<T> generator, EntityStore store) where T : UiControl?
    {
        // 🚨 Important: do NOT collapse the first emission into ret here.
        // ViewStream<T> bodies typically call `host.UpdateData(...)` to seed
        // the editor's DataContext BEFORE returning the control observable.
        // Both UpdateData and the control-emission go through Stream.Update —
        // which queues them. If we inline the control into ret, the client
        // receives Full(control) BEFORE the queued data Patch lands, and any
        // test/UI code that reads the control's DataContext and immediately
        // posts a JsonPatch (`UpdatePointer`) hits "target path not reachable"
        // because the data hasn't arrived yet. Symptom: EditorTest.TestEditorWithDelayed
        // hangs forever with Json.Patch InvalidOperationException retry-loops.
        //
        // Keep the original "Full(empty) → Patch(data) → Patch(control)" order:
        // returning ret without the view, then UpdateArea fires later when the
        // observable emits, lets the data Patch land before the control Patch.
        var ret = DisposeExistingAreas(store, context);
        RegisterForDisposal(context.Parent?.Area ?? context.Area,
            generator.Invoke(this, context, ret.Store)
                .Subscribe(c => UpdateArea(context, c), FailRendering)
        );
        return ret;
    }
    internal EntityStoreAndUpdates RenderArea<T>(RenderingContext context, AsyncViewStream<T> asyncGenerator, EntityStore store) where T : UiControl?
    {
        var ret = DisposeExistingAreas(store, context);

        // Reactive: bridge the Task-producing generator at the boundary via
        // Observable.FromAsync (no await in hub-reachable code), flatten to the inner
        // control stream, and feed UpdateArea on each emission.
        RegisterForDisposal(context.Parent?.Area ?? context.Area,
            FromViewBuilder(ct => asyncGenerator.Invoke(this, context, store, ct))
                .Switch()
                .Subscribe(c => UpdateArea(context, c), FailRendering));
        return ret;
    }

    private void FailRendering(Exception ex)
    {
        logger.LogWarning(ex, "Rendering failed");
    }

    /// <summary>
    /// Reactive bridge for a Task-returning view-builder (orchestration, NOT an I/O leaf) —
    /// the replacement for <c>Observable.FromAsync</c> here. View generators must NOT run on an
    /// <see cref="MeshWeaver.Mesh.Threading.IIoPool"/>: area renders nest (a parent render
    /// subscribes child-area renders), so a shared pool would self-deadlock on a nested slot
    /// acquire. This composes reactively instead — cold (the builder starts on Subscribe), with a
    /// cancellation token that trips when the subscription is disposed (RegisterForDisposal).
    /// </summary>
    internal static IObservable<T> FromViewBuilder<T>(Func<CancellationToken, Task<T>> builder) =>
        Observable.Create<T>(observer =>
        {
            var cts = new CancellationTokenSource();
            var inner = builder(cts.Token).ToObservable().Subscribe(observer);
            return new CompositeDisposable(inner, Disposable.Create(() =>
            {
                try { cts.Cancel(); } finally { cts.Dispose(); }
            }));
        });

    public void UpdateProgress(string area, ProgressControl progress)
        => Stream.Update(state => Stream.ApplyChanges(
            new(state ?? new EntityStore(), [new(LayoutAreaReference.Areas, area, progress)], Stream.StreamId)),
            ex => logger.LogWarning(ex, "Cannot update progress for {Area}", area));

    internal EntityStoreAndUpdates RenderArea(RenderingContext context, ViewDefinition generator, EntityStore store)
    {
        var ret = DisposeExistingAreas(store, context);
        // Reactive: bridge the Task-producing ViewDefinition via Observable.FromAsync
        // (no await in hub-reachable code) and feed UpdateArea once it resolves.
        RegisterForDisposal(context.Parent?.Area ?? context.Area,
            FromViewBuilder(ct => generator.Invoke(this, context, ct))
                .Subscribe(view => UpdateArea(context, view!), FailRendering));
        return ret;
    }
    internal EntityStoreAndUpdates RenderArea(
        RenderingContext context,
        IObservable<ViewDefinition> generator,
        EntityStore store)
    {
        // Reactive: for each emitted ViewDefinition, bridge its Task via
        // Observable.FromAsync (no await in hub-reachable code) and feed UpdateArea.
        // Switch() keeps only the latest definition's resolution in flight.
        RegisterForDisposal(context.Area,
            generator
                .Select(vd => FromViewBuilder(ct => vd.Invoke(this, context, ct)))
                .Switch()
                .Subscribe(view => UpdateArea(context, view!), FailRendering));

        return DisposeExistingAreas(store, context);
    }


    /// <summary>
    /// Nested-area render of an observable control generator (container/dialog
    /// sub-areas). The synchronous part — clearing the area — is returned to the
    /// caller's <c>Render</c>; the live emissions feed <see cref="UpdateArea"/> on a
    /// subscription tied to the area's lifecycle. This path runs POST-init (the
    /// container control itself reached the client through the init subscription), so
    /// subscribing inline is safe — there is no init window to drop into. The TOP-LEVEL
    /// render path uses
    /// <see cref="RenderAreaObservable(RenderingContext, IObservable{object}, EntityStore)"/>
    /// instead, which routes emissions through the init subscription's SetCurrent.
    /// </summary>
    internal EntityStoreAndUpdates RenderArea(RenderingContext context, IObservable<object?> generator, EntityStore store)
    {
        // 🚨 Important: do NOT collapse the first emission into ret here — see
        // notes on the ViewStream<T> overload above. View functions seed
        // DataContext via `host.UpdateData(...)` which is queued through
        // Stream.Update; inlining the control into ret would let the client
        // see the control before the data Patch arrives, breaking any
        // UpdatePointer call that follows. The original queued-Patch flow
        // preserves data-before-control ordering.
        var ret = DisposeExistingAreas(store, context);
        RegisterForDisposal(
            context.Area,
            generator
                .DistinctUntilChanged()
                .Subscribe(
                    view => UpdateArea(context, view),
                    FailRendering)
        );
        return ret;
    }

    internal ISynchronizationStream<EntityStore> GetStream()
    {
        return Stream;
    }

    /// <summary>
    /// Navigates to the specified URI by posting a NavigationRequest to the subscriber (portal).
    /// Safe to call from LayoutAreaHost context (click handlers, etc.)
    /// </summary>
    /// <param name="uri">The URI to navigate to.</param>
    /// <param name="forceLoad">Whether to force a full page reload.</param>
    /// <param name="replace">Whether to replace the current history entry instead of adding a new one.</param>
    public void NavigateTo(string uri, bool forceLoad = false, bool replace = false)
    {
        var subscriber = Stream.Get<Address>(nameof(SubscribeRequest.Subscriber));
        if (subscriber != null)
        {
            if (subscriber.Host is { })
                subscriber = subscriber.Host;
            Stream.Hub.Post(new NavigationRequest(uri) { ForceLoad = forceLoad, Replace = replace }, o => o.WithTarget(subscriber));
        }
        else
        {
            logger.LogWarning("Cannot navigate: no subscriber address found for stream {StreamId}", Stream.StreamId);
        }
    }

    internal IEnumerable<LayoutAreaDefinition> GetLayoutAreaDefinitions()
        => LayoutDefinition.AreaDefinitions.Values.Where(l => l.IsVisible());

    /// <summary>
    /// Resolves the default area name from LayoutDefinition.DefaultArea,
    /// or falls back to the first visible area definition.
    /// </summary>
    private string ResolveDefaultArea()
    {
        // Try to use the configured default area
        if (!string.IsNullOrEmpty(LayoutDefinition.DefaultArea))
            return LayoutDefinition.DefaultArea;

        // Fall back to the first visible area definition
        var firstArea = LayoutDefinition.AreaDefinitions.Values
            .Where(l => l.IsVisible())
            .OrderBy(l => l.Order ?? 0)
            .FirstOrDefault();

        return firstArea?.Area ?? string.Empty;
    }
}

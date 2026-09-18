using System;
namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// Well-known I/O pool names, one per resource class. Each name maps to an
/// independently-bounded <see cref="IIoPool"/> resolved from <see cref="IoPoolRegistry"/>.
/// Mirrors how Postgres uses a different <c>MaxPoolSize</c> per connection role.
/// </summary>
public static class IoPoolNames
{
    /// <summary>Local file-system I/O (read/write/list/delete on disk).</summary>
    public const string FileSystem = "FileSystem";

    /// <summary>Cloud blob/object storage (Azure Blob, etc.).</summary>
    public const string Blob = "Blob";

    /// <summary>Outbound HTTP/network leaves (social, AI providers, embeddings, MCP). Wave 2.</summary>
    public const string Http = "Http";

    /// <summary>
    /// AI agent execution rounds — the long-lived LLM streaming round (model turns + inline
    /// tool calls + delegation), driven from <c>ThreadExecution.ExecuteMessageAsync</c>. Distinct
    /// from <see cref="Http"/> so a burst of multi-minute rounds can't starve quick outbound HTTP
    /// (social, MCP, embeddings). See <see cref="IoPoolOptions.Ai"/> for why its cap is a
    /// runaway-fan-out stop, not a fine-grained throttle.
    /// </summary>
    public const string Ai = "Ai";

    /// <summary>
    /// MeshQuery change-feed subscribe leaves (<see cref="IIoPool.SubscribeThroughPool{T}"/>). Exists
    /// so the query SUBSCRIBE — which opens providers + emits the initial snapshot and can route →
    /// create a per-node hub — is TRACKED and DRAINABLE at teardown (the endemic teardown SIGSEGV was a
    /// query straggler creating a hub on the disposing Autofac scope). Generous cap: it's a drain hook,
    /// not a throttle (the slot is held only for the bounded subscribe window).
    /// </summary>
    public const string Query = "Query";

    /// <summary>
    /// Layout-area render subscribes (<see cref="IIoPool.SubscribeThroughPool{T}"/>, via
    /// <c>IPooledSubscribeScheduler</c>). Exists so the render SUBSCRIBE — which hops off the owning
    /// hub's action block to stay query-in-render-safe, and runs the synchronous render body
    /// (including menu renderers that resolve services and touch collectible NodeType ALC types) — is
    /// TRACKED and DRAINABLE at teardown. Without it the render straggler executes on a bare
    /// ThreadPool thread after the Autofac scope is disposed and the NodeType ALC unloaded → the
    /// endemic teardown SIGSEGV (FutuRe.Test exit=139). Generous cap: it's a drain hook, not a
    /// throttle (the slot is held only for the bounded subscribe window).
    /// </summary>
    public const string Layout = "Layout";

    /// <summary>
    /// Mesh-node reads/writes issued by the Microsoft Agent Framework agent stores
    /// (<c>MeshNodeAgentFileStore</c>, <c>MeshAgentSkillsSource</c>) — the leaves that bridge
    /// MAF's <c>Task</c>-shaped abstract surface onto our reactive mesh APIs.
    ///
    /// <para>🚨 Deliberately NOT <see cref="Ai"/>. A store call happens INSIDE a tool call, which
    /// runs inside an agent round that is already holding an <see cref="Ai"/> slot. Re-entering
    /// the same bounded pool from within a slot it already holds is the classic nested-gate
    /// deadlock: at the cap, every holder waits for a slot only a holder can release. A separate
    /// pool makes the nesting acyclic.</para>
    /// </summary>
    public const string AgentStore = "AgentStore";

    /// <summary>
    /// Silo-side message routing (<c>RoutingGrain.RouteMessage</c>), via
    /// <see cref="IIoPool.SubscribeThroughPool{T}"/>. Exists so the routing SUBSCRIBE — path
    /// resolution, the memory-stream post, the per-node grain hand-off and the DeliveryFailure
    /// NACK — runs OFF the routing grain's activation thread.
    ///
    /// <para>🚨 This is the fix for issue #1028. <c>RoutingGrain</c> is <c>[StatelessWorker(1)]</c>
    /// and NON-reentrant, so the silo has exactly ONE routing turn: any work the turn performs
    /// inline is work the whole silo's routing waits on, and Orleans' request timeout does NOT
    /// apply inside a turn. Prod (2026-08-07) had a single <c>RouteMessage</c> turn
    /// executing for <c>06:00:22</c> with <c>NonReentrancyQueueSize=541</c> — every other message
    /// the silo needed to route was stuck behind it for 37 h. Routing work therefore never runs on
    /// the turn; it runs here, where one stuck delivery costs one pool slot and nothing else.</para>
    ///
    /// <para>Generous cap: a drain hook + isolation boundary, not a throttle — the slot is held
    /// only for the bounded subscribe window (see <see cref="IIoPool.SubscribeThroughPool{T}"/>),
    /// and routing is the hottest path in the mesh.</para>
    /// </summary>
    public const string Routing = "Routing";

    /// <summary>CPU-bound compilation (Roslyn compile/script). Wave 3.</summary>
    public const string Compile = "Compile";

    /// <summary>External process execution (<c>Process.Start</c>). Wave 3.</summary>
    public const string Process = "Process";

    /// <summary>
    /// The cases of a NodeType's <c>Tests</c> area (<c>MeshWeaver.Testing.InMesh.MeshTestRunner</c>).
    /// A case runs as ONE leaf on this pool so that the runner's bound cancels it through the pool's
    /// linked token rather than abandoning it. The pool does NOT order the cases (the runner does);
    /// its cap (<see cref="IoPoolOptions.Tests"/>) bounds how many cases that ignored their
    /// cancellation may still be running.
    /// </summary>
    public const string Tests = "Tests";

    /// <summary>
    /// Prefix for per-Postgres-storage-<b>provider</b> pools (<c>pg:{providerName}</c>). Capped at
    /// ONE in-flight WRITE. See <see cref="IoPoolOptions.MaxConcurrencyFor"/>.
    ///
    /// <para>🚨 <b>The cap is half a CONNECTION BUDGET, not a mirror of one connection</b>
    /// (measured 2026-09-15 while re-checking issue #1198's third item). This comment used to say
    /// "the gate IS the connection… the single Npgsql connection that adapter holds
    /// (<c>MaxPoolSize=1</c>)", and that is not what the partitioned Postgres backend wires: every
    /// per-schema adapter shares ONE <c>NpgsqlDataSource</c> (<c>MaxPoolSize=50</c> in the portal)
    /// and the SAME <c>pg:Postgres</c> pool, because minting a data source per <c>(schema, table)</c>
    /// leaked a pool per hub and exhausted the server — that design was deliberately REMOVED. The
    /// one place the old sentence is literally true is
    /// <c>PostgreSqlChunkedContentVectorStore</c>, which does hold a dedicated
    /// <c>MaxPoolSize=1</c> source beside its <c>pg:vector</c> pool.</para>
    ///
    /// <para>So the pairing to read is <c>PostgresRead</c> (16) + this (1) = 17 concurrent
    /// connections, comfortably under the shared source's 50 — a budget, with headroom that has
    /// never been spent against a measurement. <c>Doc/Architecture/ControlledIoPooling</c> states
    /// this correctly and is the reference; the sentence here asserted the opposite and made a
    /// live question look settled, which is why #1198's third item sat unmeasured for a month.
    /// 🚨 It follows that the cap is a PROCESS-WIDE write serializer: a recursive delete's next
    /// leaf removal queues behind unrelated writes from every other partition.</para>
    ///
    /// <para>🚨 <b>That distribution has now been READ, and it does not indict this cap.</b>
    /// Measured 2026-09-16 on memex.systemorph.com over 828 minutes of uptime: <c>pg:Postgres</c>
    /// granted 2,786 admissions, mean 6.5 ms, <b>max 205 ms</b>, and <b>zero</b> in either tail
    /// bucket — against a 30 s operation budget. Meanwhile <c>pg-read:Postgres</c> (cap 16) granted
    /// <b>31,897,169</b> at a MEAN of 342 ms with 48,122 over a second. The write gate is not the
    /// contended one; the read gate is, by four orders of magnitude in traffic. Denominator: ONE
    /// portal, ONE pod, ONE process lifetime — and NOT memex-cloud, which produced every logged
    /// occurrence of MeshWeaver#1198 and runs an image predating the instrument.</para>
    ///
    /// <para>So the number still is not a knob to turn on a hunch — now for the opposite reason.
    /// There is nothing on this pool to relieve, and a high mean on the READ pool is not by itself
    /// a cap that is too small (<c>InvokeStream</c> holds one slot for a whole enumeration, so
    /// long-held slots and too-few slots produce the same mean and are different problems).
    /// <c>Doc/Architecture/RecursiveDeleteDrain</c> carries the reading and what it does not
    /// settle.</para>
    /// </summary>
    public const string PostgresAdapterPrefix = "pg:";

    /// <summary>
    /// Prefix for the per-Postgres-storage-adapter <b>read</b> pool (<c>pg-read:{adapterName}</c>).
    /// Distinct from <see cref="PostgresAdapterPrefix"/> (the cap-1 write/provisioning pool): reads
    /// run against the shared base connection pool and are capped BELOW its <c>MaxPoolSize</c> so a
    /// synced-query read fan-out storm cannot drain the pool and starve writes (onboarding/chat stay
    /// ungated and always have headroom). This pool IS the former hand-woven <c>ReadConcurrencyGate</c>
    /// — its <see cref="SemaphoreSlim"/> folded into the one sanctioned <see cref="IIoPool"/> primitive.
    /// Cap from <see cref="IoPoolOptions.PostgresRead"/>. See <see cref="IoPoolOptions.MaxConcurrencyFor"/>.
    /// </summary>
    public const string PostgresReadAdapterPrefix = "pg-read:";

    /// <summary>
    /// Prefix for per-Snowflake-storage-adapter pools (<c>sf:{adapterName}</c>). Capped at ONE
    /// in-flight op so writes/provisioning serialize through a single logical connection —
    /// the same gate-IS-the-connection contract as <see cref="PostgresAdapterPrefix"/>.
    /// </summary>
    public const string SnowflakeAdapterPrefix = "sf:";

    /// <summary>
    /// Prefix for the per-Snowflake-storage-adapter <b>read</b> pool (<c>sf-read:{adapterName}</c>).
    /// Bounds read fan-out below Snowflake's session pool the same way
    /// <see cref="PostgresReadAdapterPrefix"/> does for Npgsql.
    /// Cap from <see cref="IoPoolOptions.SnowflakeRead"/>.
    /// </summary>
    public const string SnowflakeReadAdapterPrefix = "sf-read:";
}

/// <summary>
/// Per-resource-class concurrency caps for the controlled I/O pools. Sensible
/// defaults that are options-ready: a host can override any value via
/// <c>AddIoPools(o =&gt; o with { ... })</c> (or future appsettings binding)
/// without any API change at the call sites.
///
/// <para>The caps govern a bounded slice of the shared ThreadPool — they do not
/// allocate dedicated threads. See <see cref="IoPool"/> for the two governor
/// mechanisms (async semaphore gate vs. limited-concurrency scheduler).</para>
/// </summary>
public sealed record IoPoolOptions
{
    /// <summary>
    /// How long <see cref="IoPool.Drain"/> waits at EACH of its joins before giving up and
    /// reporting a residual.
    ///
    /// <para>🚨 Leave this at the default in production — 30 s is the teardown contract. It is
    /// settable so a TEST that must let the budget EXPIRE (the only way to observe a residual)
    /// can do so in milliseconds. <c>Drain</c> spends the budget three times over, so at the
    /// production value such a test costs 30-90 s of shard time and cannot pass at all under
    /// <c>test/xunit.runner.json</c>'s <c>methodTimeout: 30000</c>.</para>
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = IoPool.DefaultDrainTimeout;

    /// <summary>
    /// How long <see cref="IoPool.Drain"/> waits for the NEXT in-flight leaf to finish on its own
    /// before it cancels anything — the grace that lets accepted work finish its job at teardown.
    /// Every completion restarts it; a leaf that outlives one grace with nothing else finishing is
    /// wedged and is cancelled. See <see cref="IoPool.DefaultDrainGrace"/>.
    ///
    /// <para>🚨 Leave this at the default in production. It is settable so a TEST whose leaf can
    /// only end by cancellation need not spend the grace on every drain.</para>
    /// </summary>
    public TimeSpan DrainGrace { get; init; } = IoPool.DefaultDrainGrace;

    /// <summary>
    /// Concurrent file-system ops. These are async leaves (the thread is released
    /// during the await), so a generous cap avoids bottlenecking the many concurrent
    /// data-path reads/writes a busy mesh issues, while still preventing pathological
    /// unbounded fan-out. (Sync directory-walk leaves are NOT pooled — they run inline.)
    /// </summary>
    public int FileSystem { get; init; } = 256;

    /// <summary>Concurrent blob/cloud-storage ops (async, thread released during await).</summary>
    public int Blob { get; init; } = 128;

    /// <summary>Concurrent outbound HTTP ops. Defaults to 16.</summary>
    public int Http { get; init; } = 16;

    /// <summary>
    /// Concurrent AI agent rounds. A round holds its slot for the WHOLE round (model turns +
    /// inline tool calls + delegation waits), not a single network call — and because a
    /// delegating round holds its slot while awaiting a sub-thread round (which needs its own
    /// slot), the cap must comfortably exceed realistic delegation concurrency. It is therefore a
    /// runaway-fan-out STOP, not a fine-grained throttle; defaults generous (256). The thread is
    /// released during the model await, so an idle-but-streaming round costs ~0 threads.
    /// </summary>
    public int Ai { get; init; } = 256;

    /// <summary>
    /// Concurrent MeshQuery change-feed subscribes (the <c>Query</c> pool). A drain hook, not a
    /// throttle — the slot is held only for the bounded subscribe window — so the cap is generous
    /// (256) to never bottleneck query fan-out.
    /// </summary>
    public int Query { get; init; } = 256;

    /// <summary>
    /// Concurrent layout-area render subscribes (the <c>Layout</c> pool). A drain hook, not a
    /// throttle — the slot is held only for the bounded subscribe window — so the cap is generous
    /// (256) to never bottleneck concurrent area renders (a page renders many nested areas at once).
    /// </summary>
    public int Layout { get; init; } = 256;

    /// <summary>
    /// Concurrent MAF agent-store mesh ops (the <c>AgentStore</c> pool). Most ops are a single
    /// bounded mesh read/write bridged from MAF's <c>Task</c> surface, so the slot is held briefly;
    /// the live listing/search subscribes hold one only for the bounded subscribe window. The cap is
    /// a runaway-fan-out stop — a store is constructed per agent round, and a burst of rounds each
    /// listing or searching is what it bounds — not a throttle. Generous by default (128) and
    /// independent of <see cref="Ai"/> so a store call nested inside an agent round can never
    /// self-deadlock.
    /// </summary>
    public int AgentStore { get; init; } = 128;

    /// <summary>
    /// Concurrent silo-side routing subscribes (the <c>Routing</c> pool). A drain hook and an
    /// isolation boundary, not a throttle — the slot is held only for the bounded subscribe
    /// window — so the cap is generous (256): routing is the hottest path in the mesh and must
    /// never queue behind itself. See <see cref="IoPoolNames.Routing"/> for why routing work is
    /// not allowed on the routing grain's turn at all (issue #1028).
    /// </summary>
    public int Routing { get; init; } = 256;

    /// <summary>Concurrent compilations. CPU-bound; defaults to the processor count.</summary>
    public int Compile { get; init; } = Environment.ProcessorCount;

    /// <summary>Concurrent external processes. Heavy; defaults to 4.</summary>
    public int Process { get; init; } = 4;

    /// <summary>
    /// Slots of the <see cref="IoPoolNames.Tests"/> pool. NOT the serializer — the in-mesh runner
    /// already runs cases one after another — so this is the number of cases that may IGNORE their
    /// cancellation and keep running before the pool is full. A cap of one made a single such case
    /// block every later case of every suite on the mesh (MeshWeaver#4719 review); the runner names
    /// a pool its own leaked cases have filled rather than timing out behind it.
    /// </summary>
    public int Tests { get; init; } = 32;

    /// <summary>
    /// Concurrent READS per Postgres storage adapter (the <c>pg-read:{adapter}</c> pool). Kept
    /// comfortably below the shared base connection pool's <c>MaxPoolSize</c> so a synced-query
    /// read fan-out storm cannot drain the pool and starve writes (prod 2026-06-04: "connection
    /// pool has been exhausted, currently 50"). This is the cap the former <c>ReadConcurrencyGate</c>
    /// enforced; folded into <see cref="IIoPool"/>. Reads are async leaves (the thread is released
    /// during the await), so the cap bounds in-flight connections, not threads.
    /// </summary>
    public int PostgresRead { get; init; } = 16;

    /// <summary>
    /// Concurrent READS per Snowflake storage adapter (the <c>sf-read:{adapter}</c> pool).
    /// Same rationale as <see cref="PostgresRead"/>: bound the synced-query read fan-out
    /// below the driver's session pool so reads can't starve writes.
    /// </summary>
    public int SnowflakeRead { get; init; } = 16;

    /// <summary>Fallback cap for any pool name not listed above.</summary>
    public int Default { get; init; } = Environment.ProcessorCount;

    /// <summary>Resolves the configured cap for a pool name.</summary>
    public int MaxConcurrencyFor(string name) =>
        // Per-PG-adapter READ pools (pg-read:{adapter}) bound the read fan-out below the
        // shared connection pool so reads can't starve writes — checked BEFORE the cap-1
        // write prefix because "pg-read:" also starts with "pg".
        name.StartsWith(IoPoolNames.PostgresReadAdapterPrefix, StringComparison.Ordinal) ? PostgresRead :
        // Per-PG-provider WRITE pools (pg:{provider}) take one slot: half the connection BUDGET
        // (16 reads + 1 write, under the shared data source's MaxPoolSize=50) — NOT a mirror of a
        // dedicated single connection, which the partitioned backend has not had since the
        // per-(schema, table) data sources were removed as a leak. See IoPoolNames for the
        // measurement and Doc/Architecture/ControlledIoPooling for the budget.
        name.StartsWith(IoPoolNames.PostgresAdapterPrefix, StringComparison.Ordinal) ? 1 :
        // Same prefix-shadowing order for Snowflake: "sf-read:" also starts with "sf".
        name.StartsWith(IoPoolNames.SnowflakeReadAdapterPrefix, StringComparison.Ordinal) ? SnowflakeRead :
        // Per-Snowflake-adapter WRITE pools (sf:{adapter}): the gate IS the single
        // logical write connection, mirroring pg:{adapter}.
        name.StartsWith(IoPoolNames.SnowflakeAdapterPrefix, StringComparison.Ordinal) ? 1 :
        name switch
        {
            IoPoolNames.FileSystem => FileSystem,
            IoPoolNames.Blob => Blob,
            IoPoolNames.Http => Http,
            IoPoolNames.Ai => Ai,
            IoPoolNames.Query => Query,
            IoPoolNames.Layout => Layout,
            IoPoolNames.Tests => Tests,
            IoPoolNames.AgentStore => AgentStore,
            IoPoolNames.Routing => Routing,
            IoPoolNames.Compile => Compile,
            IoPoolNames.Process => Process,
            _ => Default,
        };
}

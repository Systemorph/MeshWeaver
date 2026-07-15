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

    /// <summary>CPU-bound compilation (Roslyn compile/script). Wave 3.</summary>
    public const string Compile = "Compile";

    /// <summary>External process execution (<c>Process.Start</c>). Wave 3.</summary>
    public const string Process = "Process";

    /// <summary>
    /// Prefix for per-Postgres-storage-adapter pools (<c>pg:{adapterName}</c>). Each such
    /// pool is capped at ONE in-flight op so the <see cref="IIoPool"/> gate mirrors the
    /// single Npgsql connection that adapter holds (<c>MaxPoolSize=1</c>) — the gate IS the
    /// connection. See <see cref="IoPoolOptions.MaxConcurrencyFor"/>.
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

    /// <summary>Concurrent compilations. CPU-bound; defaults to the processor count.</summary>
    public int Compile { get; init; } = Environment.ProcessorCount;

    /// <summary>Concurrent external processes. Heavy; defaults to 4.</summary>
    public int Process { get; init; } = 4;

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
        // Per-PG-adapter WRITE pools (pg:{adapter}) hold exactly one connection — the gate
        // IS the single Npgsql connection, never a parallel bound on top of it.
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
            IoPoolNames.Compile => Compile,
            IoPoolNames.Process => Process,
            _ => Default,
        };
}

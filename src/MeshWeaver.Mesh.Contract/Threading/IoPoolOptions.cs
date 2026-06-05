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

    /// <summary>Concurrent compilations. CPU-bound; defaults to the processor count.</summary>
    public int Compile { get; init; } = Environment.ProcessorCount;

    /// <summary>Concurrent external processes. Heavy; defaults to 4.</summary>
    public int Process { get; init; } = 4;

    /// <summary>Fallback cap for any pool name not listed above.</summary>
    public int Default { get; init; } = Environment.ProcessorCount;

    /// <summary>Resolves the configured cap for a pool name.</summary>
    public int MaxConcurrencyFor(string name) =>
        // Per-PG-adapter pools (pg:{adapter}) hold exactly one connection — the gate
        // IS the single Npgsql connection, never a parallel bound on top of it.
        name.StartsWith(IoPoolNames.PostgresAdapterPrefix, StringComparison.Ordinal) ? 1 :
        name switch
        {
            IoPoolNames.FileSystem => FileSystem,
            IoPoolNames.Blob => Blob,
            IoPoolNames.Http => Http,
            IoPoolNames.Compile => Compile,
            IoPoolNames.Process => Process,
            _ => Default,
        };
}

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// The modes of <see cref="InMeshImpersonationGuard"/> — an open vocabulary of string constants
/// (policy <c>open-vocabulary-string-constants</c>). An unrecognised value is never read as one of
/// these: it is logged and treated as <see cref="LogOnly"/>, the mode that changes nothing.
/// </summary>
public static class InMeshImpersonationMode
{
    /// <summary>Every in-mesh impersonation is ALLOWED and logged with its caller. The default.</summary>
    public const string LogOnly = "LogOnly";

    /// <summary>An in-mesh impersonation from code not on the trusted list is REFUSED (it throws).</summary>
    public const string Enforce = "Enforce";

    /// <summary>No check and no log — the behaviour before the guard existed.</summary>
    public const string Off = "Off";
}

/// <summary>
/// Marks an <see cref="AssemblyLoadContext"/> whose assemblies are PLATFORM code even though they
/// do not live in the default context — e.g. the Orleans grain modules context. Every other
/// non-default context is treated as in-mesh code (see <see cref="InMeshImpersonationGuard"/>).
/// </summary>
public interface IPlatformLoadContext;

/// <summary>
/// Marks a platform method OUTSIDE this assembly that is itself an impersonation surface — it
/// installs a platform principal on behalf of ITS caller (e.g. a builder's <c>ImpersonateAsSystem()</c>).
/// Such a method passes its own <see cref="Assembly.GetCallingAssembly"/> to
/// <see cref="InMeshImpersonationGuard.Check"/>, and the stack walk steps over it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ImpersonationSurfaceAttribute : Attribute;

/// <summary>Who asked for an impersonation, as the guard classified it.</summary>
/// <param name="InMesh">True when the caller is code the portal compiled at RUNTIME (a NodeType, a
/// C# Code node / script, a configuration script, or anything else loaded outside the default
/// context).</param>
/// <param name="CodeOwner">The load context's name — <c>DynamicNode_{sanitized node path}</c> for a
/// NodeType, <c>kernel-script-session</c> for a script, <c>node-config-script:{path}</c> for a
/// configuration script. Null for platform code.</param>
/// <param name="Method">The calling method, <c>Type.Method</c>, when known.</param>
/// <param name="Assembly">The calling assembly's simple name.</param>
public sealed record ImpersonationCaller(bool InMesh, string? CodeOwner, string? Method, string Assembly);

/// <summary>
/// Thrown, under <see cref="InMeshImpersonationMode.Enforce"/>, when code the mesh compiled at
/// runtime asks to act as a platform principal (System or a hub).
/// </summary>
public sealed class InMeshImpersonationRefusedException(string surface, ImpersonationCaller caller)
    : UnauthorizedAccessException(
        $"Code compiled in the mesh ('{caller.CodeOwner}', {caller.Method ?? caller.Assembly}) may not act as the platform: "
        + $"{surface} was refused. A platform write from a NodeType goes through a governed activity or a platform "
        + "service (Doc/Architecture/InMeshImpersonation).")
{
    /// <summary>The localisation catalog key of the refusal (en + de); its arguments are
    /// <c>owner</c> (<see cref="ImpersonationCaller.CodeOwner"/>), <c>method</c> and <c>surface</c>.</summary>
    public const string Key = InMeshImpersonationGuard.RefusalKey;

/// <summary>The impersonation surface that was called.</summary>
    public string Surface { get; } = surface;

    /// <summary>The caller that was refused.</summary>
    public ImpersonationCaller Caller { get; } = caller;
}

/// <summary>
/// 🚨 Decides whether the CALLER of an impersonation surface (<see cref="AccessService.ImpersonateAsSystem"/>,
/// <see cref="AccessService.ImpersonateAsHub"/>, a switch to a platform principal, a post that stamps
/// one) is platform code or code the mesh compiled at runtime — and, for the latter, logs it (and
/// under <see cref="InMeshImpersonationMode.Enforce"/> refuses it).
///
/// <para><b>Why.</b> NodeType sources, C# Code nodes and configuration scripts are user-authored
/// and compiled in the portal. Without this they can open a System scope and do everything System
/// can — the broad-grant guard now refuses System's broad grants, but not System's other powers.
/// See Doc/Architecture/InMeshImpersonation for the options and the measured blast radius.</para>
///
/// <para><b>How a caller is classified.</b> By the <see cref="AssemblyLoadContext"/> of the calling
/// method's assembly: the default context (and an <see cref="IPlatformLoadContext"/>) is platform;
/// anything else — <c>DynamicNode_*</c>, the script session, a configuration script, a context the
/// code made itself — is in-mesh. The fast path reads only the immediate caller
/// (<see cref="Assembly.GetCallingAssembly"/>, one frame); only when that caller is the BCL
/// (reflection, an Rx operator invoking a method group) or is in-mesh does it walk the stack, past
/// the surface and the BCL, to the first frame that decides. The JIT never inlines a collectible
/// method into a non-collectible caller, so an in-mesh frame cannot disappear into a platform one.</para>
///
/// <para><b>What it cannot see</b> (named in the doc, closed only by a compile-time check or a
/// sealed principal): a delegate to a surface handed to a platform subscriber with no in-mesh frame
/// left on the stack, and an <see cref="AccessContext"/> naming System constructed by hand and
/// passed to an API that does not go through a surface.</para>
///
/// <para>Instance state on the mesh-scoped <see cref="AccessService"/> — never static.</para>
/// </summary>
public sealed class InMeshImpersonationGuard
{
    /// <summary>The setting that selects the mode (<see cref="InMeshImpersonationMode"/>).</summary>
    public const string ModeKey = "Access:InMeshImpersonation:Mode";

    /// <summary>
    /// The setting listing the in-mesh code that MAY impersonate under Enforce — node-path prefixes
    /// as an indexed array (<c>Access:InMeshImpersonation:TrustedCode:0 = Governance</c>). A prefix
    /// matches the NodeType at that path and every NodeType below it. The trust is only as strong as
    /// the write protection of that namespace.
    /// </summary>
    public const string TrustedCodeKey = "Access:InMeshImpersonation:TrustedCode";

    /// <summary>The prefix of every line this guard logs — one grep is the inventory.</summary>
    public const string LogPrefix = "[InMeshImpersonation]";

    /// <summary>The catalog key of the refusal.</summary>
    public const string RefusalKey = "access.impersonation.inMeshRefused";

    /// <summary>The prefix the compiler gives a NodeType assembly's load context.</summary>
    public const string NodeTypeLoadContextPrefix = "DynamicNode_";

    private readonly ILogger? logger;
    private readonly ConcurrentDictionary<string, long> occurrences = new();
    private readonly Assembly surfaceAssembly = typeof(InMeshImpersonationGuard).Assembly;

    /// <summary>Creates the guard in <paramref name="mode"/> with the given trusted prefixes.</summary>
    public InMeshImpersonationGuard(string mode, IEnumerable<string>? trustedCode, ILogger? logger)
    {
        this.logger = logger;
        Mode = Normalise(mode, logger);
        TrustedCode = (trustedCode ?? [])
            .Select(p => p?.Trim().Trim('/') ?? "")
            .Where(p => p.Length > 0)
            .ToImmutableArray();
    }

    /// <summary>Reads <see cref="ModeKey"/> and <see cref="TrustedCodeKey"/>; absent → LogOnly, nothing trusted.</summary>
    public static InMeshImpersonationGuard FromConfiguration(IConfiguration? configuration, ILogger? logger) =>
        new(configuration?[ModeKey] ?? InMeshImpersonationMode.LogOnly,
            configuration?.GetSection(TrustedCodeKey).GetChildren().Select(c => c.Value ?? ""),
            logger);

    /// <summary>The mode in effect (one of <see cref="InMeshImpersonationMode"/>).</summary>
    public string Mode { get; }

    /// <summary>The node-path prefixes whose in-mesh code may impersonate under Enforce.</summary>
    public ImmutableArray<string> TrustedCode { get; }

    private static string Normalise(string? mode, ILogger? logger)
    {
        foreach (var known in new[] { InMeshImpersonationMode.LogOnly, InMeshImpersonationMode.Enforce, InMeshImpersonationMode.Off })
            if (string.Equals(mode?.Trim(), known, StringComparison.OrdinalIgnoreCase))
                return known;
        logger?.LogWarning("{Prefix} unknown mode '{Mode}' at {Key} — running LogOnly", LogPrefix, mode, ModeKey);
        return InMeshImpersonationMode.LogOnly;
    }

    /// <summary>
    /// Checks the caller of an impersonation surface. A no-op unless <paramref name="principal"/> is
    /// a platform principal (System or hub-shaped) — switching to an ordinary user's identity is not
    /// impersonating the platform. Logs every in-mesh caller; throws
    /// <see cref="InMeshImpersonationRefusedException"/> under Enforce when it is not trusted.
    /// </summary>
    /// <param name="immediateCaller">The assembly that called the surface — the surface reads it with
    /// <see cref="Assembly.GetCallingAssembly"/> from a <c>NoInlining</c> method. Passing a different
    /// assembly can only skip a check the caller would not otherwise make; it grants nothing.</param>
    /// <param name="surface">The surface's name, for the log.</param>
    /// <param name="principal">The identity being installed.</param>
    public void Check(Assembly immediateCaller, string surface, AccessContext? principal)
    {
        if (Mode == InMeshImpersonationMode.Off || !IsPlatformPrincipal(principal))
            return;

        // Fast path: the caller is platform code that is not the BCL, or is this assembly (whose
        // public surfaces each check their OWN caller on entry, so a call from here is a surface
        // forwarding a call it has already checked).
        if (ReferenceEquals(immediateCaller, surfaceAssembly)
            || (!IsInMeshAssembly(immediateCaller) && !IsTransparent(immediateCaller)))
            return;

        // An in-mesh immediate caller decides by itself (the walk only names its method); a BCL one
        // hands the decision to the first frame below it.
        var caller = IsInMeshAssembly(immediateCaller)
            ? FindFrameIn(immediateCaller) ?? Describe(immediateCaller, method: null)
            : FindDecisiveCaller();
        if (caller is not { InMesh: true })
            return;

        var trusted = IsTrusted(caller.CodeOwner);
        var verdict = trusted ? "TRUSTED"
            : Mode == InMeshImpersonationMode.Enforce ? "REFUSED"
            : "WOULD REFUSE";
        Log(verdict, surface, principal!, caller, trusted);
        if (!trusted && Mode == InMeshImpersonationMode.Enforce)
            throw new InMeshImpersonationRefusedException(surface, caller);
    }

    /// <summary>
    /// The COMPILE-TIME half (option C, Doc/Architecture/InMeshImpersonation): judges the references
    /// to impersonation APIs that the compiler found in in-mesh source <paramref name="codeOwner"/>
    /// (<c>DynamicNode_{node}</c>, <c>kernel-script-session</c>, <c>node-config-script:{path}</c>).
    /// Same mode, same trust list, same log prefix as the runtime half. Under Enforce an untrusted
    /// owner's compile is refused with <see cref="InMeshImpersonationRefusedException"/> — the caller
    /// reports it as a compile error, so a NodeType parks at <c>compilationStatus: Error</c> naming the
    /// reference, which is the visible place for it.
    /// </summary>
    /// <param name="codeOwner">The load context the code will run in.</param>
    /// <param name="source">What was compiled — the node path, for the log.</param>
    /// <param name="references">The references the compiler found, rendered (<c>Symbol at file(line)</c>).</param>
    public void CheckCompiled(string codeOwner, string source, IReadOnlyList<string> references)
    {
        if (Mode == InMeshImpersonationMode.Off || references.Count == 0)
            return;
        var trusted = IsTrusted(codeOwner);
        var verdict = trusted ? "TRUSTED"
            : Mode == InMeshImpersonationMode.Enforce ? "REFUSED"
            : "WOULD REFUSE";
        if (logger is not null)
        {
            // One line per (verdict, owner, reference) the first time — a recompile of the same
            // source is counted, never re-written until the 1000th.
            foreach (var reference in references)
            {
                var key = $"compile|{verdict}|{codeOwner}|{reference}";
                var count = occurrences.AddOrUpdate(key, 1, (_, c) => c + 1);
                if (count != 1 && count % 1000 != 0)
                    continue;
                logger.Log(trusted ? LogLevel.Information : LogLevel.Warning,
                    "{Prefix} COMPILE {Verdict} {CodeOwner} ({Source}) references {Reference}; occurrence {Count}, mode {Mode}",
                    LogPrefix, verdict, codeOwner, source, reference, count, Mode);
            }
        }
        if (!trusted && Mode == InMeshImpersonationMode.Enforce)
            throw new InMeshImpersonationRefusedException(
                "compile: " + string.Join("; ", references),
                new ImpersonationCaller(true, codeOwner, source, codeOwner));
    }

    /// <summary>System or a hub-shaped principal. Pure.</summary>
    public static bool IsPlatformPrincipal(AccessContext? principal) =>
        principal is not null
        && (principal.IsHub || AccessService.IsPlatformPrincipal(principal.ObjectId));

    /// <summary>
    /// True when <paramref name="assembly"/> was loaded outside the default context and outside
    /// every <see cref="IPlatformLoadContext"/> — i.e. it is code the mesh compiled at runtime. Pure.
    /// </summary>
    public static bool IsInMeshAssembly(Assembly assembly)
    {
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        return context is not null
               && !ReferenceEquals(context, AssemblyLoadContext.Default)
               && context is not IPlatformLoadContext;
    }

    /// <summary>
    /// Classifies <paramref name="assembly"/> as a caller, naming its load context. Pure.
    /// </summary>
    public static ImpersonationCaller Describe(Assembly assembly, string? method) =>
        IsInMeshAssembly(assembly)
            ? new ImpersonationCaller(true, AssemblyLoadContext.GetLoadContext(assembly)?.Name, method,
                assembly.GetName().Name ?? "")
            : new ImpersonationCaller(false, null, method, assembly.GetName().Name ?? "");

    /// <summary>
    /// True when <paramref name="codeOwner"/> (a load context name) belongs to a NodeType at or
    /// below one of <see cref="TrustedCode"/>. Only NodeType contexts can be trusted: a script
    /// session or a configuration script never is.
    /// </summary>
    public bool IsTrusted(string? codeOwner)
    {
        if (codeOwner is null || !codeOwner.StartsWith(NodeTypeLoadContextPrefix, StringComparison.Ordinal))
            return false;
        var node = codeOwner[NodeTypeLoadContextPrefix.Length..];
        foreach (var prefix in TrustedCode)
        {
            var sanitized = Sanitize(prefix);
            if (string.Equals(node, sanitized, StringComparison.Ordinal)
                || node.StartsWith(sanitized + "_", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Mirrors CodeConventions.SanitizeNodeName (MeshWeaver.Compiler sits above this assembly): the
    // path separators become underscores, which is all a configured prefix can contain.
    private static string Sanitize(string prefix) =>
        prefix.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace(' ', '_');

    /// <summary>
    /// Walks the stack: past the surface (frames in this assembly at the top), past the BCL
    /// (reflection, Rx), to the first frame that decides. Null when no frame decides — the call
    /// came from the thread pool or a scheduler with nothing of anybody's above it.
    /// </summary>
    private ImpersonationCaller? FindDecisiveCaller()
    {
        var frames = new StackTrace(skipFrames: 2, fNeedFileInfo: false).GetFrames();
        var pastSurface = false;
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            var assembly = method?.Module.Assembly;
            if (method is null || assembly is null)
                continue;
            if (!pastSurface && (ReferenceEquals(assembly, surfaceAssembly)
                                 || method.IsDefined(typeof(ImpersonationSurfaceAttribute), inherit: false)))
                continue;
            pastSurface = true;
            if (IsTransparent(assembly))
                continue;
            return Describe(assembly, $"{method.DeclaringType?.FullName}.{method.Name}");
        }
        return null;
    }

    /// <summary>The calling method in <paramref name="assembly"/>, for the log.</summary>
    private static ImpersonationCaller? FindFrameIn(Assembly assembly)
    {
        foreach (var frame in new StackTrace(skipFrames: 2, fNeedFileInfo: false).GetFrames())
            if (frame.GetMethod() is { } method && ReferenceEquals(method.Module.Assembly, assembly))
                return Describe(assembly, $"{method.DeclaringType?.FullName}.{method.Name}");
        return null;
    }

    /// <summary>
    /// The BCL — <c>System.*</c> loaded in the DEFAULT context. A frame there (reflection's
    /// <c>Invoke</c>, an Rx operator calling a delegate) is never the one that decided to
    /// impersonate. An assembly NAMED <c>System.*</c> in another context is not transparent.
    /// </summary>
    private static bool IsTransparent(Assembly assembly)
    {
        if (!ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), AssemblyLoadContext.Default))
            return false;
        var name = assembly.GetName().Name ?? "";
        return name == "System" || name == "mscorlib" || name == "netstandard"
               || name.StartsWith("System.", StringComparison.Ordinal);
    }

    private void Log(string verdict, string surface, AccessContext principal, ImpersonationCaller caller, bool trusted)
    {
        if (logger is null)
            return;
        // Every occurrence is counted; the first of each (verdict, surface, caller) and every 1000th
        // after it is written, with the running count — so a handler that impersonates per message
        // is visible with its rate and cannot flood the log.
        var key = $"{verdict}|{surface}|{principal.ObjectId}|{caller.CodeOwner}|{caller.Method}";
        var count = occurrences.AddOrUpdate(key, 1, (_, c) => c + 1);
        if (count != 1 && count % 1000 != 0)
            return;
        logger.Log(trusted ? LogLevel.Information : LogLevel.Warning,
            "{Prefix} {Verdict} {Surface} as {Principal} by {CodeOwner} ({Method}, assembly {Assembly}); occurrence {Count}, mode {Mode}",
            LogPrefix, verdict, surface, principal.ObjectId, caller.CodeOwner, caller.Method ?? "(unknown)",
            caller.Assembly, count, Mode);
    }
}

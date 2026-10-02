using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The code these tests run "in the mesh". Every method calls an impersonation surface DIRECTLY,
/// exactly as a NodeType source would. The tests load a second copy of this assembly into a
/// collectible <see cref="AssemblyLoadContext"/> named the way the compiler names a NodeType's
/// context, and call these methods through that copy — so the only difference between the in-mesh
/// call and the platform call (the copy in the default context) is the load context.
/// </summary>
public static class InMeshCode
{
    /// <summary>Opens a System scope, then reads the identity it installed.</summary>
    public static string? ImpersonateAsSystem(AccessService access)
    {
        using (access.ImpersonateAsSystem())
            return access.Context?.ObjectId;
    }

    /// <summary>Switches to <paramref name="identity"/>, then reads the identity it installed.</summary>
    public static string? Switch(AccessService access, AccessContext identity)
    {
        using (access.SwitchAccessContext(identity))
            return access.Context?.ObjectId;
    }

    // 🚨 No expression bodies that END in the surface call below: a call in tail position may be
    // compiled by the JIT as a tail call, which REMOVES this frame — the guard then sees whoever
    // called this method. That is a real residual of a stack-based guard (see the doc, "What the
    // runtime guard cannot see"); these methods model code whose frame is present.

    /// <summary>Composes (does not subscribe) a System-scoped operation.</summary>
    public static IObservable<int> RunAsSystem(AccessService access)
    {
        var composed = access.RunAsSystem(() => System.Reactive.Linq.Observable.Return(1));
        GC.KeepAlive(access);
        return composed;
    }

    /// <summary>Reaches ImpersonateAsSystem through REFLECTION, so the immediate caller is the BCL.</summary>
    public static string? ImpersonateThroughReflection(AccessService access)
    {
        if (typeof(AccessService).GetMethod(nameof(AccessService.ImpersonateAsSystem), Type.EmptyTypes)
                is not { } surface
            || surface.Invoke(access, null) is not IDisposable scope)
            throw new InvalidOperationException("AccessService.ImpersonateAsSystem() did not return a scope");
        using (scope)
            return access.Context?.ObjectId;
    }

    /// <summary>
    /// The forgery: declares a load context that CLAIMS to be the platform's
    /// (<see cref="ClaimsToBePlatformContext"/>), loads a further copy of this assembly into it and
    /// opens a System scope through that copy. Returns the identity the copy installed.
    /// </summary>
    public static string? ImpersonateThroughAContextThatClaimsToBePlatform(AccessService access)
    {
        var claimed = new ClaimsToBePlatformContext();
        try
        {
            var copy = claimed.LoadFromAssemblyPath(typeof(InMeshCode).Assembly.Location);
            if (copy.GetType(typeof(InMeshCode).FullName ?? nameof(InMeshCode), throwOnError: true)
                    ?.GetMethod(nameof(ImpersonateAsSystem)) is not { } method)
                throw new InvalidOperationException("the copy has no InMeshCode.ImpersonateAsSystem");
            return method.Invoke(null, [access]) as string;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        finally
        {
            claimed.Unload();
        }
    }

    /// <summary>Installs System with the raw setter, the shape a hand-rolled scope would use.</summary>
    public static void SetSystemContext(AccessService access)
    {
        access.SetContext(new AccessContext { ObjectId = AccessService.SystemObjectId, Name = AccessService.SystemObjectId });
        GC.KeepAlive(access);
    }
}

/// <summary>
/// A load context that carries the <see cref="IPlatformLoadContext"/> marker. Declared by the
/// PLATFORM (this type loaded in the default context) the marker is honoured; declared by in-mesh
/// code (this type loaded from a copy in a NodeType's context) it is a forgery and is not.
/// </summary>
public sealed class ClaimsToBePlatformContext()
    : AssemblyLoadContext(ContextName, isCollectible: true), IPlatformLoadContext
{
    /// <summary>The name the claimed context reports as the code owner when it is not honoured.</summary>
    public const string ContextName = "claims-to-be-platform";
}

/// <summary>
/// 🚨 Code the mesh compiles at runtime must not act as the platform once
/// <see cref="InMeshImpersonationGuard.ModeKey"/> is <see cref="InMeshImpersonationMode.Enforce"/>;
/// platform code must keep doing so; LogOnly must allow and NAME the caller.
///
/// <para>Negative control: <see cref="Enforce_SameCodeInTheDefaultContext_IsAllowed"/> runs the
/// IDENTICAL method from the default load context and must pass under Enforce. If the guard keyed on
/// anything but the load context (the method, the assembly name, the principal), that control and
/// the refusal tests could not both hold.</para>
/// </summary>
public sealed class InMeshImpersonationGuardTest : IDisposable
{
    private const string UserNodeType = "DynamicNode_rbuergi_Evil";
    private const string TrustedNodeType = "DynamicNode_Governance_Activity";

    private readonly List<AssemblyLoadContext> contexts = [];

    private static AccessService Access(string mode, ILoggerFactory? logs = null, params string[] trusted)
    {
        var settings = new Dictionary<string, string?> { [InMeshImpersonationGuard.ModeKey] = mode };
        for (var i = 0; i < trusted.Length; i++)
            settings[$"{InMeshImpersonationGuard.TrustedCodeKey}:{i}"] = trusted[i];
        return new AccessService(logs, new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    /// <summary>Loads a copy of this test assembly into a collectible context named <paramref name="contextName"/>.</summary>
    private Type InMeshCopy(string contextName)
    {
        var context = new AssemblyLoadContext(contextName, isCollectible: true);
        contexts.Add(context);
        var copy = context.LoadFromAssemblyPath(typeof(InMeshCode).Assembly.Location);
        AssemblyLoadContext.GetLoadContext(copy).Should().BeSameAs(context, "the copy must really live in the in-mesh context");
        if (copy.GetType(typeof(InMeshCode).FullName ?? nameof(InMeshCode), throwOnError: true) is not { } inMesh)
            throw new InvalidOperationException("the copy does not contain InMeshCode");
        return inMesh;
    }

    private static object? Call(Type inMesh, string method, params object[] args)
    {
        try
        {
            if (inMesh.GetMethod(method) is not { } target)
                throw new InvalidOperationException($"InMeshCode has no method '{method}'");
            return target.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    [Fact]
    public void Enforce_InMeshCode_CannotImpersonateAsSystem()
    {
        var access = Access(InMeshImpersonationMode.Enforce);
        var inMesh = InMeshCopy(UserNodeType);

        Action act = () => Call(inMesh, nameof(InMeshCode.ImpersonateAsSystem), access);

        act.Should().Throw<InMeshImpersonationRefusedException>()
            .Which.Caller.CodeOwner.Should().Be(UserNodeType);
        access.Context.Should().BeNull("a refused impersonation installs nothing");
    }

    [Fact]
    public void Enforce_InMeshCode_CannotSwitchToSystem_ButCanSwitchToAUser()
    {
        var access = Access(InMeshImpersonationMode.Enforce);
        var inMesh = InMeshCopy(UserNodeType);
        var system = new AccessContext { ObjectId = AccessService.SystemObjectId, Name = AccessService.SystemObjectId };
        var user = new AccessContext { ObjectId = "alice", Name = "alice" };

        ((Action)(() => Call(inMesh, nameof(InMeshCode.Switch), access, system)))
            .Should().Throw<InMeshImpersonationRefusedException>();
        ((Action)(() => Call(inMesh, nameof(InMeshCode.SetSystemContext), access)))
            .Should().Throw<InMeshImpersonationRefusedException>();
        Call(inMesh, nameof(InMeshCode.Switch), access, user)
            .Should().Be("alice", "switching to an ordinary user is not impersonating the platform");
    }

    [Fact]
    public void Enforce_InMeshCode_CannotComposeRunAsSystem()
    {
        var access = Access(InMeshImpersonationMode.Enforce);
        var inMesh = InMeshCopy(UserNodeType);

        // Refused at COMPOSITION: the subscriber may be platform code with no in-mesh frame left.
        ((Action)(() => Call(inMesh, nameof(InMeshCode.RunAsSystem), access)))
            .Should().Throw<InMeshImpersonationRefusedException>();
    }

    [Fact]
    public void Enforce_InMeshCode_ThroughReflection_IsRefused()
    {
        var access = Access(InMeshImpersonationMode.Enforce);
        var inMesh = InMeshCopy(UserNodeType);

        // The immediate caller is System.Private.CoreLib; the walk steps over it to the in-mesh frame.
        // Reflection wraps the refusal once more (the in-mesh code's own Invoke).
        var refused = ((Action)(() => Call(inMesh, nameof(InMeshCode.ImpersonateThroughReflection), access)))
            .Should().Throw<TargetInvocationException>()
            .Which.InnerException as InMeshImpersonationRefusedException;
        if (refused is not { } refusal)
        {
            Assert.Fail("the refusal is what the reflected call threw");
            return;
        }
        refusal.Caller.Method.Should().Contain(nameof(InMeshCode.ImpersonateThroughReflection));
        access.Context.Should().BeNull();
    }

    [Fact]
    public void Enforce_AForgedPlatformLoadContext_IsStillInMesh()
    {
        // IPlatformLoadContext is a public interface. In-mesh code that declares a context
        // implementing it and calls through a copy of itself loaded there must NOT be read as the
        // platform: the context's own type lives in the NodeType's context, so the marker is forged.
        var access = Access(InMeshImpersonationMode.Enforce);
        var inMesh = InMeshCopy(UserNodeType);

        ((Action)(() => Call(inMesh, nameof(InMeshCode.ImpersonateThroughAContextThatClaimsToBePlatform), access)))
            .Should().Throw<InMeshImpersonationRefusedException>()
            .Which.Caller.CodeOwner.Should().Be(ClaimsToBePlatformContext.ContextName);
        access.Context.Should().BeNull("a refused impersonation installs nothing");
    }

    [Fact]
    public void Enforce_APlatformDeclaredLoadContext_IsPlatform()
    {
        // CONTROL for the forgery test: the IDENTICAL context type and the identical call, with the
        // context type loaded by the default context — the marker is the platform's own, so code
        // loaded through it is platform code and may impersonate. Without this, a guard that simply
        // ignored the marker would pass the forgery test too.
        var access = Access(InMeshImpersonationMode.Enforce);

        InMeshCode.ImpersonateThroughAContextThatClaimsToBePlatform(access)
            .Should().Be(AccessService.SystemObjectId);
        access.Context.Should().BeNull("the scope was disposed");
    }

    [Fact]
    public void LogOnly_AForgedPlatformLoadContext_IsLogged()
    {
        // The mode that ships: the forgery is allowed, and it is NAMED — before the marker was
        // authenticated the fast path returned without a line.
        var logs = new CapturingLoggerFactory();
        var access = Access(InMeshImpersonationMode.LogOnly, logs);
        var inMesh = InMeshCopy(UserNodeType);

        Call(inMesh, nameof(InMeshCode.ImpersonateThroughAContextThatClaimsToBePlatform), access)
            .Should().Be(AccessService.SystemObjectId);

        logs.Lines.Should().ContainSingle(l => l.Contains(InMeshImpersonationGuard.LogPrefix))
            .Which.Should().Contain("WOULD REFUSE").And.Contain(ClaimsToBePlatformContext.ContextName);
    }

    [Fact]
    public void Enforce_SameCodeInTheDefaultContext_IsAllowed()
    {
        // NEGATIVE CONTROL: the identical methods, loaded in the default context (platform code).
        var access = Access(InMeshImpersonationMode.Enforce);

        InMeshCode.ImpersonateAsSystem(access).Should().Be(AccessService.SystemObjectId);
        InMeshCode.ImpersonateThroughReflection(access).Should().Be(AccessService.SystemObjectId);
        (InMeshCode.RunAsSystem(access) is not null).Should().BeTrue("composing is allowed for platform code");
        access.Context.Should().BeNull("every scope was disposed");
    }

    [Fact]
    public void Enforce_TrustedNodeType_MayImpersonate()
    {
        var access = Access(InMeshImpersonationMode.Enforce, null, "Governance");
        var trusted = InMeshCopy(TrustedNodeType);
        var untrusted = InMeshCopy("DynamicNode_GovernanceX_Activity");

        Call(trusted, nameof(InMeshCode.ImpersonateAsSystem), access).Should().Be(AccessService.SystemObjectId);
        ((Action)(() => Call(untrusted, nameof(InMeshCode.ImpersonateAsSystem), access)))
            .Should().Throw<InMeshImpersonationRefusedException>("a prefix matches a path segment, never a substring");
    }

    [Fact]
    public void Enforce_AScriptSession_IsNeverTrusted()
    {
        // Only NodeType contexts can be trusted; a C# Code node / script never is.
        var access = Access(InMeshImpersonationMode.Enforce, null, "kernel-script-session");
        var script = InMeshCopy("kernel-script-session");

        ((Action)(() => Call(script, nameof(InMeshCode.ImpersonateAsSystem), access)))
            .Should().Throw<InMeshImpersonationRefusedException>();
    }

    [Fact]
    public void LogOnly_InMeshCode_IsAllowedAndNamed()
    {
        var logs = new CapturingLoggerFactory();
        var access = Access(InMeshImpersonationMode.LogOnly, logs);
        var inMesh = InMeshCopy(UserNodeType);

        Call(inMesh, nameof(InMeshCode.ImpersonateAsSystem), access).Should().Be(AccessService.SystemObjectId);

        logs.Lines.Should().ContainSingle(l => l.Contains(InMeshImpersonationGuard.LogPrefix))
            .Which.Should().Contain("WOULD REFUSE").And.Contain(nameof(AccessService.ImpersonateAsSystem))
            .And.Contain(UserNodeType).And.Contain(nameof(InMeshCode.ImpersonateAsSystem));
    }

    [Fact]
    public void LogOnly_PlatformCode_IsNotLogged()
    {
        var logs = new CapturingLoggerFactory();
        var access = Access(InMeshImpersonationMode.LogOnly, logs);

        InMeshCode.ImpersonateAsSystem(access).Should().Be(AccessService.SystemObjectId);

        logs.Lines.Should().NotContain(l => l.Contains(InMeshImpersonationGuard.LogPrefix));
    }

    [Fact]
    public void Default_IsLogOnly()
    {
        new AccessService(null, new ConfigurationBuilder().Build()).ImpersonationGuard.Mode
            .Should().Be(InMeshImpersonationMode.LogOnly);
        new AccessService().ImpersonationGuard.Mode.Should().Be(InMeshImpersonationMode.LogOnly);
    }

    public void Dispose()
    {
        foreach (var context in contexts)
            context.Unload();
    }

    /// <summary>Collects formatted log lines; instance state only.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private ImmutableList<string> lines = ImmutableList<string>.Empty;

        public ImmutableList<string> Lines => lines;

        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                ImmutableInterlocked.Update(ref owner.lines, l => l.Add(formatter(state, exception)));
        }
    }
}

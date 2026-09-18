using System;

namespace MeshWeaver.Testing.InMesh;

/// <summary>
/// Marks a test case the in-mesh runner executes: a public instance method, parameterless (or taking
/// one trailing <see cref="System.Threading.CancellationToken"/>, which the runner cancels when the
/// case's <see cref="TimeoutSeconds"/> elapse), returning <c>void</c>,
/// <see cref="System.Threading.Tasks.Task"/> or <see cref="System.Threading.Tasks.ValueTask"/>, on a
/// class the <see cref="MeshTestRunner"/> instantiates with a <see cref="MeshTestContext"/> (or
/// parameterless). The xunit <c>[Fact]</c> of the migrated suites, without xunit. A timed case that
/// never observes its token is reported as having IGNORED it (the xUnit1069 shape).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class MeshFactAttribute : Attribute
{
    /// <summary>A human name for the verdict table; the method name when absent.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Why this case is not run — a skipped case renders as ⏭ with the reason, never as green.</summary>
    public string? Skip { get; init; }

    /// <summary>Per-case deadline in seconds; the runner's default when 0.</summary>
    public int TimeoutSeconds { get; init; }
}

/// <summary>A parameterised case: one <see cref="MeshInlineDataAttribute"/> per row, like xunit's <c>[Theory]</c> + <c>[InlineData]</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class MeshTheoryAttribute : Attribute
{
    /// <summary>A human name for the verdict table; the method name when absent.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Why this case is not run.</summary>
    public string? Skip { get; init; }

    /// <summary>Per-case deadline in seconds; the runner's default when 0.</summary>
    public int TimeoutSeconds { get; init; }
}

/// <summary>One argument row of a <see cref="MeshTheoryAttribute"/> case.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class MeshInlineDataAttribute(params object?[] data) : Attribute
{
    /// <summary>The arguments, in parameter order.</summary>
    public object?[] Data { get; } = data;
}

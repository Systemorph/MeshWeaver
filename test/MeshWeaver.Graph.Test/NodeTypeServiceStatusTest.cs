using System;
using FluentAssertions;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Covers the <see cref="INodeTypeService.GetStatus"/> default-method logic —
/// the four-state lifecycle (Unknown / Compiling / Error / Ok) that MCP
/// <c>GetDiagnostics</c> relies on. Regression test for the "false-green"
/// bug where <c>GetCompilationError</c> returning null was read as Ok even
/// when no compile had actually run since invalidation.
/// </summary>
public class NodeTypeServiceStatusTest
{
    private sealed class Stub : INodeTypeService
    {
        public bool Compiling;
        public string? Error;
        public DateTimeOffset? SucceededAt;

        public NodeTypeConfiguration? GetCachedConfiguration(string nodeTypePath) => null;
        public System.Threading.Tasks.Task<MeshNode> EnrichWithNodeTypeAsync(MeshNode node, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult(node);
        public System.Collections.Generic.IAsyncEnumerable<CreatableTypeInfo> GetCreatableTypesAsync(string nodePath, System.Threading.CancellationToken ct = default)
            => System.Linq.AsyncEnumerable.Empty<CreatableTypeInfo>();
        public Func<MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Messaging.MessageHubConfiguration>? GetCachedHubConfiguration(string nodeTypePath) => null;

        public bool IsCompiling(string nodeTypePath) => Compiling;
        public string? GetCompilationError(string nodeTypePath) => Error;
        public DateTimeOffset? GetLastSuccessfulCompileAt(string nodeTypePath) => SucceededAt;
    }

    [Fact]
    public void Unknown_when_neither_compiling_nor_error_nor_success_recorded()
    {
        INodeTypeService svc = new Stub();
        svc.GetStatus("type/x").Should().Be(CompilationStatus.Unknown);
    }

    [Fact]
    public void Compiling_wins_over_error_and_success()
    {
        INodeTypeService svc = new Stub
        {
            Compiling = true,
            Error = "stale error",
            SucceededAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        svc.GetStatus("type/x").Should().Be(CompilationStatus.Compiling);
    }

    [Fact]
    public void Error_wins_over_stale_success()
    {
        // An error logged after a prior success must override the old Ok —
        // the success marker should have been cleared by the service when
        // the new failure happened, but even if consumers forgot, Error
        // takes precedence in the enum reader.
        INodeTypeService svc = new Stub
        {
            Error = "CS0006: missing reference",
            SucceededAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        svc.GetStatus("type/x").Should().Be(CompilationStatus.Error);
    }

    [Fact]
    public void Ok_only_when_a_successful_compile_has_completed()
    {
        INodeTypeService svc = new Stub { SucceededAt = DateTimeOffset.UtcNow };
        svc.GetStatus("type/x").Should().Be(CompilationStatus.Ok);
    }

    [Fact]
    public void After_invalidation_the_status_flips_back_to_Unknown_not_Ok()
    {
        // Exactly the regression the user hit: right after a Recycle /
        // InvalidateCache, the error was cleared *but* no fresh compile
        // had happened yet. Old code returned Ok because
        // GetCompilationError was null. New code returns Unknown because
        // GetLastSuccessfulCompileAt is also null.
        var stub = new Stub
        {
            SucceededAt = DateTimeOffset.UtcNow,
            Error = null
        };
        INodeTypeService svc = stub;
        svc.GetStatus("type/x").Should().Be(CompilationStatus.Ok);

        // Simulate InvalidateCache clearing both error *and* success tracker.
        stub.SucceededAt = null;
        stub.Error = null;
        svc.GetStatus("type/x").Should().Be(CompilationStatus.Unknown);
    }
}

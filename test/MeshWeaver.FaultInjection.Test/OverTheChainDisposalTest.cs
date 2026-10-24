using System;
using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <c>AddFaultInjectingStorageOverTheChain</c> wraps the outermost store registration without taking
/// it out of the container's ownership: a factory-built chain (the production outermost decorator
/// releases its admitted-write connections on <c>Dispose</c>) is disposed with the provider, and an
/// instance registration stays non-owned, exactly as it was before the wrap.
/// </summary>
public class OverTheChainDisposalTest
{
    /// <summary>
    /// Negative control: building the wrapped adapter by invoking the removed registration's factory
    /// directly leaves its product outside DI, so the provider's disposal never reaches it.
    /// </summary>
    [Fact]
    public void AFactoryBuiltChain_IsDisposedWithTheProvider()
    {
        var services = new ServiceCollection();
        DisposalRecordingAdapter? chain = null;
        services.AddSingleton<IStorageAdapter>(_ => chain = new DisposalRecordingAdapter());
        FaultInjectionMeshExtensions.WrapOutermostStorageAdapter(services, arm: null);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStorageAdapter>().Should().BeOfType<FaultInjectingStorageAdapter>("the mesh's store is the injector");
        chain.Should().NotBeNull("resolving the injector built the wrapped chain");
        chain!.IsDisposed.Should().BeFalse("nothing has been torn down yet");

        provider.Dispose();

        chain.IsDisposed.Should().BeTrue("the container built the chain, so the container disposes it");
    }

    /// <summary>An instance registration was never owned by the container, and the wrap keeps it so.</summary>
    [Fact]
    public void AnInstanceRegisteredChain_StaysNonOwned()
    {
        var services = new ServiceCollection();
        var chain = new DisposalRecordingAdapter();
        services.AddSingleton<IStorageAdapter>(chain);
        FaultInjectionMeshExtensions.WrapOutermostStorageAdapter(services, arm: null);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStorageAdapter>().Should().BeOfType<FaultInjectingStorageAdapter>("the mesh's store is the injector");

        provider.Dispose();

        chain.IsDisposed.Should().BeFalse("the caller owns an instance it registered");
    }

    private sealed class DisposalRecordingAdapter : IStorageAdapter, IDisposable
    {
        private readonly InMemoryStorageAdapter _inner = new(null);
        private int _disposed;

        public bool IsDisposed => System.Threading.Volatile.Read(ref _disposed) != 0;

        public void Dispose() => System.Threading.Interlocked.Exchange(ref _disposed, 1);

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options) => _inner.Read(path, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options) => _inner.Write(node, options);

        public IObservable<string> Delete(string path) => _inner.Delete(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
            => _inner.ListChildPaths(parentPath);

        public IObservable<bool> Exists(string path) => _inner.Exists(path);

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => _inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<System.Reactive.Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => _inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => _inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => _inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}

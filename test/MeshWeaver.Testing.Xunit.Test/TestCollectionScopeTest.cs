using MeshWeaver.Testing.Xunit;
using Xunit;

namespace MeshWeaver.Testing.Xunit.Test;

/// <summary>
/// Unit tests for the scope itself — no framework, no collection, no mesh. These pin the three
/// behaviours the execution host depends on: create-once, dispose in reverse creation order, and a
/// failed creation that stays failed.
/// </summary>
public class TestCollectionScopeTest
{
    private sealed class Tracked(string name, List<string> log) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            log.Add(name);
            return ValueTask.CompletedTask;
        }
    }

    private static TestCollectionScope NewScope() => TestCollectionScope.Begin("unit");

    [Fact]
    public async Task Factory_RunsOnce_HoweverManyAsk()
    {
        await using var scope = NewScope();
        var calls = 0;
        ValueTask<object> Make() { calls++; return ValueTask.FromResult<object>(new object()); }

        var first = await scope.GetOrCreateAsync("k", Make);
        var second = await scope.GetOrCreateAsync("k", Make);
        var third = await scope.GetOrCreateAsync("k", Make);

        Assert.Equal(1, calls);
        Assert.Same(first, second);
        Assert.Same(first, third);
    }

    [Fact]
    public async Task DistinctKeys_GetDistinctResources()
    {
        await using var scope = NewScope();
        var a = await scope.GetOrCreateAsync("a", () => ValueTask.FromResult<object>(new object()));
        var b = await scope.GetOrCreateAsync("b", () => ValueTask.FromResult<object>(new object()));

        Assert.NotSame(a, b);
        Assert.Equal(["a", "b"], scope.Keys);
    }

    [Fact]
    public async Task Dispose_RunsInReverseCreationOrder()
    {
        // A mesh must go down before the service provider it resolved out of; LIFO is what
        // guarantees that without the scope knowing anything about either.
        var log = new List<string>();
        var scope = NewScope();
        await scope.GetOrCreateAsync("first", () => ValueTask.FromResult<object>(new Tracked("first", log)));
        await scope.GetOrCreateAsync("second", () => ValueTask.FromResult<object>(new Tracked("second", log)));
        await scope.GetOrCreateAsync("third", () => ValueTask.FromResult<object>(new Tracked("third", log)));

        await scope.DisposeAsync();

        Assert.Equal(["third", "second", "first"], log);
    }

    [Fact]
    public async Task Dispose_DisposesEveryResource_EvenWhenOneThrows()
    {
        var log = new List<string>();
        var scope = NewScope();
        await scope.GetOrCreateAsync("ok-1", () => ValueTask.FromResult<object>(new Tracked("ok-1", log)));
        await scope.GetOrCreateAsync("bad", () => ValueTask.FromResult<object>(new Exploding()));
        await scope.GetOrCreateAsync("ok-2", () => ValueTask.FromResult<object>(new Tracked("ok-2", log)));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(async () => await scope.DisposeAsync());

        // The neighbours of the failing resource were still disposed — a skipped disposal is a
        // leak that outlives the collection, which is the failure the scope exists to end.
        Assert.Equal(["ok-2", "ok-1"], log);
        Assert.Contains("bad", thrown.Message + thrown.InnerException?.Message);
    }

    private sealed class Exploding : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new InvalidOperationException("teardown blew up");
    }

    [Fact]
    public async Task CreationFailure_IsCached_AndRethrownToEveryCaller()
    {
        // Retrying a broken fixture per case turns one boot failure into one timeout per row,
        // which reads as a slow suite instead of the single broken fixture it is.
        await using var scope = NewScope();
        var attempts = 0;
        ValueTask<object> Boom()
        {
            attempts++;
            throw new InvalidOperationException("mesh did not boot");
        }

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await scope.GetOrCreateAsync("k", Boom));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await scope.GetOrCreateAsync("k", Boom));

        Assert.Equal(1, attempts);
        Assert.Equal("mesh did not boot", first.Message);
        Assert.Equal("mesh did not boot", second.Message);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var log = new List<string>();
        var scope = NewScope();
        await scope.GetOrCreateAsync("x", () => ValueTask.FromResult<object>(new Tracked("x", log)));

        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.Equal(["x"], log);
    }

    [Fact]
    public async Task NeverAsked_ResourceIsNeverBuilt()
    {
        // A collection whose cases need no mesh must pay nothing for the host being installed.
        var scope = NewScope();
        await scope.DisposeAsync();
        Assert.Empty(scope.Keys);
    }

    [Fact]
    public async Task AfterDispose_GetOrCreate_Throws()
    {
        var scope = NewScope();
        await scope.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await scope.GetOrCreateAsync("k", () => ValueTask.FromResult<object>(new object())));
    }
}

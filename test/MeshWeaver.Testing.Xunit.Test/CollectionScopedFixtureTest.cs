using System.Collections.Concurrent;
using MeshWeaver.Testing.Xunit;
using Xunit;

namespace MeshWeaver.Testing.Xunit.Test;

/// <summary>
/// A stand-in for the expensive thing — a booted mesh — that records how often it was built and
/// torn down, per collection.
/// </summary>
public sealed class CountedResource : IAsyncDisposable
{
    /// <summary>Collection display name → how many times a resource was built for it.</summary>
    public static readonly ConcurrentDictionary<string, int> Boots = new();

    /// <summary>Collection display name → how many times its resource was disposed.</summary>
    public static readonly ConcurrentDictionary<string, int> Disposals = new();

    private CountedResource(string collection) => Collection = collection;

    /// <summary>The collection this resource was built for.</summary>
    public string Collection { get; }

    /// <summary>Every xunit test display name that reached this resource.</summary>
    public ConcurrentDictionary<string, byte> SeenCases { get; } = new();

    /// <summary>Builds one, counting the boot.</summary>
    public static ValueTask<CountedResource> CreateAsync(string collection)
    {
        Boots.AddOrUpdate(collection, 1, (_, n) => n + 1);
        return ValueTask.FromResult(new CountedResource(collection));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Disposals.AddOrUpdate(Collection, 1, (_, n) => n + 1);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The resource for the calling test's collection — built on the first ask, shared by every
    /// later case of that collection. This is the one line a converted test writes.
    /// </summary>
    public static ValueTask<CountedResource> ForCurrentCollection()
    {
        var scope = TestCollectionScope.Current
            ?? throw new InvalidOperationException(
                "no TestCollectionScope — the assembly is not running on MeshTestFramework");
        return scope.GetOrCreateAsync(
            typeof(CountedResource), () => CreateAsync(scope.CollectionDisplayName));
    }
}

/// <summary>Shared assertions for the two collection-bound suites below.</summary>
public abstract class CollectionScopedFixtureTestBase
{
    /// <summary>The <c>[Collection]</c> name this suite is declared in.</summary>
    protected abstract string ExpectedCollection { get; }

    /// <summary>
    /// Records the running case against the collection's resource and asserts it is the SAME
    /// resource every case of this collection sees, built exactly once.
    /// </summary>
    private protected async Task<CountedResource> AssertSharedAndRecord()
    {
        var scope = TestCollectionScope.Current;
        Assert.NotNull(scope);
        Assert.Equal(ExpectedCollection, scope.CollectionDisplayName);

        var resource = await CountedResource.ForCurrentCollection();
        Assert.Equal(ExpectedCollection, resource.Collection);

        // 🚨 The measured claim: ONE boot for the whole collection, no matter how many cases —
        // and a [Theory] row is a case. Before the execution host, each of these paid its own.
        Assert.Equal(1, CountedResource.Boots[ExpectedCollection]);
        Assert.Equal(0, CountedResource.Disposals.GetValueOrDefault(ExpectedCollection));

        var caseName = TestContext.Current.Test?.TestDisplayName;
        Assert.NotNull(caseName);
        // Each row must be a DISTINCT xunit case with its own name. If a data case collapsed to one
        // aggregate verdict this would collide on the second row.
        Assert.True(resource.SeenCases.TryAdd(caseName, 0),
            $"'{caseName}' reached the resource twice — rows are not distinct cases");
        return resource;
    }
}

/// <summary>The primary collection: a theory whose every row shares one boot.</summary>
[Collection(Alpha)]
public class AlphaCollectionTest : CollectionScopedFixtureTestBase
{
    internal const string Alpha = "Alpha collection";

    /// <inheritdoc/>
    protected override string ExpectedCollection => Alpha;

    [Theory]
    [InlineData("first", 1)]
    [InlineData("second", 2)]
    [InlineData("third", 3)]
    [InlineData("fourth", 4)]
    [InlineData("fifth", 5)]
    public async Task EveryInlineDataRow_SharesTheOneBoot_AndKeepsItsOwnIdentity(string label, int ordinal)
    {
        var resource = await AssertSharedAndRecord();

        // The row's own arguments really did arrive — xunit's data enumeration is untouched.
        Assert.Equal(ordinal, label switch
        {
            "first" => 1, "second" => 2, "third" => 3, "fourth" => 4, "fifth" => 5, _ => -1,
        });
        Assert.Contains(label, TestContext.Current.Test!.TestDisplayName);
        Assert.Equal(Alpha, resource.Collection);
    }

    /// <summary>Row data through <c>[MemberData]</c> — the 24 sites in the estate that use it.</summary>
    public static TheoryData<string> Labels() => new("alpha", "beta", "gamma");

    [Theory]
    [MemberData(nameof(Labels))]
    public async Task MemberDataRows_ShareTheSameBoot(string label)
    {
        await AssertSharedAndRecord();
        Assert.NotEmpty(label);
    }

    [Fact]
    public async Task APlainFact_SharesTheSameBootAsTheTheoryRows() => await AssertSharedAndRecord();

    [Fact(Skip = "Skip= is xunit's and must survive the host substitution — this must never run.")]
    public void ASkippedCase_DoesNotRun() =>
        Assert.Fail("a skipped case executed — Skip= did not survive the custom framework");

    [Theory]
    [InlineData(1)]
    [InlineData(2, Skip = "one row skipped, the other must still run")]
    public async Task PerRowSkip_SkipsOnlyThatRow(int row)
    {
        Assert.Equal(1, row);
        await AssertSharedAndRecord();
    }
}

/// <summary>A second collection: proves each collection gets its OWN resource.</summary>
[Collection(Beta)]
public class BetaCollectionTest : CollectionScopedFixtureTestBase
{
    internal const string Beta = "Beta collection";

    /// <inheritdoc/>
    protected override string ExpectedCollection => Beta;

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    public async Task ASecondCollection_GetsItsOwnBoot(string label)
    {
        var mine = await AssertSharedAndRecord();
        Assert.Equal(Beta, mine.Collection);
        Assert.NotEqual(AlphaCollectionTest.Alpha, mine.Collection);
        Assert.NotEmpty(label);
    }
}

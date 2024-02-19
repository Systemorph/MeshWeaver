using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Scopes.Test;

public class ScopesTest : ScopesTestBase
{

    public ScopesTest(ITestOutputHelper toh)
        : base(toh)
    {
    }

    [Fact]
    public void ScopeProperties()
    {
        var (scope, storage) = GetSingleTestScope<IRandomScope>();
        var identity = storage.Identities.First();
        scope.Identity.Should().Be(identity);
        scope.GetStorage().Should().Be(storage);
        var myIdentity = scope.MyIdentity;
        myIdentity.Should().NotBe(null);
        myIdentity.Should().Be(identity);
        var s2 = scope.GetScope<IRandomScope>(identity);
        s2.Should().Be(scope);
    }

    [Fact]
    public void BasicCaching()
    {
        var (container, _) = GetSingleTestScope<IRandomScope>();

        // variables should be cached
        var r1 = container.RandomVariable;
        var r2 = container.RandomVariable;
        r1.Should().Be(r2);

        // functions are not cached
        r1 = container.Random();
        r2 = container.Random();
        r1.Should().NotBe(r2);
    }

    [Fact]
    public void IndirectCaching()
    {
        var (container, _) = GetSingleTestScope<IRandomScope>();

        var r1 = container.RandomVariable;
        var rReference = container.RandomReference;
        rReference.Should().Be(r1);
    }

    [Fact]
    public void AccessingExistingOtherScope()
    {
        var (scopes, _) = GetTestScopes<IRandomScope>(2);
        var scopesByIdentity = scopes.ToDictionary(x => x.Identity);
        var i1 = scopesByIdentity.Keys.First();
        var s1 = scopesByIdentity[i1];
        var i2 = scopesByIdentity.Keys.Skip(1).First();
        var s2 = scopesByIdentity[i2];
        var s2Retrieved = s1.GetScope<IRandomScope>(i2);
        s2Retrieved.Should().Be(s2);
        s2Retrieved.RandomVariable.Should().Be(s2.RandomVariable);
    }

    [Fact]
    public void AccessingNewOtherScope()
    {
        var (scopes, storage) = GetTestScopes<IRandomScope>();
        var newId = new GuidIdentity();
        var otherScopes = scopes.Select(s => s.GetScope<IRandomScope>(newId)).Distinct().ToArray();
        otherScopes.Should().HaveCount(1);
        var testScope = otherScopes.Single();
        testScope.Should().NotBe(null);
        testScope.Identity.Should().Be(newId);
        testScope.GetStorage().Should().Be(storage);
    }

    [Fact]
    public void ResolvingScopeWithDifferentStorage()
    {
        var scope = ScopeFactory.ForSingleton().ToScope<IScope>();
        var storage = new IdentitiesStorage(3);
        var scope1 = scope.GetScope<IRandomScope>(storage.Identities[0], options => options.WithStorage(storage));
        scope1.GetStorage().Should().NotBeNull();
        var scope2 = scope1.GetScope<IRandomScope>(storage.Identities[1]);
        scope2.GetStorage().Should().NotBe(null);
        var scope3 = scope.GetScope<IRandomScope>(storage.Identities[2]);
        scope3.GetStorage().Should().BeNull();
    }



    [Fact]
    public void ArrayOfIntegersAsIdentities()
    {
        int[] years = new int[] { 2020, 2021, 2022 };

        var universe = ScopeFactory.ForSingleton().ToScope<IMainScope>();
        var scopes = universe.GetScopes<IInitializeScope>(years.AsEnumerable().Cast<object>());
        scopes.Count.Should().Be(3);

        scopes = universe.GetScopes<IInitializeScope>(years.AsEnumerable());
        scopes.Count.Should().Be(3);

        scopes = universe.GetScopes<IInitializeScope>(years);
        scopes.Count.Should().Be(3);
    }

    [Fact]
    public void ScopeInitExceptionTest()
    {
        var parent = ScopeFactory.ForSingleton().ToScope<IParentScope>();
        Assert.Throws<Exception>(parent.GetScope<ITestScope>);
        Assert.Throws<Exception>(parent.GetScope<ITestScope>);
    }

    [Fact]
    public void MethodAndPropertyOverload()
    {
        var a = ScopeFactory.ForSingleton().ToScope<IA>();
        var b = ScopeFactory.ForSingleton().ToScope<IB>();

        a.M().Should().Be("IA");
        b.M().Should().Be("IB");

        a.P.Should().Be("IA");
        b.P.Should().Be("IB");
    }

    public interface IParentScope : IMutableScope
    {
    }

    [InitializeScope(nameof(Init))]
    public interface ITestScope : IMutableScope
    {
        int Value { get; set; }
        void Init()
        {
            Value = 2;
            throw new Exception("ScopeInitException");
        }
    }

    public interface IA : IScope { string M() => "IA"; string P => "IA"; }
    public interface IB : IA { string IA.M() => "IB"; string IA.P => "IB"; }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Reactive.Assertions.Test;

public class AssertionTests
{
    private static void Fails(Action assertion) => Assert.ThrowsAny<AssertionException>(assertion);

    [Fact]
    public void Object_Be_NotBe_Null_OfType()
    {
        "x".Should().Be("x");
        Fails(() => "x".Should().Be("y"));
        "x".Should().NotBe("y");
        Fails(() => "x".Should().NotBe("x"));

        object? n = null;
        n.Should().BeNull();
        Fails(() => "x".Should().BeNull());
        "x".Should().NotBeNull();
        Fails(() => n.Should().NotBeNull());

        object o = "hello";
        var which = o.Should().BeOfType<string>().Which;
        Assert.Equal("hello", which);
        Fails(() => o.Should().BeOfType<int>());
    }

    [Fact]
    public void Boolean()
    {
        true.Should().BeTrue();
        Fails(() => false.Should().BeTrue());
        false.Should().BeFalse();
        Fails(() => true.Should().BeFalse());
        ((bool?)true).Should().BeTrue();
    }

    [Fact]
    public void String()
    {
        "hello world".Should().Contain("lo wo").And.StartWith("hello");
        Fails(() => "hello".Should().Contain("zzz"));
        "hello".Should().EndWith("llo");
        "hello".Should().Match("he*o");
        Fails(() => "hello".Should().Match("he?o"));
        "hello".Should().NotBeNullOrEmpty();
        Fails(() => "".Should().NotBeNullOrEmpty());
        "hello".Should().HaveLength(5);
    }

    [Fact]
    public void Comparable()
    {
        5.Should().BeGreaterThan(3).And.BeLessThanOrEqualTo(5);
        Fails(() => 5.Should().BeGreaterThan(5));
        5.Should().BeInRange(1, 10);
        Fails(() => 5.Should().BeInRange(6, 10));
        3.14.Should().BeGreaterThan(3.0);
    }

    [Flags]
    private enum Perm { None = 0, Read = 1, Write = 2 }

    [Fact]
    public void Enum_HaveFlag()
    {
        (Perm.Read | Perm.Write).Should().HaveFlag(Perm.Read);
        Fails(() => Perm.Read.Should().HaveFlag(Perm.Write));
        Perm.Read.Should().NotHaveFlag(Perm.Write);
        Perm.Read.Should().Be(Perm.Read);
    }

    [Fact]
    public void Collection()
    {
        var items = new[] { 3, 1, 2 };
        items.Should().HaveCount(3).And.NotBeEmpty();
        Fails(() => items.Should().HaveCount(2));
        items.Should().Contain(2).And.NotContain(9);
        Fails(() => items.Should().Contain(9));
        items.Should().OnlyContain(x => x > 0);
        Fails(() => items.Should().OnlyContain(x => x > 1));
        new[] { 1, 2, 3 }.Should().BeInAscendingOrder(x => x);
        Fails(() => items.Should().BeInAscendingOrder(x => x));
        new[] { 1, 2 }.Should().Equal(1, 2);
        Array.Empty<int>().Should().BeEmpty();

        var single = new[] { 42 }.Should().ContainSingle().Which;
        Assert.Equal(42, single);
        Fails(() => items.Should().ContainSingle());

        new[] { "a", "b" }.Should().Contain(["a", "b"]);
        new[] { 1, 2, 3 }.Should().HaveCountGreaterThan(2).And.HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Dictionary()
    {
        var d = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        d.Should().ContainKey("a").Which.Should().Be(1);
        Fails(() => d.Should().ContainKey("z"));
        d.Should().HaveCount(2).And.NotBeEmpty();
        d.Should().NotContainKey("z");
    }

    [Fact]
    public void Action_Throw()
    {
        Action bad = () => throw new InvalidOperationException("boom");
        bad.Should().Throw<InvalidOperationException>().WithMessage("bo*");
        Fails(() => bad.Should().Throw<ArgumentException>());
        Fails(() => bad.Should().Throw<InvalidOperationException>().WithMessage("nope"));

        Action good = () => { };
        good.Should().NotThrow();
        Fails(() => good.Should().Throw<Exception>());
    }

    [Fact]
    public async Task AsyncFunction_ThrowAsync()
    {
        Func<Task> bad = () => Task.FromException(new InvalidOperationException("kaboom"));
        var ex = await bad.Should().ThrowAsync<InvalidOperationException>();
        Assert.Equal("kaboom", ex.Which.Message);
        await bad.Should().ThrowAsync<InvalidOperationException>().WithMessage("ka*");

        await Assert.ThrowsAsync<AssertionException>(async () => await bad.Should().ThrowAsync<ArgumentException>());

        Func<Task> good = () => Task.CompletedTask;
        await good.Should().NotThrowAsync();
    }

    private record Person(string Name, int Age, string? Note = null);

    [Fact]
    public void BeEquivalentTo()
    {
        var opt = JsonSerializerOptions.Default;
        new Person("Ann", 30).Should().BeEquivalentTo(new Person("Ann", 30), opt);
        Fails(() => new Person("Ann", 30).Should().BeEquivalentTo(new Person("Ann", 31), opt));

        // Excluding a member that differs makes them equivalent.
        new Person("Ann", 30, "x").Should().BeEquivalentTo(new Person("Ann", 30, "y"), opt, o => o.Excluding(p => p.Note));

        // Collections compare order-insensitively by default; strict ordering enforces order.
        new[] { 1, 2, 3 }.Should().BeEquivalentTo(new[] { 3, 2, 1 }, opt);
        Fails(() => new[] { 1, 2, 3 }.Should().BeEquivalentTo(new[] { 3, 2, 1 }, opt, o => o.WithStrictOrdering()));

        var people = new[] { new Person("Ann", 30), new Person("Bob", 40) };
        people.Should().BeEquivalentTo(new[] { new Person("Bob", 40), new Person("Ann", 30) }, opt);
    }

    [Fact]
    public void TimeExtensions()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), 10.Seconds());
        Assert.Equal(TimeSpan.FromMilliseconds(200), 200.Milliseconds());
        Assert.Equal(TimeSpan.FromMinutes(1.5), 1.5.Minutes());
    }

    [Fact]
    public void ObservableAsserts()
    {
        Observable.Return(42).Should().Be(42);
        Observable.Range(1, 5).Should().Match(x => x == 3);
        Assert.Equal(1, Observable.Return(1).Should().Emit());
        Observable.Never<int>().Should().NotEmit(50.Milliseconds());

        var subject = new Subject<int>();
        Fails(() => subject.Should().Within(50.Milliseconds()).Emit());
    }
}

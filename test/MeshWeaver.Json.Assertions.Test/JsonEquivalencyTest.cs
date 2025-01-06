using FluentAssertions;
using MeshWeaver.Json.Assertions;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Json.Assertions.Test;

public class JsonEquivalencyTest
{
    [Fact]
    public void NoExcludeProperty()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.BaseType"",
    ""prop1"": 1,
    ""prop2"": ""abc""
}");
        actual.Should().BeEquivalentTo(new BaseType { Prop1 = 1, Prop2 = "abc" }, o => o.UsingJson());
    }

    [Fact]
    public void NoExcludePropertyNegative()
    {
        var actual = new RawJson(
                                 @"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.BaseType"",
    ""prop1"": 1,
    ""prop2"": ""abc""
}");
        actual.Should().NotBeEquivalentTo(new BaseType { Prop1 = 1, Prop2 = "xyz" }, o => o.UsingJson());
    }

    [Fact]
    public void DifferentTypeDiscriminator()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.BaseType"",
    ""prop1"": 1,
    ""prop2"": ""abc""
}");
        actual.Should().NotBeEquivalentTo(new { Prop1 = 1, Prop2 = "abc" }, o => o.UsingJson());
    }

    [Fact]
    public void ExcludeTypeDiscriminator()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Unknown"",
    ""prop1"": 1,
    ""prop2"": ""abc""
}");
        actual.Should().BeEquivalentTo(new BaseType { Prop1 = 1, Prop2 = "abc" }, o => o.UsingJson(j => j.ExcludeTypeDiscriminator()));
    }

    [Fact]
    public void SimpleExcludeProperty()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.BaseType"",
    ""prop1"": 1,
    ""prop2"": ""abc""
}");
        actual.Should().BeEquivalentTo(new BaseType { Prop1 = 10, Prop2 = "abc" }, o => o.UsingJson(j => j.ExcludeProperty<BaseType, int>(x => x.Prop1)));
    }

    [Fact]
    public void SimpleExcludePropertyNegative()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.BaseType"",
    ""prop1"": 1,
    ""prop2"": ""abc""
}");
        actual.Should().NotBeEquivalentTo(new BaseType { Prop1 = 10, Prop2 = "xyz" },
                                          o => o.UsingJson(j => j.ExcludeProperty<BaseType, int>(x => x.Prop1)));
    }

    [Fact]
    public void DerivedExcludeProperty()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.InheritedType"",
    ""prop1"": 1,
    ""prop2"": ""abc"",
    ""prop3"": 3,
}");
        actual.Should().BeEquivalentTo(new InheritedType { Prop1 = 1, Prop2 = "xyz", Prop3 = 30 },
                                       o => o.UsingJson(j => j
                                                             .ExcludeProperty<BaseType, string>(x => x.Prop2)
                                                             .ExcludeProperty<InheritedType, int>(x => x.Prop3)));
    }


    [Fact]
    public void DerivedExcludePropertyNegative()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.InheritedType"",
    ""prop1"": 1,
    ""prop2"": ""abc"",
    ""prop3"": 3,
}");
        actual.Should().NotBeEquivalentTo(new InheritedType { Prop1 = 10, Prop2 = "xyz", Prop3 = 30 },
                                       o => o.UsingJson(j => j
                                                             .ExcludeProperty<BaseType, string>(x => x.Prop2)
                                                             .ExcludeProperty<InheritedType, int>(x => x.Prop3)));
    }

    [Fact]
    public void ExcludeNestedProperty()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.Container"",
    ""value"": {
        ""$type"": ""MeshWeaver.Json.Assertions.Test.BaseType"",
        ""prop1"": 1,
        ""prop2"": ""abc""
    },
}");
        actual.Should().BeEquivalentTo(new Container{Value = new BaseType { Prop1 = 1, Prop2 = "xyz" } }, o => o.UsingJson(j => j.ExcludeProperty<BaseType, string>(x => x.Prop2)));
    }


    [Fact]
    public void ExcludeNestedPropertyNegative()
    {
        var actual = new RawJson(
@"{
    ""$type"": ""MeshWeaver.Json.Assertions.Test.Container"",
    ""value"": {
        ""$type"": ""MeshWeaver.Json.Assertions.Test.BaseType"",
        ""prop1"": 1,
        ""prop2"": ""abc""
    },
}");
        actual.Should().NotBeEquivalentTo(new Container { Value = new BaseType { Prop1 = 10, Prop2 = "xyz" } }, o => o.UsingJson(j => j.ExcludeProperty<BaseType, string>(x => x.Prop2)));
    }
}

public class Container
{
    public object Value { get; set; }
}

public class BaseType
{
    public int Prop1 { get; set; }
    public string Prop2 { get; set; }
}

public class InheritedType : BaseType
{
    public int Prop3 { get; set; }
}

using System;
using MeshWeaver.Domain;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <c>$Model/{Id}</c> on a NodeType: the Id names a type of the instance model, or — the NodeType's own
/// name, which is how a caller holding only the type node addresses it (the data model explorer's Table
/// tab: <c>Esl/…/DirCarrier</c>, whose content type is <c>EslOfficeCarrier</c>) — its content type. It
/// read "Type not found" for every type whose content type is named differently.
/// </summary>
public class NodeTypeModelTypeResolutionTest
{
    private sealed record Carrier(string Name);
    private sealed record Address(string Street);

    private sealed class Def(Type type) : ITypeDefinition
    {
        public Type Type => type;
        public string DisplayName => type.Name;
        public string CollectionName => type.Name;
        public object? Icon => null;
        public object GetKey(object instance) => instance;
        public int? Order => null;
        public string? GroupName => null;
        public string Description => "";
        public Type GetKeyType() => typeof(string);
    }

    private static NodeTypeInstanceModel Model()
        => new([new Def(typeof(Carrier))], [new Def(typeof(Carrier)), new Def(typeof(Address))], nameof(Carrier), "{}");

    [Fact]
    public void TheOwnName_ResolvesToTheContentType_CaseInsensitively()
    {
        NodeTypeDataModelAreas.ResolveModelType(Model(), "DirCarrier", "Esl/EslOffice/Model/DirCarrier")!.Type
            .Should().Be(typeof(Carrier));
        NodeTypeDataModelAreas.ResolveModelType(Model(), "dircarrier", "Esl/EslOffice/Model/DirCarrier")!.Type
            .Should().Be(typeof(Carrier), "a URL segment's casing carries no meaning");
    }

    [Fact]
    public void ARegisteredTypeName_WinsOverTheOwnNameRule()
    {
        NodeTypeDataModelAreas.ResolveModelType(Model(), nameof(Address), "X/Address")!.Type
            .Should().Be(typeof(Address), "a type of that name exists, so the Id names it");
    }

    [Fact]
    public void AnyOtherName_IsNotFound()
        => NodeTypeDataModelAreas.ResolveModelType(Model(), "Gadget", "Esl/EslOffice/Model/DirCarrier")
            .Should().BeNull();
}

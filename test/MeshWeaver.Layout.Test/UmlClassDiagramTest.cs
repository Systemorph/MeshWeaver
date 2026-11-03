using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins <see cref="UmlClassDiagramControl.Layout"/> (the geometry every renderer draws) and
/// <see cref="UmlSchemaProjection.FromContentSchemas"/> (content schemas → classes and relations,
/// relations read from the <c>x-meshNode</c> extension the schemas already carry).
/// </summary>
public class UmlClassDiagramTest
{
    private static UmlClass C(string id, params UmlAttribute[] attributes) =>
        new(id, id.Split('/').Last()) { Attributes = attributes };

    private static UmlRelation R(string from, string to, string kind = UmlRelationKind.Association) => new(from, to, kind);

    private static UmlBox Box(UmlDiagramLayout layout, string id) => layout.Boxes.Single(b => b.Class.Id == id);

    [Fact]
    public void NoClasses_IsNoLayout()
    {
        Assert.Null(UmlClassDiagramControl.Layout(null, null));
        Assert.Null(UmlClassDiagramControl.Layout([], [R("a", "b")]));
    }

    [Fact]
    public void AReferencedClass_SitsBelowItsReferrer_AndABaseClassAboveItsSubclass()
    {
        var layout = UmlClassDiagramControl.Layout(
            [C("Acceptance"), C("LineOfBusiness"), C("Section"), C("QuotaShareSection")],
            [R("Acceptance", "LineOfBusiness"), R("QuotaShareSection", "Section", UmlRelationKind.Generalization)])!;

        Assert.True(Box(layout, "Acceptance").Layer < Box(layout, "LineOfBusiness").Layer,
            "the referenced dimension sits below the class that references it");
        Assert.True(Box(layout, "Section").Layer < Box(layout, "QuotaShareSection").Layer,
            "UML draws the generalization triangle UP: the base class sits above the subclass");
    }

    [Fact]
    public void ACycle_TerminatesAndPlacesEveryClassOnce()
    {
        var layout = UmlClassDiagramControl.Layout(
            [C("a"), C("b"), C("c")], [R("a", "b"), R("b", "c"), R("c", "a")])!;
        Assert.Equal(3, layout.Boxes.Count);
        Assert.Equal(3, layout.Routes.Count);
        Assert.Equal(new[] { "a", "b", "c" }, layout.Boxes.Select(b => b.Class.Id).OrderBy(x => x));
    }

    [Fact]
    public void DuplicatesUnknownEndpointsAndSelfRelations_AreNotDrawn()
    {
        var layout = UmlClassDiagramControl.Layout(
            [C("a"), C("a"), C("b")], [R("a", "b"), R("a", "ghost"), R("a", "a")])!;
        Assert.Equal(2, layout.Boxes.Count);
        Assert.Single(layout.Routes);
    }

    [Fact]
    public void NoTwoBoxesOverlap_AndTheCanvasEnclosesEverything()
    {
        var classes = Enumerable.Range(0, 24).Select(i => C($"T{i}", new UmlAttribute("name", "string"))).ToList();
        var relations = Enumerable.Range(1, 23).Select(i => R($"T{i / 3}", $"T{i}")).ToList();
        var layout = UmlClassDiagramControl.Layout(classes, relations)!;

        foreach (var a in layout.Boxes)
            foreach (var b in layout.Boxes.Where(b => b != a))
                Assert.False(a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height,
                    $"{a.Class.Id} overlaps {b.Class.Id}");
        Assert.All(layout.Boxes, b => Assert.True(b.X >= 0 && b.Y >= 0 && b.X + b.Width <= layout.Width && b.Y + b.Height <= layout.Height));
        Assert.All(layout.Routes.SelectMany(r => r.Points), p => Assert.True(p.X >= 0 && p.X <= layout.Width && p.Y >= 0 && p.Y <= layout.Height));
    }

    [Fact]
    public void Routes_AreOrthogonal_AndRunFromTheFromBoxToTheToBox()
    {
        var layout = UmlClassDiagramControl.Layout(
            [C("Acceptance"), C("LineOfBusiness"), C("Base"), C("Sub")],
            [R("Acceptance", "LineOfBusiness"), R("Sub", "Base", UmlRelationKind.Generalization)])!;
        foreach (var route in layout.Routes)
        {
            for (var i = 1; i < route.Points.Count; i++)
                Assert.True(route.Points[i].X == route.Points[i - 1].X || route.Points[i].Y == route.Points[i - 1].Y,
                    "every segment is horizontal or vertical");
            var from = Box(layout, route.Relation.From);
            var to = Box(layout, route.Relation.To);
            Assert.True(OnBorder(from, route.Points[0]), "the route starts on the From box");
            Assert.True(OnBorder(to, route.Points[^1]), "the route ends on the To box");
        }

        static bool OnBorder(UmlBox b, UmlPoint p) =>
            p.X >= b.X && p.X <= b.X + b.Width && (p.Y == b.Y || p.Y == b.Y + b.Height);
    }

    [Fact]
    public void ParallelEdgesFromOneBox_TurnAtDifferentHeights()
    {
        var layout = UmlClassDiagramControl.Layout(
            [C("a"), C("b"), C("c"), C("d")], [R("a", "b"), R("a", "c"), R("a", "d")])!;
        var turns = layout.Routes.Select(r => r.Points[1].Y).ToList();
        Assert.Equal(turns.Count, turns.Distinct().Count());
    }

    [Fact]
    public void EdgesLeavingOneBox_FanOutInTheOrderOfTheirTargets()
    {
        // Relations given in the REVERSE of their targets' left-to-right order.
        var layout = UmlClassDiagramControl.Layout(
            [C("a"), C("b"), C("c"), C("d")], [R("a", "d"), R("a", "c"), R("a", "b")])!;
        var byTarget = layout.Routes.OrderBy(r => Box(layout, r.Relation.To).X).Select(r => r.Points[0].X).ToList();
        Assert.Equal(byTarget.OrderBy(x => x), byTarget);
    }

    /// <summary>
    /// Review: A→B, B→C and A→C — the A→C edge skips B's layer and must not run through B's box.
    /// </summary>
    [Fact]
    public void AnEdgeThatSkipsALayer_NeverRunsThroughABox()
    {
        var layout = UmlClassDiagramControl.Layout([C("A"), C("B"), C("C")], [R("A", "B"), R("B", "C"), R("A", "C")])!;
        Assert.True(Box(layout, "A").Layer < Box(layout, "B").Layer && Box(layout, "B").Layer < Box(layout, "C").Layer,
            "precondition: A→C skips B's layer");
        foreach (var route in layout.Routes)
            for (var i = 1; i < route.Points.Count; i++)
                foreach (var box in layout.Boxes.Where(b => b.Class.Id != route.Relation.From && b.Class.Id != route.Relation.To))
                    Assert.False(Crosses(route.Points[i - 1], route.Points[i], box),
                        $"{route.Relation.From}→{route.Relation.To} passes through {box.Class.Id}");

        static bool Crosses(UmlPoint a, UmlPoint b, UmlBox box)
        {
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            return maxX > box.X && minX < box.X + box.Width && maxY > box.Y && minY < box.Y + box.Height;
        }
    }

    /// <summary>Review: a module's own relation kind is itself, never an association.</summary>
    [Fact]
    public void AModuleDefinedRelationKind_IsItsOwnKind()
    {
        var aggregation = new UmlRelation("a", "b", "aggregation");
        Assert.True(aggregation.IsKind("aggregation"));
        Assert.False(aggregation.IsKind(UmlRelationKind.Association));
        Assert.True(new UmlRelation("a", "b", "").IsKind(UmlRelationKind.Association), "a blank kind is an association");
    }

    [Fact]
    public void Attributes_AreCapped_AndTheRestCounted()
    {
        var attributes = Enumerable.Range(0, 10).Select(i => new UmlAttribute($"p{i}", "string")).ToArray();
        var layout = UmlClassDiagramControl.Layout([C("a", attributes)], [], maxAttributes: 4)!;
        Assert.Equal(4, layout.Boxes[0].Attributes.Count);
        Assert.Equal(6, layout.Boxes[0].HiddenAttributes);
    }

    [Fact]
    public void TheLayout_IsDeterministic()
    {
        var classes = new[] { C("a"), C("b"), C("c"), C("d") };
        var relations = new[] { R("a", "c"), R("b", "c"), R("b", "d") };
        var one = UmlClassDiagramControl.Layout(classes, relations)!;
        var two = UmlClassDiagramControl.Layout(classes, relations)!;
        Assert.Equal(one.Boxes.Select(b => (b.Class.Id, b.X, b.Y)), two.Boxes.Select(b => (b.Class.Id, b.X, b.Y)));
    }

    [Fact]
    public void AttributeLine_IsUmlNotation()
    {
        Assert.Equal("lineOfBusiness : LineOfBusiness [0..1]", new UmlAttribute("lineOfBusiness", "LineOfBusiness", "0..1").Line);
        Assert.Equal("name : string", new UmlAttribute("name", "string").Line);
    }

    // ── projection from the published content schemas ───────────────────────────────────────────

    private const string AcceptanceSchema = """
        {"type":["object","null"],"anyOf":[{"properties":{
          "$type":{"const":"Acceptance"},
          "name":{"type":"string","description":"The acceptance name."},
          "lineOfBusiness":{"type":["string","null"],"x-meshNode":["nodeType:Reinsurance/LineOfBusiness"],"description":"Path to a line of business."},
          "currency":{"type":"string","x-meshNode":["nodeType:Reinsurance/Currency"]},
          "cedents":{"type":"array","items":{"type":"string"},"x-meshNode":["nodeType:Crm/Counterparty OR nodeType:Reinsurance/Cedent"]},
          "limit":{"type":["number","null"]},
          "inception":{"type":"string","format":"date-time"}
        },"title":"Acceptance","description":"A reinsurance acceptance."}],"title":"Acceptance","description":"A reinsurance acceptance."}
        """;

    private const string LineOfBusinessSchema = """
        {"type":"object","properties":{"$type":{"const":"LineOfBusiness"},"name":{"type":"string"}},"title":"LineOfBusiness"}
        """;

    [Fact]
    public void Projection_ReadsAttributesAndReferences_FromTheSchemas()
    {
        var (classes, relations) = UmlSchemaProjection.FromContentSchemas(
            [new UmlSchemaInput("Reinsurance/Acceptance", AcceptanceSchema), new UmlSchemaInput("Reinsurance/LineOfBusiness", LineOfBusinessSchema)],
            path => $"/DataModelling/Explorer/Type/{System.Uri.EscapeDataString(path)}");

        var acceptance = classes.Single(c => c.Id == "Reinsurance/Acceptance");
        Assert.Equal("Acceptance", acceptance.Name);
        Assert.Equal("A reinsurance acceptance.", acceptance.Description);
        Assert.Equal("/DataModelling/Explorer/Type/Reinsurance%2FAcceptance", acceptance.Href);
        var lines = acceptance.Attributes!.ToDictionary(a => a.Name, a => a.Line);
        Assert.False(lines.ContainsKey("$type"), "the discriminator is not an attribute");
        Assert.Equal("lineOfBusiness : LineOfBusiness [0..1]", lines["lineOfBusiness"]);
        Assert.Equal("currency : Currency", lines["currency"]);
        Assert.Equal("cedents : Counterparty | Cedent [*]", lines["cedents"]);
        Assert.Equal("limit : number [0..1]", lines["limit"]);
        Assert.Equal("inception : DateTime", lines["inception"]);
        Assert.Equal("Path to a line of business.", acceptance.Attributes!.Single(a => a.Name == "lineOfBusiness").Description);

        Assert.Contains(relations, r => r.From == "Reinsurance/Acceptance" && r.To == "Reinsurance/LineOfBusiness"
                                        && r.Role == "lineOfBusiness" && r.ToMultiplicity == "0..1");
        Assert.Contains(relations, r => r.To == "Reinsurance/Currency" && r.ToMultiplicity == "1");
        Assert.Contains(relations, r => r.To == "Crm/Counterparty" && r.Role == "cedents" && r.ToMultiplicity == "*");
        Assert.Contains(relations, r => r.To == "Reinsurance/Cedent" && r.ToMultiplicity == "*");
    }

    [Fact]
    public void Projection_DrawsReferencedTypesOfOtherPackages_AsForeignClasses()
    {
        var (classes, _) = UmlSchemaProjection.FromContentSchemas(
            [new UmlSchemaInput("Reinsurance/Acceptance", AcceptanceSchema), new UmlSchemaInput("Reinsurance/LineOfBusiness", LineOfBusinessSchema)]);

        Assert.True(classes.Single(c => c.Id == "Reinsurance/LineOfBusiness").IsKind(UmlClassKind.Local),
            "an input type is a local class even when it is referenced");
        var counterparty = classes.Single(c => c.Id == "Crm/Counterparty");
        Assert.True(counterparty.IsKind(UmlClassKind.Foreign));
        Assert.Equal("Crm", counterparty.Package);
        Assert.Equal("Counterparty", counterparty.Name);
        Assert.Single(classes, c => c.Id == "Reinsurance/Currency");
    }

    [Fact]
    public void Projection_OfAnUnreadableSchema_IsAClassWithoutAttributes()
    {
        var (classes, relations) = UmlSchemaProjection.FromContentSchemas(
            [new UmlSchemaInput("X/Broken", "{not json"), new UmlSchemaInput("X/Empty", null)]);
        Assert.Equal(new[] { "Broken", "Empty" }, classes.Select(c => c.Name));
        Assert.All(classes, c => Assert.Empty(c.Attributes!));
        Assert.Empty(relations);
    }

    /// <summary>Review: a boolean property schema (legal JSON Schema) is an attribute, not an exception.</summary>
    [Fact]
    public void Projection_OfABooleanPropertySchema_IsAnObjectAttribute()
    {
        var (classes, _) = UmlSchemaProjection.FromContentSchemas(
            [new UmlSchemaInput("X/A", """{"type":"object","properties":{"value":true,"name":{"type":"string"}}}""")]);
        Assert.Equal("value : object", classes.Single().Attributes!.Single(a => a.Name == "value").Line);
    }

    /// <summary>Review: differently cased references resolve to ONE class id, so the layout keeps every relation.</summary>
    [Fact]
    public void Projection_ResolvesDifferentlyCasedReferences_ToOneClass()
    {
        var (classes, relations) = UmlSchemaProjection.FromContentSchemas(
        [
            new UmlSchemaInput("X/B", """{"type":"object","properties":{"n":{"type":"string"}}}"""),
            new UmlSchemaInput("X/A", """{"type":"object","properties":{"b":{"type":"string","x-meshNode":["nodeType:x/b"]},"f1":{"type":"string","x-meshNode":["nodeType:Y/F"]},"f2":{"type":"string","x-meshNode":["nodeType:y/f"]}}}"""),
        ]);
        Assert.Equal(new[] { "X/A", "X/B", "Y/F" }, classes.Select(c => c.Id).OrderBy(i => i));
        Assert.Contains(relations, r => r.Role == "b" && r.To == "X/B");
        Assert.Equal(2, relations.Count(r => r.To == "Y/F"));
        var layout = UmlClassDiagramControl.Layout(classes, relations)!;
        Assert.Equal(relations.Count, layout.Routes.Count);
    }

    /// <summary>Review: membership groups and pipe lists name every target; a negated term names none.</summary>
    [Theory]
    [InlineData("nodeType:Crm/Person", "Crm/Person")]
    [InlineData("nodeType:(Crm/Person OR Crm/Company)", "Crm/Person,Crm/Company")]
    [InlineData("nodeType:Crm/Person|Crm/Company", "Crm/Person,Crm/Company")]
    [InlineData("nodeType:(Crm/Person, Crm/Company) namespace:Crm", "Crm/Person,Crm/Company")]
    [InlineData("-nodeType:Crm/Archived nodeType:Crm/Person", "Crm/Person")]
    [InlineData("namespace:Crm scope:descendants", "")]
    public void NodeTypeValues_ReadEveryPositiveTarget(string query, string expected)
        => Assert.Equal(expected, string.Join(",", UmlSchemaProjection.NodeTypeValues(query)));

    [Fact]
    public void Projection_FeedsTheLayout()
    {
        var (classes, relations) = UmlSchemaProjection.FromContentSchemas(
            [new UmlSchemaInput("Reinsurance/Acceptance", AcceptanceSchema), new UmlSchemaInput("Reinsurance/LineOfBusiness", LineOfBusinessSchema)]);
        var layout = UmlClassDiagramControl.Layout(classes, relations)!;
        Assert.Equal(classes.Count, layout.Boxes.Count);
        Assert.Equal(relations.Count, layout.Routes.Count);
        Assert.True(Box(layout, "Reinsurance/Acceptance").Layer < Box(layout, "Reinsurance/LineOfBusiness").Layer);
    }

    [Fact]
    public void TheRowTypes_RoundTripThroughTheHubSerializer()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var cls = new UmlClass("A/B", "B") { Kind = UmlClassKind.Featured, Attributes = [new UmlAttribute("x", "string", "0..1") { IsReference = true }] };
        var json = JsonSerializer.SerializeToElement(new[] { cls }, options);
        var back = AnalysisRows.Resolve<UmlClass>(json, options);
        Assert.Equal("B", back.Single().Name);
        Assert.Equal("x : string [0..1]", back.Single().Attributes!.Single().Line);
    }
}

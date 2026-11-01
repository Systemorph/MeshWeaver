using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Completion;
using MeshWeaver.Mesh.Services.LanguageServer;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The pure half of query autocomplete: <see cref="QueryCaret"/> classifies the token under the caret
/// exactly as <see cref="QueryParser"/> would read it, <see cref="ContentSchemaFacts"/> reads a content
/// type's JSON Schema, and <see cref="QueryGrammarCompleter"/> turns the two into suggestions. No mesh.
/// </summary>
public class QueryGrammarCompletionTest
{
    private static QueryCaretToken At(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('^');
        caret.Should().BeGreaterThanOrEqualTo(0, "the test text marks the caret with '^'");
        return QueryCaret.At(textWithCaret.Remove(caret, 1), caret);
    }

    private static void ShouldBeSet(System.Collections.Generic.IEnumerable<string> actual, string[] expected, string because = "")
        => actual.Order(System.StringComparer.Ordinal).ToArray()
            .Should().Equal(expected.Order(System.StringComparer.Ordinal).ToArray(), because);

    private static string[] Labels(string textWithCaret, QueryCompletionFacts? facts = null)
        => QueryGrammarCompleter.Complete(At(textWithCaret), facts ?? QueryCompletionFacts.None, 50)
            .Select(i => i.Label).ToArray();

    // ------------------------------------------------------------------ caret classification

    [Theory]
    [InlineData("nodeTy^", QueryCaretKind.Keyword, null, "nodeTy", 0)]
    [InlineData("laptop nodeTy^", QueryCaretKind.Keyword, null, "nodeTy", 7)]
    [InlineData("scope:desc^", QueryCaretKind.Value, "scope", "desc", 6)]
    [InlineData("-nodeType:Crm/O^", QueryCaretKind.Value, "nodeType", "Crm/O", 10)]
    [InlineData("nodeType:A|B^", QueryCaretKind.Value, "nodeType", "B", 11)]
    [InlineData("content.sta^", QueryCaretKind.ContentProperty, null, "sta", 8)]
    [InlineData("content.status:Op^", QueryCaretKind.ContentValue, "status", "Op", 15)]
    [InlineData("content.amount:>=10^", QueryCaretKind.ContentValue, "amount", "10", 17)]
    [InlineData("select:name,ic^", QueryCaretKind.Value, "select", "ic", 12)]
    [InlineData("(name:a OR nodeTy^", QueryCaretKind.Keyword, null, "nodeTy", 11)]
    [InlineData("@Crm/Po^", QueryCaretKind.Reference, null, "@Crm/Po", 0)]
    public void TheCaretTokenIsClassifiedAsTheParserReadsIt(
        string text, QueryCaretKind kind, string? field, string prefix, int replaceStart)
    {
        var token = At(text);

        token.Kind.Should().Be(kind);
        token.Field.Should().Be(field);
        token.Prefix.Should().Be(prefix);
        token.ReplaceStart.Should().Be(replaceStart, "an accepted item replaces exactly what was typed of the part");
        token.ReplaceEnd.Should().Be(text.IndexOf('^'));
    }

    [Fact]
    public void ANegatedTermIsMarked()
        => At("-nodeType:X^").Negated.Should().BeTrue();

    [Fact]
    public void AnOpenQuotedValueIsAValueWhoseReplacementStartsAtTheQuote()
    {
        var token = At("name:x content.title:\"Acme Co^");

        token.Kind.Should().Be(QueryCaretKind.ContentValue);
        token.Field.Should().Be("title");
        token.Prefix.Should().Be("Acme Co");
        token.Quoted.Should().BeTrue();
        token.ReplaceStart.Should().Be("name:x content.title:".Length, "the insert re-writes the whole literal, quote included");
    }

    [Theory]
    [InlineData("\"free text phra^")]
    [InlineData("nodeType:(Crm/A OR Crm/B^")]
    public void NothingIsCompletedInsideAFreeTextQuoteOrAList(string text)
        => At(text).Kind.Should().Be(QueryCaretKind.None);

    // ------------------------------------------------------------------ grammar

    [Fact]
    public void AQualifierPrefixOffersTheQualifierWithItsColon()
    {
        var items = QueryGrammarCompleter.Complete(At("nodeTy^"), QueryCompletionFacts.None);

        items.Should().NotBeEmpty();
        items[0].Label.Should().Be("nodeType:");
        items[0].InsertText.Should().Be("nodeType:");
        items[0].Kind.Should().Be(CompletionKind.Keyword);
        items[0].DescriptionKey.Should().Be("query.completion.nodeType");
    }

    [Fact]
    public void ANegatedTermOffersOnlyQualifiersThatFilter()
    {
        var labels = Labels("-s^");

        labels.Should().NotContain("sort:", "an exclusion of an ordering means nothing");
        labels.Should().NotContain("scope:");
    }

    [Fact]
    public void EveryReservedQualifierIsOffered()
    {
        // The parser's own reserved set — a qualifier it learns must be offered.
        var reserved = QueryParser.ReservedQualifierNames;

        var offered = QueryGrammar.Qualifiers.Select(q => q.Name).ToArray();
        reserved.Should().OnlyContain(r => offered.Contains(r, System.StringComparer.OrdinalIgnoreCase),
            "the completer offers every word QueryParser treats as a reserved qualifier");
    }

    [Theory]
    [InlineData("scope:^", new[] { "children", "descendants", "subtree", "ancestors", "ancestorsAndSelf", "hierarchy", "nextLevel", "exact" })]
    [InlineData("scope:an^", new[] { "ancestors", "ancestorsAndSelf" })]
    [InlineData("partitions:^", new[] { "all" })]
    [InlineData("is:^", new[] { "main", "content" })]
    [InlineData("source:a^", new[] { "activity", "accessed" })]
    public void AQualifierValueOffersExactlyTheValuesTheParserMaps(string text, string[] expected)
        => ShouldBeSet(Labels(text), expected);

    [Fact]
    public void EveryOfferedScopeParsesToTheScopeItNames()
    {
        foreach (var scope in QueryGrammar.ScopeValues)
        {
            var parsed = new QueryParser().Parse($"scope:{scope.Name}");
            parsed.Scope.ToString().ToLowerInvariant().Should().Be(scope.Name.ToLowerInvariant(),
                $"scope:{scope.Name} must not fall through to the parser's silent default");
        }
    }

    [Fact]
    public void SortOffersWellKnownFieldsAndTheContentPropertiesInContext()
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(OpportunitySchema) };

        var labels = Labels("sort:^", facts);

        labels.Should().Contain(["name", "lastModified", "subtreeLastModified-desc", "content.amount", "content.state-desc"]);
        labels.Should().NotContain("content.tags", "an array is not a sort key");
    }

    [Fact]
    public void SortAfterADashOffersTheDirectionsOfThatField()
        => Labels("sort:lastModified-^").Should().Equal("lastModified-desc", "lastModified-asc");

    [Fact]
    public void NodeTypeValuesComeFromTheCatalogAndMatchOnPathOrName()
    {
        var facts = new QueryCompletionFacts
        {
            NodeTypes =
            [
                new QueryNodeFact("Crm/Opportunity", "Opportunity"),
                new QueryNodeFact("Crm/Counterparty", "Counterparty"),
                new QueryNodeFact("Store/Provision", "Provision Request"),
            ],
        };

        ShouldBeSet(Labels("nodeType:Crm/^", facts), ["Crm/Opportunity", "Crm/Counterparty"]);
        Labels("nodeType:Oppo^", facts).Should().Equal("Crm/Opportunity");
        Labels("nodeType:Request^", facts).Should().Equal(new[] { "Store/Provision" }, "a NAME match offers the path");
    }

    [Fact]
    public void AContentPropertyPrefixOffersTheSchemaPropertiesWithTheirColon()
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(OpportunitySchema) };

        var items = QueryGrammarCompleter.Complete(At("content.st^"), facts);

        items.Select(i => i.InsertText).Should().Equal("state:");
        items[0].Description.Should().Contain("Open").And.Contain("Where the deal stands",
            "the enum members and the property's own XML doc are shown beside it");
    }

    [Fact]
    public void ABareWordAlsoOffersTheMatchingContentProperty()
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(OpportunitySchema) };

        Labels("amou^", facts).Should().Contain("content.amount:");
    }

    [Theory]
    [InlineData("content.state:^", new[] { "Open", "Won", "Lost", "OnHold" })]
    [InlineData("content.state:o^", new[] { "Open", "OnHold" })]
    [InlineData("content.isOpen:^", new[] { "true", "false" })]
    public void AContentValueOffersEnumMembersAndBooleans(string text, string[] expected)
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(OpportunitySchema) };

        ShouldBeSet(Labels(text, facts), expected);
    }

    [Fact]
    public void AReferencePropertyOffersTheMembersOfItsQuery()
    {
        var facts = new QueryCompletionFacts
        {
            ContentProperties = ContentSchemaFacts.Parse(OpportunitySchema),
            ReferenceValues =
            [
                new QueryNodeFact("Crm/Offering/Consulting", "Consulting"),
                new QueryNodeFact("Crm/Offering/Hosting Plus", "Hosting Plus"),
            ],
        };

        var items = QueryGrammarCompleter.Complete(At("content.offering:^"), facts);

        ShouldBeSet(items.Select(i => i.InsertText),
            ["Crm/Offering/Consulting", "\"Crm/Offering/Hosting Plus\""],
            "a value with a space is inserted as a quoted literal, never as two terms");
        items.Should().OnlyContain(i => i.Kind == CompletionKind.Reference);
    }

    [Fact]
    public void ACompletionInsideAnOpenQuoteInsertsTheWholeLiteral()
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(OpportunitySchema) };

        var items = QueryGrammarCompleter.Complete(At("content.state:\"O^"), facts);

        items.Select(i => i.InsertText).Should().Contain("\"Open\"");
    }

    [Fact]
    public void WithoutFactsNoValueIsInvented()
    {
        Labels("content.state:^").Should().BeEmpty("no schema was read, so there is no property to complete");
        Labels("nodeType:Crm^").Should().BeEmpty();
    }

    [Fact]
    public void EveryDescriptionAndCategoryKeyIsInTheCatalog()
    {
        var keys = new[]
            {
                QueryGrammar.Qualifiers, QueryGrammar.NodeFields, QueryGrammar.ScopeValues, QueryGrammar.SortFields,
                QueryGrammar.SortDirections, QueryGrammar.LimitValues, QueryGrammar.PartitionValues, QueryGrammar.IsValues,
                QueryGrammar.SourceValues, QueryGrammar.SelectColumns,
            }
            .SelectMany(words => words.Select(w => w.DescriptionKey))
            .Concat([
                QueryGrammarCompleter.QualifierCategory, QueryGrammarCompleter.ValueCategory,
                QueryGrammarCompleter.ContentCategory, QueryGrammarCompleter.NodeTypeCategory,
                QueryGrammarCompleter.NodeCategory, QueryGrammarCompleter.SemanticCategory,
                "query.completion.content", "query.completion.enumMember", "query.completion.boolean",
                QueryGrammar.ContainsKey,
            ])
            .Concat(QueryGrammar.DateComparisons.Select(c => c.DescriptionKey))
            .Distinct()
            .ToArray();

        keys.Should().OnlyContain(k => MeshWeaver.Messaging.LocalizationCatalog.ContainsKey(k),
            "every word the completer shows is described in the viewer's language — a missing key renders the raw key");
    }

    // ------------------------------------------------------------------ contains: field names

    [Fact]
    public void ABareWordFindsEveryFieldWhoseNameContainsIt()
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(DatesSchema) };

        var labels = Labels("date^", facts);

        labels.Should().Contain("createdDate:", "a node field is found by a camelCase word part of its name");
        labels.Should().Contain(["content.dueDate:", "content.inception:", "content.lastActivityAt:", "content.statusChangedAt:"],
            "a content property is found by its name, and a date-valued one also by the kind of value it holds");
        labels.Should().NotContain("content.title:");
    }

    [Fact]
    public void AWordPartOfACamelCaseNameFindsTheField()
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(DatesSchema) };

        Labels("content.due^", facts).Should().Contain("followUpDue:");
        Labels("content.time^", facts).Should().Contain("creationTime:");
        Labels("time^", facts).Should().Contain("content.creationTime:");
    }

    [Fact]
    public void APrefixMatchRanksAboveAWordPartAboveAnInfix()
        => Labels("content.due^", new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(DatesSchema) })
            .Should().Equal(new[] { "dueDate:", "followUpDue:", "overdue:" },
                "a name that starts with the word beats one where it starts a word part, which beats one that merely contains it");

    [Theory]
    [InlineData("date", "createdDate", 2)]
    [InlineData("time", "creationTime", 2)]
    [InlineData("last", "lastModified", 3)]
    [InlineData("due", "overdue", 1)]
    [InlineData("zz", "lastModified", -1)]
    public void FieldMatchIsAContainsMatchRankedByWhereItHits(string typed, string field, int expected)
        => QueryGrammarCompleter.FieldMatch(typed, field).Should().Be(expected);

    [Fact]
    public void AQualifierValueStaysAPrefixOrSegmentMatch()
        => ShouldBeSet(Labels("scope:an^"), ["ancestors", "ancestorsAndSelf"]);

    // ------------------------------------------------------------------ contains: reference values

    [Fact]
    public void AReferenceValueIsMatchedByContainsOnItsNameAndLabelledWithIt()
    {
        var facts = new QueryCompletionFacts
        {
            ContentProperties = ContentSchemaFacts.Parse(SubmissionSchema),
            ReferenceValues =
            [
                new QueryNodeFact("Reinsurance/Cedent/Shanghai", "Shanghai Re"),
                new QueryNodeFact("Reinsurance/Cedent/Acme", "Acme Mutual"),
            ],
        };

        var items = QueryGrammarCompleter.Complete(At("content.cedent:hai^"), facts);

        var references = items.Where(i => i.Kind == CompletionKind.Reference).ToArray();
        references.Select(i => (i.Label, i.InsertText)).Should().Equal(new[] { ("Shanghai Re", "Reinsurance/Cedent/Shanghai") },
            "a match in the middle of the name counts; the label is the name, the insert the stored path");
    }

    [Fact]
    public void AReferenceValueIsMatchedByContainsOnItsPath()
    {
        var facts = new QueryCompletionFacts
        {
            ContentProperties = ContentSchemaFacts.Parse(SubmissionSchema),
            ReferenceValues =
            [
                new QueryNodeFact("LoneStarMutual/Profile", "Lone Star Mutual"),
                new QueryNodeFact("Reinsurance/Cedent/Acme", "Acme Mutual"),
            ],
        };

        QueryGrammarCompleter.Complete(At("content.cedent:starmut^"), facts)
            .Where(i => i.Kind == CompletionKind.Reference)
            .Select(i => i.InsertText)
            .Should().Equal(new[] { "LoneStarMutual/Profile" }, "the path contains what was typed even though the name (with spaces) does not");
    }

    [Fact]
    public void ATypedValueOffersTheLiteralContainsFilterFirst()
    {
        var facts = new QueryCompletionFacts
        {
            ContentProperties = ContentSchemaFacts.Parse(SubmissionSchema),
            ReferenceValues = [new QueryNodeFact("Reinsurance/Cedent/Shanghai", "Shanghai Re")],
        };

        var items = QueryGrammarCompleter.Complete(At("content.cedent:ha^"), facts);

        items[0].InsertText.Should().Be("*ha*");
        items[0].DescriptionKey.Should().Be(QueryGrammar.ContainsKey);
        var parsed = new QueryParser().Parse("content.cedent:" + items[0].InsertText);
        parsed.Filter.Should().BeOfType<QueryComparison>()
            .Which.Condition.Operator.Should().Be(QueryOperator.Like, "*x* is the language's contains spelling");
    }

    [Theory]
    [InlineData("content.cedent:^")]
    [InlineData("content.cedent:\"ha^")]
    [InlineData("content.cedent:h*a^")]
    [InlineData("content.isOpen:t^")]
    public void TheContainsFilterIsNotOfferedForAnEmptyQuotedSyntacticOrNonTextValue(string text)
    {
        var facts = new QueryCompletionFacts
        {
            ContentProperties = [.. ContentSchemaFacts.Parse(SubmissionSchema), .. ContentSchemaFacts.Parse(OpportunitySchema)],
        };

        QueryGrammarCompleter.Complete(At(text), facts).Should().NotContain(i => i.DescriptionKey == QueryGrammar.ContainsKey);
    }

    // ------------------------------------------------------------------ dates

    private static readonly System.DateOnly Today = new(2026, 11, 1);

    [Theory]
    [InlineData("content.followUpDue:^")]
    [InlineData("content.inception:^")]
    [InlineData("lastModified:^")]
    [InlineData("createdDate:^")]
    public void ADateFieldOffersTheComparisonsAgainstToday(string text)
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(DatesSchema), Today = Today };

        var items = QueryGrammarCompleter.Complete(At(text), facts);

        items.Select(i => i.InsertText).Should().Equal(">2026-11-01", "<2026-11-01", ">=2026-11-01", "<=2026-11-01");
        items.Select(i => i.DescriptionKey).Should().Equal(
            "query.completion.date.after", "query.completion.date.before",
            "query.completion.date.onOrAfter", "query.completion.date.onOrBefore");
        new QueryParser().Parse(text.Replace("^", "") + items[2].InsertText).Filter.Should().BeOfType<QueryComparison>()
            .Which.Condition.Operator.Should().Be(QueryOperator.GreaterOrEqual);
    }

    [Theory]
    [InlineData("content.followUpDue:>^", new[] { "2026-11-01", "=2026-11-01" })]
    [InlineData("content.followUpDue:<=^", new[] { "2026-11-01" })]
    public void AfterATypedOperatorTheDateCompletesThatOperator(string text, string[] expected)
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(DatesSchema), Today = Today };

        QueryGrammarCompleter.Complete(At(text), facts).Select(i => i.InsertText).Should().Equal(expected);
    }

    [Fact]
    public void ATypedOperatorIsCarriedByTheCaretToken()
    {
        var token = At("content.followUpDue:>=^");

        token.Operator.Should().Be(">=");
        token.Prefix.Should().BeEmpty();
        token.ReplaceStart.Should().Be("content.followUpDue:>=".Length, "the operator stays; only the value is replaced");
    }

    [Fact]
    public void ADateWithSomethingTypedOffersNoComparison()
    {
        var facts = new QueryCompletionFacts { ContentProperties = ContentSchemaFacts.Parse(DatesSchema), Today = Today };

        Labels("content.followUpDue:2026^", facts).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ reference legs

    [Fact]
    public void EveryNonTemplateReferenceLegIsReadFilteredByNameAndById()
    {
        var prop = new ContentPropertyFact("cedent", "string", [], null,
        [
            "namespace:Reinsurance scope:descendants nodeType:Reinsurance/Cedent",
            "namespace:ReinsuranceDemo scope:descendants nodeType:Reinsurance/Cedent",
            "namespace:{node.namespace} nodeType:Reinsurance/Cedent",
        ]);

        var reads = QueryCompleter.ReferenceLegReads(prop, "ha");

        reads.Should().HaveCount(4, "two non-template legs, each read by name and by id; the template leg needs a record");
        reads.Should().Contain(r => r.StartsWith("namespace:ReinsuranceDemo ") && r.Contains("name:*ha*"));
        reads.Should().Contain(r => r.StartsWith("namespace:ReinsuranceDemo ") && r.Contains("id:*ha*"));
        reads.Should().NotContain(r => r.Contains('{'));
    }

    // ------------------------------------------------------------------ schema reading

    [Fact]
    public void TheSchemaReaderReadsAPolymorphicContentSchema()
    {
        var props = ContentSchemaFacts.Parse(OpportunitySchema);

        props.Select(p => p.Name).Should().Equal("state", "amount", "isOpen", "offering", "tags");
        props.Single(p => p.Name == "state").EnumValues.Should().Equal("Open", "Won", "Lost", "OnHold");
        props.Single(p => p.Name == "amount").IsNumber.Should().BeTrue();
        props.Single(p => p.Name == "isOpen").IsBoolean.Should().BeTrue();
        props.Single(p => p.Name == "offering").ReferenceQueries.Should().Equal("nodeType:Crm/Offering");
        props.Single(p => p.Name == "offering").JsonType.Should().Be("string", "a nullable type array names its non-null type");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void ANonSchemaYieldsNoProperties(string? schema)
        => ContentSchemaFacts.Parse(schema).Should().BeEmpty();

    // ------------------------------------------------------------------ context + semantic query

    [Fact]
    public void TheContextNodeTypesComeFromTheHiddenQueryAndTheVisibleText()
        => QueryCompleter.ContextNodeTypes("namespace:acme nodeType:Crm/Opportunity\nnodeType:Crm/Offer|Crm/Contact", "nodeType:Crm/Opportunity -nodeType:Crm/Lead")
            .Should().Equal("Crm/Opportunity", "Crm/Offer", "Crm/Contact");

    [Fact]
    public void TheSemanticQueryIsAnchoredOrDeclaredMeshWide()
    {
        QueryCompleter.SemanticQuery(new QueryCompletionRequest("laptop pri", 10, "namespace:acme scope:descendants sort:name limit:5"))
            .Should().Be("namespace:acme scope:descendants laptop pri select:path,name,nodeType,icon",
                "the box's own anchor scopes the vector search, and its sort/limit/select do not ride along");
        QueryCompleter.SemanticQuery(new QueryCompletionRequest("laptop pri", 10))
            .Should().Contain("partitions:all", "an unanchored query would be refused, not answered");
    }

    /// <summary>A content type with date-valued and camelCase properties.</summary>
    private const string DatesSchema = """
        {
          "properties": {
            "title": { "type": "string" },
            "dueDate": { "type": "string", "format": "date" },
            "followUpDue": { "type": ["string","null"], "format": "date-time" },
            "overdue": { "type": "boolean" },
            "inception": { "type": "string", "format": "date" },
            "lastActivityAt": { "type": ["string","null"], "format": "date-time" },
            "statusChangedAt": { "type": ["string","null"], "format": "date-time" },
            "creationTime": { "type": "integer" }
          }
        }
        """;

    /// <summary>A submission: a cedent reference declared per partition.</summary>
    private const string SubmissionSchema = """
        {
          "properties": {
            "cedent": { "type": ["string","null"], "x-meshNode": [
              "namespace:Reinsurance scope:descendants nodeType:Reinsurance/Cedent",
              "namespace:ReinsuranceDemo scope:descendants nodeType:Reinsurance/Cedent"] }
          }
        }
        """;

    /// <summary>The shape <c>@Crm/Opportunity/schema/</c> answers: polymorphic anyOf, nullable type arrays.</summary>
    private const string OpportunitySchema = """
        {
          "type": ["object","null"],
          "required": ["$type"],
          "anyOf": [{
            "properties": {
              "$type": { "const": "Opportunity" },
              "state": { "enum": ["Open","Won","Lost","OnHold"], "description": "Where the deal stands." },
              "amount": { "type": ["number","null"] },
              "isOpen": { "type": "boolean" },
              "offering": { "type": ["string","null"], "x-meshNode": ["nodeType:Crm/Offering"] },
              "tags": { "type": "array", "items": { "type": "string" } }
            }
          }]
        }
        """;
}

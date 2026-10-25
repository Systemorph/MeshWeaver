using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Fixture;
using MeshWeaver.Yaml;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The YAML reader against the OFFICIAL YAML test suite (<c>yaml/yaml-test-suite</c>, the
/// <c>data-2022-01-17</c> release, vendored under <c>YamlTestSuite/</c> with its MIT licence).
///
/// <para>Every case must, in STRICT mode, either produce exactly its <c>in.json</c> (numbers compared
/// by value, mapping keys order-free), be refused with <see cref="YamlParseException"/> when the
/// suite marks it as an error, or — valid YAML with no JSON form — parse without an exception. A
/// case that cannot is listed in <see cref="KnownUnsupported"/> WITH ITS REASON, and the doc page
/// <c>Doc/Architecture/TolerantYaml</c> enumerates the same list. The list is a ratchet in both
/// directions: a listed case that starts passing fails this test until its line is removed.</para>
///
/// <para>TOLERANT mode must give the same answer on every valid case. On an error case it may
/// accept the text only where <see cref="ToleratedErrors"/> names the deliberate leniency — again a
/// two-way ratchet.</para>
/// </summary>
public class YamlTestSuiteConformanceTest(ITestOutputHelper output)
{
    /// <summary>Cases strict mode does not get right, each with the reason it is out of scope.</summary>
    private const string Tabs = "Tabs: the reader refuses every tab in the leading whitespace of a content line and accepts tabs between tokens; YAML 1.2 allows a tab after the indentation in some positions and forbids it in others, and that finer rule is not implemented";
    private const string EndOfInputBreak = "End of input without a final line break: the suite's reference output treats it as a break kept by clip/keep chomping; this reader — like libyaml (Ruby's Psych) and YamlDotNet — adds no break that is not in the text";
    private const string DuplicateKeys = "Duplicate keys: strict mode refuses a repeated key (a JSON object cannot hold two); the suite treats key uniqueness as a check above the syntax";
    private const string ColonInPlainValue = "A ': ' inside a plain value is read as text (the documented leniency: a mapping cannot start mid-line)";
    private const string SequenceOnKeyLine = "A block sequence on the line of its key or properties is read as text / a sequence";
    private const string RepeatedProperties = "Repeated properties (the last wins) and properties on an alias are accepted";
    private const string UnknownEscape = "An unknown escape in a double-quoted scalar is kept verbatim (backslash and character)";
    private const string BlockScalarLeadingLine = "A leading empty line of a block scalar indented past its content is read as empty";
    private const string ContinuationIndent = "Continuation lines of a flow collection or quoted scalar are not required to be indented past their block";
    private const string CommentWithoutSpace = "A '#' straight after a token is read as a comment";
    private const string DirectivesNotValidated = "Directives are not validated (the JSON tree has no use for them); a directive without a document, extra words, a duplicate %YAML or an undeclared tag handle are accepted";
    private const string PercentStarter = "A plain scalar may start with '%' (here: a second %YAML without a preceding '...', read as text)";
    private const string MultiLineFlowKey = "An implicit flow key spanning lines is accepted";
    private const string LoneDashInFlow = "A lone '-' in a flow collection is read as the string \"-\"";
    private const string CommaStarter = "A plain scalar may start with ','";

    internal static readonly ImmutableDictionary<string, string> KnownUnsupported = new Dictionary<string, string>
    {
        ["2JQS"] = DuplicateKeys,
        ["6CA3"] = Tabs,
        ["DK95/00"] = Tabs,
        ["DK95/01"] = Tabs,
        ["JEF9/02"] = EndOfInputBreak,
        ["L24T/01"] = EndOfInputBreak,
        ["Q5MG"] = Tabs,
        ["X38W"] = DuplicateKeys,
        ["Y79Y/000"] = Tabs,
        ["Y79Y/003"] = Tabs,
        ["Y79Y/004"] = Tabs,
        ["Y79Y/005"] = Tabs,
        ["Y79Y/006"] = Tabs,
        ["Y79Y/007"] = Tabs,
        ["Y79Y/008"] = Tabs,
        ["Y79Y/009"] = Tabs,
    }.ToImmutableDictionary();

    /// <summary>Error cases the TOLERANT reader deliberately accepts, each naming the leniency.</summary>
    internal static readonly ImmutableDictionary<string, string> ToleratedErrors = new Dictionary<string, string>
    {
        ["2CMS"] = ColonInPlainValue,
        ["4JVG"] = RepeatedProperties,
        ["55WF"] = UnknownEscape,
        ["5LLU"] = BlockScalarLeadingLine,
        ["5U3A"] = SequenceOnKeyLine,
        ["9C9N"] = ContinuationIndent,
        ["9JBA"] = CommentWithoutSpace,
        ["9KBC"] = ColonInPlainValue,
        ["9MMA"] = DirectivesNotValidated,
        ["B63P"] = DirectivesNotValidated,
        ["CVW2"] = CommentWithoutSpace,
        ["CXX2"] = ColonInPlainValue,
        ["DK4H"] = MultiLineFlowKey,
        ["DK95/01"] = Tabs,
        ["EW3V"] = ColonInPlainValue,
        ["G5U8"] = LoneDashInFlow,
        ["H7TQ"] = DirectivesNotValidated,
        ["HRE5"] = UnknownEscape,
        ["HU3P"] = ColonInPlainValue,
        ["MUS6/00"] = DirectivesNotValidated,
        ["MUS6/01"] = PercentStarter,
        ["QB6E"] = ContinuationIndent,
        ["QLJ7"] = DirectivesNotValidated,
        ["S98Z"] = BlockScalarLeadingLine,
        ["SF5V"] = DirectivesNotValidated,
        ["SR86"] = RepeatedProperties,
        ["SU5Z"] = CommentWithoutSpace,
        ["SU74"] = RepeatedProperties,
        ["SY6V"] = SequenceOnKeyLine,
        ["U99R"] = CommaStarter,
        ["VJP3/00"] = ContinuationIndent,
        ["W9L4"] = BlockScalarLeadingLine,
        ["X4QW"] = CommentWithoutSpace,
        ["Y79Y/000"] = Tabs,
        ["Y79Y/003"] = Tabs,
        ["Y79Y/004"] = Tabs,
        ["Y79Y/005"] = Tabs,
        ["Y79Y/006"] = Tabs,
        ["Y79Y/007"] = Tabs,
        ["Y79Y/008"] = Tabs,
        ["Y79Y/009"] = Tabs,
        ["YJV2"] = LoneDashInFlow,
        ["ZCZ6"] = ColonInPlainValue,
        ["ZXT5"] = MultiLineFlowKey,
    }.ToImmutableDictionary();

    private enum Outcome { Matched, RefusedCorrectly, ParsedNoJsonForm, Mismatch, WronglyRefused, WronglyAccepted, Crashed, TooSlow }

    private static bool IsPass(Outcome o) => o is Outcome.Matched or Outcome.RefusedCorrectly or Outcome.ParsedNoJsonForm;

    private sealed record Case(string Id, string Name, string Yaml, string? Json, bool Error);

    private static ImmutableList<Case> Cases()
    {
        var root = Path.Combine(RepoPaths.Root, "test", "MeshWeaver.Hosting.Test", "YamlTestSuite", "data");
        return Directory.EnumerateFiles(root, "in.yaml", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Select(dir => new Case(
                Path.GetRelativePath(root, dir!).Replace(Path.DirectorySeparatorChar, '/'),
                File.Exists(Path.Combine(dir!, "name")) ? File.ReadAllText(Path.Combine(dir!, "name")).Trim() : "",
                File.ReadAllText(Path.Combine(dir!, "in.yaml"), Encoding.UTF8),
                File.Exists(Path.Combine(dir!, "in.json")) ? File.ReadAllText(Path.Combine(dir!, "in.json"), Encoding.UTF8) : null,
                File.Exists(Path.Combine(dir!, "error"))))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToImmutableList();
    }

    private static (Outcome Outcome, string Detail) Run(Case c, YamlParseOptions mode)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<JsonNode?> documents;
        try
        {
            documents = TolerantYaml.ParseAll(c.Yaml, mode);
        }
        catch (YamlParseException ex)
        {
            return c.Error ? (Outcome.RefusedCorrectly, ex.Message) : (Outcome.WronglyRefused, ex.Message);
        }
        catch (Exception ex)
        {
            return (Outcome.Crashed, $"{ex.GetType().Name}: {ex.Message}");
        }
        if (watch.Elapsed > TimeSpan.FromSeconds(2))
            return (Outcome.TooSlow, watch.Elapsed.ToString());
        if (c.Error)
            return (Outcome.WronglyAccepted, Show(documents));
        if (c.Json is null)
            return (Outcome.ParsedNoJsonForm, "");
        var expected = ExpectedDocuments(c.Json);
        var match = expected.Count == documents.Count
                    && expected.Zip(documents).All(p => Same(p.First, p.Second));
        return match ? (Outcome.Matched, "") : (Outcome.Mismatch, $"expected {Show(expected)} got {Show(documents)}");
    }

    private static string Show(IEnumerable<JsonNode?> documents) =>
        "[" + string.Join(" | ", documents.Select(d => d?.ToJsonString() ?? "null")) + "]";

    private static ImmutableList<JsonNode?> ExpectedDocuments(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowMultipleValues = true });
        var documents = ImmutableList<JsonNode?>.Empty;
        while (reader.Read())
            documents = documents.Add(JsonNode.Parse(ref reader));
        return documents;
    }

    /// <summary>JSON equality with numbers compared by VALUE (<c>1.0</c> = <c>1</c>) and mapping keys in any order.</summary>
    private static bool Same(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (JsonObject x, JsonObject y):
                return x.Count == y.Count && x.All(kv => y.TryGetPropertyValue(kv.Key, out var v) && Same(kv.Value, v));
            case (JsonArray x, JsonArray y):
                return x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second));
            case (JsonValue x, JsonValue y):
                var kx = x.GetValueKind();
                var ky = y.GetValueKind();
                if (kx == JsonValueKind.Number && ky == JsonValueKind.Number)
                    return double.Parse(x.ToJsonString(), CultureInfo.InvariantCulture)
                           == double.Parse(y.ToJsonString(), CultureInfo.InvariantCulture);
                return kx == ky && x.ToJsonString() == y.ToJsonString();
            default:
                return false;
        }
    }

    [Fact]
    public void EveryCase_MatchesIsRefusedOrIsAReasonedException_InBothModes()
    {
        var cases = Cases();
        Assert.True(cases.Count > 350, $"only {cases.Count} suite cases found — the vendored data moved");

        var failures = new List<string>();
        var counts = new Dictionary<Outcome, int>();
        var tolerantAccepted = 0;
        foreach (var c in cases)
        {
            var (strictOutcome, strictDetail) = Run(c, YamlParseOptions.StrictSpec);
            counts[strictOutcome] = counts.GetValueOrDefault(strictOutcome) + 1;
            if (strictOutcome is Outcome.Crashed or Outcome.TooSlow)
                failures.Add($"{c.Id} ({c.Name}) strict: {strictOutcome} {strictDetail}");
            else if (KnownUnsupported.ContainsKey(c.Id))
            {
                if (IsPass(strictOutcome))
                    failures.Add($"{c.Id} ({c.Name}): listed in KnownUnsupported but now passes ({strictOutcome}) — remove its line");
            }
            else if (!IsPass(strictOutcome))
                failures.Add($"{c.Id} ({c.Name}) strict: {strictOutcome} {strictDetail}");

            var (tolerantOutcome, tolerantDetail) = Run(c, YamlParseOptions.Tolerant);
            if (tolerantOutcome is Outcome.Crashed or Outcome.TooSlow)
                failures.Add($"{c.Id} ({c.Name}) tolerant: {tolerantOutcome} {tolerantDetail}");
            else if (c.Error)
            {
                var tolerated = ToleratedErrors.ContainsKey(c.Id);
                if (tolerantOutcome == Outcome.WronglyAccepted)
                {
                    tolerantAccepted++;
                    if (!tolerated)
                        failures.Add($"{c.Id} ({c.Name}) tolerant: accepts an error case not listed in ToleratedErrors: {tolerantDetail}");
                }
                else if (tolerated)
                    failures.Add($"{c.Id} ({c.Name}): listed in ToleratedErrors but the tolerant reader refuses it — remove its line");
            }
            else if (!KnownUnsupported.ContainsKey(c.Id) && tolerantOutcome != strictOutcome)
                failures.Add($"{c.Id} ({c.Name}) tolerant differs from strict on a valid case: {tolerantOutcome} {tolerantDetail}");
        }

        output.WriteLine($"cases={cases.Count} " + string.Join(" ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))
                         + $" knownUnsupported={KnownUnsupported.Count} toleratedErrors={tolerantAccepted}");
        Assert.True(failures.Count == 0, $"{failures.Count} conformance failures:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void TheExceptionLists_NameOnlyRealCases()
    {
        var ids = Cases().Select(c => c.Id).ToImmutableHashSet();
        Assert.Empty(KnownUnsupported.Keys.Where(k => !ids.Contains(k)));
        Assert.Empty(ToleratedErrors.Keys.Where(k => !ids.Contains(k)));
        Assert.All(KnownUnsupported.Values.Concat(ToleratedErrors.Values), reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }
}

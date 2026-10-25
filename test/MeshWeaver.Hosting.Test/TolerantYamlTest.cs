using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Yaml;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The platform's own YAML reader and writer (<see cref="TolerantYaml"/>,
/// <c>Doc/Architecture/TolerantYaml</c>): every construct the grammar covers, the JSON-style
/// binding tolerances, the cases the previous third-party reader refused, the strict mode, and
/// the writer's round trip.
/// </summary>
public class TolerantYamlTest
{
    private static JsonObject Map(string yaml) => Assert.IsType<JsonObject>(TolerantYaml.Parse(yaml));

    private static string? Text(JsonNode? node) => TolerantYaml.ScalarText(node);

    private static JsonValueKind Kind(JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;

    // ───────────────────────────── block structure ─────────────────────────────

    [Fact]
    public void BlockMapping_NestedByIndentation()
    {
        var map = Map("""
            a: 1
            b:
              c: two
              d:
                e: true
            f: last
            """);
        Assert.Equal(1, map["a"]!.GetValue<int>());
        Assert.Equal("two", Text(map["b"]!["c"]));
        Assert.True(map["b"]!["d"]!["e"]!.GetValue<bool>());
        Assert.Equal("last", Text(map["f"]));
        Assert.Equal(["a", "b", "f"], map.Select(kv => kv.Key));
    }

    [Fact]
    public void BlockSequence_IndentedAndIndentlessUnderAKey()
    {
        var map = Map("""
            indented:
              - a
              - b
            indentless:
            - c
            - d
            after: x
            """);
        Assert.Equal(["a", "b"], map["indented"]!.AsArray().Select(Text));
        Assert.Equal(["c", "d"], map["indentless"]!.AsArray().Select(Text));
        Assert.Equal("x", Text(map["after"]));
    }

    [Fact]
    public void SequenceOfMappings_CompactForm()
    {
        var map = Map("""
            steps:
              - name: checkout
                uses: actions/checkout@v4
              - name: build
                run: dotnet build
                env:
                  A: 1
              -
                name: on the next line
            """);
        var steps = map["steps"]!.AsArray();
        Assert.Equal(3, steps.Count);
        Assert.Equal("actions/checkout@v4", Text(steps[0]!["uses"]));
        Assert.Equal("dotnet build", Text(steps[1]!["run"]));
        Assert.Equal("1", Text(steps[1]!["env"]!["A"]));
        Assert.Equal("on the next line", Text(steps[2]!["name"]));
    }

    [Fact]
    public void NestedSequences()
    {
        var root = Assert.IsType<JsonArray>(TolerantYaml.Parse("- - a\n  - b\n- - c\n"));
        Assert.Equal("a", Text(root[0]![0]));
        Assert.Equal("b", Text(root[0]![1]));
        Assert.Equal("c", Text(root[1]![0]));
    }

    [Fact]
    public void EmptyValues_AreNull()
    {
        var map = Map("a:\nb: ~\nc: null\nd: \"\"\ne:\n  # only a comment\nf: x\n");
        Assert.Null(map["a"]);
        Assert.Null(map["b"]);
        Assert.Null(map["c"]);
        Assert.Equal("", Text(map["d"]));
        Assert.True(map.ContainsKey("e"));
        Assert.Null(map["e"]);
        Assert.Equal("x", Text(map["f"]));
    }

    // ───────────────────────────── scalars ─────────────────────────────

    [Fact]
    public void PlainScalars_ResolveWithTheCoreSchema()
    {
        var map = Map("""
            int: 42
            neg: -7
            plus: +3
            zeros: 007
            hex: 0x1F
            oct: 0o17
            float: 1.10
            exp: 1e3
            dot: .5
            inf: .inf
            nan: .nan
            yes11: yes
            on: on
            t: True
            f: FALSE
            date: 2026-01-01
            version: 1.2.3
            """);
        Assert.Equal(JsonValueKind.Number, Kind(map["int"]));
        Assert.Equal(42, map["int"]!.GetValue<int>());
        Assert.Equal(-7, map["neg"]!.GetValue<int>());
        Assert.Equal(3, map["plus"]!.GetValue<int>());
        Assert.Equal(7, map["zeros"]!.GetValue<int>());
        Assert.Equal(31, map["hex"]!.GetValue<int>());
        Assert.Equal(15, map["oct"]!.GetValue<int>());
        Assert.Equal("1.10", Text(map["float"])); // digits kept exactly as written
        Assert.Equal(1000d, map["exp"]!.GetValue<double>());
        Assert.Equal(0.5d, map["dot"]!.GetValue<double>());
        Assert.Equal("Infinity", Text(map["inf"]));
        Assert.Equal("NaN", Text(map["nan"]));
        // YAML 1.2: yes/on are strings, only true/false are booleans.
        Assert.Equal(JsonValueKind.String, Kind(map["yes11"]));
        Assert.Equal("on", Text(map["on"]));
        Assert.Equal(JsonValueKind.True, Kind(map["t"]));
        Assert.Equal(JsonValueKind.False, Kind(map["f"]));
        Assert.Equal("2026-01-01", Text(map["date"]));
        Assert.Equal("1.2.3", Text(map["version"]));
    }

    [Fact]
    public void QuotedScalars_AreAlwaysStrings_WithEscapes()
    {
        var map = Map("""
            a: "123"
            b: 'true'
            c: "tab\there \"q\" \\ \u00e9 \x41 \U0001F600"
            d: 'it''s'
            e: "line\nbreak"
            f: ""
            g: "a # not a comment"
            """);
        Assert.Equal(JsonValueKind.String, Kind(map["a"]));
        Assert.Equal("123", Text(map["a"]));
        Assert.Equal("true", Text(map["b"]));
        Assert.Equal("tab\there \"q\" \\ é A 😀", Text(map["c"]));
        Assert.Equal("it's", Text(map["d"]));
        Assert.Equal("line\nbreak", Text(map["e"]));
        Assert.Equal("", Text(map["f"]));
        Assert.Equal("a # not a comment", Text(map["g"]));
    }

    [Fact]
    public void QuotedScalars_FoldAcrossLines()
    {
        var map = Map("a: \"one\n  two\n\n  three\"\nb: 'x\n  y'\nc: \"joined\\\n  here\"\n");
        Assert.Equal("one two\nthree", Text(map["a"]));
        Assert.Equal("x y", Text(map["b"]));
        Assert.Equal("joinedhere", Text(map["c"]));
    }

    [Fact]
    public void MultiLinePlainScalar_Folds()
    {
        var map = Map("""
            description: first line
              second line

              after a blank
            next: x
            """);
        Assert.Equal("first line second line\nafter a blank", Text(map["description"]));
        Assert.Equal("x", Text(map["next"]));
    }

    [Fact]
    public void Comments_FullLineAndTrailing()
    {
        var map = Map("""
            # leading comment
            a: 1 # trailing
            b: text#not-a-comment
            # between
            c: "q" # after quotes
            """);
        Assert.Equal("1", Text(map["a"]));
        Assert.Equal("text#not-a-comment", Text(map["b"]));
        Assert.Equal("q", Text(map["c"]));
        Assert.Equal(3, map.Count);
    }

    [Fact]
    public void KeysWithDotsQuotesAndUnicode()
    {
        var map = Map("my.dotted.key: 1\n\"quoted key\": 2\n'single': 3\nré-sumé: 4\nurl: http://x.y/z\n");
        Assert.Equal(["my.dotted.key", "quoted key", "single", "ré-sumé", "url"], map.Select(kv => kv.Key));
        Assert.Equal("http://x.y/z", Text(map["url"]));
    }

    // ───────────────────────────── block scalars ─────────────────────────────

    [Fact]
    public void LiteralBlockScalar_WithChomping()
    {
        var map = Map("clip: |\n  a\n   b\n\nstrip: |-\n  a\n  b\n\nkeep: |+\n  a\n\n\nlast: x\n");
        Assert.Equal("a\n b\n", Text(map["clip"]));
        Assert.Equal("a\nb", Text(map["strip"]));
        Assert.Equal("a\n\n\n", Text(map["keep"]));
        Assert.Equal("x", Text(map["last"]));
    }

    [Fact]
    public void FoldedBlockScalar_FoldsLinesAndKeepsMoreIndented()
    {
        var map = Map("f: >\n  one\n  two\n\n  three\n    indented\n  four\nn: x\n");
        Assert.Equal("one two\nthree\n  indented\nfour\n", Text(map["f"]));
        var stripped = Map("f: >-\n  a\n  b\n");
        Assert.Equal("a b", Text(stripped["f"]));
    }

    [Fact]
    public void BlockScalar_ExplicitIndentation_AndInASequence()
    {
        var map = Map("a: |2\n    leading spaces\n  normal\nb:\n  - |\n    in a sequence\n  - x\n");
        Assert.Equal("  leading spaces\nnormal\n", Text(map["a"]));
        Assert.Equal("in a sequence\n", Text(map["b"]![0]));
        Assert.Equal("x", Text(map["b"]![1]));
    }

    [Fact]
    public void BlockScalar_KeepsHashesAndColons()
    {
        var map = Map("run: |\n  set -e # not a comment\n  echo \"a: b\"\nnext: 1\n");
        Assert.Equal("set -e # not a comment\necho \"a: b\"\n", Text(map["run"]));
    }

    // ───────────────────────────── flow collections ─────────────────────────────

    [Fact]
    public void FlowCollections()
    {
        var map = Map("""
            seq: [a, "b c", 3, [x, y], {k: v}]
            map: {a: 1, "b": [true, null], c: }
            multi: [
              one,  # comment
              two,
            ]
            pairs: [a: 1, b: 2]
            json: {"x":1,"y":"z"}
            empty: []
            emptyMap: {}
            """);
        var seq = map["seq"]!.AsArray();
        Assert.Equal("a", Text(seq[0]));
        Assert.Equal("b c", Text(seq[1]));
        Assert.Equal(3, seq[2]!.GetValue<int>());
        Assert.Equal("y", Text(seq[3]![1]));
        Assert.Equal("v", Text(seq[4]!["k"]));
        Assert.Equal(1, map["map"]!["a"]!.GetValue<int>());
        Assert.Null(map["map"]!["b"]![1]);
        Assert.Null(map["map"]!["c"]);
        Assert.Equal(["one", "two"], map["multi"]!.AsArray().Select(Text));
        Assert.Equal("2", Text(map["pairs"]![1]!["b"]));
        Assert.Equal("z", Text(map["json"]!["y"]));
        Assert.Empty(map["empty"]!.AsArray());
        Assert.Empty(map["emptyMap"]!.AsObject());
    }

    [Fact]
    public void FlowScalars_KeepUrlsAndExpressions()
    {
        var map = Map("needs: [build, test]\nlinks: [http://a.b/c, \"${{ x }}\"]\n");
        Assert.Equal(["build", "test"], map["needs"]!.AsArray().Select(Text));
        Assert.Equal("http://a.b/c", Text(map["links"]![0]));
        Assert.Equal("${{ x }}", Text(map["links"]![1]));
    }

    // ───────────────────────────── documents, anchors, tags ─────────────────────────────

    [Fact]
    public void DocumentMarkers()
    {
        Assert.Equal("v", Text(Map("---\na: v\n...\n")["a"]));
        var all = TolerantYaml.ParseAll("--- \na: 1\n---\nb: 2\n...\n---\n- x\n");
        Assert.Equal(3, all.Count);
        Assert.Equal("2", Text(all[1]!["b"]));
        Assert.Equal("x", Text(all[2]![0]));
        Assert.Null(TolerantYaml.Parse(""));
        Assert.Null(TolerantYaml.Parse("# just a comment\n"));
        Assert.Equal("scalar", Text(TolerantYaml.Parse("--- scalar\n")));
    }

    [Fact]
    public void AnchorsAliasesAndMerge()
    {
        var map = Map("""
            defaults: &defaults
              timeout: 5
              retries: 2
            name: &n shared
            job:
              <<: *defaults
              retries: 9
            other: *n
            list: [*n, *n]
            multi:
              <<: [*defaults, {extra: 1}]
            """);
        Assert.Equal(5, map["job"]!["timeout"]!.GetValue<int>());
        Assert.Equal(9, map["job"]!["retries"]!.GetValue<int>()); // explicit key wins over the merge
        Assert.False(map["job"]!.AsObject().ContainsKey("<<"));
        Assert.Equal("shared", Text(map["other"]));
        Assert.Equal(["shared", "shared"], map["list"]!.AsArray().Select(Text));
        Assert.Equal(1, map["multi"]!["extra"]!.GetValue<int>());
        Assert.Equal(2, map["multi"]!["retries"]!.GetValue<int>());
    }

    [Fact]
    public void CoreTags_ChangeResolution_OtherTagsAreIgnored()
    {
        var map = Map("a: !!str 0x1F\nb: !!int \"12\"\nc: !Ref some-name\nd: !!bool 'true'\n");
        Assert.Equal("0x1F", Text(map["a"]));
        Assert.Equal(JsonValueKind.String, Kind(map["a"]));
        Assert.Equal(12, map["b"]!.GetValue<int>());
        Assert.Equal("some-name", Text(map["c"]));
        Assert.Equal(JsonValueKind.True, Kind(map["d"]));
    }

    [Fact]
    public void CrLfAndByteOrderMark_AreNormalised()
    {
        var map = Map("\uFEFFa: 1\r\nb: |\r\n  x\r\n  y\r\n");
        Assert.Equal("1", Text(map["a"]));
        Assert.Equal("x\ny\n", Text(map["b"]));
    }

    // ───────────────────────────── tolerance ─────────────────────────────

    /// <summary>
    /// 🚨 The live case behind #4319: an unquoted value containing <c>": "</c>. YAML refuses the
    /// whole document; a mapping cannot begin in the middle of a value line, so the only other
    /// reading is the text itself — and that is what the tolerant reader returns.
    /// </summary>
    [Fact]
    public void ColonSpaceInsideAPlainValue_IsText()
    {
        var map = Map("description: new deal at Initech: fund reporting pilot, 45k\nname: x\n");
        Assert.Equal("new deal at Initech: fund reporting pilot, 45k", Text(map["description"]));
        Assert.Equal("x", Text(map["name"]));
    }

    [Fact]
    public void DuplicateKeys_LastWins()
    {
        var map = Map("a: 1\nb: 2\na: 3\n");
        Assert.Equal(3, map["a"]!.GetValue<int>());
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void StrictMode_RefusesWhatTheTolerantReaderAccepts()
    {
        var colon = Assert.Throws<YamlParseException>(() =>
            TolerantYaml.Parse("description: a: b\n", YamlParseOptions.StrictSpec));
        Assert.Equal(1, colon.Line);
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse("a: 1\na: 2\n", YamlParseOptions.StrictSpec));
        // And strict still reads valid YAML.
        Assert.Equal("a: b", Text(TolerantYaml.Parse("d: \"a: b\"\n", YamlParseOptions.StrictSpec)!["d"]));
    }

    [Theory]
    [InlineData("a: \"unterminated\nb: 1\n", 1)]
    [InlineData("a: [1, 2\n", 1)]
    [InlineData("a: {x: 1\n", 1)]
    [InlineData("a: *missing\n", 1)]
    [InlineData("a:\n  b: 1\n c: 2\n", 3)]
    [InlineData("a: 1\n\tb: 2\n", 2)]
    [InlineData("a: \"x\" trailing\n", 1)]
    public void MalformedText_ThrowsOneExceptionTypeWithAPosition(string yaml, int line)
    {
        var ex = Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml));
        Assert.Equal(line, ex.Line);
        Assert.True(ex.Column >= 1);
        Assert.Contains($"line {line}", ex.Message);
    }

    // ───────────────────────────── binding ─────────────────────────────

    public enum Colour { Red, Green }

    public sealed class Config
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public int Count { get; set; }
        public int? Order { get; set; }
        public double Ratio { get; set; }
        public bool Enabled { get; set; }
        public bool? Flag { get; set; }
        public Colour Colour { get; set; }
        public List<string>? Tags { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public Config? Child { get; set; }
        public DateTimeOffset? When { get; set; }
        public string Missing { get; set; } = "default";
    }

    [Fact]
    public void Binding_IsCaseInsensitive_AndIgnoresUnknownKeys()
    {
        var config = TolerantYaml.Deserialize<Config>("""
            NAME: upper
            unknownKey: whatever
            nested_unknown:
              deep: [1, 2]
            child:
              Name: inner
              alsoUnknown: 1
            """)!;
        Assert.Equal("upper", config.Name);
        Assert.Equal("inner", config.Child!.Name);
        Assert.Equal("default", config.Missing); // a missing key keeps the default
    }

    [Fact]
    public void Binding_ConvertsBetweenScalarKinds()
    {
        var config = TolerantYaml.Deserialize<Config>("""
            name: 123
            version: 1.10
            count: "42"
            order: '7'
            ratio: "0.25"
            enabled: "true"
            flag: False
            colour: green
            tags: [a, 1, true]
            env:
              A: 1
              B: x
            when: 2026-10-25T10:00:00Z
            """)!;
        Assert.Equal("123", config.Name);       // an unquoted number into a string
        Assert.Equal("1.10", config.Version);   // with its digits exactly as written
        Assert.Equal(42, config.Count);         // a quoted number into an int
        Assert.Equal(7, config.Order);
        Assert.Equal(0.25, config.Ratio);
        Assert.True(config.Enabled);            // a quoted boolean into a bool
        Assert.False(config.Flag);
        Assert.Equal(Colour.Green, config.Colour); // enums by name, case-insensitively
        Assert.Equal(["a", "1", "true"], config.Tags!);
        Assert.Equal("1", config.Env!["A"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero), config.When);
    }

    [Fact]
    public void Binding_AnEmptyDocumentIsDefault_AndAMismatchIsAJsonException()
    {
        Assert.Null(TolerantYaml.Deserialize<Config>("# nothing\n"));
        Assert.Throws<JsonException>(() => TolerantYaml.Deserialize<Config>("count: many\n"));
        Assert.Throws<JsonException>(() => TolerantYaml.Deserialize<Config>("name:\n  nested: map\n"));
    }

    // ───────────────────────────── writing ─────────────────────────────

    [Fact]
    public void Serialize_WritesBlockStyle_WithIndentlessSequencesUnderKeys()
    {
        var node = new JsonObject
        {
            ["NodeType"] = "Agent",
            ["Tags"] = new JsonArray("a", "b"),
            ["Order"] = 3,
            ["Nested"] = new JsonObject { ["X"] = true },
            ["Items"] = new JsonArray(new JsonObject { ["a"] = 1, ["b"] = 2 }),
            ["Empty"] = new JsonArray(),
        };
        Assert.Equal(
            "NodeType: Agent\nTags:\n- a\n- b\nOrder: 3\nNested:\n  X: true\nItems:\n- a: 1\n  b: 2\nEmpty: []\n",
            TolerantYaml.Serialize(node));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("Foo: Bar")]
    [InlineData("has # hash")]
    [InlineData("123")]
    [InlineData("1.10")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("off")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("")]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData("- dash")]
    [InlineData("[not a list]")]
    [InlineData("{not: a map}")]
    [InlineData("it's")]
    [InlineData("\"quoted\"")]
    [InlineData("tab\there")]
    [InlineData("control\u0001char")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("line one\nline two")]
    [InlineData("line one\nline two\n")]
    [InlineData("  indented first\nsecond")]
    [InlineData("ends with blank lines\n\n")]
    [InlineData("whitespace only line\n   \nafter")]
    [InlineData("1_000")]
    [InlineData("1:20")]
    [InlineData("---")]
    [InlineData("&anchor")]
    [InlineData("*alias")]
    [InlineData("key:")]
    [InlineData("\n")]
    [InlineData("last line ends in a space\nsecond ")]
    public void Serialize_StringsRoundTripExactly(string value)
    {
        var yaml = TolerantYaml.Serialize(new JsonObject { ["k"] = value, ["seq"] = new JsonArray(value) });
        var back = Map(yaml);
        Assert.Equal(JsonValueKind.String, Kind(back["k"]));
        Assert.Equal(value, Text(back["k"]));
        Assert.Equal(value, Text(back["seq"]![0]));
        // And a strict reader agrees.
        Assert.Equal(value, Text(TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec)!["k"]));
    }

    [Fact]
    public void Serialize_MultiLineStringsAsLiteralBlocks()
    {
        Assert.Equal("Notes: |-\n  first\n  second\n", TolerantYaml.Serialize(new JsonObject { ["Notes"] = "first\nsecond" }));
        Assert.Equal("Notes: |\n  first\n", TolerantYaml.Serialize(new JsonObject { ["Notes"] = "first\n" }));
    }

    [Fact]
    public void Serialize_ObjectWithOptions_RoundTripsThroughDeserialize()
    {
        var config = new Config { Name = "n: colon", Version = "2.0", Count = 2, Tags = ["x", "y z"], Colour = Colour.Green, Missing = "set" };
        var yaml = TolerantYaml.Serialize(config);
        var back = TolerantYaml.Deserialize<Config>(yaml)!;
        Assert.Equal("n: colon", back.Name);
        Assert.Equal("2.0", back.Version);
        Assert.Equal(2, back.Count);
        Assert.Equal(["x", "y z"], back.Tags!);
        Assert.Equal(Colour.Green, back.Colour);
        Assert.Equal("set", back.Missing);
        Assert.DoesNotContain("child", yaml); // nulls omitted
    }

    // ───────────────────────────── hostile input (review of #7180) ─────────────────────────────

    /// <summary>Runs <paramref name="parse"/> and asserts it REFUSES promptly — a bounded failure, not a hang or a crash.</summary>
    private static YamlParseException RefusedPromptly(Action parse)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<YamlParseException>(parse);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the refusal took {watch.Elapsed}");
        return ex;
    }

    [Fact]
    public void AnEmbeddedNul_IsRefusedWithItsPosition_NotReadAsAnEndThatNeverAdvances()
    {
        var ex = RefusedPromptly(() => TolerantYaml.Parse("a: |\n  \0text\n"));
        Assert.Equal(2, ex.Line);
        Assert.Equal(3, ex.Column);
        RefusedPromptly(() => TolerantYaml.Parse("a: x\0y\n"));
    }

    [Fact]
    public void DeepNesting_IsRefused_NeverAStackOverflow()
    {
        RefusedPromptly(() => TolerantYaml.Parse(new string('[', 10_000) + new string(']', 10_000)));
        RefusedPromptly(() => TolerantYaml.Parse(new string('{', 10_000)));
        RefusedPromptly(() => TolerantYaml.Parse(string.Concat(Enumerable.Range(0, 10_000).Select(i => new string(' ', i) + "a:\n"))));
        RefusedPromptly(() => TolerantYaml.Parse(string.Concat(Enumerable.Range(0, 10_000).Select(i => new string(' ', 2 * i) + "- \n"))));
        RefusedPromptly(() => TolerantYaml.Parse(string.Concat(Enumerable.Range(0, 10_000).Select(i => $"&a{i}\n")) + "x\n"));
        // The limit leaves ordinary depth alone.
        var ok = TolerantYaml.Parse(new string('[', 64) + new string(']', 64));
        Assert.NotNull(ok);
    }

    [Fact]
    public void BillionLaughs_IsRefusedByTheExpansionBudget()
    {
        var yaml = "l0: &l0 [lol, lol, lol, lol, lol, lol, lol, lol, lol, lol]\n"
            + string.Concat(Enumerable.Range(1, 9).Select(i =>
                $"l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 10))}]\n"));
        RefusedPromptly(() => TolerantYaml.Parse(yaml));

        // Block aliases and merge-key copies are charged against the same budget.
        var block = "l0: &l0\n  - lol\n  - lol\n  - lol\n  - lol\n  - lol\n  - lol\n  - lol\n  - lol\n  - lol\n  - lol\n"
            + string.Concat(Enumerable.Range(1, 9).Select(i =>
                $"l{i}: &l{i}\n" + string.Concat(Enumerable.Repeat($"  - *l{i - 1}\n", 10))));
        RefusedPromptly(() => TolerantYaml.Parse(block));

        var merge = "m0: &m0 {k: [lol, lol, lol, lol, lol, lol, lol, lol, lol, lol]}\n"
            + string.Concat(Enumerable.Range(1, 9).Select(i =>
                $"m{i}: &m{i}\n" + string.Concat(Enumerable.Range(0, 10).Select(j => $"  k{j}:\n    <<: *m{i - 1}\n"))));
        RefusedPromptly(() => TolerantYaml.Parse(merge));
    }

    [Theory]
    [InlineData("a: \"\\U00110000\"\n")]
    [InlineData("a: \"\\uD800\"\n")]
    [InlineData("a: \"\\uDC00x\"\n")]
    public void AnEscapeOutsideUnicode_IsAYamlParseException(string yaml) =>
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml));

    [Fact]
    public void ASurrogatePairEscape_IsOneCharacter() =>
        Assert.Equal("😀", Text(Map("a: \"\\uD83D\\uDE00\"\n")["a"]));

    [Fact]
    public void AVerbatimTag_IsReadThroughItsClosingBracket()
    {
        var map = Map("a: !<tag:yaml.org,2002:str> 123\nb: [!<tag:yaml.org,2002:str> 7, x]\n");
        Assert.Equal(JsonValueKind.String, Kind(map["a"]));
        Assert.Equal("123", Text(map["a"]));
        Assert.Equal("7", Text(map["b"]![0]));
        Assert.Equal(JsonValueKind.String, Kind(map["b"]![0]));
    }

    [Theory]
    [InlineData("description: @invalid\n")]
    [InlineData("description: `cmd`\n")]
    [InlineData("description: %x\n")]
    [InlineData("tags: [@a]\n")]
    public void StrictMode_RefusesReservedPlainStarters_TheTolerantReaderKeepsTheText(string yaml)
    {
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec));
        Assert.NotNull(TolerantYaml.Parse(yaml));
    }

    [Fact]
    public void BlockScalar_ATabAfterTheIndentation_IsContent()
    {
        var map = Map("notes: |\n  \tfirst\n  second\nnext: x\n");
        Assert.Equal("\tfirst\nsecond\n", Text(map["notes"]));
        Assert.Equal("x", Text(map["next"]));
    }

    [Theory]
    [InlineData("a: |\n  text", "text")]
    [InlineData("a: |+\n  text", "text")]
    [InlineData("a: >\n  x\n  y", "x y")]
    [InlineData("a: |\n  text\n", "text\n")]
    [InlineData("a: |+\n  text\n\n", "text\n\n")]
    public void Chomping_KeepsOnlyTheBreaksThatWereThere(string yaml, string expected) =>
        Assert.Equal(expected, Text(Map(yaml)["a"]));

    [Fact]
    public void FlowKeys_KeepTheirWrittenText_LikeBlockKeys()
    {
        var flow = Map("m: {null: keep, '': empty, 1.0: one, true: t, 0x1F: hex}\n")["m"]!.AsObject();
        Assert.Equal(["null", "", "1.0", "true", "0x1F"], flow.Select(kv => kv.Key));
        var pairs = Map("s: [null: a, 007: b]\n")["s"]!.AsArray();
        Assert.Equal("null", pairs[0]!.AsObject().Single().Key);
        Assert.Equal("007", pairs[1]!.AsObject().Single().Key);
        var block = Map("null: keep\n'': empty\n1.0: one\n");
        Assert.Equal(["null", "", "1.0"], block.Select(kv => kv.Key));
    }

    [Fact]
    public void ParseThenSerialize_IsStable()
    {
        const string yaml = "a: 1\nb:\n- x\n- w: 2\n  z: three\nc:\n  d: 'Foo: Bar'\n";
        Assert.Equal(yaml, TolerantYaml.Serialize(TolerantYaml.Parse(yaml)));
    }
}

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

    /// <summary>
    /// #7398: a plain scalar bound to a <see cref="string"/> gets the text as WRITTEN, not the
    /// core-schema value it resolves to — <c>00123</c> stays <c>"00123"</c> (not <c>"123"</c>),
    /// <c>0x1F</c> stays <c>"0x1F"</c> (not <c>"31"</c>), <c>TRUE</c> stays <c>"TRUE"</c> — at every
    /// position a string is declared: a property, a list element, a dictionary value, a nested
    /// object, an aliased copy and a merged key.
    /// </summary>
    [Fact]
    public void Binding_APlainScalarIntoAString_KeepsItsSourceSpelling()
    {
        var config = TolerantYaml.Deserialize<Config>("""
            name: 00123
            version: 0x1F
            tags: [TRUE, 0o17, +5, 1., .INF, -007]
            env:
              A: False
              B: &zip 08001
              C: *zip
            child:
              <<: {name: 0x0A}
              version: 1E3
            """)!;
        Assert.Equal("00123", config.Name);
        Assert.Equal("0x1F", config.Version);
        Assert.Equal(["TRUE", "0o17", "+5", "1.", ".INF", "-007"], config.Tags!);
        Assert.Equal("False", config.Env!["A"]);
        Assert.Equal("08001", config.Env["B"]);
        Assert.Equal("08001", config.Env["C"]); // an alias is a copy of the spelling too
        Assert.Equal("0x0A", config.Child!.Name); // and so is a merged key
        Assert.Equal("1E3", config.Child.Version);
    }

    /// <summary>
    /// The negative control for #7398: the SAME spellings bound to typed properties still bind as
    /// the core-schema values, and the JSON tree <see cref="TolerantYaml.Parse"/> returns is
    /// unchanged — numbers stay numbers, booleans stay booleans.
    /// </summary>
    [Fact]
    public void Binding_TheSameSpellingsIntoTypedProperties_StillBindAsTheirValues()
    {
        var config = TolerantYaml.Deserialize<Config>("""
            count: 0x1F
            order: 00123
            ratio: 1E3
            enabled: TRUE
            flag: False
            """)!;
        Assert.Equal(31, config.Count);
        Assert.Equal(123, config.Order);
        Assert.Equal(1000d, config.Ratio);
        Assert.True(config.Enabled);
        Assert.False(config.Flag);

        var map = Map("name: 00123\nhex: 0x1F\nflag: TRUE\n");
        Assert.Equal(JsonValueKind.Number, Kind(map["name"]));
        Assert.Equal(123, map["name"]!.GetValue<int>());
        Assert.Equal(31, map["hex"]!.GetValue<int>());
        Assert.Equal(JsonValueKind.True, Kind(map["flag"]));
    }

    /// <summary>
    /// The spelling is restored only where the tolerant string converter would have converted the
    /// scalar: options without it keep System.Text.Json's own refusal of a number for a string.
    /// </summary>
    [Fact]
    public void Binding_ANumericOnlyTarget_WithASourceGeneratedContextThatDeclaresNoString_StillBinds()
    {
        // Review of #7628: restoring a spelling must not ask the options for a STRING contract the caller
        // never declared. A source-generated context with only `int` has none, and asking threw
        // NotSupportedException for `00123` (a recorded spelling) while `123` (none) bound fine.
        var options = IntOnlyYamlContext.Default.Options;
        Assert.Equal(123, TolerantYaml.Deserialize<int>("123", options)); // NEGATIVE CONTROL: no spelling recorded
        Assert.Equal(123, TolerantYaml.Deserialize<int>("00123", options));
    }

    [Fact]
    public void Binding_WithoutTheTolerantStringConverter_ANumberIntoAStringStillFails() =>
        Assert.Throws<JsonException>(() => TolerantYaml.Deserialize<Config>(
            "name: 00123\n", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

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

    // ───────────────────────────── second review of #7180 + self-audit ─────────────────────────────

    [Fact]
    public void AWideNullAnchor_IsChargedPerSlot()
    {
        var yaml = "a: &a [" + string.Join(", ", Enumerable.Repeat("~", 10_000)) + "]\n"
            + "b: [" + string.Join(", ", Enumerable.Repeat("*a", 20)) + "]\n";
        RefusedPromptly(() => TolerantYaml.Parse(yaml));
        // Under the budget it still expands.
        var small = Map("a: &a [~, ~]\nb: [*a, *a]\n");
        Assert.Equal(2, small["b"]![1]!.AsArray().Count);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\'')]
    public void ALongWhitespaceRunInAQuotedScalar_IsReadInOnePass(char quote)
    {
        var run = new string(' ', 200_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var value = Text(Map($"a: {quote}x{run}y{quote}\n")["a"]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.Equal("x" + run + "y", value);
    }

    [Theory]
    [InlineData("0xFF_FF")]
    [InlineData("-0xFF")]
    [InlineData("+0x1")]
    [InlineData("2026-01-01")]
    [InlineData("2026-1-1 10:00:00")]
    [InlineData("2026-01-01T10:00:00Z")]
    [InlineData("1,000")]
    [InlineData("1.2.3")]
    [InlineData("0b1010")]
    [InlineData("0o17")]
    [InlineData("017")]
    [InlineData("1:20")]
    [InlineData("-1:20:30.5")]
    [InlineData(".Inf")]
    [InlineData("-.inf")]
    [InlineData(".NaN")]
    [InlineData("~")]
    [InlineData("Y")]
    [InlineData("NO")]
    [InlineData("On")]
    [InlineData("=")]
    [InlineData("<<")]
    public void Serialize_QuotesWhatAYaml11ReaderWouldNotReadAsAString(string value)
    {
        var yaml = TolerantYaml.Serialize(new JsonObject { ["k"] = value });
        Assert.NotEqual($"k: {value}\n", yaml);
        Assert.Equal(value, Text(Map(yaml)["k"]));
    }

    [Theory]
    [InlineData("name: !!str\n  007\n", "007")]
    [InlineData("name: !!str\n  null\n", "null")]
    [InlineData("name: !!str\n  true\n", "true")]
    [InlineData("name: &x !!str\n  0x1F\n", "0x1F")]
    public void ATagBeforeTheLineBreak_AppliesBeforeResolution_InAMapping(string yaml, string expected)
    {
        var value = Map(yaml)["name"];
        Assert.Equal(JsonValueKind.String, Kind(value));
        Assert.Equal(expected, Text(value));
    }

    [Fact]
    public void ATagBeforeTheLineBreak_AppliesBeforeResolution_InASequenceAndAtTheRoot()
    {
        var seq = Assert.IsType<JsonArray>(TolerantYaml.Parse("- !!str\n  007\n- !!str\n  null\n"));
        Assert.Equal("007", Text(seq[0]));
        Assert.Equal("null", Text(seq[1]));
        Assert.Equal(JsonValueKind.String, Kind(seq[1]));
        var root = TolerantYaml.Parse("--- !!str\n007\n");
        Assert.Equal("007", Text(root));
        Assert.Equal(12, Map("n: !!int\n  \"12\"\n")["n"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("a: |-\n  x\n   \n", "x\n ")]
    [InlineData("a: |\n  x\n  \t\n", "x\n\t\n")]
    [InlineData("a: |\n  x\n\n   \nb: 1\n", "x\n\n \n")]
    [InlineData("a: |+\n  x\n  \n", "x\n\n")]
    [InlineData("a: |\n  x\n \n", "x\n")]
    public void BlockScalar_WhitespacePastTheIndentation_IsContent(string yaml, string expected) =>
        Assert.Equal(expected, Text(Map(yaml)["a"]));

    [Fact]
    public void BlockScalar_ALeadingBlankLineIndentedPastTheContent_IsEmptyTolerantly_AndRefusedStrictly()
    {
        const string yaml = "a: |\n    \n  x\n";
        Assert.Equal("\nx\n", Text(Map(yaml)["a"]));
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec));
    }

    [Theory]
    [InlineData("tags: [a,,b]\n")]
    [InlineData("m: {,}\n")]
    [InlineData("s: [,]\n")]
    [InlineData("m: {a: 1,, b: 2}\n")]
    public void AnEmptyFlowEntry_IsRefused_InBothModes(string yaml)
    {
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml));
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec));
    }

    [Fact]
    public void TheLegalEmptyFlowForms_StillRead()
    {
        var map = Map("a: [x, y,]\nb: {k: }\nc: {: v}\nd: [!!str , z]\ne: [k: ]\n");
        Assert.Equal(2, map["a"]!.AsArray().Count);
        Assert.Null(map["b"]!["k"]);
        Assert.Equal("v", Text(map["c"]![""]));
        Assert.Equal("", Text(map["d"]![0]));
        Assert.Null(map["e"]![0]!["k"]);
    }

    [Theory]
    [InlineData("a: ,x\n")]
    [InlineData("a: ]x\n")]
    [InlineData("a: }x\n")]
    public void StrictMode_RefusesFlowIndicatorPlainStarters(string yaml)
    {
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec));
        Assert.NotNull(TolerantYaml.Parse(yaml));
    }

    [Fact]
    public void ManyDuplicateKeys_StayLinear_AndKeepTheLastValue()
    {
        var yaml = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"k{i}: {i}\n"))
            + string.Concat(Enumerable.Repeat("k0: dup\n", 20_000)) + "k0: last\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var map = Map(yaml);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Equal("last", Text(map["k0"]));
        Assert.Equal(20_000, map.Count);
    }

    /// <summary>
    /// The self-audit's fuzz half: a few hundred generated hostile texts — long whitespace runs in
    /// every scalar style, wide and deep collections, alias fans, long lines, tabs everywhere and
    /// indicator soup. Each must, within its own time budget, either parse or throw
    /// <see cref="YamlParseException"/> — never hang and never any other exception type — in both
    /// modes, and whatever parses must serialize and read back equal.
    /// </summary>
    [Fact]
    public void GeneratedHostileInputs_ParseOrRefuse_PromptlyAndOnlyWithYamlParseException()
    {
        var random = new Random(7180);
        var inputs = HostileInputs(random).ToList();
        Assert.True(inputs.Count >= 300, $"only {inputs.Count} inputs generated");
        foreach (var (name, text) in inputs)
        {
            foreach (var mode in new[] { YamlParseOptions.Tolerant, YamlParseOptions.StrictSpec })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                IReadOnlyList<JsonNode?>? documents = null;
                try
                {
                    documents = TolerantYaml.ParseAll(text, mode);
                }
                catch (YamlParseException)
                {
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{name} (strict={mode.Strict}): {ex.GetType().Name}: {ex.Message}");
                }
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{name} (strict={mode.Strict}) took {watch.Elapsed}");
                foreach (var document in documents ?? [])
                {
                    var back = TolerantYaml.Parse(TolerantYaml.Serialize(document), YamlParseOptions.StrictSpec);
                    Assert.True(JsonNode.DeepEquals(document, back), $"{name}: the written form did not read back equal");
                }
            }
        }
    }

    private static IEnumerable<(string Name, string Text)> HostileInputs(Random random)
    {
        const int Run = 100_000;
        var spaces = new string(' ', Run);
        var tabs = new string('\t', Run);
        var mixed = string.Concat(Enumerable.Range(0, Run / 2).Select(i => i % 3 == 0 ? "\t" : " "));
        foreach (var (wsName, ws) in new[] { ("spaces", spaces), ("tabs", tabs), ("mixed", mixed) })
        {
            yield return ($"double-quoted {wsName}", $"a: \"x{ws}y\"\n");
            yield return ($"double-quoted {wsName} unterminated", $"a: \"x{ws}");
            yield return ($"single-quoted {wsName}", $"a: 'x{ws}y'\n");
            yield return ($"single-quoted {wsName} folded", $"a: 'x{ws}\n{ws}y'\n");
            yield return ($"plain {wsName}", $"a: x{ws}y\n");
            yield return ($"plain trailing {wsName}", $"a: x{ws}\n");
            yield return ($"literal {wsName}", $"a: |\n  x{ws}\n  {ws}\n");
            yield return ($"folded {wsName}", $"a: >\n  x{ws}\n\n  y\n");
            yield return ($"flow {wsName}", $"a: [x{ws}, {ws}y, {{k{ws}: v}}]\n");
            yield return ($"indent {wsName}", $"{ws}a: 1\n");
            yield return ($"blank lines {wsName}", string.Concat(Enumerable.Repeat(ws[..100] + "\n", 1000)) + "a: 1\n");
            yield return ($"key {wsName}", $"a{ws}: 1\n");
            yield return ($"comment {wsName}", $"a: 1{ws}# c\n");
        }
        yield return ("wide flow", "a: [" + string.Join(",", Enumerable.Range(0, 100_000)) + "]\n");
        yield return ("wide flow map", "a: {" + string.Join(",", Enumerable.Range(0, 50_000).Select(i => $"k{i}: {i}")) + "}\n");
        yield return ("wide block", string.Concat(Enumerable.Range(0, 50_000).Select(i => $"- {i}\n")));
        yield return ("wide mapping", string.Concat(Enumerable.Range(0, 50_000).Select(i => $"k{i}: v\n")));
        yield return ("long line", "a: " + new string('x', 1_000_000) + "\n");
        yield return ("long key", new string('k', 1_000_000) + ": v\n");
        yield return ("long quoted", "a: \"" + new string('x', 1_000_000) + "\"\n");
        yield return ("many escapes", "a: \"" + string.Concat(Enumerable.Repeat("\\u00e9\\n\\t", 100_000)) + "\"\n");
        yield return ("alias fan", "a: &a {k: v}\n" + string.Concat(Enumerable.Range(0, 30_000).Select(i => $"b{i}: *a\n")));
        yield return ("alias fan flow", "a: &a [1, 2, 3]\nb: [" + string.Join(",", Enumerable.Repeat("*a", 40_000)) + "]\n");
        yield return ("merge fan", "a: &a {k: v}\n" + string.Concat(Enumerable.Range(0, 20_000).Select(i => $"b{i}:\n  <<: *a\n")));
        yield return ("many anchors", string.Concat(Enumerable.Range(0, 50_000).Select(i => $"k{i}: &a{i} v\n")));
        yield return ("many documents", string.Concat(Enumerable.Repeat("---\na: 1\n", 20_000)));
        yield return ("many markers", string.Concat(Enumerable.Repeat("---\n...\n", 20_000)));
        yield return ("deep flow 63", new string('[', 63) + new string(']', 63));
        yield return ("deep flow 65", new string('[', 65) + new string(']', 65));
        yield return ("deep block 1000", string.Concat(Enumerable.Range(0, 1000).Select(i => new string(' ', i) + "a:\n")));
        yield return ("deep sequences inline", string.Concat(Enumerable.Repeat("- ", 1000)) + "x\n");
        yield return ("deep mixed", string.Concat(Enumerable.Range(0, 1000).Select(i => new string(' ', 2 * i) + (i % 2 == 0 ? "- " : "k:") + "\n")));
        yield return ("unclosed brackets", new string('[', 100_000));
        yield return ("closing soup", new string(']', 100_000));
        yield return ("many colons", "a: " + string.Concat(Enumerable.Repeat(": ", 100_000)) + "\n");
        yield return ("many hashes", "a: " + string.Concat(Enumerable.Repeat(" #", 100_000)) + "\n");
        yield return ("many dashes", string.Concat(Enumerable.Repeat("-", 100_000)) + "\n");
        yield return ("block scalar headers", string.Concat(Enumerable.Repeat("a: |-9\n", 10_000)));
        yield return ("tags", "a: " + string.Concat(Enumerable.Repeat("!t ", 50_000)) + "x\n");
        yield return ("tag lines", string.Concat(Enumerable.Repeat("!!str\n", 50_000)) + "x\n");
        yield return ("verbatim tag unclosed", "a: !<" + new string('x', 100_000) + "\n");
        yield return ("hex escapes bad", "a: \"" + string.Concat(Enumerable.Repeat("\\uZZZZ", 1000)) + "\"\n");
        yield return ("cr only", string.Concat(Enumerable.Repeat("a: 1\r", 50_000)));
        yield return ("bom and nul", "\uFEFFa: 1\n\0");
        yield return ("directives", string.Concat(Enumerable.Repeat("%YAML 1.2\n", 20_000)) + "---\na: 1\n");

        // Indicator soup: random texts over the characters the grammar cares about.
        const string alphabet = " \t\n-?:,[]{}#&*!|>'\"%@`~\\0123abcxyz.<=+_";
        for (var i = 0; i < 260; i++)
        {
            var length = random.Next(1, i < 200 ? 200 : 20_000);
            var chars = new char[length];
            for (var c = 0; c < length; c++)
                chars[c] = alphabet[random.Next(alphabet.Length)];
            yield return ($"soup {i}", new string(chars));
        }
        // Valid-shaped documents with one random character changed.
        const string valid = "a: 1\nb:\n  - x\n  - {k: v, l: [1, 2]}\nc: |\n  text\n  more\nd: \"q\\n\"\ne: &e {z: 1}\nf: *e\ng:\n  <<: *e\n";
        for (var i = 0; i < 100; i++)
        {
            var chars = valid.ToCharArray();
            chars[random.Next(chars.Length)] = alphabet[random.Next(alphabet.Length)];
            yield return ($"mutant {i}", new string(chars));
        }
    }

    // ───────────────────────────── third review of #7180 ─────────────────────────────

    [Fact]
    public void AliasesOfAliases_CannotBuildATreeDeeperThanTheLimit()
    {
        var yaml = "a0: &a0 []\n" + string.Concat(Enumerable.Range(1, 64).Select(i => $"a{i}: &a{i} [*a{i - 1}]\n"));
        RefusedPromptly(() => TolerantYaml.Parse(yaml));
        // Shallow aliasing is untouched, and whatever parses can be written back.
        var ok = TolerantYaml.Parse("a0: &a0 []\na1: &a1 [*a0]\na2: [*a1]\n");
        Assert.NotNull(TolerantYaml.Serialize(ok));
    }

    [Fact]
    public void ImplicitFlowPairs_CountTowardTheDepthLimit()
    {
        var yaml = string.Concat(Enumerable.Repeat("[k: ", 33)) + "[]" + new string(']', 33) + "\n";
        RefusedPromptly(() => TolerantYaml.Parse(yaml));
        var shallow = TolerantYaml.Parse(string.Concat(Enumerable.Repeat("[k: ", 20)) + "[]" + new string(']', 20) + "\n");
        Assert.NotNull(TolerantYaml.Serialize(shallow));
    }

    [Theory]
    [InlineData("name: valid\n@invalid: value\n")]
    [InlineData("name: valid\n`x`: value\n")]
    [InlineData("- ,k: v\n")]
    public void StrictMode_RefusesReservedStartersInPlainKeysToo(string yaml)
    {
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec));
        Assert.NotNull(TolerantYaml.Parse(yaml));
        Assert.Equal("value", Text(TolerantYaml.Parse("\"@quoted\": value\n", YamlParseOptions.StrictSpec)!["@quoted"]));
    }

    [Theory]
    [InlineData("cfg: {<<: [{a: 1}, 2]}\n")]
    [InlineData("cfg:\n  <<: [{a: 1}, [b]]\n")]
    [InlineData("cfg:\n  <<: text\n")]
    public void AMergeSourceThatIsNotAMapping_IsRefused(string yaml)
    {
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml));
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec));
    }

    [Fact]
    public void ApplicationTags_ResolveLikeTheUntaggedScalar()
    {
        var map = Map("a: !Ref 123\nb: !Ref true\nc: [!Ref 1.5, !Ref null]\nd: !Ref \"123\"\ne: !Ref name\n");
        Assert.Equal(123, map["a"]!.GetValue<int>());
        Assert.Equal(JsonValueKind.True, Kind(map["b"]));
        Assert.Equal(1.5, map["c"]![0]!.GetValue<double>());
        Assert.Null(map["c"]![1]);
        Assert.Equal(JsonValueKind.String, Kind(map["d"])); // quoted stays a string
        Assert.Equal("name", Text(map["e"]));
        // The non-specific '!' makes a plain scalar a string.
        Assert.Equal(JsonValueKind.String, Kind(Map("a: ! 12\n")["a"]));
    }

    [Theory]
    [InlineData("a: !!int true\n")]
    [InlineData("a: !!bool 123\n")]
    [InlineData("a: !!int \"abc\"\n")]
    [InlineData("a: !!float yes\n")]
    [InlineData("a: !!null x\n")]
    [InlineData("a: !!str [x]\n")]
    [InlineData("a: !!map [x]\n")]
    [InlineData("a: !!seq {x: 1}\n")]
    public void ACoreTagOnTextOfAnotherType_IsRefusedWithItsPosition(string yaml)
    {
        var ex = Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml));
        Assert.Equal(1, ex.Line);
        Assert.Equal(4, ex.Column);
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec));
    }

    [Fact]
    public void ACoreTag_ResolvesAsItsType()
    {
        var map = Map("a: !!int \"12\"\nb: !!float 1\nc: !!bool \"false\"\nd: !!null ''\ne: !!int 0x1F\nf: !<tag:yaml.org,2002:int> \"7\"\n");
        Assert.Equal(12, map["a"]!.GetValue<int>());
        Assert.Equal(1, map["b"]!.GetValue<int>());
        Assert.Equal(JsonValueKind.False, Kind(map["c"]));
        Assert.Null(map["d"]);
        Assert.Equal(31, map["e"]!.GetValue<int>());
        Assert.Equal(7, map["f"]!.GetValue<int>());
        // A %TAG that redefines !! makes !!int an application tag (yaml-test-suite P76L).
        Assert.Equal("1 - 3", Text(TolerantYaml.Parse("%TAG !! tag:example.com,2000:app/\n---\n!!int 1 - 3\n", YamlParseOptions.StrictSpec)));
    }

    [Fact]
    public void BlockScalar_ATabOnlyLineAfterTheIndentation_IsContent()
    {
        const string yaml = "a: |\n  \t\n  x\n";
        Assert.Equal("\t\nx\n", Text(Map(yaml)["a"]));
        Assert.Equal("\t\nx\n", Text(TolerantYaml.Parse(yaml, YamlParseOptions.StrictSpec)!["a"]));
    }

    [Fact]
    public void DoubleQuoted_EscapedLineBreak_KeepsTheEmptyLinesAfterIt()
    {
        Assert.Equal("one\ntwo", Text(Map("a: \"one\\\n\n  two\"\n")["a"]));
        Assert.Equal("one\n\ntwo", Text(Map("a: \"one\\\n\n\n  two\"\n")["a"]));
        Assert.Equal("onetwo", Text(Map("a: \"one\\\n  two\"\n")["a"]));
    }

    [Fact]
    public void StrictMode_AcceptsATaggedEmptyFlowNode_AndStillRefusesAnEmptyEntry()
    {
        var strict = TolerantYaml.Parse("a: [!!str , z]\nb: {k: !!str , l: x}\n", YamlParseOptions.StrictSpec)!;
        Assert.Equal("", Text(strict["a"]![0]));
        Assert.Equal("", Text(strict["b"]!["k"]));
        Assert.Throws<YamlParseException>(() => TolerantYaml.Parse("a: [a,,b]\n", YamlParseOptions.StrictSpec));
    }

    [Fact]
    public void ExplicitKeys_AndPropertiesOnKeys_Read()
    {
        var map = Map("? a\n: 1\n? b\n&k c: 2\n!!str 3: x\nd: *k\n");
        Assert.Equal(1, map["a"]!.GetValue<int>());
        Assert.Null(map["b"]);
        Assert.Equal(2, map["c"]!.GetValue<int>());
        Assert.Equal("x", Text(map["3"]));
        Assert.Equal("c", Text(map["d"]));
        Assert.Equal("baz", Text(TolerantYaml.Parse("[? foo bar : baz]\n")![0]!["foo bar"]));
    }

    [Fact]
    public void ParseThenSerialize_IsStable()
    {
        const string yaml = "a: 1\nb:\n- x\n- w: 2\n  z: three\nc:\n  d: 'Foo: Bar'\n";
        Assert.Equal(yaml, TolerantYaml.Serialize(TolerantYaml.Parse(yaml)));
    }
}

/// <summary>A source-generated context that declares <c>int</c> and nothing else — no string contract.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
internal sealed partial class IntOnlyYamlContext : System.Text.Json.Serialization.JsonSerializerContext;

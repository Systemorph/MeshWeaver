using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A <see cref="SensitiveDataAttribute"/> value never reaches a log line, an exception text or
/// an egress rendering — and the store still holds it.</b> Node content may be health data (a
/// medical stop-loss book keeps diagnoses in it); the store guards it with access control, and a
/// log line has none. See <c>Doc/Architecture/SensitiveContentInLogs</c>.
///
/// <para>A REAL monolith mesh, a real NodeType whose content type marks two members, a node
/// written with distinctive values, and a capture provider at <see cref="LogLevel.Trace"/> that
/// sees every line the mesh writes while the node is created, read, updated and rendered. The
/// assertions are by VALUE: neither marker may appear in any captured line, any exception text or
/// any logging rendering — while a read of the stored node returns both, so persistence is
/// untouched.</para>
///
/// <para>🚨 <b>The controls are load-bearing.</b> "No line holds the marker" is vacuous for a blind
/// recorder or a renderer that drops everything: the recorder must have seen Debug lines naming the
/// node's path, and the unmarked members' values must survive every rendering.</para>
/// </summary>
public class SensitiveContentNeverReachesALogTest : MonolithMeshTestBase
{
    private const string PatientType = "SensitivePatient";

    /// <summary>The diagnosis — marked on the positional record parameter.</summary>
    private const string Diagnosis = "DIAGNOSIS-c41f9e-type2-diabetes";

    /// <summary>A visit note — marked on a property of a nested record in a collection.</summary>
    private const string VisitNote = "NOTE-8b02d7-insulin-dose";

    /// <summary>An UNMARKED member's value: must survive every rendering (the control).</summary>
    private const string Ward = "WARD-east-wing-3";

    /// <summary>The content type under test.</summary>
    /// <param name="Diagnosis">Marked on the parameter only — no <c>property:</c> target.</param>
    public record PatientRecord([SensitiveData] string? Diagnosis = null)
    {
        /// <summary>An ordinary member.</summary>
        public string? Ward { get; init; }

        /// <summary>A marked non-string member.</summary>
        [SensitiveData] public int? MemberNumber { get; init; }

        /// <summary>Nested records in a collection, each with a marked member.</summary>
        public ImmutableList<Visit> Visits { get; init; } = [];

        /// <summary>A polymorphic member: the runtime type is resolved through the same options.</summary>
        public object? Latest { get; init; }
    }

    /// <summary>A nested record.</summary>
    public record Visit
    {
        /// <summary>Ordinary.</summary>
        public string? Clinic { get; init; }

        /// <summary>Marked.</summary>
        [SensitiveData] public string? Note { get; init; }
    }

    private readonly TraceCapture capture = new();

    public SensitiveContentNeverReachesALogTest(ITestOutputHelper output) : base(output)
        // After the base constructor's ClearProviders(). A provider-specific rule beats every
        // category rule of the shared appsettings, so this provider sees Trace.
        => Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(capture);
            l.AddFilter<TraceCapture>(null, LogLevel.Trace);
        });

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(PatientType)
            {
                Name = "Sensitive patient",
                Content = new NodeTypeDefinition
                {
                    Configuration = "// stands in for in-mesh source — the delegate below is what runs",
                },
                HubConfiguration = config => config
                    .WithTypes(typeof(Visit))
                    .AddMeshDataSource(source => source.WithContentType<PatientRecord>()),
            })
            .ConfigureHub(c => c.WithTypes(typeof(PatientRecord), typeof(Visit)));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithTypes(typeof(PatientRecord), typeof(Visit)).AddData();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static PatientRecord Patient(string ward = Ward) => new(Diagnosis)
    {
        Ward = ward,
        MemberNumber = 4711_0815,
        Visits = [new Visit { Clinic = "North", Note = VisitNote }],
        Latest = new Visit { Clinic = "South", Note = VisitNote },
    };

    private static void ShouldHoldNoMarker(string? text, string what)
    {
        text.Should().NotBeNull(what);
        text.Should().NotContain(Diagnosis, $"{what} must not carry the marked diagnosis");
        text.Should().NotContain(VisitNote, $"{what} must not carry the marked visit note");
        text.Should().NotContain("47110815", $"{what} must not carry the marked member number");
    }

    private async Task<MeshNode> CreatePatient(string id)
    {
        var ct = TestContext.Current.CancellationToken;
        return await MeshService.CreateNode(new MeshNode(id, TestPartition)
            {
                Name = "Patient " + id,
                NodeType = PatientType,
                State = MeshNodeState.Active,
                Content = Patient(),
            })
            .Take(1)
            .Should().Within(60.Seconds()).Emit("the patient node must be created", ct);
    }

    /// <summary>
    /// 🚨 THE GUARD. Write, read back, update and render a patient while every line the mesh writes
    /// is captured at Trace: the store returns both marked values; no line, and no logging rendering
    /// of the node, carries either.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AMarkedValue_IsStored_AndAppearsInNoLogLineOrRendering()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = "p" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        await CreatePatient(id);

        // Persistence untouched: the owning hub's own stream holds the values.
        var stored = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<PatientRecord>(Mesh.JsonSerializerOptions)?.Diagnosis == Diagnosis)
            .FirstAsync().Should().Within(60.Seconds()).Emit("the stored node keeps its diagnosis", ct);
        var content = stored!.ContentAs<PatientRecord>(Mesh.JsonSerializerOptions)!;
        content.Visits.Single().Note.Should().Be(VisitNote, "a marked nested value is stored as written");
        content.MemberNumber.Should().Be(4711_0815);

        // An update through the mesh, so the change paths run too.
        // Through the canonical mutation API, derived from the CURRENT content.
        await GetClient().GetWorkspace().GetMeshNodeStream(path)
            .Update(node => node with
            {
                Content = node.ContentAs<PatientRecord>(Mesh.JsonSerializerOptions)! with { Ward = Ward + "-moved" },
            })
            .Should().Within(60.Seconds()).Emit("the update lands", ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<PatientRecord>(Mesh.JsonSerializerOptions)?.Ward == Ward + "-moved")
            .FirstAsync().Should().Within(60.Seconds()).Emit("the update is visible", ct);

        // Every logging rendering of the node — typed content, and the same content as untyped JSON.
        var typed = Mesh.RenderForLog(stored);
        ShouldHoldNoMarker(typed, "the logging rendering of the typed node");
        typed.Should().Contain(Ward, "CONTROL — an unmarked value survives the rendering");
        typed.Should().Contain(SensitiveDataAttribute.Placeholder, "a marked member stays visible, its value replaced");
        typed.Should().Contain("North").And.Contain("South",
            "CONTROL — the nested and the polymorphic records are rendered, not dropped");

        var untyped = stored with { Content = JsonSerializer.SerializeToElement(content, Mesh.JsonSerializerOptions) };
        var untypedRender = Mesh.RenderForLog(untyped);
        ShouldHoldNoMarker(untypedRender, "the logging rendering of untyped content (resolved through $type)");
        untypedRender.Should().Contain(Ward, "CONTROL — untyped content is redacted by member, not wiped");

        // The existing logging options (what MessageService renders a body opt-in with) — the shape
        // that carried the value before this change.
        ShouldHoldNoMarker(JsonSerializer.Serialize(stored, Mesh.CreateLoggingSerializerOptions()),
            "CreateLoggingSerializerOptions");
        ShouldHoldNoMarker(Mesh.RedactJson(JsonSerializer.Serialize(stored, Mesh.JsonSerializerOptions)),
            "RedactJson over the wire JSON");
        ShouldHoldNoMarker(stored.ToString(), "MeshNode.ToString()");

        // No bleed: rendering for a log never changes what the hub's own options write.
        var wire = JsonSerializer.Serialize(stored, Mesh.JsonSerializerOptions);
        wire.Should().Contain(Diagnosis, "the hub's own options — persistence and the wire — are never redacted")
            .And.Contain(VisitNote);

        // The Trace capture: nothing the mesh wrote while doing all of the above holds a marker.
        var lines = capture.Lines;
        lines.Should().Contain(l => l.Level <= LogLevel.Debug && l.Text.Contains(path, StringComparison.Ordinal),
            "CONTROL — the recorder sees Debug-and-below lines about this node, or it proves nothing");
        foreach (var leak in lines.Where(l => l.Text.Contains(Diagnosis) || l.Text.Contains(VisitNote)).Take(5))
            Output.WriteLine($"LEAK {leak.Level} {leak.Category}: {leak.Text}");
        lines.Should().NotContain(l => l.Text.Contains(Diagnosis) || l.Text.Contains(VisitNote),
            "no log line at any level may carry a [SensitiveData] value");
    }

    /// <summary>
    /// A deserialisation failure echoes nothing: content whose shape does not bind (a string where
    /// a list is declared) is created through the mesh; whatever the platform logs and whatever
    /// error text the write answers, neither holds the marked value.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ADeserializationFailure_EchoesNoMarkedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = "bad" + Guid.NewGuid().ToString("N")[..8];
        var json = $$"""{"$type":"{{nameof(PatientRecord)}}","diagnosis":"{{Diagnosis}}","ward":"{{Ward}}","visits":"not-a-list"}""";

        string answer;
        try
        {
            var created = await MeshService.CreateNode(new MeshNode(id, TestPartition)
                {
                    Name = "broken",
                    NodeType = PatientType,
                    State = MeshNodeState.Active,
                    Content = JsonDocument.Parse(json).RootElement.Clone(),
                })
                .Take(1).Timeout(60.Seconds()).Await(ct);
            answer = $"created {created.Path}";
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            // A refusal is an answer too — and its text is built from the payload.
            answer = ex.ToString();
        }
        Output.WriteLine(answer);
        answer.Should().NotContain(Diagnosis, "an error answer names the problem, never the value");

        // Read it back, so the read-side binding (the degrade seam) runs as well.
        await MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{TestPartition}/{id}").AsSystem())
            .Take(1).Timeout(60.Seconds()).Await(ct);

        var lines = capture.Lines;
        foreach (var leak in lines.Where(l => l.Text.Contains(Diagnosis)).Take(5))
            Output.WriteLine($"LEAK {leak.Level} {leak.Category}: {leak.Text}");
        lines.Should().NotContain(l => l.Text.Contains(Diagnosis),
            "a deserialisation failure is logged by path and type — never by echoing the payload");
    }

    /// <summary>
    /// Free text written ABOUT content (a gist an agent wrote) cannot carry an attribute, so the
    /// egress paths scrub the content's marked values out of it.
    /// </summary>
    [Fact]
    public void FreeTextWrittenAboutContent_IsScrubbedOfItsMarkedValues()
    {
        var values = Mesh.SensitiveValues(Patient(), out var complete);
        complete.Should().BeTrue("every object of a registered content type is typed");
        values.Should().Contain(Diagnosis).And.Contain(VisitNote).And.Contain("47110815");
        values.Should().NotContain(Ward, "an unmarked value is not collected");

        var untypedValues = Mesh.SensitiveValues(JsonSerializer.SerializeToElement(Patient(), Mesh.JsonSerializerOptions));
        untypedValues.SetEquals(values).Should().BeTrue("untyped content resolves through its $type to the same members");

        var gist = $"Patient on {Ward}; last note {VisitNote.ToLowerInvariant()}, diagnosis {Diagnosis}.";
        var scrubbed = SensitiveDataRedaction.ScrubText(gist, values);
        ShouldHoldNoMarker(scrubbed, "a scrubbed gist");
        scrubbed.Should().Contain(Ward, "CONTROL — the rest of the text is kept");
    }

    /// <summary>
    /// A <c>$type</c> nobody in this process can resolve fails CLOSED in a rendering: which members
    /// are sensitive cannot be known, so every scalar is redacted.
    /// </summary>
    [Fact]
    public void AnUnresolvableType_FailsClosed()
    {
        var rendered = Mesh.RedactJson($$$"""{"$type":"NoSuchType9f1","whatever":"{{{Diagnosis}}}","nested":{"x":"{{{VisitNote}}}"}}""");
        ShouldHoldNoMarker(rendered, "an unresolvable $type");
        rendered.Should().Contain("NoSuchType9f1", "the discriminator still identifies the payload");
        Mesh.RedactJson("{ not json " + Diagnosis).Should().NotContain(Diagnosis, "unparseable text is never echoed");

        Mesh.SensitiveValues(JsonDocument.Parse($$"""{"$type":"NoSuchType9f1","whatever":"{{Diagnosis}}"}""").RootElement,
                out var complete)
            .Should().BeEmpty();
        complete.Should().BeFalse("an egress path must be told that the marked members could not be known");
    }

    /// <summary>A base type marking a VIRTUAL member.</summary>
    public record MarkedBase
    {
        /// <summary>Marked here, overridden below without repeating the attribute.</summary>
        [SensitiveData] public virtual string? Secret { get; init; }
    }

    /// <summary>Overrides the marked member and does not repeat the attribute.</summary>
    public record Overriding : MarkedBase
    {
        /// <inheritdoc />
        public override string? Secret { get; init; }
    }

    /// <summary>A type with marked members that a CUSTOM converter writes.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(CustomWrittenConverter))]
    public record CustomWritten([SensitiveData] string Secret);

    /// <summary>Writes <see cref="CustomWritten"/> in its own shape.</summary>
    public sealed class CustomWrittenConverter : System.Text.Json.Serialization.JsonConverter<CustomWritten>
    {
        /// <inheritdoc />
        public override CustomWritten Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(JsonDocument.ParseValue(ref reader).RootElement.GetProperty("s").GetString()!);

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, CustomWritten value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("s", value.Secret);
            writer.WriteEndObject();
        }
    }

    /// <summary>Holds the edge shapes.</summary>
    public record Holder
    {
        /// <summary>A custom-written member.</summary>
        public CustomWritten? Custom { get; init; }

        /// <summary>An untyped JSON member.</summary>
        public JsonElement? Extra { get; init; }
    }

    /// <summary>Writes a <see cref="PatientRecord"/> in its own shape — used at PROPERTY level.</summary>
    public sealed class PatientAsTextConverter : System.Text.Json.Serialization.JsonConverter<PatientRecord>
    {
        /// <inheritdoc />
        public override PatientRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString());

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, PatientRecord value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Diagnosis);
    }

    /// <summary>An UNMARKED member whose own converter writes the marked member under no name.</summary>
    public record PropertyConverted
    {
        /// <summary>Written by <see cref="PatientAsTextConverter"/>.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(PatientAsTextConverter))]
        public PatientRecord? Patient { get; init; }
    }

    /// <summary>A type written as a bare string by its own converter.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(TextWrittenConverter))]
    public record TextWritten([SensitiveData] string Secret);

    /// <summary>Writes <see cref="TextWritten"/> as a JSON string.</summary>
    public sealed class TextWrittenConverter : System.Text.Json.Serialization.JsonConverter<TextWritten>
    {
        /// <inheritdoc />
        public override TextWritten Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString()!);

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, TextWritten value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Secret);
    }

    /// <summary>A marked dictionary, and a marked extension bag.</summary>
    public record Bags
    {
        /// <summary>Its KEYS are data.</summary>
        [SensitiveData] public ImmutableDictionary<string, string>? ByPatient { get; init; }

        /// <summary>Flattened into the object: its entries match no member name.</summary>
        [SensitiveData, System.Text.Json.Serialization.JsonExtensionData]
        public System.Collections.Generic.Dictionary<string, JsonElement>? Extra { get; init; }
    }

    /// <summary>Marked on the BASE's positional parameter only.</summary>
    public record PositionalBase([SensitiveData] string Secret);

    /// <summary>Re-declares the parameter unmarked.</summary>
    public record PositionalDerived(string Secret) : PositionalBase(Secret);

    /// <summary>A sealed element with a marked member, held in a read-only list.</summary>
    public sealed record Entry
    {
        /// <summary>Marked.</summary>
        [SensitiveData] public string? Note { get; init; }
    }

    /// <summary>Holds read-only collections and a document.</summary>
    public record Lists
    {
        /// <summary>Kind None under the hub's ReadOnlyCollectionConverterFactory.</summary>
        public IReadOnlyList<Entry>? Entries { get; init; }

        /// <summary>A raw document.</summary>
        public JsonDocument? Document { get; init; }
    }

    /// <summary>An UNMARKED wrapper whose own converter writes a descendant's marked value.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(EnvelopeConverter))]
    public record Envelope(PatientRecord Patient);

    /// <summary>Writes the wrapped patient's diagnosis verbatim.</summary>
    public sealed class EnvelopeConverter : System.Text.Json.Serialization.JsonConverter<Envelope>
    {
        /// <inheritdoc />
        public override Envelope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(new PatientRecord(reader.GetString()));

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, Envelope value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Patient.Diagnosis);
    }

    /// <summary>The third review round's edges.</summary>
    [Fact]
    public void TheEdges_BaseParameters_ExtensionKeys_UnknownKeys_MalformedTypes_Documents_ReadOnlyLists_AndDescendants()
    {
        var registered = Mesh.TypeRegistry;
        registered.WithTypes(typeof(PositionalDerived), typeof(Entry), typeof(Lists));

        // A derived positional record re-declaring the base's marked parameter.
        ShouldHoldNoMarker(Mesh.RenderForLog(new PositionalDerived(Diagnosis)), "a derived positional record");
        Mesh.SensitiveValues(new PositionalDerived(Diagnosis)).Should().Contain(Diagnosis);

        // A marked extension bag's KEY.
        var bags = new Bags { Extra = new() { ["PATIENT-55001"] = JsonDocument.Parse("1").RootElement.Clone() } };
        Mesh.SensitiveValues(bags).Should().Contain("PATIENT-55001");
        Mesh.RedactJson(JsonSerializer.Serialize(bags, Mesh.JsonSerializerOptions)).Should().NotContain("PATIENT-55001");

        // An unknown type's keys are data too; a malformed discriminator names nothing.
        Mesh.RedactJson("""{"$type":"UnavailablePatientType","byPatient":{"PATIENT-77123":"x"}}""")
            .Should().NotContain("PATIENT-77123").And.Contain("UnavailablePatientType");
        foreach (var malformed in new[] { "42", "null", "[]", "{}" })
        {
            Mesh.RedactJson($$"""{"$type":{{malformed}},"diagnosis":"{{Diagnosis}}"}""").Should().NotContain(Diagnosis, malformed);
            Mesh.SensitiveValues(JsonDocument.Parse($$"""{"$type":{{malformed}},"diagnosis":"x"}""").RootElement, out var c);
            c.Should().BeFalse($"a $type of {malformed} is not a discriminator");
        }

        // A JsonDocument — at the root and as a member.
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(Patient(), Mesh.JsonSerializerOptions));
        ShouldHoldNoMarker(Mesh.RenderForLog(document), "a JsonDocument root");
        ShouldHoldNoMarker(Mesh.RenderForLog(new Lists { Document = document }), "a JsonDocument member");

        // A read-only list of sealed elements (no $type on the elements).
        var lists = new Lists { Entries = [new Entry { Note = VisitNote }] };
        ShouldHoldNoMarker(Mesh.RenderForLog(lists), "a typed read-only list");
        ShouldHoldNoMarker(Mesh.RedactJson(JsonSerializer.Serialize(lists, Mesh.JsonSerializerOptions)), "an untyped read-only list");
        Mesh.SensitiveValues(lists).Should().Contain(VisitNote);

        // An unmarked wrapper whose converter writes a marked descendant.
        ShouldHoldNoMarker(Mesh.RenderForLog(new Envelope(new PatientRecord(Diagnosis))), "a converter over a marked descendant");
        Mesh.SensitiveValues(new Envelope(new PatientRecord(Diagnosis)), out var envelopeComplete).Should().Contain(Diagnosis);
        envelopeComplete.Should().BeFalse();
    }

    /// <summary>The second review round's edges.</summary>
    [Fact]
    public void TheEdges_PropertyConverters_TextRoots_DictionaryKeys_AndExtensionData_ExposeNothing()
    {
        // A property-level converter over a type with marked members.
        var converted = new PropertyConverted { Patient = new PatientRecord(Diagnosis) };
        ShouldHoldNoMarker(Mesh.RenderForLog(converted), "a property-level converter");
        var convertedJson = JsonSerializer.Serialize(converted, Mesh.JsonSerializerOptions);
        convertedJson.Should().Contain(Diagnosis, "CONTROL — the hub's own options write it");
        Mesh.SensitiveValues(converted, out var convertedComplete).Should().Contain(Diagnosis);
        convertedComplete.Should().BeFalse("the converter's JSON cannot be mapped to members");

        // A marked root written as a bare string.
        Mesh.SensitiveValues(new TextWritten(Diagnosis), out var textComplete).Should().Contain(Diagnosis);
        textComplete.Should().BeFalse();
        ShouldHoldNoMarker(Mesh.RenderForLog(new TextWritten(Diagnosis)), "a root written as text");

        // A marked dictionary's keys are data.
        var bags = new Bags
        {
            ByPatient = ImmutableDictionary<string, string>.Empty.Add("PATIENT-77123", "diagnosis on file"),
            Extra = new() { ["noteOfVisit"] = JsonDocument.Parse($"\"{VisitNote}\"").RootElement.Clone() },
        };
        var bagValues = Mesh.SensitiveValues(bags);
        bagValues.Should().Contain("PATIENT-77123", "a marked dictionary's keys are values");
        bagValues.Should().Contain(VisitNote, "a marked extension bag's entries are values");
        bagValues.Should().NotContain("byPatient", "a type's own member names are never collected");

        // Extension data: typed and untyped renderings.
        ShouldHoldNoMarker(Mesh.RenderForLog(bags), "a typed marked extension bag");
        var untyped = Mesh.RedactJson(JsonSerializer.Serialize(bags, Mesh.JsonSerializerOptions));
        ShouldHoldNoMarker(untyped, "an untyped marked extension bag");
        untyped.Should().NotContain("PATIENT-77123");
    }

    /// <summary>
    /// The edges the review of the redaction point named: each one exposed a marked (or unknowable)
    /// value before it was closed.
    /// </summary>
    [Fact]
    public void TheEdges_AnOverride_ACustomConverter_AnUnknownType_AndAPlainObject_ExposeNothing()
    {
        // A nested $type is a member's value: only the ROOT discriminator identifies a payload.
        DeliveryPayloadBounds.Discriminator($$"""{"privateData":{"$type":"{{Diagnosis}}"},"$type":"Envelope"}""")
            .Should().Be("Envelope");
        DeliveryPayloadBounds.Discriminator($$"""{"privateData":{"$type":"{{Diagnosis}}" """).Should().BeNull();

        // An override that does not repeat the attribute keeps the protection (Inherited = true).
        ShouldHoldNoMarker(Mesh.RenderForLog(new Overriding { Secret = Diagnosis }), "an overriding member");

        // A custom converter hides the members from the resolver: the whole value is redacted.
        var holder = new Holder { Custom = new CustomWritten(Diagnosis) };
        ShouldHoldNoMarker(Mesh.RenderForLog(holder), "a custom-written member");
        var values = Mesh.SensitiveValues(holder, out var complete);
        values.Should().Contain(Diagnosis, "a custom-written type's leaves are all treated as marked");
        complete.Should().BeFalse("its members could not be mapped, so the answer is not complete");

        // An unknown $type inside an untyped (JsonElement) member, and inside a known type: fail closed.
        var extra = new Holder { Extra = JsonDocument.Parse($$"""{"$type":"NoSuchType2c","x":"{{Diagnosis}}"}""").RootElement.Clone() };
        ShouldHoldNoMarker(Mesh.RenderForLog(extra), "an unknown $type under a JsonElement member");
        Mesh.SensitiveValues(extra, out var extraComplete);
        extraComplete.Should().BeFalse();
        var unknownDerived = Mesh.RedactJson($$$"""{"$type":"{{{nameof(PatientRecord)}}}","ward":"{{{Ward}}}","latest":{"$type":"NoSuchVisit","clinic":"{{{VisitNote}}}"}}""");
        ShouldHoldNoMarker(unknownDerived, "an unknown $type under an object member");
        unknownDerived.Should().Contain(Ward, "CONTROL — the known outer type is still redacted by member");

        // Under an unknown OUTER type even a known inner type's unmarked member is redacted.
        ShouldHoldNoMarker(
            Mesh.RedactJson($$$"""{"$type":"NoSuchOuter","inner":{"$type":"{{{nameof(Visit)}}}","clinic":"{{{VisitNote}}}"}}"""),
            "a known type inside an unknown one");

        // A plain System.Object renders, and does not recurse.
        Mesh.RenderForLog(new object()).Should().Be("{}");
    }

    /// <summary>Records every line at every level, with its category. Instance state only.</summary>
    private sealed class TraceCapture : ILoggerProvider
    {
        private ImmutableList<(string Category, LogLevel Level, string Text)> lines =
            ImmutableList<(string, LogLevel, string)>.Empty;

        public ImmutableList<(string Category, LogLevel Level, string Text)> Lines => lines;

        public ILogger CreateLogger(string categoryName) => new Recording(this, categoryName);

        public void Dispose() { }

        private void Add(string category, LogLevel level, string text)
            => ImmutableInterlocked.Update(ref lines, l => l.Add((category, level, text)));

        private sealed class Recording(TraceCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                // The exception's text too: an exception message is text that reaches a log.
                => owner.Add(category, logLevel, formatter(state, exception) + (exception is null ? "" : "\n" + exception));
        }
    }
}

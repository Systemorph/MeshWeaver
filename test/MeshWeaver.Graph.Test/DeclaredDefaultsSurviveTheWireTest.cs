#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using MeshWeaver.ActionLinks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// An explicitly stored value must never come back as the member's DECLARED default. The hub
/// serializes with <see cref="JsonIgnoreCondition.WhenWritingDefault"/>, which compares against the
/// CLR default, so <c>bool Enabled = true</c> stored <c>false</c> was dropped on write and the reader
/// re-applied <c>true</c>.
///
/// <para><b>Measured on the public instance:</b> <c>Store/CouponJourney/ReviewRule</c> is committed
/// with <c>"enabled": false</c> (it ships disabled; the owner approves the enable) and read
/// <c>enabled: true</c> after its repository sync — an owner-approval gate armed with no approval.</para>
///
/// <para>The fix is for the whole class (<c>DeclaredDefaultsSurviveTheWire</c> in the hub's type-info
/// resolver): a CLR-default value is omitted only when the constructor IL proves the reader rebuilds
/// it; nothing is constructed or cached to decide. The guard runs over shapes no platform type has yet. The negative control runs the
/// same round trip through options without the fix, so a round trip that could not lose the value
/// cannot pass for a fixed one.</para>
/// </summary>
public class DeclaredDefaultsSurviveTheWireTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "TestData";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).DeclareActionHandlerRuleNamespaces(Partition);

    public enum Mode { Off, On }

    /// <summary>Every scalar shape whose initializer is not its CLR default.</summary>
    public sealed record InitializerDefaults
    {
        public bool Flag { get; init; } = true;
        public bool? NullableFlag { get; init; } = true;
        public int Count { get; init; } = 1;
        public Mode Mode { get; init; } = Mode.On;
        public string? Text { get; init; } = "declared";
    }

    /// <summary>Constructor-parameter defaults, plus an initializer on a type built through a constructor.</summary>
    public sealed record ParameterDefaults(string Id, bool Flag = true, int Count = 2)
    {
        public bool Enabled { get; init; } = true;
    }

    /// <summary>Declared default IS the CLR default — must still be omitted, as before.</summary>
    public sealed record ClrDefaults
    {
        public bool Flag { get; init; }
        public int Count { get; init; }
    }

    /// <summary>An initializer computed from a constructor argument — the MeshNode.MainNode shape.</summary>
    public sealed record ArgumentDependent(string Id)
    {
        public string? Main { get; init; } = Id;
    }

    /// <summary>An initializer that throws for a null argument, so the all-defaults probe fails.</summary>
    public sealed record ThrowsOnNullArgument(string Area)
    {
        public string? Title { get; init; } = Area.ToUpperInvariant();
        public string? Image { get; init; } = "declared.png";
    }

    /// <summary>A getter that throws on a probe instance but not on a real one.</summary>
    public sealed record ThrowingGetter(string Text)
    {
        public int Length { get => Text.Length; init { } }
    }

    /// <summary>An initializer over SEVERAL arguments, which any fixed set of probes can miss.</summary>
    public sealed record MixedArguments(bool Left, bool Right)
    {
        public bool Flag { get; init; } = Left != Right;
    }

    /// <summary>A MUTABLE type whose initializer reads a constructor argument that can change between writes.</summary>
    public sealed class MutableFromArgument(bool left)
    {
        public bool Left { get; set; } = left;
        public bool Flag { get; set; } = left;
    }

    /// <summary>Counts how often a constructor or initializer ran against it.</summary>
    public sealed class Counter
    {
        public int Count { get; set; }
        public int Increment() => ++Count;
    }

    /// <summary>A constructor body with a side effect on a live argument (round 6: a rebuild would bump it).</summary>
    public sealed record Sparse
    {
        public Sparse(string id, Counter calls)
        {
            Id = id;
            Calls = calls;
            calls.Count++;
        }

        public string Id { get; }
        public Counter Calls { get; }
        public string? Name { get; init; }
        public int? Order { get; init; }
    }

    /// <summary>An initializer with a side effect on a live argument.</summary>
    public sealed record CountedInitializer(Counter Calls)
    {
        public int Seen { get; init; } = Calls.Increment();
    }

    /// <summary>
    /// Round 6, thread 1: a member derived from a constructor input whose own write condition omits it.
    /// <c>new DerivedFromOmittedInput(null)</c> omits <c>Text</c> (its author's condition), so the reader
    /// passes <c>"declared"</c> and the initializer would rebuild <c>Title</c> from that.
    /// </summary>
    public sealed record DerivedFromOmittedInput(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = "declared")
    {
        public string? Title { get; init; } = Text;
    }

    /// <summary>Positional parameter whose default IS the CLR default — proven, so still omitted.</summary>
    public sealed record PositionalClrDefault(string Id, bool Flag = false, int Count = 0);

    /// <summary>
    /// Round 6, thread 3: two argument-dependent members around a getter that serializes the same
    /// instance again, so two writes of one instance interleave (W1.A → W2.A → W2.B → W1.B).
    /// </summary>
    public sealed class Interleaved(bool left)
    {
        [JsonIgnore] public JsonSerializerOptions? Options { get; set; }
        private bool inside;

        public bool Left { get; set; } = left;
        public bool A { get; set; } = left;

        public bool Reentrant
        {
            get
            {
                if (Options is not null && !inside)
                {
                    inside = true;
                    try { JsonSerializer.Serialize(this, Options); }
                    finally { inside = false; }
                }
                return true;
            }
            set { }
        }

        public bool B { get; set; } = left;
    }

    /// <summary>Proven CLR default on a constructor-built type — must still be omitted.</summary>
    public sealed record ProvenDefault(string Id)
    {
        public bool Flag { get; init; }
    }

    /// <summary>Review round 7: a default applied by an OnDeserializing callback, not the constructor.</summary>
    public sealed class DefaultedOnDeserializing : IJsonOnDeserializing
    {
        public bool Enabled { get; set; }
        public void OnDeserializing() => Enabled = true;
    }

    /// <summary>Review round 7: a hand-written setter of one member that sets ANOTHER member.</summary>
    public sealed class CrossMemberSetter
    {
        public string? Text { get => null; set => Flag = true; }
        public bool Flag { get; set; }
    }

    /// <summary>A normalizing setter (the <c>UiControl.Skins</c> shape): it calls out to compute the
    /// stored value, but never hands <c>this</c> to anything — the CLR-default flag stays provable.</summary>
    public sealed class NormalizingSetter
    {
        private ImmutableList<string> items = [];
        public ImmutableList<string> Items { get => items; init => items = value.ToImmutableList(); }
        public bool Flag { get; init; }
    }

    /// <summary>A setter that hands <c>this</c> to a call, which can set another member.</summary>
    public sealed class SetterPassingThis
    {
        private string? text;
        public string? Text { get => text; set => text = Arm(this, value); }
        public bool Flag { get; set; }
        private static string? Arm(SetterPassingThis self, string? value)
        {
            self.Flag = true;
            return value;
        }
    }

    /// <summary>Review round 7: an initializer writes the parameter (starg) before the direct store.</summary>
    public sealed class ArgumentWritten(bool flag = false)
    {
        public bool Prepared { get; init; } = flag = true;
        public bool Flag { get; set; } = flag;
    }

    /// <summary>Review round 7: the parameter's address escapes (ldarga) before the direct store.</summary>
    public sealed class ArgumentAddressEscapes(bool flag = false)
    {
        public bool Prepared { get; init; } = Arm(ref flag);
        public bool Flag { get; set; } = flag;
        private static bool Arm(ref bool value) => value = true;
    }

    /// <summary>Review round 8: a child with no constructor store, populated into an existing instance.</summary>
    public sealed class PopulatedFlags
    {
        public bool Enabled { get; set; }
    }

    /// <summary>Review round 8: the containing PROPERTY populates its initialized child.</summary>
    public sealed class PopulatingProperty
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public PopulatedFlags Flags { get; set; } = new() { Enabled = true };
    }

    /// <summary>Review round 8: the containing TYPE prefers populating its properties.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public sealed class PopulatingType
    {
        public PopulatedFlags Flags { get; set; } = new() { Enabled = true };
    }

    /// <summary>Review round 8: a child whose only scalar is never omitted (its initializer is not the CLR default).</summary>
    public sealed class WrittenFlags
    {
        public bool Enabled { get; set; } = true;
    }

    /// <summary>Review round 8: populating a child that omits nothing is safe and stays allowed.</summary>
    public sealed class PopulatingWrittenChild
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public WrittenFlags Flags { get; set; } = new() { Enabled = true };
    }

    /// <summary>Review round 8: populated only through the options' preference.</summary>
    public sealed class PreferringParent
    {
        public PopulatedFlags Flags { get; set; } = new() { Enabled = true };
    }

    /// <summary>Review round 9: an explicit layout overlaps a candidate field with an ignored, initialized one.</summary>
    [StructLayout(LayoutKind.Explicit)]
    public sealed class OverlappingFields
    {
        [FieldOffset(0)] public bool Flag;
        [FieldOffset(0)] [JsonIgnore] public bool Seed = true;
    }

    /// <summary>Review round 9: a child with no initializer whose factory a resolver modifier replaces.</summary>
    public sealed class FactoryFlags
    {
        public bool Enabled { get; set; }
    }

    private static readonly Action<JsonTypeInfo> ArmingFactory = typeInfo =>
    {
        if (typeInfo.Type == typeof(FactoryFlags))
            typeInfo.CreateObject = () => new FactoryFlags { Enabled = true };
    };

    /// <summary>The hub's options with a resolver whose own modifier replaces the factory (before the rule).</summary>
    private JsonSerializerOptions WithModifierBeforeTheRule()
    {
        var resolver = new PolymorphicTypeInfoResolver(Mesh.TypeRegistry);
        resolver.Modifiers.Add(ArmingFactory);
        return new JsonSerializerOptions(Mesh.JsonSerializerOptions) { TypeInfoResolver = resolver };
    }

    /// <summary>The hub's options with the factory replaced by an outer modifier (after the rule).</summary>
    private JsonSerializerOptions WithModifierAfterTheRule() =>
        new(Mesh.JsonSerializerOptions)
        {
            TypeInfoResolver = new PolymorphicTypeInfoResolver(Mesh.TypeRegistry).WithAddedModifier(ArmingFactory),
        };

    private static JsonSerializerOptions WithoutTheFix() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private static T RoundTrip<T>(T value, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, options), options)!;

    private static InitializerDefaults AllClrDefaults() =>
        new() { Flag = false, NullableFlag = null, Count = 0, Mode = Mode.Off, Text = null };

    [Fact]
    public void ActionHandlerRule_Enabled_false_survives_the_stored_node_shape()
    {
        var options = Mesh.JsonSerializerOptions;
        var node = new MeshNode("ReviewRule", "Store/CouponJourney")
        {
            NodeType = ActionHandlerRuleNodeType.NodeType,
            Content = new ActionHandlerRule { Id = "ReviewRule", Enabled = false, Claims = ["coupon.review"] },
        };

        var json = JsonSerializer.Serialize(node, options);
        json.Should().Contain("\"enabled\":false", "the stored JSON must carry the explicit false");
        JsonSerializer.Deserialize<MeshNode>(json, options)!
            .ContentAs<ActionHandlerRule>(options)!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_disabled_rule_read_back_from_its_stored_json_claims_nothing()
    {
        var options = Mesh.JsonSerializerOptions;
        var disabledKey = "test.disabled-" + Guid.NewGuid().ToString("N")[..8];
        var controlKey = "test.control-" + Guid.NewGuid().ToString("N")[..8];

        // The node exactly as a store read hands it back: serialized and deserialized with the hub's options.
        var stored = JsonSerializer.Deserialize<MeshNode>(JsonSerializer.Serialize(
            new MeshNode("off" + Guid.NewGuid().ToString("N")[..6], $"{Partition}/ActionHandlers")
            {
                Name = "disabled rule",
                NodeType = ActionHandlerRuleNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new ActionHandlerRule
                {
                    Claims = [disabledKey], Handler = $"{Partition}/Coupons/Review", Enabled = false,
                },
            }, options), options)!;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(stored).FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        // Positive signal: an enabled rule written AFTER it is in the chain, so the snapshot has read both.
        await meshService.CreateNode(new MeshNode("on" + Guid.NewGuid().ToString("N")[..6], $"{Partition}/ActionHandlers")
        {
            Name = "control rule",
            NodeType = ActionHandlerRuleNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new ActionHandlerRule { Claims = [controlKey], Handler = $"{Partition}/Coupons/Review" },
        }).FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        var chain = await Mesh.ActionHandlerChain()
            .Where(c => c.Handlers.Any(h => h.Capability.HandlerKey == controlKey))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        chain.Handlers.Should().NotContain(h => h.Capability.HandlerKey == disabledKey,
            "a rule stored enabled:false must stay disabled after its JSON round trip");
    }

    [Fact]
    public void Every_scalar_shape_keeps_its_clr_default_against_a_different_declared_default()
    {
        var options = Mesh.JsonSerializerOptions;
        RoundTrip(AllClrDefaults(), options).Should().Be(AllClrDefaults());

        var positional = new ParameterDefaults("p", Flag: false, Count: 0) { Enabled = false };
        RoundTrip(positional, options).Should().Be(positional);
    }

    [Fact]
    public void Absent_members_still_read_their_declared_default_and_clr_defaults_stay_omitted()
    {
        var options = Mesh.JsonSerializerOptions;
        JsonSerializer.Deserialize<InitializerDefaults>("{}", options).Should().Be(new InitializerDefaults());
        RoundTrip(new InitializerDefaults(), options).Should().Be(new InitializerDefaults());

        var json = JsonSerializer.Serialize(new ClrDefaults(), options);
        json.Should().NotContain("flag").And.NotContain("count",
            "a member whose declared default is the CLR default serialises compactly, exactly as before");
    }

    [Fact]
    public void An_initializer_computed_from_a_constructor_argument_keeps_an_explicit_null()
    {
        var options = Mesh.JsonSerializerOptions;
        RoundTrip(new ArgumentDependent("n") { Main = null }, options).Main.Should().BeNull();

        var node = JsonSerializer.Deserialize<MeshNode>("{\"id\":\"n\",\"mainNode\":null}", options)!;
        node.MainNode.Should().BeNull();
        RoundTrip(node, options).MainNode.Should().BeNull("the stored null is not the argument-derived default");
    }

    [Fact]
    public void An_initializer_over_mixed_arguments_keeps_an_explicit_false()
    {
        var options = Mesh.JsonSerializerOptions;
        RoundTrip(new MixedArguments(true, false) { Flag = false }, options).Flag.Should().BeFalse();
        // Not provable statically, so always written — even where the reader would agree.
        JsonSerializer.Serialize(new MixedArguments(true, true), options).Should().Contain("\"flag\":false");
    }

    [Fact]
    public void A_repeated_write_of_a_mutated_instance_uses_its_current_constructor_inputs()
    {
        var options = Mesh.JsonSerializerOptions;
        var instance = new MutableFromArgument(false);
        JsonSerializer.Serialize(instance, options).Should().Contain("\"flag\":false",
            "an argument-derived member is never provable, so it is always written");

        instance.Left = true;
        var back = JsonSerializer.Deserialize<MutableFromArgument>(JsonSerializer.Serialize(instance, options), options)!;
        back.Left.Should().BeTrue();
        back.Flag.Should().BeFalse("the second write carries the explicit false; no earlier verdict is reused");
    }

    [Fact]
    public void Negative_control_without_the_fix_the_mutated_instance_loses_the_false()
    {
        var instance = new MutableFromArgument(false) { Left = true };
        var options = WithoutTheFix();
        JsonSerializer.Deserialize<MutableFromArgument>(JsonSerializer.Serialize(instance, options), options)!
            .Flag.Should().BeTrue("if this ever fails, the repeated-write case no longer exercises the trap");
    }

    [Fact]
    public void A_serialized_instance_is_not_retained_after_the_write()
    {
        var options = Mesh.JsonSerializerOptions;
        var weak = SerializeAndForget(options);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        weak.IsAlive.Should().BeFalse("the serializer metadata must not keep the last payload reachable");
    }

    [Fact]
    public void Negative_control_a_held_instance_is_seen_as_retained()
    {
        // The reachability instrument can fail: an instance something still holds reads alive.
        var held = new MixedArguments(true, true);
        var weak = new WeakReference(held);
        JsonSerializer.Serialize(held, Mesh.JsonSerializerOptions);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        weak.IsAlive.Should().BeTrue();
        GC.KeepAlive(held);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference SerializeAndForget(JsonSerializerOptions options)
    {
        // CLR-default Flag on a constructor-built type.
        var instance = new MixedArguments(true, true);
        JsonSerializer.Serialize(instance, options);
        return new WeakReference(instance);
    }

    [Fact]
    public void Serialization_never_invokes_a_constructor_or_initializer()
    {
        // Round 6, thread 2: a rebuild ran the constructor against the live payload and mutated it.
        var options = Mesh.JsonSerializerOptions;
        var calls = new Counter();
        var sparse = new Sparse("s", calls);
        var counted = new CountedInitializer(new Counter());
        calls.Count.Should().Be(1);
        counted.Calls.Count.Should().Be(1);

        var first = JsonSerializer.Serialize(sparse, options);
        JsonSerializer.Serialize(sparse, options).Should().Be(first, "serializing twice yields the same bytes");
        JsonSerializer.Serialize(counted, options);
        JsonSerializer.Serialize(counted, options);

        calls.Count.Should().Be(1, "no constructor body runs while the instance is written");
        counted.Calls.Count.Should().Be(1, "no initializer runs while the instance is written");
        first.Should().Contain("\"count\":1", "the source graph is written as it is, unchanged");
    }

    [Fact]
    public void A_member_derived_from_an_omitted_constructor_input_round_trips()
    {
        // Round 6, thread 1.
        var options = Mesh.JsonSerializerOptions;
        var json = JsonSerializer.Serialize(new DerivedFromOmittedInput(null), options);
        json.Should().NotContain("\"text\"", "Text keeps its author's WhenWritingNull condition");
        json.Should().Contain("\"title\":null", "a derived member is not provable, so it is written");
        var back = JsonSerializer.Deserialize<DerivedFromOmittedInput>(json, options)!;
        back.Title.Should().BeNull();
    }

    [Fact]
    public void Negative_control_without_the_fix_the_derived_member_reads_the_declared_input()
    {
        RoundTrip(new DerivedFromOmittedInput(null), WithoutTheFix()).Title.Should().Be("declared",
            "if this ever fails, the derived-member case no longer exercises the trap");
    }

    [Fact]
    public void Interleaved_writes_of_one_instance_then_a_change_keep_the_explicit_false()
    {
        // Round 6, thread 3: the verdict is a per-member constant — no write state to go stale.
        var options = Mesh.JsonSerializerOptions;
        var instance = new Interleaved(false) { Options = options };
        JsonSerializer.Serialize(instance, options);

        instance.Options = null;
        instance.Left = true;
        var back = JsonSerializer.Deserialize<Interleaved>(JsonSerializer.Serialize(instance, options), options)!;
        back.Left.Should().BeTrue();
        back.A.Should().BeFalse();
        back.B.Should().BeFalse();
    }

    [Fact]
    public void Negative_control_without_the_fix_the_interleaved_shape_loses_the_false()
    {
        var instance = new Interleaved(false) { Left = true };
        var back = RoundTrip(instance, WithoutTheFix());
        back.A.Should().BeTrue("if this ever fails, the interleaved case no longer exercises the trap");
        back.B.Should().BeTrue();
    }

    [Fact]
    public void A_positional_parameter_whose_default_is_the_clr_default_stays_omitted()
    {
        var json = JsonSerializer.Serialize(new PositionalClrDefault("x"), Mesh.JsonSerializerOptions);
        json.Should().NotContain("flag").And.NotContain("count");
        RoundTrip(new PositionalClrDefault("x"), Mesh.JsonSerializerOptions).Should().Be(new PositionalClrDefault("x"));
    }

    [Fact]
    public void A_sparse_mesh_node_round_trips_compactly()
    {
        var options = Mesh.JsonSerializerOptions;
        var node = new MeshNode("n", "a");
        var json = JsonSerializer.Serialize(node, options);
        json.Should().NotContain("\"name\"").And.NotContain("\"description\"").And.NotContain("\"icon\"");
        MeshNode.SerializedEquals(node, RoundTrip(node, options), options).Should().BeTrue();
    }

    [Fact]
    public void Negative_control_without_the_fix_mixed_arguments_lose_the_false()
    {
        RoundTrip(new MixedArguments(true, false) { Flag = false }, WithoutTheFix()).Flag.Should().BeTrue(
            "if this ever fails, the mixed-argument case no longer exercises the trap");
    }

    [Fact]
    public void An_unprovable_initializer_keeps_every_member_written()
    {
        var options = Mesh.JsonSerializerOptions;
        RoundTrip(new ThrowsOnNullArgument("a") { Image = null, Title = null }, options)
            .Should().Be(new ThrowsOnNullArgument("a") { Image = null, Title = null });

        var area = new MeshWeaver.Layout.LayoutAreaDefinition("Area", "/Area") { ImageUrl = null };
        RoundTrip(area, options).ImageUrl.Should().BeNull();
    }

    [Fact]
    public void A_getter_that_throws_on_a_probe_does_not_break_serialization_of_a_valid_instance()
    {
        JsonSerializer.Serialize(new ThrowingGetter("abc"), Mesh.JsonSerializerOptions)
            .Should().Contain("\"length\":3");
    }

    [Fact]
    public void A_proven_clr_default_on_a_constructor_built_type_stays_omitted()
    {
        JsonSerializer.Serialize(new ProvenDefault("x"), Mesh.JsonSerializerOptions).Should().NotContain("flag");
    }

    [Fact]
    public void Negative_control_without_the_fix_argument_dependent_and_unprobeable_values_are_lost()
    {
        var options = WithoutTheFix();
        RoundTrip(new ArgumentDependent("n") { Main = null }, options).Main.Should().Be("n",
            "if this ever fails, the argument-dependent case no longer exercises the trap");
        RoundTrip(new ThrowsOnNullArgument("a") { Image = null }, options).Image.Should().Be("declared.png");
    }

    [Fact]
    public void Negative_control_without_the_fix_the_round_trip_loses_the_values()
    {
        var options = WithoutTheFix();
        RoundTrip(AllClrDefaults(), options).Should().Be(new InitializerDefaults(),
            "if this ever fails, the round trip no longer exercises the trap and the guard above measures nothing");
        RoundTrip(new ActionHandlerRule { Enabled = false }, options).Enabled.Should().BeTrue();
    }

    [Fact]
    public void A_default_applied_by_an_on_deserializing_callback_keeps_an_explicit_false()
    {
        RoundTrip(new DefaultedOnDeserializing { Enabled = false }, Mesh.JsonSerializerOptions).Enabled.Should().BeFalse();
    }

    [Fact]
    public void Negative_control_without_the_fix_the_on_deserializing_default_wins()
    {
        RoundTrip(new DefaultedOnDeserializing { Enabled = false }, WithoutTheFix()).Enabled.Should().BeTrue(
            "if this ever fails, the OnDeserializing case no longer exercises the trap");
    }

    [Fact]
    public void A_setter_that_sets_another_member_does_not_turn_an_omitted_false_true()
    {
        var options = Mesh.JsonSerializerOptions;
        JsonSerializer.Serialize(new CrossMemberSetter(), options).Should().Contain("\"flag\":false");
        RoundTrip(new CrossMemberSetter(), options).Flag.Should().BeFalse();
    }

    [Fact]
    public void A_normalizing_setter_that_never_hands_out_this_keeps_clr_defaults_omitted()
    {
        var options = Mesh.JsonSerializerOptions;
        var json = JsonSerializer.Serialize(new NormalizingSetter { Items = ["a"] }, options);
        json.Should().NotContain("flag", "the Items setter only normalizes its value; it cannot reach Flag");
        RoundTrip(new NormalizingSetter { Items = ["a"] }, options).Flag.Should().BeFalse();
    }

    [Fact]
    public void A_setter_that_hands_this_to_a_call_keeps_every_scalar_written()
    {
        var options = Mesh.JsonSerializerOptions;
        var value = new SetterPassingThis { Text = "t" };
        value.Flag = false;
        JsonSerializer.Serialize(value, options).Should().Contain("\"flag\":false");
        RoundTrip(value, options).Flag.Should().BeFalse();
    }

    [Fact]
    public void Negative_control_without_the_fix_a_setter_handing_out_this_turns_an_omitted_false_true()
    {
        var value = new SetterPassingThis { Text = "t" };
        value.Flag = false;
        RoundTrip(value, WithoutTheFix()).Flag.Should().BeTrue(
            "if this ever fails, the this-escaping setter no longer exercises the trap");
    }

    /// <summary>
    /// The proof must not accept <c>ldarg.n; stfld</c> as "the parameter's value" once the parameter was
    /// written (<c>starg</c>) or its address escaped (<c>ldarga</c>): the explicit value is written. No
    /// serializer setting can make these two shapes round-trip — the reader passes the value to the
    /// constructor, which overwrites it — so what is asserted is that the value is not OMITTED.
    /// </summary>
    [Fact]
    public void An_argument_written_before_the_direct_store_is_never_proven_and_stays_written()
    {
        var options = Mesh.JsonSerializerOptions;
        JsonSerializer.Serialize(new ArgumentWritten { Flag = false }, options).Should().Contain("\"flag\":false");
        JsonSerializer.Serialize(new ArgumentAddressEscapes { Flag = false }, options).Should().Contain("\"flag\":false");
    }

    [Fact]
    public void Negative_control_without_the_fix_the_written_argument_is_omitted()
    {
        var options = WithoutTheFix();
        JsonSerializer.Serialize(new ArgumentWritten { Flag = false }, options).Should().NotContain("flag\"",
            "if this ever fails, the starg case no longer exercises the trap");
        JsonSerializer.Serialize(new ArgumentAddressEscapes { Flag = false }, options).Should().NotContain("flag\"",
            "if this ever fails, the ldarga case no longer exercises the trap");
    }

    /// <summary>
    /// Review round 8: "no constructor store" proves nothing when the reader POPULATES an existing
    /// instance instead of constructing one — the instance holds whatever its holder initialized. A
    /// holder that populates an omitting child (its property's attribute, or its type's preference) is
    /// refused before anything is written; under the options' preference nothing is proven, so the
    /// explicit false is written and survives.
    /// </summary>
    [Fact]
    public void A_populated_child_never_loses_an_explicit_false()
    {
        var options = Mesh.JsonSerializerOptions;
        ((Action)(() => JsonSerializer.Serialize(new PopulatingProperty { Flags = new() { Enabled = false } }, options)))
            .Should().Throw<InvalidOperationException>();
        ((Action)(() => JsonSerializer.Serialize(new PopulatingType { Flags = new() { Enabled = false } }, options)))
            .Should().Throw<InvalidOperationException>();

        var preferring = new JsonSerializerOptions(options) { PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate };
        var json = JsonSerializer.Serialize(new PreferringParent { Flags = new() { Enabled = false } }, preferring);
        json.Should().Contain("\"enabled\":false");
        JsonSerializer.Deserialize<PreferringParent>(json, preferring)!.Flags.Enabled.Should().BeFalse();
    }

    [Fact]
    public void A_populated_child_that_omits_nothing_round_trips()
    {
        RoundTrip(new PopulatingWrittenChild { Flags = new() { Enabled = false } }, Mesh.JsonSerializerOptions)
            .Flags.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Negative_control_without_the_fix_a_populated_child_reads_its_owners_initializer()
    {
        var options = WithoutTheFix();
        RoundTrip(new PopulatingProperty { Flags = new() { Enabled = false } }, options).Flags.Enabled.Should().BeTrue(
            "if this ever fails, the populated-property case no longer exercises the trap");
        RoundTrip(new PopulatingType { Flags = new() { Enabled = false } }, options).Flags.Enabled.Should().BeTrue(
            "if this ever fails, the populated-type case no longer exercises the trap");
        var preferring = new JsonSerializerOptions(options) { PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate };
        RoundTrip(new PreferringParent { Flags = new() { Enabled = false } }, preferring).Flags.Enabled.Should().BeTrue(
            "if this ever fails, the options-preference case no longer exercises the trap");
    }

    /// <summary>
    /// Review round 9: with an explicit layout one store can initialize an overlapping field, so "the
    /// constructor never stores Flag" proves nothing. Every candidate of such a type is written.
    /// </summary>
    [Fact]
    public void An_overlapping_field_keeps_an_explicit_false()
    {
        var options = Mesh.JsonSerializerOptions;
        var json = JsonSerializer.Serialize(new OverlappingFields { Flag = false }, options);
        json.Should().Contain("\"flag\":false");
        JsonSerializer.Deserialize<OverlappingFields>(json, options)!.Flag.Should().BeFalse();
    }

    [Fact]
    public void Negative_control_without_the_fix_an_overlapping_field_reads_the_seed()
    {
        var options = new JsonSerializerOptions(WithoutTheFix()) { IncludeFields = true };
        RoundTrip(new OverlappingFields { Flag = false }, options).Flag.Should().BeTrue(
            "if this ever fails, the overlapping-field case no longer exercises the trap");
    }

    /// <summary>
    /// Review round 9: the constructor IL describes the reader only when the reader runs that
    /// constructor. A factory a modifier installed — before the rule or after it — is never invoked to
    /// find out; the member is written instead.
    /// </summary>
    [Fact]
    public void A_replaced_factory_never_loses_an_explicit_false()
    {
        foreach (var options in new[] { WithModifierBeforeTheRule(), WithModifierAfterTheRule() })
        {
            var json = JsonSerializer.Serialize(new FactoryFlags { Enabled = false }, options);
            json.Should().Contain("\"enabled\":false");
            JsonSerializer.Deserialize<FactoryFlags>(json, options)!.Enabled.Should().BeFalse();
        }
    }

    [Fact]
    public void Without_a_modifier_the_proven_clr_default_stays_omitted()
    {
        JsonSerializer.Serialize(new FactoryFlags { Enabled = false }, Mesh.JsonSerializerOptions)
            .Should().NotContain("enabled");
    }

    [Fact]
    public void Negative_control_without_the_fix_a_replaced_factory_reads_its_own_value()
    {
        var options = new JsonSerializerOptions(WithoutTheFix())
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier(ArmingFactory),
        };
        RoundTrip(new FactoryFlags { Enabled = false }, options).Enabled.Should().BeTrue(
            "if this ever fails, the replaced-factory case no longer exercises the trap");
    }
}

#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MeshWeaver.ActionLinks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
/// resolver), so the guard runs over shapes no platform type has yet. The negative control runs the
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

    /// <summary>Counts its own constructor calls, so a test can see how many rebuilds one write costs.</summary>
    public sealed class ConstructorCalls
    {
        public int Count { get; set; }
    }

    /// <summary>A sparse constructor-built type: several unset members whose CLR default must be checked by a rebuild.</summary>
    public sealed record Sparse
    {
        public Sparse(string id, ConstructorCalls calls)
        {
            Id = id;
            Calls = calls;
            calls.Count++;
        }

        public string Id { get; }
        public ConstructorCalls Calls { get; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? Icon { get; init; }
        public int? Order { get; init; }
    }

    /// <summary>Proven CLR default on a constructor-built type — must still be omitted.</summary>
    public sealed record ProvenDefault(string Id)
    {
        public bool Flag { get; init; }
    }

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
        // Compact where the reader rebuilds the same value: (true, true) yields Flag == false.
        JsonSerializer.Serialize(new MixedArguments(true, true), options).Should().NotContain("flag");
    }

    [Fact]
    public void A_repeated_write_of_a_mutated_instance_uses_its_current_constructor_inputs()
    {
        var options = Mesh.JsonSerializerOptions;
        var instance = new MutableFromArgument(false);
        JsonSerializer.Serialize(instance, options).Should().NotContain("flag",
            "the reader rebuilds Flag == false from Left == false, so the value is omittable");

        instance.Left = true;
        var back = JsonSerializer.Deserialize<MutableFromArgument>(JsonSerializer.Serialize(instance, options), options)!;
        back.Left.Should().BeTrue();
        back.Flag.Should().BeFalse("the second write must judge against Left == true, not the first write's rebuild");
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
        // CLR-default Flag on a constructor-built type: the rebuild path runs.
        var instance = new MixedArguments(true, true);
        JsonSerializer.Serialize(instance, options);
        return new WeakReference(instance);
    }

    [Fact]
    public void A_sparse_instance_costs_one_rebuild_per_write_not_one_per_unset_member()
    {
        var options = Mesh.JsonSerializerOptions;
        var calls = new ConstructorCalls();
        var sparse = new Sparse("s", calls);
        calls.Count.Should().Be(1);

        var json = JsonSerializer.Serialize(sparse, options);
        json.Should().NotContain("name").And.NotContain("description").And.NotContain("icon").And.NotContain("order");
        calls.Count.Should().Be(2, "one rebuild answers all four unset members of one write");

        JsonSerializer.Serialize(sparse, options);
        calls.Count.Should().Be(3, "the next write rebuilds again from its own current inputs");
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
    public void A_failed_constructor_probe_keeps_every_member_written()
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
}

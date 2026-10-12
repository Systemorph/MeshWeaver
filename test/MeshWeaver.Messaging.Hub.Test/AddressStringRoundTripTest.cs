#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Messaging.Serialization;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>#6604 — an address's string form must parse back to the SAME address.</b>
///
/// <para><see cref="Address.ToString"/> rendered <c>path~host</c> while the parser reads the FIRST
/// <c>~</c> as the end of the host, so a key minted with <c>ToString()</c> — the
/// <c>IPodHubGrain</c> key, a memory-stream id — came back reversed: <c>sync/g~ui/c</c> parsed as
/// <c>ui/c</c> hosted on <c>sync/g</c>, and the pod-hub grain looked up the wrong local route with no
/// error. The canonical form is <c>host~path</c>: the parser, the wire serializer and
/// <c>AccessContext.ObjectId</c> already used it.</para>
///
/// <para>Property-style: a seeded generator walks representative shapes — one to four segments,
/// zero to three nested hosts — plus the address types production actually hosts. A segment holds
/// neither <c>/</c> (the segment separator) nor <c>~</c> (the host separator); both are structural
/// in the string form.</para>
/// </summary>
public class AddressStringRoundTripTest
{
    private static readonly string[] Types =
        ["mesh", "portal", "cache", "client", "sync", "activity", "ui", "le", "ACME", "rbuergi"];

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.@";

    private static string Segment(Random random) =>
        new(Enumerable.Range(0, random.Next(1, 12)).Select(_ => Alphabet[random.Next(Alphabet.Length)]).ToArray());

    private static Address Leaf(Random random) =>
        new([Types[random.Next(Types.Length)], .. Enumerable.Range(0, random.Next(0, 4)).Select(_ => Segment(random))]);

    private static Address Generate(Random random)
    {
        var address = Leaf(random);
        // Each WithHost wraps the existing chain, exactly as HierarchicalRouting stamps a sender
        // on its way out through each parent.
        for (var depth = random.Next(0, 4); depth > 0; depth--)
            address = address.WithHost(Leaf(random));
        return address;
    }

    public static IEnumerable<TheoryDataRow<int>> Seeds() =>
        Enumerable.Range(0, 25).Select(seed => new TheoryDataRow<int>(seed));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ToString_ParsesBackToTheSameAddress(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 200; i++)
        {
            var address = Generate(random);
            var text = address.ToString();

            Address parsed = text;
            parsed.Should().Be(address, $"'{text}' must parse back to the address that rendered it");
            parsed.Path.Should().Be(address.Path);
            text.Should().Be(address.ToFullString(), "there is ONE string form");
            ((string?)address).Should().Be(text, "the implicit string conversion is the same form");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheWireForm_IsTheSameStringAndRoundTrips(int seed)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new AddressConverter());
        var random = new Random(seed);
        for (var i = 0; i < 50; i++)
        {
            var address = Generate(random);
            var json = JsonSerializer.Serialize(address, options);
            json.Should().Be(JsonSerializer.Serialize(address.ToString()),
                "the serializer and ToString() must not disagree about the order");
            JsonSerializer.Deserialize<Address>(json, options).Should().Be(address);
        }
    }

    /// <summary>The production shapes, spelled out — the generator above covers their neighbourhood.</summary>
    [Fact]
    public void RepresentativeHostedAddresses_RenderHostFirst()
    {
        var sync = new Address("sync", "g1").WithHost(new Address("ui", "c1"));
        sync.ToString().Should().Be("ui/c1~sync/g1");

        // Nested: the sender leaves through two parents; the OUTERMOST host comes first.
        var nested = new Address("sync", "g1")
            .WithHost(new Address("ACME", "Doc"))
            .WithHost(new Address("mesh", "root"));
        nested.ToString().Should().Be("mesh/root~ACME/Doc~sync/g1");
        ((Address)nested.ToString()).Should().Be(nested);
        ((Address)nested.ToString()).Host!.Host!.Should().Be(new Address("mesh", "root"));

        new Address("ACME", "Doc").ToString().Should().Be("ACME/Doc", "an unhosted address is its path");
    }

    /// <summary>
    /// Negative control for the generator: the PREVIOUS rendering, <c>path~host</c>, does NOT round
    /// trip for a hosted address. Without this the property above could pass on any rendering the
    /// generator happened never to exercise with a host.
    /// </summary>
    [Fact]
    public void ThePreviousRendering_DoesNotRoundTrip()
    {
        var random = new Random(7);
        var hosted = Enumerable.Range(0, 200).Select(_ => Generate(random)).Where(a => a.Host is not null).ToList();
        hosted.Should().NotBeEmpty("the generator must produce hosted addresses or the property is vacuous");

        static string Legacy(Address a) => a.Host is null ? a.Path : a.Path + '~' + Legacy(a.Host);
        hosted.Should().Contain(a => !((Address)Legacy(a)).Equals(a),
            "path~host is read by the parser as host~path — the reversal #6604 reports");
    }
}

using System.Text.Json;
using MeshWeaver.Mesh;
using Xunit;

// Two compilations of "the same" content class — the @@-include shape from agentic-pensions#12:
// the fact NodeType's own assembly vs the dashboard's included copy. Same short name, same JSON
// shape, DIFFERENT CLR identity. (Different namespaces model different dynamic assemblies without
// needing Roslyn in this test project — the CLR-identity mismatch is identical.)
namespace MeshWeaver.Hosting.Test.OwnerCopy
{
    public record SharedEntry
    {
        public string Position { get; init; } = string.Empty;
        public string Year { get; init; } = string.Empty;
        public double Amount { get; init; }
    }
}

namespace MeshWeaver.Hosting.Test.ConsumerCopy
{
    public record SharedEntry
    {
        public string Position { get; init; } = string.Empty;
        public string Year { get; init; } = string.Empty;
        public double Amount { get; init; }
    }
}

namespace MeshWeaver.Hosting.Test
{
    /// <summary>
    /// Pins the <see cref="MeshNodeContentExtensions.ContentAs{T}"/> recovery contract for Content that
    /// arrives TYPED with a different CLR type of the same shape — the same class compiled into two
    /// dynamic node assemblies, or a same-short-named type resolved by another hub's registry at the
    /// query boundary. Pre-fix the default branch returned a SILENT null, so every projection filtering
    /// on <c>Content is not null</c> dropped the whole result set: the atioz "BalanceSheet dashboards
    /// render empty" outage (agentic-pensions#12) — 200 fact nodes arrived typed with the fact
    /// NodeType's own assembly and the dashboard's loader (holding its @@-included copy) kept none.
    /// </summary>
    public class MeshNodeContentAsTest
    {
        private static readonly JsonSerializerOptions Options = new();

        private static MeshNode NodeWith(object content) =>
            new("entry", "Test/Entries") { Name = "Entry", NodeType = "Test/Entry", Content = content };

        [Fact]
        public void TypedForeignContent_SameShape_IsRecovered()
        {
            var node = NodeWith(new OwnerCopy.SharedEntry
            {
                Position = "Test/Position/FreeFunds",
                Year = "Test/Year/2025",
                Amount = 10.5,
            });

            var recovered = node.ContentAs<ConsumerCopy.SharedEntry>(Options);

            recovered.Should().NotBeNull(
                "a same-shaped foreign CLR type is recoverable via JSON round-trip — silent null "
                + "drops the whole collection at every 'Content is not null' projection");
            recovered!.Position.Should().Be("Test/Position/FreeFunds");
            recovered.Year.Should().Be("Test/Year/2025");
            recovered.Amount.Should().Be(10.5);
        }

        [Fact]
        public void TypedSameClrContent_IsReturnedAsIs()
        {
            var content = new ConsumerCopy.SharedEntry { Position = "p", Year = "y", Amount = 1 };
            NodeWith(content).ContentAs<ConsumerCopy.SharedEntry>(Options).Should().BeSameAs(content);
        }

        [Fact]
        public void JsonElementContent_IsDeserialized()
        {
            var element = JsonSerializer.SerializeToElement(
                new ConsumerCopy.SharedEntry { Position = "p", Year = "y", Amount = 2 }, Options);
            var recovered = NodeWith(element).ContentAs<ConsumerCopy.SharedEntry>(Options);
            recovered.Should().NotBeNull();
            recovered!.Amount.Should().Be(2);
        }

        [Fact]
        public void UnrecoverableContent_ReturnsNull_WithoutThrowing()
        {
            // A string cannot bind into an object shape — the read must degrade to null, never throw
            // (a throw on read faults the node).
            NodeWith("just a string").ContentAs<ConsumerCopy.SharedEntry>(Options).Should().BeNull();
        }

        [Fact]
        public void NullContent_ReturnsNull()
        {
            NodeWith(null!).ContentAs<ConsumerCopy.SharedEntry>(Options).Should().BeNull();
        }
    }
}

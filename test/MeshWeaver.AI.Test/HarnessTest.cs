#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for the <b>harness</b> architecture — the top-level "how a round runs"
/// choice that is distinct from a model provider. A harness lives in its own
/// assembly (MeshWeaver / Claude Code / Copilot), is registered as an
/// <see cref="IHarness"/>, projected into a catalog node by
/// <see cref="BuiltInHarnessProvider"/>, and resolved at execution time by
/// <see cref="HarnessNodeType.ResolveHarness"/>.
/// </summary>
public class HarnessTest
{
    /// <summary>A CLI-style fake harness for tests — returns a non-null client.</summary>
    private sealed class FakeCliHarness(string id) : IHarness
    {
        public string Id => id;
        public Harness Definition => new() { Id = id, DisplayName = id, Order = 9, SupportsAgentSelection = false };
        public IChatClient? CreateChatClient(HarnessExecutionContext context) => new FakeChatClient();
    }

    private sealed class FakeChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
    }

    [Fact]
    public void MeshWeaverHarness_IsDefault_SupportsAgentSelection_AndUsesAgentPath()
    {
        var h = new MeshWeaverHarness();

        h.Id.Should().Be(Harnesses.MeshWeaver);
        h.Definition.IsDefault.Should().BeTrue();
        h.Definition.SupportsAgentSelection.Should().BeTrue();
        // Null client → ThreadExecution falls through to the AgentChatClient path.
        h.CreateChatClient(new HarnessExecutionContext(null!, null, null)).Should().BeNull();
    }

    [Fact]
    public void BuiltInHarnessProvider_EmitsPolicy_PlusOneNodePerHarness()
    {
        var provider = new BuiltInHarnessProvider(new IHarness[]
        {
            new MeshWeaverHarness(),
            new FakeCliHarness("Claude Code"),
            new FakeCliHarness("GitHub Copilot"),
        });

        var nodes = provider.GetStaticNodes().ToList();

        nodes.Should().Contain(n => n.NodeType == "PartitionAccessPolicy");

        var harnessNodes = nodes.Where(n => n.NodeType == HarnessNodeType.NodeType).ToList();
        harnessNodes.Should().HaveCount(3);
        harnessNodes.Should().OnlyContain(n => n.Namespace == HarnessNodeType.RootNamespace);
        harnessNodes.Should().OnlyContain(n => n.Content is Harness);
        var ids = harnessNodes.Select(n => n.Id).ToList();
        ids.Should().Contain(Harnesses.MeshWeaver);
        ids.Should().Contain("Claude Code");
        ids.Should().Contain("GitHub Copilot");
    }

    [Fact]
    public void ResolveHarness_MatchesById_CaseInsensitive_AndNullForUnknown()
    {
        var sp = new ServiceCollection()
            .AddSingleton<IHarness, MeshWeaverHarness>()
            .AddSingleton<IHarness>(new FakeCliHarness("Claude Code"))
            .BuildServiceProvider();

        HarnessNodeType.ResolveHarness(sp, Harnesses.MeshWeaver).Should().BeOfType<MeshWeaverHarness>();
        HarnessNodeType.ResolveHarness(sp, "claude code")!.Id.Should().Be("Claude Code");
        HarnessNodeType.ResolveHarness(sp, "Nope").Should().BeNull();
        HarnessNodeType.ResolveHarness(sp, null).Should().BeNull();
    }

    [Fact]
    public void CliHarness_ReturnsNonNullClient_SoExecutionBypassesProviderChain()
    {
        IHarness cli = new FakeCliHarness("Claude Code");
        cli.Definition.SupportsAgentSelection.Should().BeFalse();
        cli.CreateChatClient(new HarnessExecutionContext(null!, null, null)).Should().NotBeNull();
    }
}

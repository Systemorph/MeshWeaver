using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins that a data source serving a type with NO key — no <c>[Key]</c> property, no <c>Id</c>,
/// no <c>SystemName</c>, no <c>WithKey(...)</c> — fails its hub's initialization with an error that
/// NAMES the type and the collection (Systemorph/MeshWeaver#6643, #6644, #6645).
///
/// <para><b>What went wrong.</b> <c>TypeDefinition</c> resolved its key function with a
/// null-forgiving <c>!</c>, so a key-less type carried a null key function that nothing reported.
/// <c>TypeSource.Initialize</c> builds the collection's <c>InstanceCollection</c> keyed through
/// <c>TypeDefinition.GetKey</c>, which dereferenced that null: a bare
/// <see cref="NullReferenceException"/> at <c>TypeDefinition.GetKey</c>, at every activation, naming
/// neither the type nor the collection — while the intended "No key mapping is defined" message sat
/// behind the dereference, unreachable. In production that was thousands of occurrences of one
/// unnamed fault on a tenant NodeType's hubs.</para>
///
/// <para><b>RED before the fix:</b> the recorded init error carries a
/// <see cref="NullReferenceException"/> and no type name. <b>GREEN after:</b> it carries the named
/// <see cref="InvalidOperationException"/> with the CLR type and the collection.</para>
/// </summary>
public class KeylessTypeSourceInitNamesTheTypeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static TimeSpan AnswerBound => TestTimeouts.Quick / 2;

    /// <summary>No [Key], no Id, no SystemName: the key-function builder finds nothing.</summary>
    private record KeylessItem(string Name);

    // NOT PingRequest: the DataContextInit gate deliberately exempts liveness pings.
    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .AddData(data => data
                .AddSource(src => src
                    .WithType<KeylessItem>(t => t
                        .WithInitialData(() =>
                            Observable.Return<IEnumerable<KeylessItem>>([new KeylessItem("first")])))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    [Fact(Timeout = 120_000)]
    public async Task KeylessDataSourceType_FailsInit_NamingTheTypeAndCollection_NotANullReference()
    {
        var host = GetHost();
        var client = GetClient();

        // Drive the hub to its terminal state through the door production uses: a request parks
        // behind the DataContextInit gate and is answered by the failed-state rejection.
        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(AnswerBound).Await(TestContext.Current.CancellationToken);
        var rejection = (await act.Should().ThrowAsync<Exception>(
            "a hub whose data source cannot key its instances must answer with an error")).Which;
        rejection.Should().NotBeOfType<TimeoutException>(
            "the failed state must answer the probe — a timeout is the probe's own wait expiring");

        var initError = host.GetWorkspace().DataContext.InitializationError;
        initError.Should().NotBeNull("a key-less data-source type cannot be indexed, so init fails");

        var full = initError!.ToString();
        full.Should().NotContain(nameof(NullReferenceException),
            "the key-less type must be refused BY NAME, not surface as a bare null dereference "
            + "inside TypeDefinition.GetKey (the #6643 shape)");
        full.Should().Contain("No key mapping is defined",
            "the intended named refusal must be reachable");
        full.Should().Contain(typeof(KeylessItem).FullName!,
            "the message must name the CLR type — the half the production stack could not tell");
        full.Should().Contain($"collection '{nameof(KeylessItem)}'",
            "the message must name the collection the type source serves");
    }
}

/// <summary>
/// Companion: an initial-data batch that contains a NULL element is refused naming the type and
/// the fact, instead of a <see cref="NullReferenceException"/> inside the compiled key accessor.
/// </summary>
public class NullInstanceInTypeSourceBatchNamesTheTypeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static TimeSpan AnswerBound => TestTimeouts.Quick / 2;

    private record KeyedItem(string Id);

    private record ProbeRequest : IRequest<ProbeResponse>;

    private record ProbeResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(ProbeRequest), typeof(ProbeResponse))
            .WithHandler<ProbeRequest>((hub, request) =>
            {
                hub.Post(new ProbeResponse(), o => o.ResponseFor(request));
                return request.Processed();
            })
            .AddData(data => data
                .AddSource(src => src
                    .WithType<KeyedItem>(t => t
                        .WithInitialData(() =>
                            Observable.Return<IEnumerable<KeyedItem>>([new KeyedItem("a"), null!])))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithTypes(typeof(ProbeRequest), typeof(ProbeResponse));

    [Fact(Timeout = 120_000)]
    public async Task NullElementInInitialBatch_FailsInit_NamingTheTypeAndTheNull()
    {
        var host = GetHost();
        var client = GetClient();

        var act = () => client
            .Observe(new ProbeRequest(), o => o.WithTarget(host.Address))
            .FirstAsync().Timeout(AnswerBound).Await(TestContext.Current.CancellationToken);
        var rejection = (await act.Should().ThrowAsync<Exception>(
            "a hub whose initial batch carries a null instance must answer with an error")).Which;
        rejection.Should().NotBeOfType<TimeoutException>(
            "the failed state must answer the probe — a timeout is the probe's own wait expiring");

        var full = host.GetWorkspace().DataContext.InitializationError!.ToString();
        full.Should().NotContain(nameof(NullReferenceException),
            "a null element must be refused by name, not dereferenced inside the key accessor");
        full.Should().Contain("null instance",
            "the message must say what was wrong: the batch carried a null element");
        full.Should().Contain(typeof(KeyedItem).FullName!,
            "the message must name the CLR type the batch was for");
    }
}

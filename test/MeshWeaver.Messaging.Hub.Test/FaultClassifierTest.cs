using System;
using System.Data.Common;
using System.Net.Sockets;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The ONE classifier for cancellation, timeout, teardown and transient faults
/// (<see cref="FaultClassifier"/>), stated as an executable truth table, plus the disagreements
/// between the copies it replaced that live in this layer — each written so it FAILS on the copy
/// that was wrong.
/// </summary>
public class FaultClassifierTest
{
    private sealed class FakeDbException(string message, Exception? inner = null, string? sqlState = null, bool transient = false)
        : DbException(message, inner)
    {
        public override string? SqlState { get; } = sqlState;
        public override bool IsTransient => transient;
    }

    /// <summary>A driver exception that compares EQUAL by value, as a provider type may — the
    /// classifier must still judge each instance on its own.</summary>
    private sealed class ValueEqualDbException(string message, string sqlState) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
        public override bool Equals(object? obj) => obj is ValueEqualDbException;
        public override int GetHashCode() => 1;
    }

    /// <summary>The shape of the query provider's stall (<c>QueryProviderStalledException</c>,
    /// which lives above this assembly): a typed <see cref="FaultLayer.StoreStall"/>.</summary>
    private sealed class StallException() : Exception("provider stalled"), IClassifiedFault
    {
        public string ClassifiedKind => FaultKind.Transient;
        public string? ClassifiedLayer => FaultLayer.StoreStall;
    }

    private static DeliveryFailureException Failure(string message, ErrorType type)
        => new(new DeliveryFailure(null!, message) { ErrorType = type });

    public static TheoryData<string, Exception?, string, string?> Corpus() => new()
    {
        { "null — nothing benign to say about an unknown", null, FaultKind.Fault, null },
        { "a cooperative cancellation", new OperationCanceledException(), FaultKind.CooperativeCancel, null },
        { "an HttpClient-style timeout: a cancellation carrying a TimeoutException",
            new TaskCanceledException("HttpClient.Timeout elapsed", new TimeoutException()), FaultKind.Timeout, null },
        { "a cancellation whose timeout cause sits at aggregate index 1",
            new OperationCanceledException("t", new AggregateException(new InvalidOperationException("x"), new TimeoutException())),
            FaultKind.Timeout, null },
        { "a bare timeout", new TimeoutException(), FaultKind.Timeout, null },
        { "a routed transport timeout flattened to text",
            Failure("Delivery to 'Store' failed: Response did not arrive on time in 00:00:30", ErrorType.Failed), FaultKind.Timeout, null },
        { "a hub announcing its disposal", new HubDisposingException(new Address("node", "a"), "stream"), FaultKind.HubTeardown, null },
        { "a ShuttingDown delivery failure", Failure("Hub node/a is shutting down", ErrorType.ShuttingDown), FaultKind.HubTeardown, null },
        { "the Autofac disposed-scope fault", new ObjectDisposedException("LifetimeScope", "Instances cannot be resolved and nested lifetimes cannot be created from this LifetimeScope"),
            FaultKind.ScopeTeardown, null },
        { "a bare disposed object with no context — not provably a teardown", new ObjectDisposedException("MemoryStream"), FaultKind.Fault, null },
        { "a bare socket fault", new SocketException((int)SocketError.HostNotFound), FaultKind.Transient, FaultLayer.Network },
        { "a driver wrapping a connect timeout", new FakeDbException("Failed to connect", new TimeoutException()), FaultKind.Transient, FaultLayer.Store },
        { "a connection-class SQLSTATE", new FakeDbException("cannot connect now", sqlState: "57P03"), FaultKind.Transient, FaultLayer.Store },
        { "a provider-transient conflict", new FakeDbException("deadlock", sqlState: "40P01", transient: true), FaultKind.Transient, FaultLayer.StoreContention },
        { "a schema error", new FakeDbException("undefined_table", sqlState: "42P01"), FaultKind.Fault, null },
        { "the database's own 'is shutting down' is a STORE fault, never a hub teardown",
            new FakeDbException("the database system is shutting down", sqlState: "57P03"), FaultKind.Transient, FaultLayer.Store },
        { "an Orleans forwarding reject flattened to text",
            new InvalidOperationException("Forwarding failed: tried to forward message to invalid activation. Rejecting now."), FaultKind.Transient, FaultLayer.Transport },
        { "a genuine fault", new InvalidOperationException("boom"), FaultKind.Fault, null },
        { "a mixed aggregate keeps the defect", new AggregateException(new SocketException(), new InvalidOperationException("boom")), FaultKind.Fault, null },
        { "a teardown anywhere wins", new AggregateException(new InvalidOperationException("boom"), new HubDisposingException(new Address("node", "a"), "x")),
            FaultKind.HubTeardown, null },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Classifies(string shape, Exception? exception, string kind, string? layer)
    {
        var verdict = FaultClassifier.Classify(exception);
        verdict.Kind.Should().Be(kind, shape);
        verdict.Layer.Should().Be(layer, shape);
    }

    [Fact]
    public void IsCancellation_IsTheShape_WhateverTheCause()
    {
        FaultClassifier.Classify(new OperationCanceledException()).IsCancellation.Should().BeTrue();
        FaultClassifier.Classify(new OperationCanceledException("hub gone", new HubDisposingException(new Address("node", "a"), "x")))
            .IsCancellation.Should().BeTrue("a cancellation raised by a teardown is still a cancellation");
        FaultClassifier.Classify(new AggregateException(new OperationCanceledException()))
            .IsCancellation.Should().BeTrue("a bridged task's single-inner aggregate");
        FaultClassifier.Classify(new AggregateException(new OperationCanceledException(), new InvalidOperationException("boom")))
            .IsCancellation.Should().BeFalse("a defect beside the cancellation is a fault");
        FaultClassifier.Classify(new InvalidOperationException("wrapped", new OperationCanceledException()))
            .IsCancellation.Should().BeFalse("a fault wrapping a cancellation is a fault");
        FaultClassifier.Classify(new TaskCanceledException("timeout", new TimeoutException()))
            .IsCancellation.Should().BeFalse("a cancellation raised to express a timeout is the impostor");
    }

    [Fact]
    public void ScopeProbe_DecidesWhoseScope()
    {
        var autofac = new ObjectDisposedException("LifetimeScope", "nested lifetimes cannot be created from this LifetimeScope");
        FaultClassifier.Classify(autofac, new TeardownContext(() => false)).Kind.Should().NotBe(FaultKind.ScopeTeardown,
            "the caller's own scope is alive: another scope's disposal is not this caller's teardown");
        FaultClassifier.Classify(new ObjectDisposedException("x"), new TeardownContext(() => true)).Kind.Should().Be(FaultKind.ScopeTeardown);
    }

    [Fact]
    public void OwnAddress_LimitsHubTeardownToThisHub()
    {
        var other = new HubDisposingException(new Address("node", "other"), "x");
        FaultClassifier.Classify(other, new TeardownContext(OwnAddress: new Address("node", "me"))).IsTeardown.Should().BeFalse();
        FaultClassifier.Classify(other, new TeardownContext(OwnAddress: new Address("node", "other"))).Kind.Should().Be(FaultKind.HubTeardown);
    }

    [Fact]
    public void APathologicallyDeepGraph_Terminates()
    {
        Exception current = new SocketException();
        for (var i = 0; i < 400; i++)
            current = new AggregateException(current, current);
        FaultClassifier.Classify(current).Kind.Should().BeOneOf(FaultKind.Transient, FaultKind.Fault);
    }

    /// <summary>
    /// 🚨 Disagreement: <see cref="InfrastructureFault"/> trusted only the provider's
    /// <c>IsTransient</c> flag, <c>StorageFaults</c> only the SQLSTATE. A connection-class SQLSTATE
    /// the provider did not flag was "store unreachable" to the query retry and the render frame,
    /// and a permanent FAILED latch to the hub initializer. The SQLSTATE is the server's own word
    /// that it could not serve a session — transient for both.
    /// </summary>
    [Fact]
    public void AConnectClassSqlState_IsTransientInfrastructure_EvenUnflagged()
        => InfrastructureFault.IsTransient(new FakeDbException("the database system is starting up", sqlState: "57P03"))
            .Should().BeTrue();

    /// <summary>
    /// 🚨 A typed timeout over typed store or network evidence is that evidence, not a plain timeout:
    /// the connect retry must still see a socket fault or a connection-class SQLSTATE beneath the
    /// time-box. A cause-free timeout, and a timeout over nothing typed, stay non-transient.
    /// </summary>
    [Fact]
    public void ATimeoutOverTypedStoreOrNetworkEvidence_IsTransientInfrastructure()
    {
        InfrastructureFault.IsTransient(new TimeoutException("read failed", new SocketException()))
            .Should().BeTrue("the socket fault is why it timed out");
        InfrastructureFault.IsTransient(new TimeoutException("connect timed out",
                new FakeDbException("the database system is starting up", sqlState: "57P03")))
            .Should().BeTrue("a connection-class SQLSTATE beneath the time-box is still an unreachable store");
        InfrastructureFault.IsTransient(new TimeoutException("hung"))
            .Should().BeFalse("a cause-free timeout is a hang, not a transient dependency fault");
        InfrastructureFault.IsTransient(new TimeoutException("hung", new InvalidOperationException("defect")))
            .Should().BeFalse("nothing typed beneath it: the timeout stands");
    }

    /// <summary>
    /// 🚨 Two flagged driver frames over <c>Aggregate(socket, JsonException)</c> carry the content
    /// defect, exactly as one flagged driver over it does: the inner driver's flag does not stand in
    /// for the branches it encloses.
    /// </summary>
    [Fact]
    public void TwoFlaggedDriversOverAMixedAggregate_AreNotTransient()
    {
        var mixed = new AggregateException(new SocketException(), new System.Text.Json.JsonException("bad content"));
        var one = new FakeDbException("outer", mixed, transient: true);
        var two = new FakeDbException("outer", new FakeDbException("inner", mixed, transient: true), transient: true);

        InfrastructureFault.IsTransient(one).Should().BeFalse("the single-driver shape carries the defect");
        InfrastructureFault.IsTransient(two).Should().BeFalse("so does the double-driver shape");
        FaultClassifier.Classify(two).Kind.Should().Be(FaultKind.Fault);
        InfrastructureFault.IsTransient(new FakeDbException("outer",
                new FakeDbException("inner", new AggregateException(new SocketException(), new SocketException()), transient: true),
                transient: true))
            .Should().BeTrue("a double driver over an all-transient aggregate is still transient");
    }

    /// <summary>
    /// 🚨 A connection-class SQLSTATE (<c>57P03</c>) over <c>Aggregate(socket, JsonException)</c> carries
    /// the content defect, like a flagged driver over it does — directly, and as a nested driver frame
    /// under another driver. The SQLSTATE speaks for its frame, never for the branches it encloses.
    /// </summary>
    [Fact]
    public void AConnectClassSqlStateOverAMixedAggregate_IsNotTransient()
    {
        AggregateException Mixed() => new(new SocketException((int)SocketError.ConnectionReset), new System.Text.Json.JsonException("bad content"));
        var direct = new FakeDbException("cannot connect now", Mixed(), sqlState: "57P03");
        var nested = new FakeDbException("read failed", new FakeDbException("cannot connect now", Mixed(), sqlState: "57P03"));
        var nestedFlagged = new FakeDbException("read failed",
            new FakeDbException("cannot connect now", Mixed(), sqlState: "57P03"), transient: true);

        foreach (var shape in new Exception[] { direct, nested, nestedFlagged })
        {
            InfrastructureFault.IsTransient(shape).Should().BeFalse("the JsonException branch is a permanent defect");
            FaultClassifier.Classify(shape).Kind.Should().Be(FaultKind.Fault);
        }

        // The control: the same SQLSTATE over an all-transport aggregate is still an unreachable store.
        var allTransport = new FakeDbException("cannot connect now",
            new AggregateException(new SocketException((int)SocketError.ConnectionReset), new SocketException()), sqlState: "57P03");
        FaultClassifier.Classify(allTransport).EveryBranchTypedTransientIn(FaultLayer.Store, FaultLayer.Network).Should().BeTrue();
        InfrastructureFault.IsTransient(new FakeDbException("read failed",
                new FakeDbException("cannot connect now", new AggregateException(new SocketException(), new SocketException()), sqlState: "57P03")))
            .Should().BeTrue();
    }

    /// <summary>
    /// 🚨 Disagreement: two test-side copies accepted the Autofac "nested lifetimes cannot be
    /// created" text on ANY exception type; the product's copy required an
    /// <see cref="ObjectDisposedException"/>. Autofac raises it as one, so the text on anything else
    /// is a coincidence, not a disposed scope.
    /// </summary>
    [Fact]
    public void TheAutofacText_OnANonDisposedException_IsNotADisposedContainer()
    {
        FaultClassifier.IsDisposedContainer(new InvalidOperationException("nested lifetimes cannot be created"))
            .Should().BeFalse();
        FaultClassifier.IsDisposedContainer(new ObjectDisposedException("x", "nested lifetimes cannot be created"))
            .Should().BeTrue();
    }

    /// <summary>
    /// 🚨 A shared node reached from a second branch contributed nothing, so the answer depended on
    /// branch ORDER: the wrapped branch's "this is a fault around a cancellation" was dropped once the
    /// bare cancellation had been visited first.
    /// </summary>
    [Fact]
    public void ASharedCancellation_IsJudgedPerBranch_WhateverTheOrder()
    {
        var cancelled = new OperationCanceledException();
        var wrapped = new InvalidOperationException("wrapped", cancelled);
        FaultClassifier.Classify(new AggregateException(cancelled, wrapped)).IsCancellation
            .Should().BeFalse("the wrapped branch is a fault around the cancellation");
        FaultClassifier.Classify(new AggregateException(wrapped, cancelled)).IsCancellation
            .Should().BeFalse("order must not change the verdict");
    }

    /// <summary>
    /// 🚨 An aggregate under a wrapping fault reset the head-ness of its branches, so a failure
    /// wrapping a bridged task's cancellation read as a cooperative cancellation.
    /// </summary>
    [Fact]
    public void AFaultWrappingAnAggregateOfCancellations_IsNotACancellation()
        => FaultClassifier.Classify(new InvalidOperationException("failed",
                new AggregateException(new OperationCanceledException())))
            .IsCancellation.Should().BeFalse();

    /// <summary>
    /// 🚨 A non-transient driver exception was a store outage when ANY frame below it was a socket
    /// fault, so a permanent defect beside it was retried as an unreachable store.
    /// </summary>
    [Fact]
    public void ADriverException_OverADefectBesideASocketFault_IsNotAStoreOutage()
    {
        var verdict = FaultClassifier.Classify(new FakeDbException("db",
            new AggregateException(new InvalidOperationException("bad configuration"), new SocketException())));
        verdict.Kind.Should().Be(FaultKind.Fault);
        StorageFaults_IsNot(verdict);
    }

    private static void StorageFaults_IsNot(FaultClassification verdict)
        => verdict.EveryBranchTypedTransientIn(FaultLayer.Store, FaultLayer.StoreContention, FaultLayer.Network)
            .Should().BeFalse();

    /// <summary>
    /// 🚨 A message match on a wrapper hid the typed transient cause it wrapped: the verdict was
    /// by-message, which every typed forwarder refuses.
    /// </summary>
    [Fact]
    public void AMessageMatchOnAWrapper_YieldsToTheTypedCauseItWraps()
    {
        var verdict = FaultClassifier.Classify(new InvalidOperationException("Failed to connect",
            new FakeDbException("db", sqlState: "08006")));
        verdict.Kind.Should().Be(FaultKind.Transient);
        verdict.Layer.Should().Be(FaultLayer.Store);
        verdict.ByMessage.Should().BeFalse();
        InfrastructureFault.IsTransient(new InvalidOperationException("Failed to connect",
            new FakeDbException("db", transient: true))).Should().BeTrue();
    }

    /// <summary>A message match still stands when nothing below decides by type.</summary>
    [Fact]
    public void AMessageMatch_StillStands_WhenNothingBelowIsTyped()
    {
        var verdict = FaultClassifier.Classify(new InvalidOperationException("Failed to connect", new InvalidOperationException("x")));
        verdict.Kind.Should().Be(FaultKind.Transient);
        verdict.ByMessage.Should().BeTrue();
    }

    /// <summary>
    /// 🚨 Eight levels of <c>AggregateException(current, current)</c> over one socket fault hold
    /// nine DISTINCT nodes but 511 paths. The driver's every-branch probe counted paths, ran out of
    /// budget, and the unflagged driver frame lost its typed <see cref="FaultLayer.Store"/> verdict.
    /// </summary>
    [Fact]
    public void ADriverException_OverASharedGraphOfSocketFaults_IsAStoreOutage()
    {
        Exception current = new SocketException((int)SocketError.ConnectionRefused);
        for (var level = 0; level < 8; level++)
            current = new AggregateException(current, current);

        var verdict = FaultClassifier.Classify(new FakeDbException("Exception while reading from stream", current));

        verdict.Kind.Should().Be(FaultKind.Transient);
        verdict.Layer.Should().Be(FaultLayer.Store);
        verdict.EveryBranchTypedTransientIn(FaultLayer.Store).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 A transient driver frame with no SQLSTATE over a nested driver frame carrying the
    /// connection-class <c>57P03</c> is an unreachable store — not contention because the outer
    /// frame happened to be flagged.
    /// </summary>
    [Fact]
    public void ATransientDriverException_OverAConnectClassSqlState_IsAStoreOutage()
    {
        var verdict = FaultClassifier.Classify(new FakeDbException("read failed",
            new FakeDbException("the database system is starting up", sqlState: "57P03"), transient: true));

        verdict.Kind.Should().Be(FaultKind.Transient);
        verdict.Layer.Should().Be(FaultLayer.Store);
        verdict.ByMessage.Should().BeFalse();
    }

    /// <summary>
    /// 🚨 An unflagged driver frame with no recognised SQLSTATE and no cause decides nothing by
    /// type, so its TEXT must still be read — exactly as the same text flattened across a grain
    /// boundary is.
    /// </summary>
    [Fact]
    public void AnUndecidedDriverException_IsReadByItsText_LikeItsFlattenedForm()
    {
        var driver = FaultClassifier.Classify(new FakeDbException("connection refused"));
        var flattened = FaultClassifier.Classify(new InvalidOperationException("connection refused"));

        driver.Kind.Should().Be(FaultKind.Transient);
        driver.Layer.Should().Be(FaultLayer.Store);
        driver.ByMessage.Should().BeTrue();
        flattened.Kind.Should().Be(driver.Kind);
        flattened.Layer.Should().Be(driver.Layer);
        flattened.ByMessage.Should().BeTrue();
    }

    /// <summary>
    /// 🚨 Two DISTINCT driver exceptions that compare equal by value — an outage (<c>57P03</c>) and a
    /// schema defect (<c>42P01</c>) — are two branches. Memoized by value, the second reused the
    /// first's verdict and the whole aggregate read as a retryable store outage.
    /// </summary>
    [Fact]
    public void ValueEqualDriverExceptions_AreJudgedEachOnTheirOwn()
    {
        var verdict = FaultClassifier.Classify(new AggregateException(
            new ValueEqualDbException("cannot connect now", "57P03"),
            new ValueEqualDbException("relation does not exist", "42P01")));

        verdict.Kind.Should().Be(FaultKind.Fault);
        verdict.EveryBranchTypedTransientIn(FaultLayer.Store).Should().BeFalse();
    }

    /// <summary>
    /// 🚨 A wrapper's TEXT match stands in only for a cause nothing typed decides — it is never
    /// copied into an aggregate child beside a sibling that DID decide. Otherwise a configuration
    /// defect next to a socket fault inherits "Failed to connect" and the mix reads as transient.
    /// </summary>
    [Fact]
    public void AWrappersText_NeverReplacesAnUndecidedSibling_OfATypedBranch()
    {
        var verdict = FaultClassifier.Classify(new InvalidOperationException("Failed to connect",
            new AggregateException(
                new SocketException((int)SocketError.ConnectionRefused),
                new InvalidOperationException("bad configuration"))));

        verdict.Kind.Should().Be(FaultKind.Fault);

        // …while an aggregate in which NOTHING decides still takes the wrapper's text.
        var undecided = FaultClassifier.Classify(new InvalidOperationException("Failed to connect",
            new AggregateException(new InvalidOperationException("x"), new InvalidOperationException("y"))));
        undecided.Kind.Should().Be(FaultKind.Transient);
        undecided.Layer.Should().Be(FaultLayer.Store);
        undecided.ByMessage.Should().BeTrue();
    }

    /// <summary>
    /// 🚨 A TYPED timeout wrapping a stalled provider is the stall: the provider's stall is WHY it
    /// timed out. Read as a plain timeout, a view retried it (<c>IsTransientHubFailure</c>) and the
    /// storage-unavailable frame never rendered (<c>IsStorageUnavailable</c>).
    /// </summary>
    [Fact]
    public void ATimeoutWrappingAStall_IsTheStall()
    {
        var verdict = FaultClassifier.Classify(new TimeoutException("query failed", new StallException()));

        verdict.Kind.Should().Be(FaultKind.Transient);
        verdict.Layer.Should().Be(FaultLayer.StoreStall);
        verdict.EveryBranchTypedTransientIn(FaultLayer.Store, FaultLayer.StoreStall).Should().BeTrue();

        // The mixed-branch rule is kept: a stall beside a defect under the timeout is a fault.
        FaultClassifier.Classify(new TimeoutException("query failed",
                new AggregateException(new StallException(), new InvalidOperationException("defect"))))
            .Kind.Should().Be(FaultKind.Fault);

        // And a timeout over anything else is still a timeout.
        FaultClassifier.Classify(new TimeoutException("slow", new InvalidOperationException("x")))
            .Kind.Should().Be(FaultKind.Timeout);
    }

    /// <summary>
    /// 🚨 A provider-flagged driver frame over a socket fault BESIDE a permanent defect is not
    /// contention: the flag speaks for the driver, the nested aggregate says one branch is a real
    /// defect, and re-opening a standing watch on it trades a latch for a loop.
    /// </summary>
    [Fact]
    public void ATransientDriverException_OverADefectBesideASocketFault_IsNotRetryable()
    {
        var verdict = FaultClassifier.Classify(new FakeDbException("read failed",
            new AggregateException(new SocketException((int)SocketError.ConnectionReset),
                new System.Text.Json.JsonException("bad content")), transient: true));

        verdict.Kind.Should().Be(FaultKind.Fault);
        verdict.EveryBranchTypedTransientIn(FaultLayer.Store, FaultLayer.StoreContention, FaultLayer.Network)
            .Should().BeFalse();

        // …while the same flag over an aggregate of transport faults alone is still a store fault.
        FaultClassifier.Classify(new FakeDbException("read failed",
                new AggregateException(new SocketException((int)SocketError.ConnectionReset), new IOException("reset")),
                transient: true))
            .EveryBranchTypedTransientIn(FaultLayer.Store, FaultLayer.StoreContention).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 A hub teardown recognised only by its "is shutting down" BANNER is a verdict by message —
    /// with or without <see cref="TeardownContext.OwnAddress"/> — so a typed forwarder can refuse it.
    /// A typed teardown (<see cref="HubDisposingException"/>) is not relabelled.
    /// </summary>
    [Fact]
    public void ABannerOnlyHubTeardown_IsAVerdictByMessage()
    {
        var banner = new InvalidOperationException("Hub node/a is shutting down");

        var plain = FaultClassifier.Classify(banner);
        plain.Kind.Should().Be(FaultKind.HubTeardown);
        plain.ByMessage.Should().BeTrue();

        var elsewhere = FaultClassifier.Classify(banner, new TeardownContext(OwnAddress: new Address("node", "b")));
        elsewhere.Kind.Should().Be(FaultKind.Transient);
        elsewhere.Layer.Should().Be(FaultLayer.Transport);
        elsewhere.ByMessage.Should().BeTrue();
        elsewhere.EveryBranchTypedTransientIn(FaultLayer.Transport).Should().BeFalse();

        var typed = new HubDisposingException(new Address("node", "a"), "stream");
        FaultClassifier.Classify(typed).ByMessage.Should().BeFalse();
        FaultClassifier.Classify(typed, new TeardownContext(OwnAddress: new Address("node", "b")))
            .EveryBranchTypedTransientIn(FaultLayer.Transport).Should().BeTrue();
    }

    private static ObjectDisposedException DisposedScope() => new("LifetimeScope",
        "Instances cannot be resolved and nested lifetimes cannot be created from this LifetimeScope");

    /// <summary>
    /// 🚨 The teardown suppressors observe an unobserved fault only when EVERY branch is the
    /// disposed-scope shape — through wrappers too. A wrapper over a disposed scope BESIDE a real
    /// defect carries the defect, so it is not benign (the graph-wide "contains" answer says it is).
    /// </summary>
    [Fact]
    public void EveryBranchIsDisposedContainer_RefusesAWrapperOverAMixedAggregate()
    {
        var mixed = new InvalidOperationException("wrapper",
            new AggregateException(DisposedScope(), new InvalidOperationException("configuration defect")));

        FaultClassifier.IsDisposedContainer(mixed).Should().BeTrue("the graph CONTAINS the scope fault");
        FaultClassifier.EveryBranchIsDisposedContainer(mixed).Should().BeFalse();
        FaultClassifier.EveryBranchIsDisposedContainer(new InvalidOperationException("wrapper",
            new AggregateException(DisposedScope(), DisposedScope()))).Should().BeTrue();
    }

    /// <summary>
    /// 🚨 The first-chance capturer names only the disposed-scope FRAME itself: an aggregate that
    /// merely contains one is not admitted, because formatting a nested aggregate's message is
    /// exponential in its depth.
    /// </summary>
    [Fact]
    public void IsDisposedContainerFrame_AdmitsTheFrameOnly()
    {
        Exception current = DisposedScope();
        for (var level = 0; level < 8; level++)
            current = new AggregateException(current, current);

        FaultClassifier.IsDisposedContainerFrame(DisposedScope()).Should().BeTrue();
        FaultClassifier.IsDisposedContainerFrame(current).Should().BeFalse();
    }
}

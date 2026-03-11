using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Monolith.Test.Security;

public record TestItem(string Id, string Name);

/// <summary>
/// Tests that HubDataSource propagates access errors instead of hanging.
/// Each test verifies one link in the error propagation chain.
/// </summary>
public class HubDataSourceSecurityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("SourceHub") { Name = "Source Hub" },
                new MeshNode("SourceHub/A") { Name = "Source A" },
                new MeshNode("SourceHub/B") { Name = "Source B" })
            .ConfigureDefaultNodeHub(c => c.AddData(d => d));

    private async Task EnsureHubStarted(Address address)
    {
        var client = GetClient();
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Link 1: HubDataSource with denied access → workspace stream errors.
    /// Verifies: data source stream's OnError propagates through workspace stream to subscriber.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task HubDataSource_WithoutAccess_ShouldErrorStream()
    {
        await EnsureHubStarted(new Address("SourceHub"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        var consumerHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("consumer", "1"),
            c => c.AddData(d =>
                d.AddHubSource(new Address("SourceHub"), ds => ds.WithType<TestItem>())));

        var stream = consumerHub.GetWorkspace().GetStream<TestItem>();

        stream.Should().NotBeNull("workspace stream should exist for registered type");

        var act = async () => await stream!.Timeout(5.Seconds()).FirstAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        Output.WriteLine($"Exception: {ex.GetType().Name}: {ex.Message}");
        ex.Should().NotBeOfType<TimeoutException>(
            "access denial should propagate as an error, not cause a timeout");
    }

    /// <summary>
    /// Link 2: PartitionedHubDataSource → combined stream errors.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task PartitionedHubDataSource_WithoutAccess_ShouldErrorStream()
    {
        await EnsureHubStarted(new Address("SourceHub/A"));
        await EnsureHubStarted(new Address("SourceHub/B"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        var consumerHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("group-consumer", "1"),
            c => c.AddData(d =>
                d.AddPartitionedHubSource<Address>(
                    ds => ds.WithType<TestItem>(item => (Address)("SourceHub/" + item.Id))
                        .InitializingPartitions(new Address("SourceHub/A"), new Address("SourceHub/B")))));

        var stream = consumerHub.GetWorkspace().GetStream<TestItem>();

        stream.Should().NotBeNull("workspace stream should exist for registered type");

        var act = async () => await stream!.Timeout(5.Seconds()).FirstAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        Output.WriteLine($"Exception: {ex.GetType().Name}: {ex.Message}");
        ex.Should().NotBeOfType<TimeoutException>(
            "partitioned subscription denial should propagate as an error, not cause a timeout");
    }

    /// <summary>
    /// Link 3: Error data source stream FIRST, then subscribe remotely.
    /// Verifies: when workspace stream is already errored, CreateSynchronizationStream's
    /// onError handler fires and posts DeliveryFailure to remote subscriber.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ErroredDataSource_ThenRemoteSubscribe_ShouldError()
    {
        await EnsureHubStarted(new Address("SourceHub"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        // Create group-hub whose data source will fail
        var groupAddress = new Address("group-hub-seq", "1");
        var groupHub = Mesh.ServiceProvider.CreateMessageHub(
            groupAddress,
            c => c.AddData(d =>
                d.AddHubSource(new Address("SourceHub"), ds => ds.WithType<TestItem>())));

        // STEP 1: Wait for internal workspace stream to error (proves data source failed)
        var internalStream = groupHub.GetWorkspace().GetStream<TestItem>();
        internalStream.Should().NotBeNull("workspace stream should exist for registered type");

        var internalAct = async () => await internalStream!.Timeout(5.Seconds()).FirstAsync();
        var internalEx = await Assert.ThrowsAnyAsync<Exception>(internalAct);
        Output.WriteLine($"Step 1 - Internal stream errored: {internalEx.GetType().Name}: {internalEx.Message}");
        internalEx.Should().NotBeOfType<TimeoutException>("internal stream should error from access denial");

        // STEP 2: Now subscribe remotely AFTER the data source already failed
        Output.WriteLine("Step 2 - Subscribing remotely after data source failure...");
        var client = GetClient(c => c.AddData(d => d));
        var remoteStream = client.GetWorkspace().GetRemoteStream<EntityStore>(
            groupAddress, new CollectionsReference(typeof(TestItem).FullName!));

        var remoteAct = async () => await remoteStream.Timeout(5.Seconds()).FirstAsync();
        var remoteEx = await Assert.ThrowsAnyAsync<Exception>(remoteAct);
        Output.WriteLine($"Step 2 - Remote stream result: {remoteEx.GetType().Name}: {remoteEx.Message}");

        remoteEx.Should().NotBeOfType<TimeoutException>(
            "remote subscriber should get DeliveryFailure, not timeout");
    }

    /// <summary>
    /// Link 4: Subscribe remotely CONCURRENTLY with data source initialization (race).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ClientRemoteStream_WhenDataSourceErrors_ShouldError()
    {
        await EnsureHubStarted(new Address("SourceHub"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        var groupAddress = new Address("group-hub-race", "1");
        Mesh.ServiceProvider.CreateMessageHub(
            groupAddress,
            c => c.AddData(d =>
                d.AddHubSource(new Address("SourceHub"), ds => ds.WithType<TestItem>())));

        var client = GetClient(c => c.AddData(d => d));
        var stream = client.GetWorkspace().GetRemoteStream<EntityStore>(
            groupAddress, new CollectionsReference(typeof(TestItem).FullName!));

        var act = async () => await stream.Timeout(5.Seconds()).FirstAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        Output.WriteLine($"Exception: {ex.GetType().Name}: {ex.Message}");

        ex.Should().NotBeOfType<TimeoutException>(
            "client subscription should propagate data source error, not timeout");
    }

    /// <summary>
    /// Link 5: DataContext failure state — when initialization fails, DataContext stores the error.
    /// Subsequent SubscribeRequests get immediate DeliveryFailure with a meaningful message.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FailedHub_SubsequentSubscribe_ShouldGetImmediateError()
    {
        await EnsureHubStarted(new Address("SourceHub"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        // Create a group hub whose data source will fail (access denied)
        var groupAddress = new Address("group-hub-failstate", "1");
        var groupHub = Mesh.ServiceProvider.CreateMessageHub(
            groupAddress,
            c => c.AddData(d =>
                d.AddHubSource(new Address("SourceHub"), ds => ds.WithType<TestItem>())));

        // Wait for the internal stream to error (proves initialization failed)
        var internalStream = groupHub.GetWorkspace().GetStream<TestItem>();
        internalStream.Should().NotBeNull();
        var internalAct = async () => await internalStream!.Timeout(5.Seconds()).FirstAsync();
        var internalEx = await Assert.ThrowsAnyAsync<Exception>(internalAct);
        Output.WriteLine($"Internal stream errored: {internalEx.GetType().Name}: {internalEx.Message}");
        internalEx.Should().NotBeOfType<TimeoutException>();

        // Now a second client subscribes — should get immediate error, not timeout.
        // This also gives time for DataContext.OpenInitializationGate's ContinueWith to run.
        var client2 = GetClient(c => c.AddData(d => d));
        var remoteStream2 = client2.GetWorkspace().GetRemoteStream<EntityStore>(
            groupAddress, new CollectionsReference(typeof(TestItem).FullName!));

        var remoteAct2 = async () => await remoteStream2.Timeout(3.Seconds()).FirstAsync();
        var remoteEx2 = await Assert.ThrowsAnyAsync<Exception>(remoteAct2);
        Output.WriteLine($"Second subscriber result: {remoteEx2.GetType().Name}: {remoteEx2.Message}");

        remoteEx2.Should().NotBeOfType<TimeoutException>(
            "second subscriber should get immediate DeliveryFailure from failed hub, not timeout");

        // Verify DataContext is in failed state (by now ContinueWith has completed)
        var dataContext = groupHub.GetWorkspace().DataContext;
        dataContext.InitializationError.Should().NotBeNull(
            "DataContext should be in failed state after initialization failure");
        Output.WriteLine($"DataContext.InitializationError: {dataContext.InitializationError!.Message}");
    }

    /// <summary>
    /// Link 6: Failed hub rejects GetDataRequest with DeliveryFailure.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FailedHub_GetDataRequest_ShouldReturnError()
    {
        await EnsureHubStarted(new Address("SourceHub"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        var groupAddress = new Address("group-hub-getdata", "1");
        var groupHub = Mesh.ServiceProvider.CreateMessageHub(
            groupAddress,
            c => c.AddData(d =>
                d.AddHubSource(new Address("SourceHub"), ds => ds.WithType<TestItem>())));

        // Wait for internal stream error (proves init failed)
        var stream = groupHub.GetWorkspace().GetStream<TestItem>();
        stream.Should().NotBeNull();
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await stream!.Timeout(5.Seconds()).FirstAsync());

        // GetDataRequest should throw, not timeout
        var client = GetClient();
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.AwaitResponse(
                new GetDataRequest(new CollectionsReference(typeof(TestItem).FullName!)),
                o => o.WithTarget(groupAddress),
                TestContext.Current.CancellationToken));

        Output.WriteLine($"GetDataRequest error: {ex.GetType().Name}: {ex.Message}");
        ex.Should().NotBeOfType<TimeoutException>("should get error, not timeout");
        ex.ToString().Should().Contain("initialization failed");
    }

    /// <summary>
    /// Link 7: Failed hub rejects DataChangeRequest with DeliveryFailure.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FailedHub_DataChangeRequest_ShouldReturnError()
    {
        await EnsureHubStarted(new Address("SourceHub"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        var groupAddress = new Address("group-hub-change", "1");
        var groupHub = Mesh.ServiceProvider.CreateMessageHub(
            groupAddress,
            c => c.AddData(d =>
                d.AddHubSource(new Address("SourceHub"), ds => ds.WithType<TestItem>())));

        // Wait for internal stream error
        var stream = groupHub.GetWorkspace().GetStream<TestItem>();
        stream.Should().NotBeNull();
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await stream!.Timeout(5.Seconds()).FirstAsync());

        // DataChangeRequest should throw, not timeout
        var client = GetClient();
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.AwaitResponse(
                new DataChangeRequest { Updates = [new TestItem("1", "Test")] },
                o => o.WithTarget(groupAddress),
                TestContext.Current.CancellationToken));

        Output.WriteLine($"DataChangeRequest error: {ex.GetType().Name}: {ex.Message}");
        ex.Should().NotBeOfType<TimeoutException>("should get error, not timeout");
        ex.ToString().Should().Contain("initialization failed");
    }
}

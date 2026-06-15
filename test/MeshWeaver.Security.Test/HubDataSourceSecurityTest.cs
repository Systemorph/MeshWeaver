using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Security.Test;

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
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).Should().Emit();
    }

    /// <summary>
    /// Link 1: HubDataSource with denied access â†’ workspace stream errors.
    /// Verifies: data source stream's OnError propagates through workspace stream to subscriber.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task HubDataSource_WithoutAccess_ShouldErrorStream()
    {
        await EnsureHubStarted(new Address("SourceHub"));
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "NobodyUser", Name = "Nobody" });

        var consumerHub = Mesh.ServiceProvider.CreateMessageHub(
            new Address("consumer", "1"),
            c => c.AddData(d =>
                d.AddHubSource(new Address("SourceHub"), ds => ds.WithType<TestItem>())));

        var stream = consumerHub.GetWorkspace().GetStream<TestItem>();

        ((object?)stream).Should().NotBeNull("workspace stream should exist for registered type");

        Func<Task> act = () => stream!.FirstAsync().Timeout(5.Seconds()).ToTask();
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        Output.WriteLine($"Exception: {ex.GetType().Name}: {ex.Message}");
        ex.Should().NotBeOfType<TimeoutException>(
            "access denial should propagate as an error, not cause a timeout");
    }

    /// <summary>
    /// Link 2: PartitionedHubDataSource â†’ combined stream errors.
    /// </summary>
    [Fact(Timeout = 20000)]
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

        ((object?)stream).Should().NotBeNull("workspace stream should exist for registered type");

        Func<Task> act = () => stream!.FirstAsync().Timeout(5.Seconds()).ToTask();
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        Output.WriteLine($"Exception: {ex.GetType().Name}: {ex.Message}");
        ex.Should().NotBeOfType<TimeoutException>(
            "partitioned subscription denial should propagate as an error, not cause a timeout");
    }

    /// <summary>
    /// Link 3: Error data source stream FIRST, then subscribe remotely.
    /// Verifies: when workspace stream is already errored, CreateSynchronizationStream's
    /// onError handler fires and posts DeliveryFailure to remote subscriber.
    /// </summary>
    [Fact(Timeout = 20000)]
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
        ((object?)internalStream).Should().NotBeNull("workspace stream should exist for registered type");

        Func<Task> internalAct = () => internalStream!.FirstAsync().Timeout(5.Seconds()).ToTask();
        var internalEx = (await internalAct.Should().ThrowAsync<Exception>()).Which;
        Output.WriteLine($"Step 1 - Internal stream errored: {internalEx.GetType().Name}: {internalEx.Message}");
        internalEx.Should().NotBeOfType<TimeoutException>("internal stream should error from access denial");

        // STEP 2: Now subscribe remotely AFTER the data source already failed
        Output.WriteLine("Step 2 - Subscribing remotely after data source failure...");
        var client = GetClient(c => c.AddData(d => d));
        var remoteStream = client.GetWorkspace().GetRemoteStream<EntityStore>(
            groupAddress, new CollectionsReference(typeof(TestItem).FullName!));

        Func<Task> remoteAct = () => remoteStream.FirstAsync().Timeout(5.Seconds()).ToTask();
        var remoteEx = (await remoteAct.Should().ThrowAsync<Exception>()).Which;
        Output.WriteLine($"Step 2 - Remote stream result: {remoteEx.GetType().Name}: {remoteEx.Message}");

        remoteEx.Should().NotBeOfType<TimeoutException>(
            "remote subscriber should get DeliveryFailure, not timeout");
    }

    /// <summary>
    /// Link 4: Subscribe remotely CONCURRENTLY with data source initialization (race).
    /// </summary>
    [Fact(Timeout = 20000)]
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

        Func<Task> act = () => stream.FirstAsync().Timeout(5.Seconds()).ToTask();
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        Output.WriteLine($"Exception: {ex.GetType().Name}: {ex.Message}");

        ex.Should().NotBeOfType<TimeoutException>(
            "client subscription should propagate data source error, not timeout");
    }

    /// <summary>
    /// Link 5: DataContext failure state â€” when initialization fails, DataContext stores the error.
    /// Subsequent SubscribeRequests get immediate DeliveryFailure with a meaningful message.
    /// </summary>
    [Fact(Timeout = 20000)]
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
        ((object?)internalStream).Should().NotBeNull();
        Func<Task> internalAct = () => internalStream!.FirstAsync().Timeout(5.Seconds()).ToTask();
        var internalEx = (await internalAct.Should().ThrowAsync<Exception>()).Which;
        Output.WriteLine($"Internal stream errored: {internalEx.GetType().Name}: {internalEx.Message}");
        internalEx.Should().NotBeOfType<TimeoutException>();

        // Now a second client subscribes â€” should get immediate error, not timeout.
        // This also gives time for DataContext.OpenInitializationGate's ContinueWith to run.
        var client2 = GetClient(c => c.AddData(d => d));
        var remoteStream2 = client2.GetWorkspace().GetRemoteStream<EntityStore>(
            groupAddress, new CollectionsReference(typeof(TestItem).FullName!));

        Func<Task> remoteAct2 = () => remoteStream2.FirstAsync().Timeout(3.Seconds()).ToTask();
        var remoteEx2 = (await remoteAct2.Should().ThrowAsync<Exception>()).Which;
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
    [Fact(Timeout = 20000)]
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
        ((object?)stream).Should().NotBeNull();
        await ((Func<Task>)(() => stream!.FirstAsync().Timeout(5.Seconds()).ToTask())).Should().ThrowAsync<Exception>();

        // GetDataRequest should throw, not timeout
        var client = GetClient();
        Func<Task> act = () => client.Observe(new GetDataRequest(new CollectionsReference(typeof(TestItem).FullName!)), o => o.WithTarget(groupAddress)).FirstAsync().ToTask();
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;

        Output.WriteLine($"GetDataRequest error: {ex.GetType().Name}: {ex.Message}");
        ex.Should().NotBeOfType<TimeoutException>("should get error, not timeout");
        ex.ToString().Should().Contain("initialization failed");
    }

    /// <summary>
    /// Link 7: Failed hub rejects DataChangeRequest with DeliveryFailure.
    /// </summary>
    [Fact(Timeout = 20000)]
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
        ((object?)stream).Should().NotBeNull();
        await ((Func<Task>)(() => stream!.FirstAsync().Timeout(5.Seconds()).ToTask())).Should().ThrowAsync<Exception>();

        // DataChangeRequest should throw, not timeout
        var client = GetClient();
        Func<Task> act = () => client.Observe(new DataChangeRequest { Updates = [new TestItem("1", "Test")] }, o => o.WithTarget(groupAddress)).FirstAsync().ToTask();
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;

        Output.WriteLine($"DataChangeRequest error: {ex.GetType().Name}: {ex.Message}");
        ex.Should().NotBeOfType<TimeoutException>("should get error, not timeout");
        ex.ToString().Should().Contain("initialization failed");
    }
}

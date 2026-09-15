using System;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Messaging;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 The kernel's idle-disconnect must not dispose a hub whose submission is still executing
/// (MeshWeaver#4422). The timer is re-armed only by messages DELIVERED to the hub, and a running
/// script delivers none — so on memex (2026-09-15) an approved OperationRequest's script died
/// silently 15 min after the last inbound message, mid-run. Idle now means "no message AND nothing
/// in flight", and the reclamation of a FINISHED hub (#1324/#1435) is unchanged — pinned in both
/// directions below.
/// </summary>
public class KernelIdleDisconnectWhileRunningTest
{
    private static (KernelContainer.IdleState State, IMessageHub Hub, IMessageHub Executor) Arrange()
    {
        var hub = Substitute.For<IMessageHub>();
        var executor = Substitute.For<IMessageHub>();
        executor.IsDisposing.Returns(false);
        var state = new KernelContainer.IdleState(new WeakReference<IMessageHub>(hub), TimeSpan.FromMinutes(15));
        state.Executor(executor);
        return (state, hub, executor);
    }

    [Fact]
    public void RunningSubmission_KeepsTheHub()
    {
        var (state, hub, _) = Arrange();
        state.Begin();

        state.Elapsed();

        hub.DidNotReceive().Dispose();
        Assert.Equal(1, state.InFlight);
    }

    [Fact]
    public void FinishedSubmission_IsReclaimedAsBefore()
    {
        var (state, hub, _) = Arrange();
        state.Begin();
        state.End();

        state.Elapsed();

        hub.Received(1).Dispose();
    }

    [Fact]
    public void NothingEverSubmitted_IsReclaimedAsBefore()
    {
        var (state, hub, _) = Arrange();

        state.Elapsed();

        hub.Received(1).Dispose();
    }

    [Fact]
    public void DeadExecutor_CannotKeepTheHubAliveForever()
    {
        var (state, hub, executor) = Arrange();
        state.Begin();
        executor.IsDisposing.Returns(true);

        state.Elapsed();

        hub.Received(1).Dispose();
    }

    [Fact]
    public void UnbalancedEnd_NeverCountsBelowZero()
    {
        var (state, _, _) = Arrange();
        state.End();
        state.End();
        state.Begin();

        Assert.Equal(1, state.InFlight);
    }
}

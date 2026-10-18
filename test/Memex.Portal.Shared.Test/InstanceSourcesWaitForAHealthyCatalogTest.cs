using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The default-install pass reads the instance's own package sources only from a HEALTHY catalog
/// (<see cref="InstanceAutoRegistrationService.WhenHealthy"/>): a catalog that loaded once and then
/// faulted still answers <c>WhenLoaded</c> at once, with the listing it held before the outage, and
/// a pass that read it would report itself complete although a source added during the outage was
/// never read.
/// </summary>
public class InstanceSourcesWaitForAHealthyCatalogTest
{
    [Fact]
    public void ALoadedCatalogThatFaulted_IsNotReadUntilItRecovers()
    {
        var loaded = new BehaviorSubject<Unit>(Unit.Default);
        var changes = new BehaviorSubject<Unit>(Unit.Default);
        var faulted = true;
        var healthy = 0;

        using var _ = InstanceAutoRegistrationService
            .WhenHealthy(loaded, changes, () => faulted)
            .Subscribe(_ => healthy++);
        Assert.Equal(0, healthy);

        // A change while still down is not recovery.
        changes.OnNext(Unit.Default);
        Assert.Equal(0, healthy);

        // The re-opened query delivers a complete listing: the fault clears, then observers are told.
        faulted = false;
        changes.OnNext(Unit.Default);
        Assert.Equal(1, healthy);
    }

    [Fact]
    public void AHealthyLoadedCatalog_IsReadAtOnce()
    {
        var healthy = 0;
        using var _ = InstanceAutoRegistrationService
            .WhenHealthy(Observable.Return(Unit.Default), new BehaviorSubject<Unit>(Unit.Default), () => false)
            .Subscribe(_ => healthy++);
        Assert.Equal(1, healthy);
    }

    [Fact]
    public void ACatalogNotYetLoaded_IsNotRead()
    {
        var healthy = 0;
        using var _ = InstanceAutoRegistrationService
            .WhenHealthy(Observable.Never<Unit>(), new BehaviorSubject<Unit>(Unit.Default), () => false)
            .Subscribe(_ => healthy++);
        Assert.Equal(0, healthy);
    }
}

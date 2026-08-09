using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive.Disposables;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// #995 — the mesh-node picker's 200 ms search debounce must be owned by the COMPONENT, not by
/// the next keystroke.
///
/// <para><b>Why this one survived the #996 sweep.</b> The subscription was assigned to a field
/// (<c>_debounceSub</c>), which is what "owned" looks like to any static rule — and indeed a
/// discarded-<c>Subscribe</c> analyzer passes this file clean. But the only code that ever
/// disposed that field was the *next* <c>OnSearchInput</c>. Type into the picker and navigate
/// away, and the last <c>Observable.Timer</c> stays on the process-wide <c>TimerQueue</c> — a
/// strong GC root — holding the tick closure → the component → its injected <c>IMeshService</c>,
/// past component teardown. Capture was present; ownership was not.</para>
///
/// <para><b>What is asserted.</b> Both halves of the ownership, timing-free: a keystroke arms the
/// debounce INTO the component-registered handle (observable as the previously pending debounce
/// being cancelled), and <c>DisposeAsync</c> — the component's real teardown path, which releases
/// <c>BlazorView.Disposables</c> — cancels whatever is still pending. Negative control: drop the
/// <c>Disposables.Add(_debounceSub)</c> from <c>MeshNodePickerView.OnInitialized</c> and the
/// "registered exactly one handle" assertion fails.</para>
/// </summary>
public class MeshNodePickerDebounceOwnershipTest
{
    /// <summary>
    /// Drives the component through its REAL lifecycle methods (<c>OnInitialized</c>,
    /// <c>DisposeAsync</c>) without a renderer — this test is about disposal wiring, not markup.
    /// </summary>
    private sealed class Probe : MeshNodePickerView
    {
        // A bare view-model with no Queries and no Items, so OnSearchInput takes the remote
        // (debounced) branch rather than the in-memory filter. Stream/Area stay unset: neither
        // lifecycle method under test reads them.
        [SetsRequiredMembers]
        public Probe()
        {
            ViewModel = new MeshNodePickerControl(new object());
            Logger = NullLogger<MeshNodePickerView>.Instance;
        }

        public void Initialize() => OnInitialized();

        public IReadOnlyList<IDisposable> Owned => Disposables;
    }

    /// <summary>
    /// A keystroke arms the debounce into the component-owned handle, and component teardown
    /// cancels it — not merely the next keystroke.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_CancelsTheLastPendingDebounce()
    {
        var probe = new Probe();
        probe.Initialize();

        var owned = probe.Owned.OfType<SerialDisposable>().ToArray();
        owned.Should().ContainSingle(
            "OnInitialized must register the debounce handle in BlazorView.Disposables — that "
            + "registration is the ONLY thing that lets component teardown cancel a pending "
            + "200 ms search timer (#995)");
        var debounce = owned[0];

        // Stand-in for a debounce that is already pending when the user types again.
        var previousCancelled = false;
        debounce.Disposable = Disposable.Create(() => previousCancelled = true);

        InvokeSearchInput(probe, "acme");

        previousCancelled.Should().BeTrue(
            "each keystroke must arm into the component-owned handle, which cancels the "
            + "previously armed debounce — otherwise the registration guards a slot the "
            + "search path never writes to");
        debounce.IsDisposed.Should().BeFalse("the component is still alive");

        await probe.DisposeAsync();

        debounce.IsDisposed.Should().BeTrue(
            "component teardown must cancel the debounce armed by the last keystroke; before "
            + "#995 only the NEXT keystroke disposed it, so typing and navigating away left a "
            + "200 ms timer on the TimerQueue rooting the component and its injected services");
    }

    /// <summary>
    /// Invokes the picker's private input handler — the debounce entry point. The guard turns a
    /// rename into a loud failure instead of a silently vacuous test.
    /// </summary>
    private static void InvokeSearchInput(MeshNodePickerView view, string text)
    {
        var handler = typeof(MeshNodePickerView)
            .GetMethod("OnSearchInput", BindingFlags.Instance | BindingFlags.NonPublic);
        handler.Should().NotBeNull(
            "MeshNodePickerView.OnSearchInput is the debounce entry point this test drives — "
            + "if it was renamed, update this test rather than losing the coverage");
        handler!.Invoke(view, [new ChangeEventArgs { Value = text }]);
    }
}

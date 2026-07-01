using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Maui.Abstractions;
using Xunit;

namespace MeshWeaver.Maui.Abstractions.Test;

/// <summary>
/// <see cref="MauiAreaProgress.ShowsProgress"/> — the native mirror of Blazor's NamedAreaView spinner gate.
/// Pins the contract the view pack relies on: an EXPLICIT <c>false</c> / <see cref="SpinnerType.None"/> makes
/// an area silent (no spinner, no "couldn't load" notice while empty — the fix for the Overview's
/// <c>Approvals</c> embed), while unset (null) / true keeps the spinner so no existing area regresses.
/// </summary>
public class MauiAreaProgressTest
{
    [Fact]
    public void NullShowProgress_DefaultsToShowing() =>
        MauiAreaProgress.ShowsProgress(null, SpinnerType.Ring).Should().BeTrue();

    [Fact]
    public void ExplicitBoolFalse_Suppresses() =>
        MauiAreaProgress.ShowsProgress(false, SpinnerType.Ring).Should().BeFalse();

    [Fact]
    public void ExplicitBoolTrue_Shows() =>
        MauiAreaProgress.ShowsProgress(true, SpinnerType.Ring).Should().BeTrue();

    [Fact]
    public void SpinnerTypeNone_SuppressesEvenWhenShowProgressTrue() =>
        MauiAreaProgress.ShowsProgress(true, SpinnerType.None).Should().BeFalse();

    [Fact]
    public void JsonFalse_Suppresses() =>
        MauiAreaProgress.ShowsProgress(JsonDocument.Parse("false").RootElement, SpinnerType.Ring).Should().BeFalse();

    [Fact]
    public void JsonTrue_Shows() =>
        MauiAreaProgress.ShowsProgress(JsonDocument.Parse("true").RootElement, SpinnerType.Ring).Should().BeTrue();

    [Fact]
    public void StringFalse_Suppresses() =>
        MauiAreaProgress.ShowsProgress("false", SpinnerType.Ring).Should().BeFalse();

    [Fact]
    public void UnknownShape_DefaultsToShowing() =>
        MauiAreaProgress.ShowsProgress(42, SpinnerType.Ring).Should().BeTrue();
}

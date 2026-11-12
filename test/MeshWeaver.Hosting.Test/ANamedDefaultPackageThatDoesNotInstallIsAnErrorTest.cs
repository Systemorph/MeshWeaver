using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A package the deployment configuration NAMES exactly and the default install does not
/// select is an ERROR, judged per entry</b> (<see cref="InstanceAutoRegistrationService.ReportUnselectedOperatorEntries"/>).
///
/// <para>Measured 2026-11-12 on a fleet machine: <c>PluginCatalog:InstallByDefault:0 =
/// Plugins/Hosting</c>, the instance on <c>free</c>, Hosting on tier <c>enterprise</c>. The registry's
/// listing did not carry Hosting at all, and the only line was a Warning that fired only when NO
/// operator pattern matched — so the package carrying the fleet heartbeat never installed for 13
/// days and nobody was told. An Error is what reaches a person: the instance's error digest carries
/// it to the control instance's triage (<see cref="ErrorSignatureCollector"/>).</para>
/// </summary>
public class ANamedDefaultPackageThatDoesNotInstallIsAnErrorTest
{
    private const string Source = "Plugins";

    private static readonly IReadOnlyCollection<string> Sources = [Source];

    private static PluginGrantEntry Entry(string package) => new() { Source = Source, PackageId = package };

    private static PackageManifest Listed(string id) => new() { Id = id, Name = id, Source = Source };

    private static PackageManifest Refused(string id, string tier, string plan) =>
        PackageManifest.FromRefusal(new PlanTierRefusal(id, null, tier, plan), Source);

    [Fact]
    public void AnExactlyNamedPackageMissingFromTheListing_IsOneErrorNamingItAndThePlan()
    {
        var logger = new RecordingLogger();
        // Another default-set package the plan refuses is what tells the pass which plan it is on.
        var catalog = new[] { Listed("Store"), Refused("Billing", "pro", "free") };
        var named = new[] { Entry("Hosting"), Entry("Store") };

        var reported = InstanceAutoRegistrationService.ReportUnselectedOperatorEntries(
            logger, named, named, catalog, Sources, selectedCount: 2, listingComplete: true);

        reported.Select(e => e.ToString()).Should().Equal(["Plugins/Hosting"]);
        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errors.Should().HaveCount(1, "exactly one named entry selects nothing");
        errors[0].Template.Should().Be(InstanceAutoRegistrationService.NamedEntryNotListedTemplate);
        errors[0].Message.Should().Contain("Plugins/Hosting");
        errors[0].Message.Should().Contain("this instance is on plan free");
        errors[0].Message.Should().Contain("Settings > Instance grants");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning,
            "every pattern here is named exactly; none falls back to the wildcard Warning");
    }

    [Fact]
    public void WithNoRefusalInTheAnswer_ThePlanIsSaidToBeUnknown_AndAFailedListingIsNamed()
    {
        var logger = new RecordingLogger();
        var named = new[] { Entry("Hosting") };

        InstanceAutoRegistrationService.ReportUnselectedOperatorEntries(
            logger, named, named, [Listed("Store")], Sources, selectedCount: 1, listingComplete: false);

        var error = logger.Entries.Single(e => e.Level == LogLevel.Error);
        error.Message.Should().Contain(InstanceAutoRegistrationService.PlanUnknown);
        error.Message.Should().Contain("listing complete: False");
    }

    [Fact]
    public void EveryNamedEntryThatSelectsNothing_IsReported_NotOnlyWhenNoneMatched()
    {
        var logger = new RecordingLogger();
        var named = new[] { Entry("Hosting"), Entry("Store"), Entry("Mail") };

        var reported = InstanceAutoRegistrationService.ReportUnselectedOperatorEntries(
            logger, named, named, [Listed("Store")], Sources, selectedCount: 1, listingComplete: true);

        reported.Select(e => e.PackageId).Should().Equal(["Hosting", "Mail"]);
        logger.Entries.Count(e => e.Level == LogLevel.Error).Should().Be(2);
    }

    [Fact]
    public void ANamedPackageRefusedByPlanTier_IsAnError()
    {
        var logger = new RecordingLogger();
        var named = new[] { Entry("Hosting") };

        var reported = InstanceAutoRegistrationService.ReportUnselectedOperatorEntries(
            logger, named, named, [Refused("Hosting", "enterprise", "free")], Sources,
            selectedCount: 1, listingComplete: true);

        reported.Should().HaveCount(1);
        var error = logger.Entries.Single(e => e.Level == LogLevel.Error);
        error.Template.Should().Be(InstanceAutoRegistrationService.NamedEntryRefusedByPlanTemplate);
        error.Message.Should().Contain("Hosting needs plan tier enterprise, this instance is on free");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void AWildcardMatchingNothing_IsAWarningOnly()
    {
        var logger = new RecordingLogger();
        var wanted = new[] { Entry(PluginGrantEntry.AllPackages) };

        var reported = InstanceAutoRegistrationService.ReportUnselectedOperatorEntries(
            logger, wanted, wanted, [Listed("Other") with { Source = "Education" }], Sources,
            selectedCount: 8, listingComplete: true);

        reported.Should().BeEmpty();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error,
            "a whole-source pattern names nothing — it is no declared requirement");
        var warning = logger.Entries.Single(e => e.Level == LogLevel.Warning);
        warning.Message.Should().Contain("matched no packages");
        warning.Message.Should().Contain("Plugins/*");
    }

    [Fact]
    public void ANamedEntryThatIsSelected_RaisesNothing()
    {
        var logger = new RecordingLogger();
        var named = new[] { Entry("Hosting") };
        var wanted = new[] { Entry("Hosting"), Entry(PluginGrantEntry.AllPackages) };

        var reported = InstanceAutoRegistrationService.ReportUnselectedOperatorEntries(
            logger, named, wanted, [Listed("Hosting"), Listed("Store")], Sources,
            selectedCount: 2, listingComplete: true);

        reported.Should().BeEmpty();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void AnEntryNamingAnUnconfiguredSource_IsLeftToTheUnmatchablePatternsError()
    {
        var logger = new RecordingLogger();
        var named = new[] { new PluginGrantEntry { Source = "Elsewhere", PackageId = "Hosting" } };

        var reported = InstanceAutoRegistrationService.ReportUnselectedOperatorEntries(
            logger, named, named, [Listed("Store")], Sources, selectedCount: 1, listingComplete: true);

        reported.Should().BeEmpty("UnmatchablePatterns already reports it at Error before any listing");
        InstanceAutoRegistrationService.UnmatchablePatterns(named, Sources).Should().HaveCount(1);
    }

    /// <summary>Captures each record's level, formatted text and structured TEMPLATE — the
    /// template is what the error digest carries to triage, so it is asserted too.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private ImmutableList<(LogLevel Level, string Message, string? Template)> entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, string? Template)> Entries => Volatile.Read(ref entries);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Disposable.Empty;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var template = (state as IEnumerable<KeyValuePair<string, object?>>)?
                .FirstOrDefault(kv => kv.Key == "{OriginalFormat}").Value as string;
            ImmutableInterlocked.Update(ref entries, (list, e) => list.Add(e),
                (logLevel, formatter(state, exception), template));
        }
    }
}

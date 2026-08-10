using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The DATA-LOSS contract for <see cref="CompletionMemoryStore"/>.
///
/// <para>The store persists by REPLACEMENT — one settings node whose Content is the whole
/// acceptance history — so every save deletes whatever it did not put back. That makes one question
/// load-bearing: <i>did a read actually establish what is stored?</i> A read that reached no verdict
/// (timed out, faulted, no storage yet) must never be answered with "the user has no history",
/// because that answer, once cached and saved, IS the deletion.</para>
///
/// <para>What used to happen: a 15 s load timeout was folded into an empty node list by a blanket
/// <c>Catch(Observable.Return(Empty))</c>, cached as an empty memory that nothing ever reloaded, and
/// the very next acceptance marked it dirty so the debounced save wrote that near-empty memory over
/// the real history. Nothing threw and nothing logged a loss.</para>
///
/// <para>Everything here runs on a <see cref="HistoricalScheduler"/>: the 15 s load timeout and the
/// 10 s save debounce are crossed in virtual time, so these are exact, not races.</para>
/// </summary>
public class CompletionMemoryLoadVerdictTest
{
    private const string Viewer = "alice";

    /// <summary>An LSP completion kind — which one is irrelevant, only that it is stable.</summary>
    private const int Kind = 2;

    /// <summary>Three acceptances standing in for the user's real, already-stored history.</summary>
    private static CompletionMemory StoredHistory() =>
        new CompletionMemory()
            .Record("St", "Stack", Kind)
            .Record("Ma", "Markdown", Kind)
            .Record("La", "LayoutGrid", Kind);

    /// <summary>The query fold as it looks when the viewer's settings node IS stored.</summary>
    private static ImmutableList<MeshNode> StoredNodes(CompletionMemory memory) =>
        ImmutableList.Create(
            MeshNode.FromPath(CompletionMemoryStore.PathFor(Viewer)) with
            {
                Name = "Completion memory",
                NodeType = CompletionMemoryNodeType.NodeType,
                Content = memory,
            });

    /// <summary>A live query that has answered "nothing here" and stays open (never completes).</summary>
    private static IObservable<ImmutableList<MeshNode>> Answers(ImmutableList<MeshNode> nodes) =>
        Observable.Return(nodes).Concat(Observable.Never<ImmutableList<MeshNode>>());

    /// <summary>A read that never reaches a verdict — the shape a stalled backend produces.</summary>
    private static IObservable<ImmutableList<MeshNode>> NeverAnswers() =>
        Observable.Never<ImmutableList<MeshNode>>();

    private sealed record Harness(CompletionMemoryStore Store, List<MeshNode> Saves, Func<int> LoadAttempts);

    /// <summary>
    /// Wires the store's seam constructor: <paramref name="loadForAttempt"/> receives the 0-based
    /// load attempt (so a test can make the first read stall and a later one answer), and every save
    /// is captured instead of hitting a mesh.
    /// </summary>
    private static Harness Build(
        HistoricalScheduler scheduler,
        Func<int, IObservable<ImmutableList<MeshNode>>> loadForAttempt)
    {
        var saves = new List<MeshNode>();
        var attempts = 0;
        var store = new CompletionMemoryStore(
            NullLogger.Instance,
            () => null,
            () => new JsonSerializerOptions(),
            _ => loadForAttempt(attempts++),
            node =>
            {
                saves.Add(node);
                return Observable.Return(node);
            },
            scheduler);
        return new Harness(store, saves, () => attempts);
    }

    private static IReadOnlyList<string> SavedLabels(MeshNode node) =>
        ((CompletionMemory)node.Content!).Entries.Select(e => e.Label).ToList();

    /// <summary>
    /// 🚨 THE REGRESSION. A load that reaches no verdict, followed by an acceptance, must not
    /// produce a save at all — the alternative is writing a one-entry memory over a history this
    /// process never managed to read.
    /// </summary>
    [Fact]
    public void LoadThatReachesNoVerdict_IsNeverPersistedOverStoredHistory()
    {
        var scheduler = new HistoricalScheduler();
        var harness = Build(scheduler, _ => NeverAnswers());
        using var store = harness.Store;

        // A completion request kicks the load off. It never answers.
        store.For(Viewer).Entries.Should().BeEmpty("nothing has been read yet");

        // Past the 15 s load timeout.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(16));

        // The user accepts one completion, then goes quiet past the 10 s save debounce.
        store.Record(Viewer, "St", "Stack", Kind);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(11));

        harness.Saves.Should().BeEmpty(
            "a read that reached no verdict says NOTHING about what is stored, so a memory built on "
            + "it must never be written back — that write would replace the user's whole acceptance "
            + "history with this single acceptance");
    }

    /// <summary>
    /// The same loss without any timeout: an acceptance that beats a HEALTHY load home. The old
    /// <c>TryAdd</c> here kept the in-flight fragment and discarded the history it had just read;
    /// the merge must go the other way — replay the fragment ON TOP of the stored history.
    /// </summary>
    [Fact]
    public void AcceptanceRecordedWhileLoading_IsMergedOntoStoredHistory_NotSubstitutedForIt()
    {
        var scheduler = new HistoricalScheduler();
        var source = new Subject<ImmutableList<MeshNode>>();
        var harness = Build(scheduler, _ => source);
        using var store = harness.Store;

        store.For(Viewer);                              // load in flight
        store.Record(Viewer, "Ne", "NewThing", Kind);   // acceptance beats it home

        source.OnNext(StoredNodes(StoredHistory()));
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1));   // past the 500 ms quiet window
        scheduler.AdvanceBy(TimeSpan.FromSeconds(11));  // past the save debounce

        var labels = SavedLabels(harness.Saves.Should().ContainSingle().Subject);
        labels.Should().Contain("NewThing", "the in-flight acceptance must survive the load");
        labels.Should().Contain("Stack", "the loaded history must survive the in-flight acceptance");
        labels.Should().Contain("Markdown");
        labels.Should().Contain("LayoutGrid");
    }

    /// <summary>
    /// The guard must not silence a genuine first-time user: a read that DEFINITIVELY finds nothing
    /// is a verdict, so their first acceptance is persisted normally.
    /// </summary>
    [Fact]
    public void LoadThatDefinitivelyFindsNothing_StillPersists()
    {
        var scheduler = new HistoricalScheduler();
        var harness = Build(scheduler, _ => Answers(ImmutableList<MeshNode>.Empty));
        using var store = harness.Store;

        store.For(Viewer);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1));   // verdict reached: nothing is stored

        store.Record(Viewer, "St", "Stack", Kind);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(11));

        SavedLabels(harness.Saves.Should().ContainSingle(
                "a definitive absence is a verdict, so saving over it loses nothing").Subject)
            .Should().Contain("Stack");
    }

    /// <summary>
    /// Re-resolution is USER-DRIVEN, not a timer: after a no-verdict read the store caches nothing
    /// and releases its load marker, so the viewer's next completion request runs a fresh load
    /// through the same chain the success path takes — and once that one answers, saving is armed.
    /// </summary>
    [Fact]
    public void AfterANoVerdictLoad_TheNextCompletionRequestReResolves_AndThenSaves()
    {
        var scheduler = new HistoricalScheduler();
        var harness = Build(scheduler, attempt => attempt == 0
            ? NeverAnswers()
            : Answers(StoredNodes(StoredHistory())));
        using var store = harness.Store;

        store.For(Viewer);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(16));  // no verdict
        harness.LoadAttempts().Should().Be(1);

        store.For(Viewer);                              // the next completion request re-resolves
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
        harness.LoadAttempts().Should().Be(2, "the stalled read left nothing cached to reuse");

        store.Record(Viewer, "Ne", "NewThing", Kind);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(11));

        var labels = SavedLabels(harness.Saves.Should().ContainSingle().Subject);
        labels.Should().Contain("NewThing");
        labels.Should().Contain("Stack", "the re-resolved load grounds the save in stored history");
    }
}

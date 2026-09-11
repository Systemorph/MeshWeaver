#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using MeshWeaver.GitSync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 The decision table behind "a green build reaches every installation": which
/// <c>workflow_run</c> deliveries become a <c>BuildCompletion</c> record and trigger the GitSync
/// import, and which are discarded.
///
/// <para><b>Why this lives here, and why it is a pure test.</b> The suite that boots a mesh and
/// drives <c>GitHubWebhookProcessor.Process</c> end to end (<c>MeshWeaver.GitSync.Test</c>) moved to
/// MeshWeaver.Plugins with the boots-a-mesh split, and it pins the WIRING — that a green default-branch
/// run records and imports while a PR-branch one does not. The POLICY — which trigger, branch and
/// workflow file together count as "the repository's content CI proved its default-branch tree" —
/// is three pure functions of a payload, needs no mesh, and is the part that was wrong; so it is
/// pinned in core, next to the code it governs.</para>
///
/// <para><b>The failure it exists for (2026-09-02,
/// <c>Systemorph/MeshWeaver.Plugins#1194</c>).</b> The trigger test was <c>event == "push"</c>, a
/// single value rather than a set. <c>MeshWeaver.Reinsurance</c>'s <c>main</c> built green three
/// times at <c>636ebd5</c> that day — 11:17, 12:27 and 12:55Z — every one of them
/// <c>event=repository_dispatch</c>, because the release-follow lane rebuilds every module against a
/// new platform pin with no commit to push. All three were discarded, and
/// <c>Underwriting/_GitSync</c> on <c>memex.systemorph.com</c> stayed 38 hours behind a merged main
/// with the webhook armed, every delivery 200 OK, and nothing anywhere reporting a problem. A
/// dropped publish signal has no symptom other than content that quietly stops arriving.</para>
///
/// <para>🚨 <b>Every case is stated as data, both directions.</b> A test that only listed the
/// admitted triggers would go green against a gate that admits EVERYTHING — which is the failure a
/// deny-list would produce, and the reason the gate is an allow-list.</para>
/// </summary>
public class GreenBuildPublishSignalTest
{
    private const string Default = "main";
    private const string ContentWorkflow = ".github/workflows/ci.yml";
    private static readonly RepoIdentity Repository = new("Systemorph", "MeshWeaver.Reinsurance");

    /// <summary>
    /// Every trigger under which the content CI is eligible to publish, with why. Stated here
    /// rather than read off the production set: a test that enumerates its subject's own list
    /// asserts nothing about what that list should contain.
    /// </summary>
    public static TheoryData<string, string> AdmittedTriggers() => new()
    {
        { "push", "the branch moved and its content CI ran — the original case" },
        { "repository_dispatch", "only ever runs on the default branch; how a platform release "
                                 + "re-verifies a satellite with no commit to push (#1194)" },
        { "schedule", "the content CI's cron run only exists on the default branch" },
        { "workflow_dispatch", "may target any ref — admitted here, discriminated by head_branch" },
    };

    /// <summary>
    /// Every trigger that must stay REFUSED, with why. <c>merge_group</c> is not here because it is
    /// rejected one guard later — it has its own case below, asserting the mechanism that rejects it.
    /// </summary>
    public static TheoryData<string, string> RefusedTriggers() => new()
    {
        { "pull_request", "green UNMERGED code" },
        { "pull_request_target", "same, with the base repo's token" },
        { "dynamic", "GitHub's Copilot reviewer — completes green on the default branch, not a build" },
        { "check_run", "not a build of the tree" },
        { "issues", "not a build at all" },
        { "release", "a tag's tree, not necessarily the branch's" },
        { "deployment", "not a build of the tree" },
        { "a_trigger_github_has_not_invented_yet", "fail closed: unknown means refused" },
        { "", "an unreadable event is not a publish signal" },
    };

    // ── the trigger half ─────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AdmittedTriggers))]
    public void AnAdmittedTriggerOnTheDefaultBranch_IsAPublishSignal(string trigger, string why)
    {
        Assert.True(GitHubWebhookProcessor.IsPublishSignalTrigger(trigger), $"{trigger}: {why}");
        Assert.True(IsPublishSignal(trigger, headBranch: Default), $"{trigger}: {why}");
    }

    [Theory]
    [MemberData(nameof(RefusedTriggers))]
    public void ARefusedTrigger_IsNotAPublishSignal_EvenOnTheDefaultBranch(string trigger, string why)
    {
        Assert.False(GitHubWebhookProcessor.IsPublishSignalTrigger(trigger), $"{trigger}: {why}");
        // 🚨 On the DEFAULT branch specifically. The branch guard cannot save us here — a Copilot
        // review and a pull_request run of the content workflow both report head_branch=main — so if
        // the trigger guard ever fails open, this is the assertion that catches it.
        Assert.False(IsPublishSignal(trigger, headBranch: Default), $"{trigger}: {why}");
    }

    [Fact]
    public void TheTriggerGuardFailsClosedOnAMissingEvent()
    {
        Assert.False(GitHubWebhookProcessor.IsPublishSignalTrigger(null));
        Assert.False(GitHubWebhookProcessor.IsPublishSignalTrigger(""));
        Assert.False(GitHubWebhookProcessor.IsPublishSignalTrigger("   "));
    }

    [Fact]
    public void TheTriggerGuardIsCaseInsensitive_BecauseTheWireIsNotOurs()
    {
        Assert.True(GitHubWebhookProcessor.IsPublishSignalTrigger("Repository_Dispatch"));
        Assert.True(GitHubWebhookProcessor.IsPublishSignalTrigger("PUSH"));
        Assert.False(GitHubWebhookProcessor.IsPublishSignalTrigger("Pull_Request"));
    }

    // ── the branch half: the second, independent guard ───────────────────────

    [Theory]
    [MemberData(nameof(AdmittedTriggers))]
    public void AnAdmittedTriggerOffTheDefaultBranch_IsNotAPublishSignal(string trigger, string why)
    {
        // A workflow_dispatch aimed at a feature branch is the live shape of this; the others cannot
        // reach it today, and are asserted anyway so the guard stays independent of that fact.
        Assert.False(IsPublishSignal(trigger, headBranch: "feat/x"), $"{trigger}: {why}");
        Assert.False(IsPublishSignal(trigger, headBranch: ""), $"{trigger}: {why}");           // a tag run
        Assert.False(IsPublishSignal(trigger, headBranch: Default, defaultBranch: ""), why);   // unreadable repo
    }

    /// <summary>
    /// 🚨 <c>merge_group</c> is absent from the allow-list ON PURPOSE, and this is the measurement
    /// that says it may stay absent: a merge-queue run's <c>head_branch</c> is the temporary
    /// <c>gh-readonly-queue/…</c> ref, so the branch guard rejects it whatever the trigger list says.
    /// Measured on <c>Systemorph/MeshWeaver</c>'s own queue, 2026-09-02.
    /// </summary>
    [Fact]
    public void AMergeQueueRunIsRejectedByTheBranchGuard_WhichIsWhyItIsNotOnTheAllowList()
    {
        const string queueRef = "gh-readonly-queue/main/pr-3143-508c1ee373c66aba2999622fa21c395ffc0a9480";
        Assert.False(GitHubWebhookProcessor.IsDefaultBranchBuild(queueRef, Default));
        Assert.False(IsPublishSignal("merge_group", headBranch: queueRef));
        // …and admitting the trigger would still not publish it — the entry would be unreachable.
        Assert.False(IsPublishSignal("push", headBranch: queueRef));
    }

    [Fact]
    public void TheBranchGuardMatchesCaseInsensitivelyAndFailsClosedOnEitherSideMissing()
    {
        Assert.True(GitHubWebhookProcessor.IsDefaultBranchBuild("Main", "main"));
        Assert.False(GitHubWebhookProcessor.IsDefaultBranchBuild(null, "main"));
        Assert.False(GitHubWebhookProcessor.IsDefaultBranchBuild("main", null));
        Assert.False(GitHubWebhookProcessor.IsDefaultBranchBuild("", ""));
        Assert.False(GitHubWebhookProcessor.IsDefaultBranchBuild("mainline", "main"));
    }

    // ── the workflow identity: the repository's content CI, not any green run ────────────────

    [Theory]
    [InlineData("Systemorph", "MeshWeaver", ".github/workflows/dotnet-test.yml", true)]
    [InlineData("Systemorph", "MeshWeaver", ".github/workflows/chart-gate.yml", false)]
    [InlineData("Systemorph", "MeshWeaver", ".github/workflows/prod-synthetic-probe.yml", false)]
    [InlineData("someone", "MeshWeaver", ".github/workflows/ci.yml", true)]
    [InlineData("someone", "MeshWeaver", ".github/workflows/dotnet-test.yml", false)]
    [InlineData("Systemorph", "MeshWeaver.Reinsurance", ".github/workflows/ci.yml", true)]
    [InlineData("Systemorph", "MeshWeaver.Reinsurance", ".github/workflows/auto-update-green-prs.yml", false)]
    [InlineData("someone", "content-repo", ".github/workflows/ci.yml", true)]
    [InlineData("someone", "content-repo", ".github/workflows/deploy.yml", false)]
    public void OnlyTheRepositorysContentCiWorkflowCanPublish(
        string owner, string repo, string workflowPath, bool expected)
    {
        Assert.Equal(expected,
            GitHubWebhookProcessor.IsRepositoryContentWorkflow(
                new RepoIdentity(owner, repo), workflowPath));
    }

    [Fact]
    public void AWorkflowWithNoPathCannotClaimItCheckedContent()
    {
        var repository = new RepoIdentity("Systemorph", "MeshWeaver.Plugins");
        Assert.False(GitHubWebhookProcessor.IsRepositoryContentWorkflow(repository, null));
        Assert.False(GitHubWebhookProcessor.IsRepositoryContentWorkflow(repository, ""));
        Assert.False(GitHubWebhookProcessor.IsRepositoryContentWorkflow(repository, "   "));
    }

    [Fact]
    public void AnArbitraryRepositoryCanDeclareItsOneRepositoryLevelWorkflowPath()
    {
        var repository = new RepoIdentity("customer", "knowledge");
        var overrides = new[]
        {
            new GitHubContentWorkflow
            {
                Repository = "https://github.com/CUSTOMER/Knowledge",
                Path = ".github/workflows/publish-content.yml",
            },
        };

        Assert.True(GitHubWebhookProcessor.IsRepositoryContentWorkflow(
            repository, ".github/workflows/publish-content.yml", overrides));
        Assert.False(GitHubWebhookProcessor.IsRepositoryContentWorkflow(
            repository, ".github/workflows/ci.yml", overrides));
    }

    [Fact]
    public void RepositoryLevelWorkflowOverridesBindFromTheDocumentedConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:ContentWorkflows:Repositories:0:Repository"] = "customer/knowledge",
                ["GitHub:ContentWorkflows:Repositories:0:Path"] =
                    ".github/workflows/publish-content.yml",
            })
            .Build();
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddGitHubSyncServices()
            .BuildServiceProvider();

        var configured = services
            .GetRequiredService<IOptions<GitHubContentWorkflowOptions>>()
            .Value.Repositories;
        var entry = Assert.Single(configured);
        Assert.Equal("customer/knowledge", entry.Repository);
        Assert.Equal(".github/workflows/publish-content.yml", entry.Path);
    }

    [Fact]
    public void WorkflowPathsAreGitPaths_AndTheirCaseIsNotInventedAway()
    {
        var repository = new RepoIdentity("Systemorph", "MeshWeaver.Plugins");
        Assert.True(GitHubWebhookProcessor.IsRepositoryContentWorkflow(
            repository, GitHubWebhookProcessor.StandardContentWorkflowPath));
        Assert.False(GitHubWebhookProcessor.IsRepositoryContentWorkflow(
            repository, ".github/workflows/CI.yml"));
    }

    [Fact]
    public void AGreenScheduledPrUpdaterOnMain_IsNotAPublishSignal()
    {
        Assert.True(GitHubWebhookProcessor.IsPublishSignalTrigger("schedule"),
            "the trigger stays broad because the real content CI also runs on a schedule");
        Assert.True(GitHubWebhookProcessor.IsDefaultBranchBuild(Default, Default));
        Assert.False(IsPublishSignal(
            "schedule", Default, workflowPath: ".github/workflows/auto-update-green-prs.yml"));
    }

    [Fact]
    public void CoreContentCiRemainsAtThePublishSignalPath()
    {
        var root = SourceScan.FindRepoRoot();
        var workflowNames = Directory
            .EnumerateFiles(Path.Combine(root, ".github", "workflows"))
            .Select(Path.GetFileName);
        Assert.Contains(workflowNames,
            name => string.Equals(name, "dotnet-test.yml", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(root, ".github", "workflows", "dotnet-test.yml")),
            "moving core content CI would make every workflow_run fail the path discriminator; "
            + "the renamed workflow must fail red instead of silently freezing GitSync");
        Assert.Equal(".github/workflows/dotnet-test.yml",
            GitHubWebhookProcessor.ExpectedContentWorkflowPath(
                new RepoIdentity("Systemorph", "MeshWeaver")));
    }

    // ── the composition is the processor's, not this file's ──────────────────

    /// <summary>
    /// 🚨 <b>Control arm.</b> Everything above tests three predicates; this asserts that
    /// <c>ProcessWorkflowRun</c> still CALLS all three — otherwise a refactor that drops one guard
    /// leaves every assertion above green while the gate is gone, which is exactly the shape
    /// AGENTS.md warns about ("a guard whose subject moved and whose roots did not passes having
    /// checked nothing").
    /// </summary>
    [Fact]
    public void ProcessWorkflowRunStillRunsAllThreeGuards()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceScan.FindRepoRoot(), "src", "MeshWeaver.GitSync", "GitHubWebhookProcessor.cs"));

        var body = MethodBody(source, "private IObservable<int> ProcessWorkflowRun(JsonElement payload)");
        Assert.Contains("IsPublishSignalTrigger(", body, StringComparison.Ordinal);
        Assert.Contains("IsDefaultBranchBuild(", body, StringComparison.Ordinal);
        Assert.Contains("IsRepositoryContentWorkflow(", body, StringComparison.Ordinal);
        // The conclusion gate is the fourth leg of the same decision and is asserted end-to-end by the
        // mesh suite; pinned here too because losing it would publish RED builds, silently.
        Assert.Contains("\"success\"", body, StringComparison.Ordinal);
    }

    /// <summary>The braces-balanced body of the method whose signature line is given.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0,
            $"'{signature}' was not found in GitHubWebhookProcessor.cs — the signature moved, and this "
            + "guard would otherwise report green having read nothing.");

        var open = source.IndexOf('{', start + signature.Length);
        Assert.True(open >= 0, "no method body found after " + signature);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        Assert.Fail("unbalanced braces while reading the body of " + signature);
        return string.Empty;
    }

    /// <summary>
    /// The processor's own composition, in the order it applies it: a delivery publishes when the
    /// trigger is admitted AND the run is on the default branch AND the workflow path is the
    /// repository's content CI.
    /// </summary>
    private static bool IsPublishSignal(
        string trigger,
        string headBranch,
        string defaultBranch = Default,
        string workflowPath = ContentWorkflow)
        => GitHubWebhookProcessor.IsPublishSignalTrigger(trigger)
           && GitHubWebhookProcessor.IsDefaultBranchBuild(headBranch, defaultBranch)
           && GitHubWebhookProcessor.IsRepositoryContentWorkflow(Repository, workflowPath);
}

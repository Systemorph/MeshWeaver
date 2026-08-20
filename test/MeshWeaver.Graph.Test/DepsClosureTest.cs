#pragma warning disable CS1591

using MeshWeaver.Plugin.Build;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The deps-closure derivation (<see cref="DepsClosure"/>): a module bundle carries the module's
/// OWN package dependencies and never the platform's. Entry-DLL-only bundles landed modules that
/// faulted at first use (<c>Microsoft.Extensions.AI.OpenAI</c> on chat, <c>Microsoft.Graph</c> at
/// host start — the 2026-08-19/20 memex outage), and bundling the platform side instead would
/// shadow <c>/app</c> at the consumer. The split is the whole contract, so it is pinned here on a
/// synthetic graph shaped like the real one.
/// </summary>
public class DepsClosureTest
{
    /// <summary>A deps.json shaped like MeshWeaver.Mail.MicrosoftGraph's: one platform project
    /// reference, one own package with a transitive dependency, and a DIAMOND package reachable
    /// from both sides.</summary>
    private const string Graph = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
          "targets": {
            ".NETCoreApp,Version=v10.0": {
              "MeshWeaver.Mail.MicrosoftGraph/1.0.0": {
                "dependencies": {
                  "MeshWeaver.AI": "3.0.0",
                  "Microsoft.Graph": "5.56.0",
                  "Azure.Identity": "1.13.1"
                },
                "runtime": { "MeshWeaver.Mail.MicrosoftGraph.dll": {} }
              },
              "MeshWeaver.AI/3.0.0": {
                "dependencies": { "Microsoft.Extensions.Options": "10.0.0" },
                "runtime": { "MeshWeaver.AI.dll": {} }
              },
              "Microsoft.Graph/5.56.0": {
                "dependencies": {
                  "Microsoft.Kiota.Abstractions": "1.12.0",
                  "Microsoft.Extensions.Options": "10.0.0"
                },
                "runtime": { "lib/net8.0/Microsoft.Graph.dll": {} }
              },
              "Microsoft.Kiota.Abstractions/1.12.0": {
                "runtime": { "lib/net8.0/Microsoft.Kiota.Abstractions.dll": {} }
              },
              "Azure.Identity/1.13.1": {
                "dependencies": { "Microsoft.Identity.Client": "4.66.1" },
                "runtime": { "lib/net8.0/Azure.Identity.dll": {} }
              },
              "Microsoft.Identity.Client/4.66.1": {
                "runtime": { "lib/net8.0/Microsoft.Identity.Client.dll": {} },
                "runtimeTargets": { "runtimes/win/lib/net8.0/msalruntime.dll": { "rid": "win", "assetType": "native" } }
              },
              "Microsoft.Extensions.Options/10.0.0": {
                "runtime": { "lib/net10.0/Microsoft.Extensions.Options.dll": {} }
              }
            }
          },
          "libraries": {
            "MeshWeaver.Mail.MicrosoftGraph/1.0.0": { "type": "project" },
            "MeshWeaver.AI/3.0.0": { "type": "project" },
            "Microsoft.Graph/5.56.0": { "type": "package" },
            "Microsoft.Kiota.Abstractions/1.12.0": { "type": "package" },
            "Azure.Identity/1.13.1": { "type": "package" },
            "Microsoft.Identity.Client/4.66.1": { "type": "package" },
            "Microsoft.Extensions.Options/10.0.0": { "type": "package" }
          }
        }
        """;

    [Fact]
    public void OwnPackages_AndTheirTransitives_AreBundled()
    {
        var result = DepsClosure.Derive(Graph, "MeshWeaver.Mail.MicrosoftGraph");

        Assert.Contains("Microsoft.Graph.dll", result.Files);
        Assert.Contains("Microsoft.Kiota.Abstractions.dll", result.Files);
        Assert.Contains("Azure.Identity.dll", result.Files);
        Assert.Contains("Microsoft.Identity.Client.dll", result.Files);
    }

    [Fact]
    public void TheDiamond_Rides_SoSheddingAPlatformDependencyCannotBreakLandedBundles()
    {
        var result = DepsClosure.Derive(Graph, "MeshWeaver.Mail.MicrosoftGraph");

        // 🚨 Microsoft.Extensions.Options is reachable from BOTH sides. It rides anyway: /app's
        // copy wins in the default load context while the platform carries one, and the module's
        // copy takes over the moment the platform stops — excluding it would couple every landed
        // bundle to the platform's transitive dependency whims (the #1912-revert trap).
        Assert.Contains("Microsoft.Extensions.Options.dll", result.Files);
    }

    [Fact]
    public void PlatformNodes_AreNeverBundled_AndNeverWalked()
    {
        var result = DepsClosure.Derive(Graph, "MeshWeaver.Mail.MicrosoftGraph");

        // The platform reference itself: bundling it would shadow /app at the consumer.
        Assert.DoesNotContain("MeshWeaver.AI.dll", result.Files);
        // The module's OWN entry is the packer's business, not the derivation's.
        Assert.DoesNotContain("MeshWeaver.Mail.MicrosoftGraph.dll", result.Files);
        // The stop is reported, not silent.
        Assert.Contains("MeshWeaver.AI", result.ExcludedPlatformCarried);
    }

    [Fact]
    public void NativeAssets_AreWarnedAbout_NotSilentlyDropped()
    {
        var result = DepsClosure.Derive(Graph, "MeshWeaver.Mail.MicrosoftGraph");

        Assert.Contains(result.Warnings, w => w.Contains("Microsoft.Identity.Client"));
    }

    [Fact]
    public void WrongModuleName_IsARefusal_NeverAnEmptyClosure()
    {
        // An empty closure from the wrong deps.json would pack an entry-only bundle — exactly the
        // outage this derivation exists to foreclose — so it must throw, loudly, naming the module.
        var e = Assert.Throws<InvalidDataException>(
            () => DepsClosure.Derive(Graph, "MeshWeaver.DoesNotExist"));
        Assert.Contains("MeshWeaver.DoesNotExist", e.Message);
    }

    [Fact]
    public void ModuleWithNoOwnPackages_DerivesEmpty_WhichIsValid()
    {
        // The "DLL alone is the closure" case (Observability, Notifications.Channels): every
        // reference is platform-side, the derivation is empty, and that is a correct answer.
        const string platformOnly = """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "MeshWeaver.Observability/1.0.0": {
                    "dependencies": { "MeshWeaver.Messaging.Hub": "3.0.0" },
                    "runtime": { "MeshWeaver.Observability.dll": {} }
                  },
                  "MeshWeaver.Messaging.Hub/3.0.0": {
                    "runtime": { "MeshWeaver.Messaging.Hub.dll": {} }
                  }
                }
              },
              "libraries": {
                "MeshWeaver.Observability/1.0.0": { "type": "project" },
                "MeshWeaver.Messaging.Hub/3.0.0": { "type": "project" }
              }
            }
            """;

        var result = DepsClosure.Derive(platformOnly, "MeshWeaver.Observability");

        Assert.Empty(result.Files);
    }
}

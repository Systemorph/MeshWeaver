#pragma warning disable CS1591

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>DevLogin is a PAIR of settings, and both must reach the container.</b>
///
/// <para><c>Authentication:EnableDevLogin</c> turns the built-in developer login on;
/// <c>Authentication:DevAdminUsers</c> is the comma-separated list of ids that get platform admin
/// when they sign in that way (<c>DevAuthController.IsConfiguredDevAdmin</c> →
/// <c>UserOnboardingService.GrantPlatformAdmin</c>). One without the other is a portal you can log
/// into and then administer nothing on.</para>
///
/// <para>The configmap names every key explicitly and the Deployment's only env path is
/// <c>envFrom</c> on it, so a key the template does not list is <b>silently dropped</b> — helm
/// reports success, the value reaches no container, and the symptom is not "bad configuration" but
/// "DevLogin does not work". <c>EnableDevLogin</c> was templated and <c>DevAdminUsers</c> was not,
/// which is exactly that shape: the login appears, and the admin grant never happens.</para>
///
/// <para>This is the same defect class as <c>Modules__Root</c> (#1924) and
/// <c>Modules__Required__N</c> (#2104). The general guard in
/// <c>PlatformBakeLaneGuard</c> catches it only for keys a tracked values file SETS; these two are
/// set by the developer in their own out-of-band overlay, so nothing was watching them.</para>
/// </summary>
public class DevLoginConfigBindingTest
{
    private static string ConfigMap()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName,
            "deploy", "helm", "templates", "memex-portal", "config.yaml"));
    }

    /// <summary>The template with its comments removed — a key named only in prose is not bound.</summary>
    private static string ExecutableTemplate(string template)
    {
        var withoutHelmComments = Regex.Replace(
            template, @"\{\{-?\s*/\*.*?\*/\s*-?\}\}", "", RegexOptions.Singleline);
        return string.Join("\n", withoutHelmComments
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#')));
    }

    [Theory]
    [InlineData("Authentication__EnableDevLogin")]
    [InlineData("Authentication__DevAdminUsers")]
    public void DevLoginSetting_IsBoundInTheConfigMap(string key)
    {
        var code = ExecutableTemplate(ConfigMap());

        Assert.True(
            Regex.IsMatch(code, $@"^\s{{2}}{Regex.Escape(key)}:\s", RegexOptions.Multiline)
            && code.Contains($".Values.config.memex_portal.{key}", StringComparison.Ordinal),
            $"'{key}' is not rendered by deploy/helm/templates/memex-portal/config.yaml. The "
            + "configmap names every key explicitly and the Deployment's only env path is envFrom "
            + "on it, so an overlay that sets this reaches NO container — silently, with helm "
            + "reporting success. DevLogin's two settings only work as a pair: the login without "
            + "the admin list is a portal nobody can administer.");
    }
}

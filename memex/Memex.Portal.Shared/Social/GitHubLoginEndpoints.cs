using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.GitSync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// "Sign in with GitHub" — a first-class AUTHENTICATION provider (distinct from
/// <see cref="GitHubConnectEndpoints"/>, which only CONNECTS a GitHub identity for repo sync
/// without signing anyone in). It runs the same GitHub OAuth authorization-code flow
/// (<see cref="GitHubOAuthService"/>, reusing the already-configured <c>GitHub:OAuth</c> creds),
/// then issues the portal cookie with the <b>IDENTICAL claim shape</b> Entra login produces via
/// <c>ExternalAuthController</c> — so a GitHub login resolves, through
/// <c>UserContextMiddleware</c>, to the SAME mesh <c>User.Id</c> as an Entra login with the same
/// verified email. That is how the user's decision "GitHub users get the same access as Entra
/// users" is realized: no special-case access logic, just the same identity by email.
///
///   GET /login/github?returnUrl=…            — start (CSRF state cookie, redirect to GitHub authorize)
///   GET /login/github/callback?code=&amp;state=  — exchange code → token, fetch verified email, sign in
///
/// <para><b>GitHub App note.</b> The configured credential is a GitHub App (client id <c>Ov23…</c>),
/// which supports this user-authorization flow on the same endpoints. Resolving the user's email
/// needs the App's "Email addresses: Read" account permission; without it the login FAILS
/// gracefully (never a fabricated identity). The App must whitelist the callback URL
/// <c>{portal}/login/github/callback</c>.</para>
/// </summary>
public static class GitHubLoginEndpoints
{
    public const string StateCookieName = "gh_login_state";
    private const string CallbackPath = "/login/github/callback";

    /// <summary>
    /// The single gate deciding WHO may sign in with GitHub. Today it admits everyone (the user's
    /// explicit choice: "any GitHub account = same access as Entra users"). To restrict later —
    /// e.g. to Systemorph org members or an allowlist — this is the ONE line to change (add the org
    /// check here and thread the token through; nothing else needs to move).
    /// </summary>
    internal static bool IsGitHubUserAllowed(string? login, string email) => true;

    public static IEndpointRouteBuilder MapGitHubLogin(this IEndpointRouteBuilder endpoints)
    {
        // START — NOT RequireAuthorization: this IS the sign-in.
        endpoints.MapGet("/login/github", (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? returnUrl,
            GitHubOAuthService oauth) =>
        {
            if (!oauth.IsConfigured)
                return Results.Redirect("/login?error=" + Uri.EscapeDataString("GitHub sign-in is not configured on this server."));

            var state = GenerateState();
            var ru = SafeLocal(returnUrl);
            http.Response.Cookies.Append(StateCookieName,
                WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{state}|{ru}")),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromMinutes(10),
                });

            return Results.Redirect(oauth.BuildAuthorizeUrl(BuildRedirectUri(http), state));
        });

        // CALLBACK — exchange code, fetch the verified email, sign the user in with the Entra claim shape.
        endpoints.MapGet(CallbackPath, async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? code,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? state,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? error,
            GitHubOAuthService oauth,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("GitHubLogin");

            string cookieState = "", returnUrl = "/";
            if (http.Request.Cookies.TryGetValue(StateCookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
            {
                http.Response.Cookies.Delete(StateCookieName);
                try
                {
                    var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cookie)).Split('|', 2);
                    cookieState = parts[0];
                    returnUrl = parts.Length > 1 ? parts[1] : "/";
                }
                catch { /* malformed cookie — fails the state check below */ }
            }

            IResult Fail(string reason)
            {
                logger.LogWarning("GitHub sign-in failed: {Reason}", reason);
                return Results.Redirect("/login?error=" + Uri.EscapeDataString(reason));
            }

            if (!string.IsNullOrEmpty(error)) return Fail(error!);
            if (string.IsNullOrEmpty(cookieState)) return Fail("missing or bad sign-in state cookie (CSRF)");
            if (!string.Equals(cookieState, state, StringComparison.Ordinal)) return Fail("sign-in state mismatch (CSRF)");
            if (string.IsNullOrEmpty(code)) return Fail("no authorization code returned by GitHub");

            var redirectUri = BuildRedirectUri(http);
            // Reactive end-to-end; bridge to Task ONCE at the HTTP boundary (the sanctioned edge
            // pattern, as in GitHubConnectEndpoints / OAuthConnectController). Exchange the code,
            // then fetch login + primary verified email together.
            return await oauth.ExchangeCode(code!, redirectUri)
                .SelectMany(token => oauth.GetLogin(token.AccessToken)
                    .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
                    .SelectMany(login => oauth.GetPrimaryEmail(token.AccessToken)
                        .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
                        .Select(mail => (login, email: mail))))
                .SelectMany(async t =>
                {
                    var (login, email) = t;
                    if (string.IsNullOrEmpty(email))
                        return Fail("GitHub sign-in needs a verified email — ensure the GitHub App has "
                                    + "'Email addresses: Read' account permission and your GitHub email is verified.");
                    if (!IsGitHubUserAllowed(login, email))
                        return Fail("This GitHub account is not permitted to sign in.");

                    // The SAME cookie identity ExternalAuthController builds for Entra: objectId = email,
                    // preferred_username = email, name + email claims. UserContextMiddleware maps the email
                    // to the mesh User.Id downstream → same user, same access as an Entra login.
                    var name = login ?? email;
                    var claims = new List<Claim>
                    {
                        new(ClaimTypes.NameIdentifier, email),
                        new(ClaimTypes.Name, name),
                        new("name", name),
                        new("preferred_username", email),
                        new(ClaimTypes.Email, email),
                        new("email", email),
                    };
                    var principal = new ClaimsPrincipal(
                        new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
                    await http.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        principal,
                        new AuthenticationProperties
                        {
                            IsPersistent = true,
                            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14),
                        });
                    logger.LogInformation("GitHub sign-in for {Email} (login {Login})", email, login);
                    return (IResult)Results.Redirect(SafeLocal(returnUrl));
                })
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "GitHub sign-in failed during token exchange / profile fetch");
                    return Observable.Return(Fail(ex.Message));
                })
                .FirstAsync()
                .ToTask(http.RequestAborted);
        });

        return endpoints;
    }

    /// <summary>Only ever redirect to a local path — never an attacker-supplied absolute URL (open-redirect guard).</summary>
    internal static string SafeLocal(string? returnUrl) =>
        string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith("/", StringComparison.Ordinal) || returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? "/"
            : returnUrl;

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return WebEncoders.Base64UrlEncode(buf);
    }

    private static string BuildRedirectUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{CallbackPath}";
}

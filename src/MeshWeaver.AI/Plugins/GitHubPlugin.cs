using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Configuration for the GitHub plugin.
/// </summary>
public class GitHubConfiguration
{
    /// <summary>
    /// GitHub Personal Access Token for API authentication.
    /// If not set, all tools return an error prompting configuration.
    /// </summary>
    public string? PersonalAccessToken { get; set; }

    /// <summary>
    /// Default repository owner (organization or user).
    /// </summary>
    public string? DefaultOwner { get; set; }

    /// <summary>
    /// Default repository name.
    /// </summary>
    public string? DefaultRepo { get; set; }

    /// <summary>
    /// Returns <see cref="PersonalAccessToken"/> if set, otherwise falls back
    /// to the <c>GITHUB_TOKEN</c> environment variable.
    /// </summary>
    public string? ResolvedToken =>
        !string.IsNullOrWhiteSpace(PersonalAccessToken)
            ? PersonalAccessToken
            : Environment.GetEnvironmentVariable("GITHUB_TOKEN");
}

/// <summary>
/// Plugin providing GitHub issue management tools for AI agents.
/// Register via <see cref="GitHubPluginExtensions.AddGitHubPlugin"/>.
/// </summary>
public class GitHubPlugin : IAgentPlugin
{
    private readonly HttpClient httpClient;
    private readonly GitHubConfiguration config;
    private readonly ILogger<GitHubPlugin> logger;

    public string Name => "GitHub";

    public GitHubPlugin(
        HttpClient httpClient,
        IOptions<GitHubConfiguration> options,
        ILogger<GitHubPlugin> logger)
    {
        this.httpClient = httpClient;
        this.config = options.Value;
        this.logger = logger;
    }

    [Description("Creates a new GitHub issue in the specified repository. Returns the issue URL and number.")]
    public async Task<string> CreateIssue(
        [Description("Repository owner (org or user). Uses default if omitted.")] string? owner,
        [Description("Repository name. Uses default if omitted.")] string? repo,
        [Description("Issue title")] string title,
        [Description("Issue body in Markdown format")] string body,
        [Description("Comma-separated labels to apply (e.g., 'feature-spec,priority:high')")] string? labels = null,
        [Description("Milestone name to assign")] string? milestone = null)
    {
        if (!EnsureConfigured(out var error)) return error;

        owner ??= config.DefaultOwner;
        repo ??= config.DefaultRepo;
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            return "Error: owner and repo are required. Set defaults in GitHubConfiguration or pass explicitly.";

        logger.LogInformation("CreateIssue called for {Owner}/{Repo}: {Title}", owner, repo, title);

        try
        {
            var payload = new Dictionary<string, object> { ["title"] = title, ["body"] = body };
            if (!string.IsNullOrEmpty(labels))
                payload["labels"] = labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var request = CreateRequest(HttpMethod.Post, $"repos/{owner}/{repo}/issues", payload);
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return JsonSerializer.Serialize(new
            {
                url = root.GetProperty("html_url").GetString(),
                number = root.GetProperty("number").GetInt32(),
                state = root.GetProperty("state").GetString()
            });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "CreateIssue failed for {Owner}/{Repo}", owner, repo);
            return FormatHttpError(ex, $"creating issue in {owner}/{repo}");
        }
    }

    [Description("Gets details of a GitHub issue by number. Returns title, state, body, labels, and assignees.")]
    public async Task<string> GetIssue(
        [Description("Repository owner")] string? owner,
        [Description("Repository name")] string? repo,
        [Description("Issue number")] int issueNumber)
    {
        if (!EnsureConfigured(out var error)) return error;

        owner ??= config.DefaultOwner;
        repo ??= config.DefaultRepo;
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            return "Error: owner and repo are required.";

        logger.LogInformation("GetIssue called for {Owner}/{Repo}#{Number}", owner, repo, issueNumber);

        try
        {
            var request = CreateRequest(HttpMethod.Get, $"repos/{owner}/{repo}/issues/{issueNumber}");
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return JsonSerializer.Serialize(new
            {
                title = root.GetProperty("title").GetString(),
                state = root.GetProperty("state").GetString(),
                body = root.GetProperty("body").GetString(),
                url = root.GetProperty("html_url").GetString(),
                number = root.GetProperty("number").GetInt32(),
                labels = root.GetProperty("labels").EnumerateArray()
                    .Select(l => l.GetProperty("name").GetString()).ToArray(),
                created_at = root.GetProperty("created_at").GetString(),
                updated_at = root.GetProperty("updated_at").GetString()
            });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "GetIssue failed for {Owner}/{Repo}#{Number}", owner, repo, issueNumber);
            return FormatHttpError(ex, $"getting issue #{issueNumber} in {owner}/{repo}");
        }
    }

    [Description("Lists GitHub issues in a repository, filtered by state and labels.")]
    public async Task<string> ListIssues(
        [Description("Repository owner")] string? owner,
        [Description("Repository name")] string? repo,
        [Description("Filter by state: open, closed, or all (default: open)")] string state = "open",
        [Description("Comma-separated labels to filter by")] string? labels = null,
        [Description("Maximum number of issues to return (default: 10)")] int perPage = 10)
    {
        if (!EnsureConfigured(out var error)) return error;

        owner ??= config.DefaultOwner;
        repo ??= config.DefaultRepo;
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            return "Error: owner and repo are required.";

        logger.LogInformation("ListIssues called for {Owner}/{Repo} state={State}", owner, repo, state);

        try
        {
            var query = $"state={Uri.EscapeDataString(state)}&per_page={Math.Clamp(perPage, 1, 100)}";
            if (!string.IsNullOrEmpty(labels))
                query += $"&labels={Uri.EscapeDataString(labels)}";

            var request = CreateRequest(HttpMethod.Get, $"repos/{owner}/{repo}/issues?{query}");
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var issues = doc.RootElement.EnumerateArray().Select(issue => new
            {
                number = issue.GetProperty("number").GetInt32(),
                title = issue.GetProperty("title").GetString(),
                state = issue.GetProperty("state").GetString(),
                url = issue.GetProperty("html_url").GetString(),
                labels = issue.GetProperty("labels").EnumerateArray()
                    .Select(l => l.GetProperty("name").GetString()).ToArray()
            }).ToArray();

            return JsonSerializer.Serialize(issues);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "ListIssues failed for {Owner}/{Repo}", owner, repo);
            return FormatHttpError(ex, $"listing issues in {owner}/{repo}");
        }
    }

    [Description("Updates an existing GitHub issue (state, title, body, labels).")]
    public async Task<string> UpdateIssue(
        [Description("Repository owner")] string? owner,
        [Description("Repository name")] string? repo,
        [Description("Issue number to update")] int issueNumber,
        [Description("New state: open or closed")] string? state = null,
        [Description("New title")] string? title = null,
        [Description("New body")] string? body = null,
        [Description("Comma-separated labels to set")] string? labels = null)
    {
        if (!EnsureConfigured(out var error)) return error;

        owner ??= config.DefaultOwner;
        repo ??= config.DefaultRepo;
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            return "Error: owner and repo are required.";

        logger.LogInformation("UpdateIssue called for {Owner}/{Repo}#{Number}", owner, repo, issueNumber);

        try
        {
            var payload = new Dictionary<string, object>();
            if (state != null) payload["state"] = state;
            if (title != null) payload["title"] = title;
            if (body != null) payload["body"] = body;
            if (labels != null)
                payload["labels"] = labels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var request = CreateRequest(HttpMethod.Patch, $"repos/{owner}/{repo}/issues/{issueNumber}", payload);
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return JsonSerializer.Serialize(new
            {
                url = root.GetProperty("html_url").GetString(),
                number = root.GetProperty("number").GetInt32(),
                state = root.GetProperty("state").GetString()
            });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "UpdateIssue failed for {Owner}/{Repo}#{Number}", owner, repo, issueNumber);
            return FormatHttpError(ex, $"updating issue #{issueNumber} in {owner}/{repo}");
        }
    }

    public IEnumerable<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(CreateIssue),
            AIFunctionFactory.Create(GetIssue),
            AIFunctionFactory.Create(ListIssues),
            AIFunctionFactory.Create(UpdateIssue)
        ];
    }

    private static string FormatHttpError(HttpRequestException ex, string operation)
    {
        return ex.StatusCode switch
        {
            System.Net.HttpStatusCode.Forbidden =>
                $"Error: GitHub returned 403 Forbidden for {operation}. " +
                "Check that your Personal Access Token has the 'Issues: Read and write' permission " +
                "and has access to the target repository.",
            System.Net.HttpStatusCode.NotFound =>
                $"Error: GitHub returned 404 for {operation}. Check that the owner/repo exists and your token has access.",
            System.Net.HttpStatusCode.Unauthorized =>
                $"Error: GitHub returned 401 Unauthorized for {operation}. Your Personal Access Token may be expired or invalid.",
            System.Net.HttpStatusCode.UnprocessableEntity =>
                $"Error: GitHub returned 422 for {operation}. The request data may be invalid (e.g., unknown label or milestone).",
            _ => $"Error in {operation}: {ex.Message}"
        };
    }

    private bool EnsureConfigured(out string error)
    {
        if (string.IsNullOrWhiteSpace(config.ResolvedToken))
        {
            error = "GitHub plugin is not configured. Set PersonalAccessToken in GitHubConfiguration or the GITHUB_TOKEN environment variable.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? payload = null)
        => CreateGitHubRequest(method, path, config.ResolvedToken!, payload);

    /// <summary>
    /// Creates an HttpRequestMessage with standard GitHub API headers.
    /// </summary>
    internal static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string path, string pat, object? payload = null)
    {
        var request = new HttpRequestMessage(method, $"https://api.github.com/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        request.Headers.Add("User-Agent", "MeshWeaver/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        if (payload != null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8, "application/json");

        return request;
    }
}

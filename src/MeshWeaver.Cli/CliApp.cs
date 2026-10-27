using System.Collections.Immutable;
using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;

namespace MeshWeaver.Cli;

/// <summary>Where the CLI writes, where it keeps its cache, and what time it is — the seams a test replaces.</summary>
public sealed record CliHost(TextWriter Out, TextWriter Err, string ConfigDirectory, Func<DateTimeOffset> Now)
{
    /// <summary>The real console, <c>~/.memex</c>, the wall clock.</summary>
    public static CliHost FromConsole() =>
        new(Console.Out, Console.Error, Path.GetDirectoryName(MemexConfig.ConfigPath)!, () => DateTimeOffset.UtcNow);
}

/// <summary>
/// The <c>memex</c> command tree. The mesh commands are NOT compiled in: they are generated from
/// the connected instance's MCP tool catalog (<c>tools/list</c>, cached per instance URL) and run
/// with <c>tools/call</c>, so a tool the server adds or changes needs no CLI release
/// (<see cref="DynamicCommands"/> states the mapping rule). Compiled in, because they are not mesh
/// tools: <c>login</c>, <c>base-url</c>, <c>build *</c>, <c>tests</c> (streams progress) and
/// <c>upload</c> (multipart). A server tool with a built-in's name is shadowed by the built-in.
/// </summary>
public static class CliApp
{
    /// <summary>The commands compiled into the CLI. They win over a server tool of the same name.</summary>
    public static readonly ImmutableArray<string> BuiltIns = ["login", "base-url", "build", "tests", "upload"];

    private static readonly ImmutableHashSet<string> GlobalValueOptions = ["--base-url", "--token"];
    private static readonly ImmutableHashSet<string> HelpTokens = ["-h", "--help", "-?", "/?", "/h"];

    /// <summary>What the command line names before any parsing: the global connection options and the command.</summary>
    public sealed record PreScan(string? BaseUrl, string? Token, bool Refresh, string? Command, bool Help, bool Version);

    /// <summary>Reads the global options and the command name off raw args, wherever they stand.</summary>
    public static PreScan Scan(IReadOnlyList<string> args)
    {
        string? baseUrl = null, token = null, command = null;
        bool refresh = false, help = false, version = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--") break;
            var eq = a.IndexOf('=');
            var head = a.StartsWith("--", StringComparison.Ordinal) && eq > 0 ? a[..eq] : a;
            if (GlobalValueOptions.Contains(head))
            {
                var value = eq > 0 && head != a ? a[(eq + 1)..] : i + 1 < args.Count ? args[++i] : null;
                if (head == "--base-url") baseUrl = value; else token = value;
                continue;
            }
            if (a == "--refresh") { refresh = true; continue; }
            if (HelpTokens.Contains(a)) { help = true; continue; }
            if (a == "--version") { version = true; continue; }
            if (command is null && !a.StartsWith('-')) command = a;
        }
        return new PreScan(baseUrl, token, refresh, command, help, version);
    }

    /// <summary>Runs the CLI: loads the catalog when the command needs it, builds the tree, invokes.</summary>
    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken ct = default)
    {
        var scan = Scan(args);
        var isBuiltIn = scan.Command is not null && BuiltIns.Contains(scan.Command);
        var needCatalog = !isBuiltIn && !(scan.Command is null && scan.Version && !scan.Help);

        MemexConfig? config = null;
        string? noClientReason = null;
        try { config = MemexConfig.Resolve(scan.BaseUrl, scan.Token); }
        catch (InvalidOperationException ex) { noClientReason = ex.Message; }

        using var client = config is null ? null : new McpClient(config);
        ToolCatalog? catalog = null;
        string? unavailable = null;
        if (needCatalog)
        {
            var baseUrl = config?.BaseUrl ?? MemexConfig.ResolveBaseUrl(scan.BaseUrl);
            var load = await ToolCatalogCache.Load(
                host.ConfigDirectory, baseUrl, scan.Refresh, host.Now(),
                client is null ? null : client.ListTools, noClientReason, ct);
            if (load.Warning is not null) await host.Err.WriteLineAsync(load.Warning);
            catalog = load.Catalog;
            if (catalog is null)
            {
                // A mesh command was named and there is nothing to generate it from: say WHY,
                // instead of letting the parser report an "unrecognized command".
                if (scan.Command is not null)
                {
                    await host.Err.WriteLineAsync(load.Failure);
                    return 2;
                }
                unavailable = load.Failure;
            }
        }

        var root = BuildRoot(host, catalog, unavailable, client);
        var parse = root.Parse(args);
        return await parse.InvokeAsync(new InvocationConfiguration { Output = host.Out, Error = host.Err }, ct);
    }

    /// <summary>
    /// The command tree: the built-ins plus one generated command per catalog tool. Public so a test
    /// can inspect what a catalog generates without invoking anything.
    /// </summary>
    public static RootCommand BuildRoot(CliHost host, ToolCatalog? catalog, string? unavailable = null, McpClient? client = null)
    {
        var baseUrlOpt = new Option<string?>("--base-url") { Description = "Portal base URL (default: $MEMEX_BASE_URL, ~/.memex/config.json, or https://memex.meshweaver.cloud)", Recursive = true };
        var tokenOpt = new Option<string?>("--token") { Description = "API token mw_… (default: $MEMEX_TOKEN or ~/.memex/config.json)", Recursive = true };
        var refreshOpt = new Option<bool>("--refresh") { Description = $"Re-read the instance's tool catalog instead of using the cached one (cached for {ToolCatalogCache.Lifetime.TotalMinutes:0} minutes).", Recursive = true };

        var shadowed = catalog?.Tools
            .Where(t => BuiltIns.Contains(DynamicCommands.Kebab(t.Name)))
            .Select(t => t.Name)
            .ToArray() ?? [];

        var description = new StringBuilder(
            "memex — CLI for MeshWeaver / Memex. The mesh commands are loaded from the connected instance's "
            + "MCP tool catalog (tools/list, cached per instance) and run with tools/call, so new server tools "
            + "need no CLI update. Built-in commands: " + string.Join(", ", BuiltIns)
            + " — a built-in wins over a server tool of the same name.");
        if (shadowed.Length > 0)
            description.Append(" Server tools shadowed by a built-in: ").Append(string.Join(", ", shadowed)).Append('.');
        if (unavailable is not null)
            description.Append(" Mesh commands are unavailable: ").Append(unavailable);
        else if (catalog is { FromCache: true })
            description.Append($" Tool catalog cached at {catalog.FetchedAt:u}.");

        var root = new RootCommand(description.ToString());
        root.Options.Add(baseUrlOpt);
        root.Options.Add(tokenOpt);
        root.Options.Add(refreshOpt);

        AddBuiltIns(root, host, baseUrlOpt, tokenOpt);

        if (catalog is not null)
        {
            var taken = new HashSet<string>(BuiltIns, StringComparer.Ordinal);
            var generated = new List<(McpTool Tool, Command Command)>();
            foreach (var tool in catalog.Tools)
            {
                var name = DynamicCommands.Kebab(tool.Name);
                if (name.Length == 0 || !taken.Add(name)) continue;
                var cmd = DynamicCommands.Build(tool, name, host.Err,
                    (toolName, arguments, ct) => CallTool(host, client, toolName, arguments, ct));
                generated.Add((tool, cmd));
                root.Subcommands.Add(cmd);
            }

            foreach (var (alias, toolName) in DynamicCommands.LegacyShapes.CommandAliases)
                if (!taken.Contains(alias) && generated.FirstOrDefault(g => g.Tool.Name == toolName).Command is { } target)
                {
                    target.Aliases.Add(alias);
                    taken.Add(alias);
                }
        }

        return root;
    }

    /// <summary>
    /// Runs one tool and prints its result: every text content verbatim on stdout (so JSON pipes into
    /// <c>jq</c>); a tool error — <c>isError</c>, or the mesh's <c>Error:</c> sentence — on stderr
    /// with exit 1. A JSON-RPC refusal is exit 1, an HTTP failure exit 2, anything else exit 3.
    /// </summary>
    public static async Task<int> CallTool(CliHost host, McpClient? client, string toolName, JsonObject arguments, CancellationToken ct)
    {
        if (client is null)
        {
            await host.Err.WriteLineAsync("error: No API token configured. Set it with --token, $MEMEX_TOKEN, or `memex login`.");
            return 2;
        }
        try
        {
            var result = await client.CallTool(toolName, arguments, ct);
            var text = RenderContent(result);
            var isError = result["isError"] is JsonValue v && v.TryGetValue<bool>(out var e) && e;
            if (isError || text.StartsWith("Error:", StringComparison.Ordinal) || text.StartsWith("\"Error:", StringComparison.Ordinal))
            {
                await host.Err.WriteLineAsync(text);
                return 1;
            }
            await host.Out.WriteLineAsync(text);
            return 0;
        }
        catch (McpRpcException ex)
        {
            await host.Err.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
        catch (MemexCliException ex)
        {
            await host.Err.WriteLineAsync(ex.Message);
            return 2;
        }
        catch (HttpRequestException ex)
        {
            await host.Err.WriteLineAsync($"error: {ex.Message}");
            return 3;
        }
    }

    /// <summary>The printable form of a <c>CallToolResult</c>: text items verbatim, any other item as JSON.</summary>
    public static string RenderContent(JsonObject result)
    {
        if (result["content"] is JsonArray content && content.Count > 0)
            return string.Join("\n", content.Select(item =>
                item is JsonObject o && o["type"]?.GetValue<string>() == "text" && o["text"] is JsonValue t && t.TryGetValue<string>(out var s)
                    ? s
                    : item?.ToJsonString() ?? string.Empty));
        return result["structuredContent"]?.ToJsonString() ?? string.Empty;
    }

    // --- built-ins ----------------------------------------------------------------------------

    private static void AddBuiltIns(RootCommand root, CliHost host, Option<string?> baseUrlOpt, Option<string?> tokenOpt)
    {
        async Task<int> Rest(ParseResult result, CancellationToken ct, Func<MemexClient, CancellationToken, Task<string>> work)
        {
            try
            {
                var cfg = MemexConfig.Resolve(result.GetValue(baseUrlOpt), result.GetValue(tokenOpt));
                using var client = new MemexClient(cfg);
                var body = await work(client, ct);
                if (body.StartsWith("Error:", StringComparison.Ordinal) || body.StartsWith("\"Error:", StringComparison.Ordinal))
                {
                    await host.Err.WriteLineAsync(body);
                    return 1;
                }
                await host.Out.WriteLineAsync(body);
                return 0;
            }
            catch (MemexCliException ex)
            {
                await host.Err.WriteLineAsync(ex.Message);
                return 2;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await host.Err.WriteLineAsync($"error: {ex.Message}");
                return 3;
            }
        }

        // --- tests: streams the run's activity until the verdict (not a single tool call) ---------
        {
            var pathArg = new Argument<string>("path") { Description = "Node whose Tests area to run (e.g. @Admin/Maintenance/x)." };
            var timeoutOpt = new Option<int>("--timeout") { Description = "Seconds to wait for the verdict.", DefaultValueFactory = _ => 600 };
            var intervalOpt = new Option<int>("--interval") { Description = "Seconds between reads of the area.", DefaultValueFactory = _ => 2 };
            var cmd = new Command("tests", "Run a node's Tests area and stream its progress until the verdict (exit 0 = all passed, 1 = a failure, 4 = no verdict in time).")
                { pathArg, timeoutOpt, intervalOpt };
            cmd.SetAction(async (result, ct) =>
            {
                try
                {
                    var cfg = MemexConfig.Resolve(result.GetValue(baseUrlOpt), result.GetValue(tokenOpt));
                    using var client = new MemexClient(cfg);
                    return await TestsCommand.Run(client, result.GetValue(pathArg)!,
                        TimeSpan.FromSeconds(result.GetValue(timeoutOpt)), TimeSpan.FromSeconds(Math.Max(1, result.GetValue(intervalOpt))),
                        host.Out, ct);
                }
                catch (MemexCliException ex)
                {
                    await host.Err.WriteLineAsync(ex.Message);
                    return 2;
                }
            });
            root.Subcommands.Add(cmd);
        }

        // --- upload: a multipart file upload (the MCP tool takes base64 in a JSON body) ----------
        {
            var pathArg = new Argument<string>("path") { Description = "Target mesh path {nodePath}/{collection}/{filePath}." };
            var fileArg = new Argument<string>("local-file") { Description = "Local file to upload." };
            var cmd = new Command("upload", "Upload a file into a node's content collection (multipart; built-in).") { pathArg, fileArg };
            cmd.SetAction((result, ct) => Rest(result, ct,
                (c, t) => c.Upload(result.GetValue(pathArg)!, result.GetValue(fileArg)!, t)));
            root.Subcommands.Add(cmd);
        }

        // --- base-url ------------------------------------------------------------------------------
        {
            var cmd = new Command("base-url", "Print the portal base URL.");
            cmd.SetAction((result, ct) => Rest(result, ct, (c, t) => c.BaseUrl(t)));
            root.Subcommands.Add(cmd);
        }

        // --- login ---------------------------------------------------------------------------------
        {
            // Token is OPTIONAL on the command line — omit it and you're prompted to paste it
            // interactively (masked), so the secret never lands in shell history or process args.
            // The base URL to persist is the global --base-url.
            var tokenArg = new Argument<string?>("token")
            {
                Description = "API token (mw_…). Omit to be prompted interactively.",
                Arity = ArgumentArity.ZeroOrOne,
            };
            var cmd = new Command("login", $"Log on: store an API token (and --base-url) in {MemexConfig.ConfigPath}.") { tokenArg };
            cmd.SetAction(result =>
            {
                try
                {
                    var token = result.GetValue(tokenArg);
                    if (string.IsNullOrWhiteSpace(token))
                        token = PromptForToken(host.Err);
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        host.Err.WriteLine("error: no token provided.");
                        return 3;
                    }
                    if (!token.StartsWith("mw_", StringComparison.Ordinal))
                        host.Err.WriteLine("warning: token does not start with 'mw_' — saving it anyway.");
                    MemexConfig.SaveFile(result.GetValue(baseUrlOpt), token);
                    host.Out.WriteLine($"Logged on — token saved to {MemexConfig.ConfigPath}");
                    return 0;
                }
                catch (Exception ex)
                {
                    host.Err.WriteLine($"error: {ex.Message}");
                    return 3;
                }
            });
            root.Subcommands.Add(cmd);
        }

        AddBuild(root, host);
    }

    // Reads a token from the console without echoing it. Falls back to a plain read when
    // input is redirected (piped / non-interactive), where key-by-key capture isn't available.
    private static string? PromptForToken(TextWriter err)
    {
        err.Write("Paste your API token (mw_…): ");
        if (Console.IsInputRedirected)
            return Console.ReadLine()?.Trim();

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
        err.WriteLine();
        return sb.ToString().Trim();
    }

    // --- build ---------------------------------------------------------------------------------
    // 🚨 `build` does NOT talk to the portal at all: it pulls an image and runs the platform's
    // builder inside it. See Doc/Architecture/InMeshBuildAndTest.
    private static void AddBuild(RootCommand root, CliHost host)
    {
        var build = new Command("build", "Build and test artefacts inside a MeshWeaver image.");

        var pathArg = new Argument<string>("path")
        { Description = "Path to the plugin repo (the directory mounted as /repo)." };
        var imageOpt = new Option<string>("--image")
        { Description = "TESTER image, e.g. meshweaver.azurecr.io/mw-plugin-test:<tag>. Pulled and pinned by digest. It EXECUTES the build; it does not supply the reference set.", Required = true };
        // 🚨 Not `Required = true` on the option: the refusal in BuildPluginCommand explains WHY there is
        // no fallback to the tester's /app, and "Option '--platform-image' is required." would not.
        var platformImageOpt = new Option<string?>("--platform-image")
        { Description = "PORTAL image, e.g. meshweaver.azurecr.io/memex-portal-ai@sha256:… — REQUIRED. It supplies the reference set, the framework identity and the runtime; pin it from the SAME CD wave as --image." };
        var bakeOpt = new Option<string?>("--bake-output")
        { Description = "Directory for the bake (bundles + framework-mvid.txt). Default: a temp dir." };
        var extOpt = new Option<string?>("--external-modules")
        { Description = "Directory of external module DLLs to mount at /ext." };
        var shaOpt = new Option<string?>("--source-sha")
        { Description = "Commit stamped into the bake, so a bundle records the source it came from." };
        var allowOpt = new Option<string?>("--allow")
        { Description = "Allow-file relative to the plugin path (default: plugin-gate.allow, used only if present)." };
        var regUrlOpt = new Option<string?>("--registry-url")
        { Description = "Plugin registry base URL for dependency install (e.g. https://memex.meshweaver.cloud)." };
        var regModulesOpt = new Option<string?>("--registry-modules")
        { Description = "Space-separated packages to INSTALL as built artifacts before building (e.g. \"AI Essentials\") — PluginBuildContract step 2." };
        var regKeyOpt = new Option<string?>("--registry-key")
        { Description = "mwi_… instance key for the registry (default: $MW_REGISTRY_KEY; prefer the env var — argv is visible in process listings)." };
        var upstreamOpt = new Option<string?>("--upstream-seed")
        { Description = "Space-separated upstream SOURCES whose sealed publication seeds the gate's mesh (e.g. \"plugins\") — the packages a requires chain reaches, not merely their DLLs." };

        var plugin = new Command("plugin", "Build a plugin: install its dependencies, bake its packages, then test the BAKED bytes.")
        { pathArg, imageOpt, platformImageOpt, bakeOpt, extOpt, shaOpt, allowOpt, regUrlOpt, regModulesOpt, regKeyOpt, upstreamOpt };

        plugin.SetAction((result, ct) => new BuildPluginCommand(host.Out, host.Err).RunAsync(
            new BuildPluginOptions(
                result.GetValue(pathArg)!,
                result.GetValue(imageOpt)!,
                result.GetValue(bakeOpt),
                result.GetValue(extOpt),
                result.GetValue(shaOpt),
                result.GetValue(allowOpt))
            {
                RegistryUrl = result.GetValue(regUrlOpt),
                RegistryModules = result.GetValue(regModulesOpt),
                RegistryKey = result.GetValue(regKeyOpt) ?? Environment.GetEnvironmentVariable("MW_REGISTRY_KEY"),
                UpstreamSeed = result.GetValue(upstreamOpt),
                PlatformImage = result.GetValue(platformImageOpt),
            },
            ct));

        build.Subcommands.Add(plugin);

        // --- build project ------------------------------------------------------
        // 🚨 NO dotnet SDK and NO NuGet on the path: the .csproj is evaluated without MSBuild and every
        // reference is resolved from the image's /app. See Doc/Architecture/InMeshBuildAndTest.
        var projPathArg = new Argument<string>("path")
        { Description = "Path to the .csproj (or a directory holding exactly one)." };
        var projImageOpt = new Option<string?>("--image")
        { Description = "Image to build against; pulled and pinned by digest. Omit only when this command is itself running inside a MeshWeaver image." };
        var projOutOpt = new Option<string?>("--output", "-o")
        { Description = "Where the emitted assemblies land. Default: a temp dir." };
        var projRootOpt = new Option<string?>("--root")
        { Description = "Directory mounted as /repo. Default: the nearest Directory.Build.props ancestor." };
        var projRefsOpt = new Option<string[]>("--extra-refs")
        { Description = "Directory of libraries ADDITIONAL to the platform (a PackageReference the image does not supply). Repeatable.", Arity = ArgumentArity.ZeroOrMore };
        var projAcceptOpt = new Option<string[]>("--accept")
        { Description = "Acknowledge one construct the evaluator cannot reproduce (e.g. target:<Name>, embedded-resource, conditions). Repeatable.", Arity = ArgumentArity.ZeroOrMore };
        var projNoWarnOpt = new Option<bool>("--no-warn")
        { Description = "Fail the build on any warning (default true). Pass --no-warn=false or --allow-warnings to opt out.", DefaultValueFactory = _ => true };
        var projAllowWarnOpt = new Option<bool>("--allow-warnings")
        { Description = "Alias for --no-warn=false." };
        var projNoPullOpt = new Option<bool>("--no-pull")
        { Description = "Use the image the docker daemon already has instead of pulling it (for a locally built image)." };

        var project = new Command("project",
            "Compile a .csproj against a MeshWeaver image's own assemblies — no dotnet SDK, no NuGet restore.")
        { projPathArg, projImageOpt, projOutOpt, projRootOpt, projRefsOpt, projAcceptOpt, projNoWarnOpt, projAllowWarnOpt, projNoPullOpt };

        project.SetAction((result, ct) => new BuildProjectCommand(host.Out, host.Err).RunAsync(
            new BuildProjectOptions
            {
                ProjectPath = result.GetValue(projPathArg)!,
                Image = result.GetValue(projImageOpt),
                Output = result.GetValue(projOutOpt),
                SourceRoot = result.GetValue(projRootOpt),
                ExtraReferenceDirectories = result.GetValue(projRefsOpt) ?? [],
                Accept = result.GetValue(projAcceptOpt) ?? [],
                AllowWarnings = result.GetValue(projAllowWarnOpt) || !result.GetValue(projNoWarnOpt),
                NoPull = result.GetValue(projNoPullOpt),
            },
            ct));

        build.Subcommands.Add(project);
        root.Subcommands.Add(build);
    }
}

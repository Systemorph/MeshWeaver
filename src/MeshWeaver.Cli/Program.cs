using System.CommandLine;
using System.Text;
using MeshWeaver.Cli;

Console.OutputEncoding = Encoding.UTF8;

// --- Global options (apply to every command) -------------------------------
var baseUrlOpt = new Option<string?>("--base-url") { Description = "Portal base URL (default: $MEMEX_BASE_URL, ~/.memex/config.json, or https://memex.meshweaver.cloud)" };
var tokenOpt = new Option<string?>("--token") { Description = "API token mw_… (default: $MEMEX_TOKEN or ~/.memex/config.json)" };

var root = new RootCommand("memex — CLI for MeshWeaver / Memex over the portal REST API.");
root.Options.Add(baseUrlOpt);
root.Options.Add(tokenOpt);

// --- Common helpers --------------------------------------------------------
async Task<int> Run(
    System.CommandLine.ParseResult result,
    CancellationToken ct,
    Func<MemexClient, CancellationToken, Task<string>> work)
{
    try
    {
        var cfg = MemexConfig.Resolve(result.GetValue(baseUrlOpt), result.GetValue(tokenOpt));
        using var client = new MemexClient(cfg);
        var body = await work(client, ct);
        // Server returns either a JSON document or an "Error: …" string. Print verbatim
        // so consumers can pipe to jq; route the error sentinel to stderr + non-zero exit.
        if (body.StartsWith("Error:", StringComparison.Ordinal) || body.StartsWith("\"Error:", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync(body);
            return 1;
        }
        Console.WriteLine(body);
        return 0;
    }
    catch (MemexCliException ex)
    {
        await Console.Error.WriteLineAsync(ex.Message);
        return 2;
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"error: {ex.Message}");
        return 3;
    }
}

// --- get -------------------------------------------------------------------
{
    var pathArg = new Argument<string>("path") { Description = "Mesh path (e.g. @Doc/Architecture)" };
    var cmd = new Command("get", "Read a node or resource by path.") { pathArg };
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) => c.Get(result.GetValue(pathArg)!, t)));
    root.Subcommands.Add(cmd);
}

// --- search ----------------------------------------------------------------
{
    var queryArg = new Argument<string>("query") { Description = "Query string (GitHub-style, e.g. 'nodeType:Agent')" };
    var basePathOpt = new Option<string?>("--base-path") { Description = "Restrict search to a subtree" };
    var cmd = new Command("search", "Search the mesh.") { queryArg, basePathOpt };
    cmd.SetAction((result, ct) => Run(result, ct,
        (c, t) => c.Search(result.GetValue(queryArg)!, result.GetValue(basePathOpt), t)));
    root.Subcommands.Add(cmd);
}

// --- create / update / patch ----------------------------------------------
{
    var fileOpt = new Option<string>("--file", "-f") { Description = "Path to JSON file containing the node body.", Required = true };
    var cmd = new Command("create", "Create a node from a JSON file (single MeshNode object).") { fileOpt };
    cmd.SetAction((result, ct) => Run(result, ct,
        (c, t) => c.Create(File.ReadAllText(result.GetValue(fileOpt)!), t)));
    root.Subcommands.Add(cmd);
}
{
    var fileOpt = new Option<string>("--file", "-f") { Description = "Path to JSON file containing an array of MeshNode objects.", Required = true };
    var cmd = new Command("update", "Update nodes from a JSON array file (full-replace).") { fileOpt };
    cmd.SetAction((result, ct) => Run(result, ct,
        (c, t) => c.Update(File.ReadAllText(result.GetValue(fileOpt)!), t)));
    root.Subcommands.Add(cmd);
}
{
    var pathArg = new Argument<string>("path") { Description = "Mesh path of the node to patch." };
    var fieldsOpt = new Option<string?>("--fields") { Description = "Inline JSON object of fields to set." };
    var fileOpt = new Option<string?>("--file", "-f") { Description = "Path to JSON file (alternative to --fields)." };
    var cmd = new Command("patch", "Partial update of a node's top-level fields.") { pathArg, fieldsOpt, fileOpt };
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) =>
    {
        var inline = result.GetValue(fieldsOpt);
        var fromFile = result.GetValue(fileOpt);
        var fields = !string.IsNullOrEmpty(inline) ? inline
            : !string.IsNullOrEmpty(fromFile) ? File.ReadAllText(fromFile)
            : throw new InvalidOperationException("Either --fields or --file is required.");
        return c.Patch(result.GetValue(pathArg)!, fields, t);
    }));
    root.Subcommands.Add(cmd);
}

// --- delete ----------------------------------------------------------------
{
    var pathsArg = new Argument<string[]>("paths") { Description = "One or more mesh paths to delete (recursive)." };
    var cmd = new Command("delete", "Delete one or more nodes (recursive).") { pathsArg };
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) =>
    {
        var paths = result.GetValue(pathsArg) ?? Array.Empty<string>();
        var json = System.Text.Json.JsonSerializer.Serialize(paths);
        return c.Delete(json, t);
    }));
    root.Subcommands.Add(cmd);
}

// --- move / copy -----------------------------------------------------------
{
    var srcArg = new Argument<string>("source") { Description = "Current path." };
    var dstArg = new Argument<string>("target") { Description = "New path." };
    var cmd = new Command("move", "Move a node and its descendants.") { srcArg, dstArg };
    cmd.SetAction((result, ct) => Run(result, ct,
        (c, t) => c.Move(result.GetValue(srcArg)!, result.GetValue(dstArg)!, t)));
    root.Subcommands.Add(cmd);
}
{
    var srcArg = new Argument<string>("source") { Description = "Current path." };
    var nsArg = new Argument<string>("target-namespace") { Description = "Target namespace." };
    var forceOpt = new Option<bool>("--force") { Description = "Overwrite existing nodes at the target." };
    var cmd = new Command("copy", "Copy a node and its descendants to another namespace.") { srcArg, nsArg, forceOpt };
    cmd.SetAction((result, ct) => Run(result, ct,
        (c, t) => c.Copy(result.GetValue(srcArg)!, result.GetValue(nsArg)!, result.GetValue(forceOpt), t)));
    root.Subcommands.Add(cmd);
}

// --- recycle / compile / diagnostics / execute-script ---------------------
{
    var pathArg = new Argument<string>("path") { Description = "Path of the node (or NodeType) to recycle." };
    var cmd = new Command("recycle", "Force a fresh hub initialisation by disposing the current one.") { pathArg };
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) => c.Recycle(result.GetValue(pathArg)!, t)));
    root.Subcommands.Add(cmd);
}
{
    var pathArg = new Argument<string>("path") { Description = "NodeType path to compile." };
    var cmd = new Command("compile", "Compile a NodeType and wait for the result.") { pathArg };
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) => c.Compile(result.GetValue(pathArg)!, t)));
    root.Subcommands.Add(cmd);
}
{
    var pathArg = new Argument<string>("path") { Description = "NodeType (or instance) path." };
    var cmd = new Command("diagnostics", "Show compile diagnostics for a NodeType.") { pathArg };
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) => c.Diagnostics(result.GetValue(pathArg)!, t)));
    root.Subcommands.Add(cmd);
}
{
    var pathArg = new Argument<string>("path") { Description = "Executable Code node path." };
    var timeoutOpt = new Option<int>("--timeout") { Description = "Timeout in seconds.", DefaultValueFactory = _ => 120 };
    var cmd = new Command("execute-script", "Run an executable Code node through the kernel.") { pathArg, timeoutOpt };
    cmd.SetAction((result, ct) => Run(result, ct,
        (c, t) => c.ExecuteScript(result.GetValue(pathArg)!, result.GetValue(timeoutOpt), t)));
    root.Subcommands.Add(cmd);
}

// --- upload ----------------------------------------------------------------
{
    var pathArg = new Argument<string>("path") { Description = "Target mesh path {nodePath}/{collection}/{filePath}." };
    var fileArg = new Argument<string>("local-file") { Description = "Local file to upload." };
    var cmd = new Command("upload", "Upload a file into a node's content collection.") { pathArg, fileArg };
    cmd.SetAction((result, ct) => Run(result, ct,
        (c, t) => c.Upload(result.GetValue(pathArg)!, result.GetValue(fileArg)!, t)));
    root.Subcommands.Add(cmd);
}

// --- mirror ----------------------------------------------------------------
{
    var directionArg = new Argument<string>("direction") { Description = "'push' or 'pull'." };
    var remoteUrlArg = new Argument<string>("remote-url") { Description = "Remote portal base URL." };
    var sourceArg = new Argument<string>("source-path") { Description = "Subtree path on the originating side." };
    var remoteTokenOpt = new Option<string>("--remote-token") { Description = "API token issued on the remote portal.", Required = true };
    var targetOpt = new Option<string?>("--target") { Description = "Override the target path." };
    var removeMissingOpt = new Option<bool>("--remove-missing") { Description = "Delete nodes that don't exist on the source side (DESTRUCTIVE)." };
    var dryRunOpt = new Option<bool>("--dry-run") { Description = "Enumerate without writing." };
    var cmd = new Command("mirror", "Mirror a subtree push/pull between two portals.") { directionArg, remoteUrlArg, sourceArg, remoteTokenOpt, targetOpt, removeMissingOpt, dryRunOpt };
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) =>
    {
        var dir = result.GetValue(directionArg)!.ToLowerInvariant() switch
        {
            "push" => "Push",
            "pull" => "Pull",
            var x => throw new ArgumentException($"direction must be 'push' or 'pull', got '{x}'."),
        };
        return c.Mirror(
            dir,
            result.GetValue(remoteUrlArg)!,
            result.GetValue(remoteTokenOpt)!,
            result.GetValue(sourceArg)!,
            result.GetValue(targetOpt),
            result.GetValue(removeMissingOpt),
            result.GetValue(dryRunOpt),
            t);
    }));
    root.Subcommands.Add(cmd);
}

// --- navigate-to / base-url -----------------------------------------------
{
    var pathArg = new Argument<string>("path") { Description = "Mesh path to build a browser URL for." };
    var cmd = new Command("navigate-to", "Print the browser URL for a mesh path.") { pathArg };
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) => c.NavigateTo(result.GetValue(pathArg)!, t)));
    root.Subcommands.Add(cmd);
}
{
    var cmd = new Command("base-url", "Print the portal base URL.");
    cmd.SetAction((result, ct) => Run(result, ct, (c, t) => c.BaseUrl(t)));
    root.Subcommands.Add(cmd);
}

// --- login -----------------------------------------------------------------
{
    // Token is OPTIONAL on the command line — omit it and you're prompted to paste it
    // interactively (masked), so the secret never lands in shell history or process args.
    var tokenArg = new Argument<string?>("token")
    {
        Description = "API token (mw_…). Omit to be prompted interactively.",
        Arity = ArgumentArity.ZeroOrOne,
    };
    var baseOpt = new Option<string?>("--base-url") { Description = "Portal base URL to persist alongside the token." };
    var cmd = new Command("login", $"Log on: store an API token in {MemexConfig.ConfigPath}.") { tokenArg, baseOpt };
    cmd.SetAction((result, ct) =>
    {
        try
        {
            var token = result.GetValue(tokenArg);
            if (string.IsNullOrWhiteSpace(token))
                token = PromptForToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("error: no token provided.");
                return Task.FromResult(3);
            }
            if (!token.StartsWith("mw_", StringComparison.Ordinal))
                Console.Error.WriteLine("warning: token does not start with 'mw_' — saving it anyway.");
            MemexConfig.SaveFile(result.GetValue(baseOpt), token);
            Console.WriteLine($"Logged on — token saved to {MemexConfig.ConfigPath}");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return Task.FromResult(3);
        }
    });
    root.Subcommands.Add(cmd);
}

// Reads a token from the console without echoing it. Falls back to a plain read when
// input is redirected (piped / non-interactive), where key-by-key capture isn't available.
static string? PromptForToken()
{
    Console.Error.Write("Paste your API token (mw_…): ");
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
    Console.Error.WriteLine();
    return sb.ToString().Trim();
}

// --- build ------------------------------------------------------------------
// 🚨 `build` does NOT talk to the portal REST API, so it does not use Run(client, …) above: it
// pulls an image and runs the platform's builder inside it. See Doc/Architecture/InMeshBuildAndTest.
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

    plugin.SetAction((result, ct) => new BuildPluginCommand(Console.Out, Console.Error).RunAsync(
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

    project.SetAction((result, ct) => new BuildProjectCommand(Console.Out, Console.Error).RunAsync(
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

return await root.Parse(args).InvokeAsync();

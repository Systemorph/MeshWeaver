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
    Console.Error.Write("Paste your atioz API token (mw_…): ");
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

return await root.Parse(args).InvokeAsync();

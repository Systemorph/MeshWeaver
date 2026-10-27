using System.CommandLine;
using System.Text.Json.Nodes;
using MeshWeaver.Cli;
using Xunit;

namespace MeshWeaver.Cli.Test;

/// <summary>
/// The mesh commands are generated from the instance's MCP <c>tools/list</c> and run with
/// <c>tools/call</c> (Doc/Architecture/NuGetPackageRetirement §1a, policy
/// <c>cli-thin-wrapper-own-version</c>). Every test drives <see cref="CliApp.RunAsync"/> — the same
/// entry point <c>Program.cs</c> calls — against a loopback MCP endpoint, and reads what crossed the
/// wire, so "the command returned" is never the evidence.
/// </summary>
public sealed class DynamicMcpCommandsTest : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly string configDir = Path.Combine(Path.GetTempPath(), "memex-cli-test-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset now = T0;

    public void Dispose()
    {
        if (Directory.Exists(configDir)) Directory.Delete(configDir, recursive: true);
    }

    private static JsonArray Catalog() => (JsonArray)JsonNode.Parse("""
        [
          {"name":"get","description":"Retrieves a node or a resource by path.\n\nLonger detail that the listing does not show.",
           "inputSchema":{"type":"object","properties":{"path":{"type":"string","description":"Path to data."}},"required":["path"]}},
          {"name":"search","description":"Searches the mesh.",
           "inputSchema":{"type":"object","properties":{
              "query":{"type":"string","description":"Query string."},
              "basePath":{"type":["string","null"],"description":"Base path.","default":null},
              "limit":{"type":"integer","description":"Maximum results.","default":50}},
            "required":["query"]}},
          {"name":"mixed_bag","description":"A tool with every schema shape.",
           "inputSchema":{"type":"object","properties":{
              "path":{"type":"string","description":"Where."},
              "limit":{"type":"integer"},
              "ratio":{"type":"number"},
              "force":{"type":"boolean"},
              "mode":{"type":"string","enum":["fast","slow"]},
              "tags":{"type":"array","items":{"type":"string"}},
              "counts":{"type":"array","items":{"type":"integer"}},
              "meta":{"type":"object"},
              "token":{"type":"string","description":"Collides with the global --token."}},
            "required":["path","mode"]}},
          {"name":"move","description":"Moves a subtree.",
           "inputSchema":{"type":"object","properties":{"sourcePath":{"type":"string"},"targetPath":{"type":"string"}},"required":["sourcePath","targetPath"]}},
          {"name":"delete","description":"Deletes nodes.",
           "inputSchema":{"type":"object","properties":{"paths":{"type":"string","description":"JSON array of paths."}},"required":["paths"]}},
          {"name":"create","description":"Creates a node.",
           "inputSchema":{"type":"object","properties":{"node":{"type":"string"}},"required":["node"]}},
          {"name":"get_diagnostics","description":"Compile diagnostics.",
           "inputSchema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}},
          {"name":"fails","description":"Always an isError result.","inputSchema":{"type":"object","properties":{}}},
          {"name":"sentinel","description":"Answers the Error: sentence.","inputSchema":{"type":"object","properties":{}}},
          {"name":"upload","description":"SERVER upload (base64).",
           "inputSchema":{"type":"object","properties":{"path":{"type":"string"},"base64Content":{"type":"string"}},"required":["path","base64Content"]}},
          {"name":"login","description":"A server tool named like a built-in.","inputSchema":{"type":"object","properties":{}}}
        ]
        """)!;

    private async Task<(int Exit, string Out, string Err)> Run(FakeMcpServer? server, string baseUrl, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost(stdout, stderr, configDir, () => now);
        var all = new List<string> { "--base-url", server?.BaseUrl ?? baseUrl, "--token", FakeMcpServer.Token };
        all.AddRange(args);
        var exit = await CliApp.RunAsync(all.ToArray(), host, TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject Args(string stdout) =>
        (JsonObject)JsonNode.Parse(stdout)!["arguments"]!;

    // --- command generation -------------------------------------------------------------------

    [Fact]
    public void Options_are_generated_from_the_input_schema()
    {
        var catalog = new ToolCatalog(Catalog().Select(McpTool.From).OfType<McpTool>().ToArray(), T0, false);
        var root = CliApp.BuildRoot(new CliHost(TextWriter.Null, TextWriter.Null, configDir, () => T0), catalog);
        var cmd = root.Subcommands.Single(c => c.Name == "mixed-bag");

        Option Opt(string name) => cmd.Options.Single(o => o.Name == name);
        Assert.IsType<Option<long?>>(Opt("--limit"));
        Assert.IsType<Option<double?>>(Opt("--ratio"));
        Assert.IsType<Option<bool>>(Opt("--force"));
        Assert.IsType<Option<string?>>(Opt("--mode"));
        Assert.IsType<Option<string[]>>(Opt("--tags"));
        Assert.IsType<Option<string?>>(Opt("--meta"));
        // A property named like a global option is renamed rather than shadowing it.
        Assert.Contains(cmd.Options, o => o.Name == "--arg-token");
        Assert.DoesNotContain(cmd.Options, o => o.Name == "--token");
        // Required string → also positional; a required ENUM is not.
        Assert.Equal(["path"], cmd.Arguments.Select(a => a.Name));
        Assert.EndsWith("(required)", Opt("--mode").Description);
        Assert.Contains("(required)", Opt("--path").Description);
        Assert.DoesNotContain("(required)", Opt("--limit").Description ?? "");

        // snake_case tool → kebab command; the listing shows the first paragraph only.
        var get = root.Subcommands.Single(c => c.Name == "get");
        Assert.Equal("Retrieves a node or a resource by path.", get.Description);
        Assert.Contains(root.Subcommands, c => c.Name == "get-diagnostics");
        Assert.Contains("--base-path", root.Subcommands.Single(c => c.Name == "search").Options.Select(o => o.Name));
    }

    [Fact]
    public async Task Help_lists_the_dynamic_commands()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        var (exit, stdout, _) = await Run(server, "", "--help");
        Assert.Equal(0, exit);
        foreach (var name in new[] { "get", "search", "mixed-bag", "get-diagnostics", "build", "tests" })
            Assert.Contains(name, stdout);
        Assert.Equal(1, server.ListRequests);
    }

    // --- call payloads ------------------------------------------------------------------------

    [Fact]
    public async Task Every_schema_shape_reaches_the_call_typed()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        server.ReplyAsEventStream = true; // the Streamable HTTP framing must be read too
        var (exit, stdout, stderr) = await Run(server, "",
            "mixed-bag", "Doc/X", "--limit", "5", "--ratio", "0.5", "--force", "--mode", "fast",
            "--tags", "a", "--tags", "b", "--counts", "1", "--counts", "2", "--meta", """{"k":1}""", "--arg-token", "t");

        Assert.True(exit == 0, stderr);
        var call = Assert.Single(server.Calls);
        Assert.Equal("mixed_bag", call.Name);
        var expected = JsonNode.Parse("""
            {"path":"Doc/X","limit":5,"ratio":0.5,"force":true,"mode":"fast","tags":["a","b"],"counts":[1,2],"meta":{"k":1},"token":"t"}
            """);
        Assert.True(JsonNode.DeepEquals(expected, call.Arguments), call.Arguments.ToJsonString());
        // stdout is the tool's text, verbatim — machine-readable.
        Assert.True(JsonNode.DeepEquals(expected, Args(stdout)));
    }

    [Fact]
    public async Task A_missing_required_argument_is_refused_before_any_call()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        var (exit, _, stderr) = await Run(server, "", "mixed-bag", "--mode", "fast");
        Assert.NotEqual(0, exit);
        Assert.Contains("'path'", stderr);

        var (badEnum, _, _) = await Run(server, "", "mixed-bag", "Doc/X", "--mode", "medium");
        Assert.NotEqual(0, badEnum);

        var (badInt, _, _) = await Run(server, "", "mixed-bag", "Doc/X", "--mode", "fast", "--limit", "many");
        Assert.NotEqual(0, badInt);

        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task Json_and_file_supply_the_argument_object_and_explicit_options_win()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        var (exit, _, stderr) = await Run(server, "",
            "mixed-bag", "--json", """{"path":"A","mode":"slow","limit":3}""", "--limit", "7");
        Assert.True(exit == 0, stderr);

        var argsFile = Path.Combine(Directory.CreateDirectory(configDir).FullName, "args.json");
        await File.WriteAllTextAsync(argsFile, """{"path":"B","mode":"fast","meta":{"x":[1]}}""", TestContext.Current.CancellationToken);
        var (exit2, _, stderr2) = await Run(server, "", "mixed-bag", "--file", argsFile);
        Assert.True(exit2 == 0, stderr2);

        Assert.Collection(server.Calls,
            c => Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"path":"A","mode":"slow","limit":7}"""), c.Arguments), c.Arguments.ToJsonString()),
            c => Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"path":"B","mode":"fast","meta":{"x":[1]}}"""), c.Arguments), c.Arguments.ToJsonString()));
    }

    [Fact]
    public async Task The_verbs_scripts_already_use_keep_their_shape()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        var node = Path.Combine(Directory.CreateDirectory(configDir).FullName, "node.json");
        await File.WriteAllTextAsync(node, """{"id":"N","namespace":"Doc"}""", TestContext.Current.CancellationToken);

        Assert.Equal(0, (await Run(server, "", "get", "Doc/X")).Exit);
        Assert.Equal(0, (await Run(server, "", "search", "nodeType:Agent", "--base-path", "Doc")).Exit);
        Assert.Equal(0, (await Run(server, "", "move", "A/One", "B/One")).Exit);
        Assert.Equal(0, (await Run(server, "", "delete", "A/One", "A/Two")).Exit);
        Assert.Equal(0, (await Run(server, "", "create", "-f", node)).Exit);
        Assert.Equal(0, (await Run(server, "", "diagnostics", "Type/T")).Exit);

        Assert.Collection(server.Calls,
            c => Assert.Equal(("get", """{"path":"Doc/X"}"""), (c.Name, c.Arguments.ToJsonString())),
            c => Assert.Equal(("search", """{"query":"nodeType:Agent","basePath":"Doc"}"""), (c.Name, c.Arguments.ToJsonString())),
            c => Assert.Equal(("move", """{"sourcePath":"A/One","targetPath":"B/One"}"""), (c.Name, c.Arguments.ToJsonString())),
            c => Assert.Equal(("delete", """["A/One","A/Two"]"""), (c.Name, c.Arguments["paths"]!.GetValue<string>())),
            c => Assert.Equal(("create", """{"id":"N","namespace":"Doc"}"""), (c.Name, c.Arguments["node"]!.GetValue<string>())),
            c => Assert.Equal(("get_diagnostics", """{"path":"Type/T"}"""), (c.Name, c.Arguments.ToJsonString())));
    }

    // --- exit codes ---------------------------------------------------------------------------

    [Fact]
    public async Task A_tool_error_is_a_non_zero_exit_on_stderr()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());

        var (exit, stdout, stderr) = await Run(server, "", "fails");
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("the tool failed on purpose", stderr);

        var (sentinel, sentinelOut, sentinelErr) = await Run(server, "", "sentinel");
        Assert.Equal(1, sentinel);
        Assert.Equal("", sentinelOut);
        Assert.StartsWith("Error: Not permitted", sentinelErr);

        // Negative control: the same harness reports a success as exit 0 on stdout.
        var (ok, okOut, _) = await Run(server, "", "get", "Doc/X");
        Assert.Equal(0, ok);
        Assert.Contains("\"tool\":\"get\"", okOut);
    }

    // --- discovery and the cache --------------------------------------------------------------

    [Fact]
    public async Task The_catalog_is_cached_per_instance_and_refreshed_on_expiry_or_request()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        await Run(server, "", "get", "Doc/X");
        Assert.Equal(1, server.ListRequests);
        Assert.True(File.Exists(ToolCatalogCache.CachePath(configDir, server.BaseUrl)));

        await Run(server, "", "get", "Doc/X");
        Assert.Equal(1, server.ListRequests); // fresh cache: no tools/list

        await Run(server, "", "--refresh", "get", "Doc/X");
        Assert.Equal(2, server.ListRequests); // --refresh

        now = T0 + ToolCatalogCache.Lifetime + TimeSpan.FromSeconds(1);
        await Run(server, "", "get", "Doc/X");
        Assert.Equal(3, server.ListRequests); // expired
    }

    [Fact]
    public async Task An_unreachable_instance_falls_back_to_its_cached_catalog()
    {
        string baseUrl;
        await using (var server = await FakeMcpServer.StartAsync(Catalog()))
        {
            baseUrl = server.BaseUrl;
            Assert.Equal(0, (await Run(server, "", "get", "Doc/X")).Exit);
        }

        // The server is gone; --refresh forces the read, which fails, and the cache answers.
        var (exit, stdout, stderr) = await Run(null, baseUrl, "--refresh", "get", "--help");
        Assert.Equal(0, exit);
        Assert.Contains("using the catalog cached at", stderr);
        Assert.Contains("Retrieves a node", stdout);
    }

    [Fact]
    public async Task No_instance_and_no_cache_fails_clearly()
    {
        string baseUrl;
        await using (var server = await FakeMcpServer.StartAsync(Catalog()))
            baseUrl = server.BaseUrl;

        var (exit, _, stderr) = await Run(null, baseUrl, "get", "Doc/X");
        Assert.Equal(2, exit);
        Assert.Contains("no cached catalog exists", stderr);
    }

    // --- built-ins ----------------------------------------------------------------------------

    [Fact]
    public async Task A_built_in_wins_over_a_server_tool_of_the_same_name()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        var file = Path.Combine(Directory.CreateDirectory(configDir).FullName, "a.txt");
        await File.WriteAllTextAsync(file, "hello", TestContext.Current.CancellationToken);

        // Seed the cache so the built-in run below could not have avoided the catalog by accident.
        await Run(server, "", "get", "Doc/X");
        var (exit, stdout, stderr) = await Run(server, "", "upload", "Doc/content/a.txt", file);
        Assert.True(exit == 0, stderr);
        Assert.Contains("Uploaded", stdout);
        Assert.Equal(["Doc/content/a.txt"], server.Uploads);
        Assert.DoesNotContain(server.Calls, c => c.Name == "upload");

        var (_, help, _) = await Run(server, "", "--help");
        Assert.Contains("shadowed by a built-in: upload, login", help);
    }

    [Fact]
    public async Task Negative_control_a_tool_the_catalog_lacks_is_no_command()
    {
        await using var server = await FakeMcpServer.StartAsync(
            new JsonArray(Catalog().Where(t => t!["name"]!.GetValue<string>() != "mixed_bag").Select(t => t!.DeepClone()).ToArray()));
        var (exit, _, stderr) = await Run(server, "", "mixed-bag", "Doc/X", "--mode", "fast");
        Assert.NotEqual(0, exit);
        Assert.Contains("mixed-bag", stderr);
        Assert.Empty(server.Calls);
    }
}

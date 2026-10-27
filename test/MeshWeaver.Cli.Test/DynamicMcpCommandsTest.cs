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
    private TimeSpan requestTimeout = McpClient.DefaultRequestTimeout;

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
          {"name":"login","description":"A server tool named like a built-in.","inputSchema":{"type":"object","properties":{}}},
          {"name":"malformed","description":"Answers a content item whose type is a number.","inputSchema":{"type":"object","properties":{}}}
        ]
        """)!;

    private async Task<(int Exit, string Out, string Err)> Run(FakeMcpServer? server, string baseUrl, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost(stdout, stderr, configDir, () => now) { McpRequestTimeout = requestTimeout };
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

    // --- review round: the defects found in #7301's first review --------------------------------

    private static JsonArray Tools(string json) => (JsonArray)JsonNode.Parse(json)!;

    [Fact]
    public async Task Local_commands_never_touch_the_connection_settings()
    {
        // A --base-url that is not a URL must not break commands that never contact the instance.
        var (build, buildOut, buildErr) = await Run(null, "not a url", "build", "--help");
        Assert.True(build == 0, buildErr);
        Assert.Contains("plugin", buildOut);
        Assert.Equal(0, (await Run(null, "not a url", "--version")).Exit);

        // Negative control: a catalog-backed command with the same settings fails, naming why.
        var (get, _, getErr) = await Run(null, "not a url", "get", "Doc/X");
        Assert.Equal(2, get);
        Assert.Contains("is not a URL", getErr);
    }

    [Fact]
    public async Task Tools_whose_kebab_names_collide_keep_their_exact_names_in_any_order()
    {
        const string a = """{"name":"foo_bar","description":"Underscore.","inputSchema":{"type":"object","properties":{}}}""";
        const string b = """{"name":"foo-bar","description":"Hyphen.","inputSchema":{"type":"object","properties":{}}}""";
        const string c = """{"name":"other_tool","description":"No clash.","inputSchema":{"type":"object","properties":{}}}""";
        foreach (var order in new[] { $"[{a},{b},{c}]", $"[{b},{a},{c}]" })
        {
            var catalog = new ToolCatalog(Tools(order).Select(McpTool.From).OfType<McpTool>().ToArray(), T0, false);
            var names = CliApp.CommandNames(catalog).ToDictionary(n => n.Tool.Name, n => n.Command);
            Assert.Equal("foo_bar", names["foo_bar"]);
            Assert.Equal("foo-bar", names["foo-bar"]);
            Assert.Equal("other-tool", names["other_tool"]); // negative control: no clash, kebab as usual
        }

        await using var server = await FakeMcpServer.StartAsync(Tools($"[{b},{a},{c}]"));
        Assert.Equal(0, (await Run(server, "", "foo_bar")).Exit);
        Assert.Equal(0, (await Run(server, "", "foo-bar")).Exit);
        Assert.Equal(["foo_bar", "foo-bar"], server.Calls.Select(x => x.Name));
    }

    [Fact]
    public async Task A_transport_failure_is_exit_2_and_a_malformed_result_exit_3()
    {
        string baseUrl;
        await using (var server = await FakeMcpServer.StartAsync(Catalog()))
        {
            baseUrl = server.BaseUrl;
            var (malformed, _, malformedErr) = await Run(server, "", "malformed");
            Assert.Equal(3, malformed);
            Assert.StartsWith("error:", malformedErr);
        }

        // The catalog is cached; the instance is gone, so the CALL cannot connect.
        var (exit, _, stderr) = await Run(null, baseUrl, "--refresh", "get", "Doc/X");
        Assert.Equal(2, exit);
        Assert.Contains("using the catalog cached at", stderr);
    }

    [Fact]
    public async Task Compatibility_shapes_apply_only_to_string_parameters()
    {
        await using var server = await FakeMcpServer.StartAsync(Tools("""
            [
              {"name":"create","inputSchema":{"type":"object","properties":{"node":{"type":"object"}},"required":["node"]}},
              {"name":"delete","inputSchema":{"type":"object","properties":{"paths":{"type":"array","items":{"type":"string"}}},"required":["paths"]}}
            ]
            """));
        var node = Path.Combine(Directory.CreateDirectory(configDir).FullName, "node.json");
        await File.WriteAllTextAsync(node, """{"id":"N"}""", TestContext.Current.CancellationToken);

        Assert.Equal(0, (await Run(server, "", "create", "--node", """{"id":"N"}""")).Exit);
        Assert.Equal(0, (await Run(server, "", "delete", "--paths", "A/One", "--paths", "A/Two")).Exit);
        // Negative control: the string-only shapes are OFF — `-f` is no option of an object-typed
        // create, and a bare positional is no argument of an array-typed delete.
        Assert.NotEqual(0, (await Run(server, "", "create", "-f", node)).Exit);
        Assert.NotEqual(0, (await Run(server, "", "delete", "A/One")).Exit);

        Assert.Collection(server.Calls,
            x => Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"node":{"id":"N"}}"""), x.Arguments), x.Arguments.ToJsonString()),
            x => Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"paths":["A/One","A/Two"]}"""), x.Arguments), x.Arguments.ToJsonString()));
    }

    [Fact]
    public async Task Every_property_gets_its_own_option_name()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal("--arg-token", DynamicCommands.AllocateOptionName("arg-token", used));
        Assert.Equal("--arg2-token", DynamicCommands.AllocateOptionName("token", used));
        Assert.Equal("--limit", DynamicCommands.AllocateOptionName("limit", used));
        Assert.Equal("--arg-limit", DynamicCommands.AllocateOptionName("limit", used));

        await using var server = await FakeMcpServer.StartAsync(Tools("""
            [{"name":"pair","inputSchema":{"type":"object","properties":{"argToken":{"type":"string"},"token":{"type":"string"}}}}]
            """));
        var (exit, _, stderr) = await Run(server, "", "pair", "--arg-token", "one", "--arg2-token", "two");
        Assert.True(exit == 0, stderr);
        var call = Assert.Single(server.Calls);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"argToken":"one","token":"two"}"""), call.Arguments), call.Arguments.ToJsonString());
    }

    [Fact]
    public async Task A_required_property_given_as_explicit_null_is_present()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        var (exit, _, stderr) = await Run(server, "", "mixed-bag", "--json", """{"path":null,"mode":"fast"}""");
        Assert.True(exit == 0, stderr);
        var call = Assert.Single(server.Calls);
        Assert.True(call.Arguments.ContainsKey("path"));
        Assert.Null(call.Arguments["path"]);

        // Negative control: ABSENT is still refused.
        Assert.NotEqual(0, (await Run(server, "", "mixed-bag", "--json", """{"mode":"fast"}""")).Exit);
        Assert.Single(server.Calls);
    }

    [Fact]
    public async Task An_event_stream_that_stays_open_after_the_reply_does_not_hold_the_command()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        server.ReplyAsEventStream = true;
        server.HoldEventStreamOpen = true;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (exit, stdout, stderr) = await Run(server, "", "get", "Doc/X");
        clock.Stop();

        Assert.True(exit == 0, stderr);
        Assert.Contains("\"tool\":\"get\"", stdout);
        // Control: the server really did keep its streams open (initialize, tools/list, tools/call)…
        Assert.True(server.HeldStreams >= 3, $"held {server.HeldStreams}");
        // …and the command returned on the reply, not on the 10-minute HTTP timeout.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    // Unix file modes: CI and the developer hosts are Linux and macOS.
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_cache_file_is_a_cache_miss()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        Assert.Equal(0, (await Run(server, "", "get", "Doc/X")).Exit);
        var cache = ToolCatalogCache.CachePath(configDir, server.BaseUrl);
        var mode = File.GetUnixFileMode(cache);
        File.SetUnixFileMode(cache, UnixFileMode.None);
        try
        {
            // Precondition, asserted rather than assumed: the file really cannot be read here.
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(cache));
            now = T0 + TimeSpan.FromMinutes(1); // inside the lifetime: only the unreadable file forces a fetch

            var (exit, _, stderr) = await Run(server, "", "get", "Doc/X");
            Assert.True(exit == 0, stderr);
            Assert.Equal(2, server.ListRequests);
        }
        finally
        {
            if (File.Exists(cache)) File.SetUnixFileMode(cache, mode);
        }
    }

    // --- second review round --------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "--refresh", "get", "X" }, true, "get")]
    [InlineData(new[] { "--refresh=true", "get", "X" }, true, "get")]
    [InlineData(new[] { "--refresh=false", "get", "X" }, false, "get")]
    [InlineData(new[] { "--refresh", "false", "build", "--help" }, false, "build")]
    [InlineData(new[] { "--refresh", "true", "get", "X" }, true, "get")]
    [InlineData(new[] { "--refresh", "login" }, true, "login")]
    [InlineData(new[] { "get", "X" }, false, "get")] // negative control: no flag, no refresh
    public void The_pre_scan_reads_refresh_as_the_parser_does(string[] args, bool refresh, string command)
    {
        var scan = CliApp.Scan(args);
        Assert.Equal(refresh, scan.Refresh);
        Assert.Equal(command, scan.Command);
    }

    [Fact]
    public async Task Refresh_with_an_explicit_value_reaches_discovery()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        await Run(server, "", "get", "Doc/X");
        await Run(server, "", "--refresh=true", "get", "Doc/X");
        Assert.Equal(2, server.ListRequests);
        await Run(server, "", "--refresh", "false", "get", "Doc/X");
        Assert.Equal(2, server.ListRequests); // explicit false: the fresh cache is used
        // `--refresh false` before a LOCAL command does not make `false` the command.
        var (exit, _, stderr) = await Run(server, "", "--refresh", "false", "build", "--help");
        Assert.True(exit == 0, stderr);
        Assert.Equal(2, server.ListRequests);
    }

    [Fact]
    public async Task A_malformed_tools_list_never_replaces_a_good_cache()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        Assert.Equal(0, (await Run(server, "", "get", "Doc/X")).Exit);

        server.MalformedToolsList = true;
        var (exit, _, stderr) = await Run(server, "", "--refresh", "get", "Doc/X");
        Assert.True(exit == 0, stderr);
        Assert.Contains("no `tools` array", stderr);
        Assert.Contains("using the catalog cached at", stderr);

        // The cache still holds the good catalog: a later run without --refresh has every command.
        Assert.Equal(0, (await Run(server, "", "mixed-bag", "Doc/X", "--mode", "fast")).Exit);

        // Negative control: with no cache, a malformed list is a clear failure, not an empty catalog.
        Directory.Delete(Path.Combine(configDir, "tools"), recursive: true);
        var (noCache, _, noCacheErr) = await Run(server, "", "get", "Doc/X");
        Assert.Equal(2, noCache);
        Assert.Contains("no cached catalog exists", noCacheErr);
    }

    [Fact]
    public async Task A_stream_that_never_carries_the_reply_ends_at_the_deadline()
    {
        await using var server = await FakeMcpServer.StartAsync(Catalog());
        requestTimeout = TimeSpan.FromSeconds(2);

        // Negative control: the same short deadline does not trip a call that is answered.
        Assert.Equal(0, (await Run(server, "", "get", "Doc/X")).Exit);

        server.NeverAnswerCalls = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (exit, stdout, stderr) = await Run(server, "", "get", "Doc/X");
        clock.Stop();
        Assert.Equal(3, exit);
        Assert.Equal("", stdout);
        Assert.Contains("within 2s (tools/call)", stderr);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public void The_cache_identity_keeps_the_path_case_and_ignores_the_host_case()
    {
        Assert.NotEqual(
            ToolCatalogCache.CachePath(configDir, "https://portal.example.com/TenantA"),
            ToolCatalogCache.CachePath(configDir, "https://portal.example.com/tenanta"));
        Assert.Equal(
            ToolCatalogCache.CachePath(configDir, "HTTPS://Portal.Example.com/TenantA/"),
            ToolCatalogCache.CachePath(configDir, "https://portal.example.com/TenantA"));
    }
}

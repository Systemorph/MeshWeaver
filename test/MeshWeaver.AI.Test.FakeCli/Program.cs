// Deterministic fake CLI login for ConnectStrategy tests.
//
// Behaviour (mimics `claude setup-token` over a non-Ink pipe):
//   1. Print an auth URL line to stdout (scraped by StartConnect).
//   2. Read one line from stdin (the pasted code the portal forwards).
//   3. Print a token line to stdout (captured by CompleteConnect).
//
// Knobs via env vars so a test can vary the shape:
//   FAKE_CLI_URL    — the URL to print     (default a claude.ai oauth URL)
//   FAKE_CLI_TOKEN  — the token to print   (default sk-ant-FAKE-…)
//   FAKE_CLI_MODE   — "loggedin" writes a .credentials.json under
//                     CLAUDE_CONFIG_DIR and exits (for IsLoggedIn tests).

var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
var mode = Environment.GetEnvironmentVariable("FAKE_CLI_MODE");

if (string.Equals(mode, "loggedin", StringComparison.OrdinalIgnoreCase))
{
    if (!string.IsNullOrEmpty(configDir))
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, ".credentials.json"),
            "{\"accessToken\":\"sk-ant-ALREADY-LOGGED-IN-0000\"}");
    }
    Console.WriteLine("Already logged in.");
    return 0;
}

var url = Environment.GetEnvironmentVariable("FAKE_CLI_URL")
          ?? "https://claude.ai/oauth/authorize?code=true&fake=1";
var token = Environment.GetEnvironmentVariable("FAKE_CLI_TOKEN")
            ?? "sk-ant-FAKE-TOKEN-abcdefghijklmnopqrstuvwxyz0123456789";

Console.WriteLine("Open this URL in your browser to authorize:");
Console.WriteLine(url);
Console.Out.Flush();

// Wait for the pasted code on stdin (the portal forwards it via RedirectStandardInput).
var code = Console.In.ReadLine();

if (!string.IsNullOrWhiteSpace(code))
{
    // Echo the captured token so CompleteConnect's stdout scrape finds it.
    Console.WriteLine(token);
    Console.Out.Flush();

    // Also persist a credentials file (the alternate capture path).
    if (!string.IsNullOrEmpty(configDir))
    {
        try
        {
            Directory.CreateDirectory(configDir);
            File.WriteAllText(
                Path.Combine(configDir, ".credentials.json"),
                $"{{\"accessToken\":\"{token}\"}}");
        }
        catch { /* best effort */ }
    }
    return 0;
}

Console.Error.WriteLine("No code received on stdin.");
return 1;

// MeshWeaver — native Windows desktop shell (WinForms + WebView2, the Edge Chromium runtime).
//
// The Windows twin of the macOS app (clients/desktop/macos): it packages the whole local-first stack.
//   1. On launch it starts the bundled LocalMesh — the in-process ("monolith") mesh backed by SQLite —
//      as a child process (localmesh\Memex.LocalMesh.exe, self-contained: no .NET needed on the box).
//   2. LocalMesh creates a FRESH SQLite database on first launch under %LOCALAPPDATA%\Memex and serves
//      the packaged React-Native web UI + the gRPC bridge on http://localhost:5250 (same origin ⇒ no CORS).
//   3. Once the mesh is listening, the UI loads in a native WebView2 window; the mic is auto-granted so
//      the speech/dictation feature works.
//   4. On close, the mesh child process is killed with the app.
//
// NOTE: net10.0-windows (WinForms + WebView2) builds ONLY on Windows with the Windows Desktop SDK, so
// this project is intentionally NOT in MeshWeaver.slnx — build it with build.ps1 on a Windows machine/CI.

using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MeshWeaver.Windows;

internal static class Program
{
    internal const int Port = 5250;
    internal static readonly string AppUrl = $"http://localhost:{Port}/";     // ATS-free loopback; no address bar
    internal static readonly string ProbeUrl = $"http://127.0.0.1:{Port}/";   // readiness probe
    private static Process? _mesh;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        StartMesh();
        Application.ApplicationExit += (_, _) => StopMesh();
        Application.Run(new MainForm());
    }

    private static void StartMesh()
    {
        var localmesh = Path.Combine(AppContext.BaseDirectory, "localmesh");
        var exe = Path.Combine(localmesh, "Memex.LocalMesh.exe");
        if (!File.Exists(exe)) return;
        _mesh = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--Grpc:Port={Port}",
                WorkingDirectory = localmesh,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        _mesh.Start();
    }

    private static void StopMesh()
    {
        try
        {
            if (_mesh is { HasExited: false })
                _mesh.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }
}

internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public MainForm()
    {
        Text = "MeshWeaver";
        Width = 1320;
        Height = 880;
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        await _web.EnsureCoreWebView2Async();
        // Auto-grant the microphone (the speech/dictation feature) for the local origin.
        _web.CoreWebView2.PermissionRequested += (_, args) =>
        {
            if (args.PermissionKind == CoreWebView2PermissionKind.Microphone)
                args.State = CoreWebView2PermissionState.Allow;
        };
        await WaitForMeshAsync();
        _web.CoreWebView2.Navigate(Program.AppUrl);
    }

    // Poll the mesh until it serves (boot is a few seconds), then the caller navigates.
    private static async Task WaitForMeshAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 120; i++)
        {
            try
            {
                var resp = await http.GetAsync(Program.ProbeUrl);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not up yet */ }
            await Task.Delay(500);
        }
    }
}

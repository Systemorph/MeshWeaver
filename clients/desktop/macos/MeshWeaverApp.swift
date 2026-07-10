// MeshWeaver — native macOS desktop shell.
//
// A genuinely native app (AppKit + WKWebView, no Electron) that packages the whole local-first stack:
//   1. On launch it starts the bundled LocalMesh — the in-process ("monolith") mesh backed by SQLite —
//      as a child process (dotnet Memex.LocalMesh.dll from Contents/Resources/localmesh).
//   2. LocalMesh creates a FRESH SQLite database on first launch under
//      ~/Library/Application Support/Memex/memex-local.db and serves the packaged web UI + the gRPC
//      bridge on http://memex.localhost:5250 (same origin ⇒ no CORS).
//   3. Once the mesh is listening, the packaged React-Native web UI loads in a native WKWebView window.
//   4. On quit, the mesh child process is terminated with the app.
//
// The mesh + its data are entirely local; the app works offline. Point it at remote portals (prod, a
// client, the k8s cluster) from the in-app environment switcher.

import Cocoa
import WebKit

let kPort = 5250
// Load over `localhost` (a single-label host ATS treats as local, so http loads without an ATS
// exception). The desktop app has no address bar, so the hostname is internal — `memex.localhost`
// is reserved for the browser-served scenario where the user actually sees/types the URL.
let kReadyProbe = "http://127.0.0.1:\(kPort)/"
let kAppURL = "http://localhost:\(kPort)/"

// Locate the dotnet muxer. A GUI-launched .app does NOT inherit the shell PATH, so we read the absolute
// path baked at build time (Contents/Resources/dotnet-path.txt), then fall back to the usual install spots.
func resolveDotnet() -> String? {
    let fm = FileManager.default
    if let res = Bundle.main.resourcePath {
        let baked = res + "/dotnet-path.txt"
        if let s = try? String(contentsOfFile: baked, encoding: .utf8) {
            let p = s.trimmingCharacters(in: .whitespacesAndNewlines)
            if fm.isExecutableFile(atPath: p) { return p }
        }
    }
    let home = fm.homeDirectoryForCurrentUser.path
    for c in ["\(home)/.dotnet/dotnet", "/usr/local/share/dotnet/dotnet", "/opt/homebrew/bin/dotnet", "/usr/local/bin/dotnet"] {
        if fm.isExecutableFile(atPath: c) { return c }
    }
    return nil
}

final class MeshProcess {
    private var process: Process?

    func start() {
        guard let resources = Bundle.main.resourcePath else { return }
        let dll = resources + "/localmesh/Memex.LocalMesh.dll"
        guard let dotnet = resolveDotnet() else { NSLog("[MeshWeaver] dotnet runtime not found"); return }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: dotnet)
        p.arguments = [dll, "--Grpc:Port=\(kPort)"]
        p.currentDirectoryURL = URL(fileURLWithPath: resources + "/localmesh")
        var env = ProcessInfo.processInfo.environment
        env["DOTNET_ROOT"] = (dotnet as NSString).deletingLastPathComponent
        p.environment = env
        do { try p.run(); process = p; NSLog("[MeshWeaver] mesh started (pid \(p.processIdentifier))") }
        catch { NSLog("[MeshWeaver] failed to start mesh: \(error)") }
    }

    func stop() {
        guard let p = process, p.isRunning else { return }
        p.terminate()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, WKUIDelegate {
    var window: NSWindow!
    var webView: WKWebView!
    let mesh = MeshProcess()

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        mesh.start()

        let rect = NSRect(x: 0, y: 0, width: 1320, height: 880)
        window = NSWindow(contentRect: rect,
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        window.title = "MeshWeaver"
        window.minSize = NSSize(width: 720, height: 480)
        window.center()
        window.setFrameAutosaveName("MeshWeaverMain")

        webView = WKWebView(frame: rect, configuration: WKWebViewConfiguration())
        webView.uiDelegate = self   // grant the composer's dictation mic (see requestMediaCapturePermissionFor)
        webView.autoresizingMask = [.width, .height]
        window.contentView = webView
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        showSplash()
        loadWhenReady(attempt: 0)
    }

    func showSplash() {
        webView.loadHTMLString("""
        <html><head><meta charset='utf-8'><style>
          html,body{height:100%;margin:0;font:15px -apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
            background:#0d1117;color:#c9d1d9;display:flex;align-items:center;justify-content:center}
          .box{text-align:center}.mark{font-size:44px;color:#2ea043}
          .sp{margin-top:14px;width:22px;height:22px;border:3px solid #30363d;border-top-color:#2ea043;
            border-radius:50%;display:inline-block;animation:s .8s linear infinite}
          @keyframes s{to{transform:rotate(360deg)}}
        </style></head><body><div class='box'><div class='mark'>◆</div>
          <div style='margin-top:10px;font-weight:600'>Starting your local mesh…</div>
          <div style='margin-top:4px;color:#8b949e;font-size:13px'>SQLite · in-process · local-first</div>
          <div class='sp'></div></div></body></html>
        """, baseURL: nil)
    }

    func loadWhenReady(attempt: Int) {
        var req = URLRequest(url: URL(string: kReadyProbe)!)
        req.timeoutInterval = 2
        URLSession.shared.dataTask(with: req) { _, response, _ in
            DispatchQueue.main.async {
                if let http = response as? HTTPURLResponse, http.statusCode == 200 {
                    self.webView.load(URLRequest(url: URL(string: kAppURL)!))
                } else if attempt < 120 {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.loadWhenReady(attempt: attempt + 1) }
                } else {
                    self.webView.loadHTMLString("<body style='font-family:sans-serif;padding:40px'><h2>The local mesh did not start.</h2><p>Check Console.app for “MeshWeaver”.</p></body>", baseURL: nil)
                }
            }
        }.resume()
    }

    func buildMenu() {
        let mainMenu = NSMenu()
        let appItem = NSMenuItem()
        mainMenu.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About MeshWeaver", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(withTitle: "Hide MeshWeaver", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit MeshWeaver", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu

        let editItem = NSMenuItem()
        mainMenu.addItem(editItem)
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        NSApp.mainMenu = mainMenu
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) { mesh.stop() }

    // Grant the local UI's getUserMedia (the composer's dictation mic). WKWebView denies media capture
    // by default; the app still needs NSMicrophoneUsageDescription in Info.plist for the OS-level prompt.
    func webView(_ webView: WKWebView,
                 requestMediaCapturePermissionFor origin: WKSecurityOrigin,
                 initiatedByFrame frame: WKFrameInfo,
                 type: WKMediaCaptureType,
                 decisionHandler: @escaping (WKPermissionDecision) -> Void) {
        decisionHandler(.grant)
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()

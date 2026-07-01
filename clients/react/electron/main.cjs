// Electron desktop shell — the SAME @meshweaver/react renderer in a native desktop window.
// The web/Electron target reuses the renderer core + the Fluent DOM leaf pack unchanged; only the
// host (a BrowserWindow vs a browser tab) differs. The React Native target reuses the same core with
// an RN leaf pack instead — the direct analog of MAUI's native MauiViewPack.
//
// Usage:
//   npm i -D electron        # one-time
//   npm run dev              # Vite dev server (the demo, or your live-area app)
//   npm run electron         # opens the desktop window pointed at it
//
// Point it at any served renderer with MESH_URL (e.g. a page that wires GrpcAreaSource to a portal).

const { app, BrowserWindow } = require("electron");

const url = process.env.MESH_URL || "http://localhost:5173";

function createWindow() {
  const win = new BrowserWindow({
    width: 1200,
    height: 900,
    title: "MeshWeaver (Electron)",
    webPreferences: { contextIsolation: true },
  });
  win.loadURL(url);
}

app.whenReady().then(() => {
  createWindow();
  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") app.quit();
});

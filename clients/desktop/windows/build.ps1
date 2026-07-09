# Assemble MeshWeaver.exe — the native Windows desktop shell bundling LocalMesh (the SQLite monolith mesh).
#
# Run on WINDOWS with the .NET 10 SDK + Node (for the web export). Produces dist\MeshWeaver\ containing
# MeshWeaver.exe (the WebView2 shell), localmesh\ (the self-contained mesh + baked-in RN web UI), and the
# .NET runtime — no prerequisites on the target box except the WebView2 Evergreen runtime (preinstalled
# on Windows 11). The macOS twin is clients/desktop/macos/build-macos-app.sh.
#
# Usage:  pwsh clients\desktop\windows\build.ps1 [-Rid win-x64] [-Out <dir>]
param(
    [string]$Rid = "win-x64",
    [string]$Out = "$PSScriptRoot\dist"
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$app  = Join-Path $Out "MeshWeaver"
$www  = Join-Path $repo "memex\Memex.LocalMesh\wwwroot"

Write-Host "[1/4] Export the React-Native web UI -> wwwroot"
Push-Location (Join-Path $repo "clients\react-native")
& npx expo export --platform web --output-dir dist
Pop-Location
if (Test-Path $www) { Remove-Item $www -Recurse -Force }
New-Item -ItemType Directory -Force -Path $www | Out-Null
Copy-Item (Join-Path $repo "clients\react-native\dist\*") $www -Recurse -Force

Write-Host "[2/4] Publish LocalMesh ($Rid, self-contained) -> localmesh\"
& dotnet publish (Join-Path $repo "memex\Memex.LocalMesh\Memex.LocalMesh.csproj") `
    -c Release -r $Rid --self-contained true -o (Join-Path $app "localmesh")

Write-Host "[3/4] Build the WebView2 shell -> MeshWeaver.exe"
& dotnet publish "$PSScriptRoot\MeshWeaver.Windows.csproj" `
    -c Release -r $Rid --self-contained true -o $app

Write-Host "[4/4] Done -> $app\MeshWeaver.exe"
Get-Item (Join-Path $app "MeshWeaver.exe") | Select-Object FullName, Length

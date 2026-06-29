# Aggregates per-test-class memory deltas from the cross-process MEM trace
# emitted by MonolithMeshTestBase (TestMemTrace at INIT_MEM / DISPOSE_MEM).
# The trace lives at $env:TEMP\meshweaver-test-trace.log; this script does
# NOT need any external profiler, it parses the same data that drives the
# OOM watchdog and aggregates it into a per-class leak ranking.

[CmdletBinding()]
param(
    [string]$TracePath = "$env:TEMP\meshweaver-test-trace.log",
    [switch]$All
)

if (-not (Test-Path $TracePath)) {
    Write-Error "Trace file not found: $TracePath. Run a dotnet test cycle first."
    return
}

$lines = Get-Content -LiteralPath $TracePath
$disposeMem = $lines | Where-Object { $_ -match 'DISPOSE_MEM' }

if (-not $disposeMem) {
    Write-Warning "No DISPOSE_MEM lines in $TracePath."
    return
}

$rx = '(?<class>[A-Za-z0-9_]+)\] DISPOSE_MEM managed=(?<mb>\-?\d+)MiB Δ(?<delta>[+\-]\d+)MiB rss=(?<rss>\-?\d+)MiB Δ(?<rssDelta>[+\-]\d+)MiB'

$rows = foreach ($line in $disposeMem) {
    if ($line -match $rx) {
        [PSCustomObject]@{
            Class    = $Matches.class
            Managed  = [int]$Matches.mb
            Delta    = [int]$Matches.delta
            Rss      = [int]$Matches.rss
            RssDelta = [int]$Matches.rssDelta
        }
    }
}

$grouped = $rows | Group-Object Class | ForEach-Object {
    $g = $_
    [PSCustomObject]@{
        Class           = $g.Name
        Runs            = $g.Count
        TotalManagedMib = ($g.Group | Measure-Object Delta -Sum).Sum
        MaxManagedMib   = ($g.Group | Measure-Object Delta -Maximum).Maximum
        TotalRssMib     = ($g.Group | Measure-Object RssDelta -Sum).Sum
        MaxRssMib       = ($g.Group | Measure-Object RssDelta -Maximum).Maximum
    }
} | Sort-Object TotalManagedMib -Descending

$lineCount = $lines.Count
$disposeCount = $disposeMem.Count

if ($All) {
    $grouped | Format-Table -AutoSize
} else {
    Write-Output ""
    Write-Output "Top 30 leak suspects by total managed-heap retained:"
    Write-Output ""
    $grouped | Select-Object -First 30 | Format-Table -AutoSize
    Write-Output "Negative TotalManagedMib = class released more than it allocated."
    Write-Output "Positive = retained across runs (leak signal)."
    Write-Output ""
    Write-Output "Source trace: $TracePath ($lineCount lines, $disposeCount DISPOSE_MEM events)"
}

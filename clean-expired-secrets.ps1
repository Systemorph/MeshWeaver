$now = (Get-Date).ToUniversalTime()
$apps = az ad app list --all -o json | ConvertFrom-Json
$total = 0
$errors = 0

foreach ($app in $apps) {
    $expired = $app.passwordCredentials | Where-Object { $_.endDateTime -and [DateTime]$_.endDateTime -lt $now }
    if ($expired) {
        foreach ($s in $expired) {
            Write-Host "Removing secret from '$($app.displayName)' (keyId: $($s.keyId), expired: $($s.endDateTime))..."
            try {
                az ad app credential delete --id $app.appId --key-id $s.keyId 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "  OK" -ForegroundColor Green
                    $total++
                } else {
                    Write-Host "  FAILED (exit code $LASTEXITCODE)" -ForegroundColor Red
                    $errors++
                }
            } catch {
                Write-Host "  ERROR: $_" -ForegroundColor Red
                $errors++
            }
        }
    }
}

Write-Host ""
Write-Host "Done. Removed $total expired secrets. Errors: $errors"

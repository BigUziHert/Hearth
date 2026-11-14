#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name HearthHost -ErrorAction SilentlyContinue
if ($service) {
    & "$env:SystemRoot\System32\sc.exe" config HearthHost start= disabled | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Windows could not disable the service ($LASTEXITCODE)." }
    try {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -InputObject $service -Force
            $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
        }
    } finally { $service.Dispose() }
    & "$env:SystemRoot\System32\sc.exe" delete HearthHost | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Windows could not remove the service registration ($LASTEXITCODE)." }
    $serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\HearthHost'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-Path -LiteralPath $serviceKey) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Test-Path -LiteralPath $serviceKey) { throw 'Windows has not finished removing the old service. Close Services and retry. Hosting is disabled.' }
}
Write-Output 'The legacy service is disabled. Pack releases, members, owner credentials, and service files were preserved. Current Hearth hosting is stopped by quitting Hearth or choosing Stop hosting in the app.'

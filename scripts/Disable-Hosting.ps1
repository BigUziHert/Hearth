#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name HearthHost -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name HearthHost -Force
    (Get-Service HearthHost).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    & "$env:SystemRoot\System32\sc.exe" delete HearthHost | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Windows could not remove the service registration ($LASTEXITCODE)." }
}
Write-Output 'Hosting disabled. Pack releases, members, owner credentials, and service files were preserved.'

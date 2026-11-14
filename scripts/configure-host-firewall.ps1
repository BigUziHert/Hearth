#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProgramPath,
    [Parameter(Mandatory=$true)][ValidateRange(1024,65535)][int]$Port,
    [Parameter(Mandatory=$true)][ValidatePattern('^Hearth\.Hosting\.[a-f0-9]{24}$')][string]$RuleName
)
$ErrorActionPreference = 'Stop'
try {
    $program = [IO.Path]::GetFullPath($ProgramPath)
    if (-not [IO.Path]::IsPathRooted($ProgramPath) -or -not (Test-Path -LiteralPath $program -PathType Leaf)) { throw 'The Hearth host executable is missing.' }
    if ([IO.Path]::GetFileName($program) -ne 'Hearth.Host.exe') { throw 'Only the Hearth host executable can receive this rule.' }
    $existing = Get-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue
    if ($existing) {
        # Never replace a differently scoped rule with a matching name.
        $application = $existing | Get-NetFirewallApplicationFilter
        $ports = $existing | Get-NetFirewallPortFilter
        if ($application.Program -ne $program -or $ports.LocalPort -ne [string]$Port -or $ports.Protocol -ne 'TCP') { throw 'A different firewall rule already uses this name.' }
        $existing | Set-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -Profile Any
    } else {
        New-NetFirewallRule -Name $RuleName -DisplayName 'Hearth modpack hosting' -Description 'Allows encrypted modpack sharing only while the Hearth host is running.' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol TCP -LocalPort $Port -Program $program | Out-Null
    }
    exit 0
} catch { Write-Error $_; exit 1 }

#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InstallDir,
    [string]$DataDir = (Join-Path $env:ProgramData 'Hearth\Host'),
    [string]$TokenFile,
    [ValidateRange(1024,65535)][int]$Port = 17842,
    [switch]$UpdateOnly
)
$ErrorActionPreference = 'Stop'
$serviceName = 'HearthHost'
$hearthRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Hearth'))
$serviceDir = Join-Path $hearthRoot 'Service'
$sourceDir = Join-Path ([IO.Path]::GetFullPath($InstallDir)) 'host'
$DataDir = [IO.Path]::GetFullPath($DataDir)
$oldService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$previousSettings = if ($oldService) { Get-CimInstance Win32_Service -Filter "Name='HearthHost'" } else { $null }
if ($UpdateOnly -and -not $oldService) { Write-Output 'Hosting is not enabled. No service update needed.'; exit 0 }
if ($UpdateOnly) {
    $registered = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\HearthHost').ImagePath
    if ($registered -match '--data\s+"([^"]+)"') { $DataDir = [IO.Path]::GetFullPath($Matches[1]) }
    if ($registered -match '--port\s+(\d+)') { $Port = [int]$Matches[1] }
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceDir 'Hearth.Host.exe'))) { throw 'Hearth host payload is missing. Reinstall Hearth and try again.' }
if (-not $UpdateOnly -and -not (Test-Path -LiteralPath (Join-Path $DataDir 'host.json')) -and (-not $TokenFile -or -not (Test-Path -LiteralPath $TokenFile))) { throw 'An owner credential file is required when hosting is first enabled.' }

function Set-ProtectedDirectory([string]$Path, [string]$ServiceRights) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    if ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not allowed: $Path" }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagate = [Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', $inherit, $propagate, 'Allow')
        $acl.AddAccessRule($rule)
    }
    $serviceRule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-19')), $ServiceRights, $inherit, $propagate, 'Allow')
    $acl.AddAccessRule($serviceRule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Invoke-ServiceCommand([string[]]$Arguments) {
    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Windows service command failed ($LASTEXITCODE): $($Arguments[0])" }
}

# Never execute a service from the per-user installation directory. Only administrators
# and SYSTEM can change this independent copy; LocalService can only read/execute it.
New-Item -ItemType Directory -Path $hearthRoot -Force | Out-Null
if ((Get-Item -LiteralPath $hearthRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The Hearth service root cannot be a reparse point.' }
Set-ProtectedDirectory $hearthRoot 'ReadAndExecute'
Start-Transcript -LiteralPath (Join-Path $hearthRoot 'hosting-setup.log') -Append -Force | Out-Null
Set-ProtectedDirectory $DataDir 'Modify'
$stageDir = Join-Path $hearthRoot ('Service.stage-' + [Guid]::NewGuid().ToString('N'))
$backupDir = Join-Path $hearthRoot ('Service.backup-' + [Guid]::NewGuid().ToString('N'))
$wasRunning = $oldService -and $oldService.Status -eq 'Running'
$movedPrevious = $false
$installedNew = $false
$succeeded = $false
try {
    Set-ProtectedDirectory $stageDir 'ReadAndExecute'
    Copy-Item -Path (Join-Path $sourceDir '*') -Destination $stageDir -Recurse -Force
    if ($oldService) { Stop-Service -Name $serviceName -Force; (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    if (Test-Path -LiteralPath $serviceDir) {
        if ((Get-Item -LiteralPath $serviceDir).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The service directory cannot be a reparse point.' }
        Move-Item -LiteralPath $serviceDir -Destination $backupDir
        $movedPrevious = $true
    }
    Move-Item -LiteralPath $stageDir -Destination $serviceDir
    $installedNew = $true
    Set-ProtectedDirectory $serviceDir 'ReadAndExecute'
    $exe = Join-Path $serviceDir 'Hearth.Host.exe'
    if (-not (Test-Path -LiteralPath (Join-Path $DataDir 'host.json'))) {
        & $exe --init --data $DataDir --owner-token-file $TokenFile --port $Port
        if ($LASTEXITCODE -ne 0) { throw "Host initialization failed ($LASTEXITCODE)." }
    }
    # Children inherit the protected data directory ACL established before initialization.
    Set-ProtectedDirectory $DataDir 'Modify'
    $binPath = '"' + $exe + '" --data "' + $DataDir + '" --port ' + $Port
    if ($oldService) {
        # CIM passes the path as a structured string. PowerShell 5 strips embedded quotes
        # when this is passed through native sc.exe, breaking paths containing spaces.
        $registeredService = Get-CimInstance Win32_Service -Filter "Name='HearthHost'"
        $changed = Invoke-CimMethod -InputObject $registeredService -MethodName Change -Arguments @{ PathName=$binPath; StartName='NT AUTHORITY\LocalService'; StartMode='Automatic' }
        if ($changed.ReturnValue -ne 0) { throw "Windows could not update the service configuration ($($changed.ReturnValue))." }
    } else {
        New-Service -Name $serviceName -BinaryPathName $binPath -StartupType Automatic -DisplayName 'Hearth Modpack Host' | Out-Null
    }
    Invoke-ServiceCommand @('config',$serviceName,'start=','delayed-auto','obj=','NT AUTHORITY\LocalService')
    Invoke-ServiceCommand @('description',$serviceName,'Private modpack releases for Hearth. Minecraft world hosting is separate.')
    Invoke-ServiceCommand @('failure',$serviceName,'reset=','86400','actions=','restart/5000/restart/15000/restart/60000')
    if (-not $UpdateOnly -or $wasRunning) {
        Start-Service -Name $serviceName
        (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
        Write-Output 'Hearth hosting is running and will start automatically after Windows reboots.'
    } else { Write-Output 'Hearth hosting service updated; its stopped state was preserved.' }
    $succeeded = $true
} catch {
    $setupFailure = $_
    Add-Content -LiteralPath (Join-Path $hearthRoot 'hosting-setup.log') -Value $setupFailure.Exception.ToString()
    try {
        if ($movedPrevious) {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $serviceDir) {
                $failedDir = Join-Path $hearthRoot ('Service.failed-' + [Guid]::NewGuid().ToString('N'))
                Move-Item -LiteralPath $serviceDir -Destination $failedDir
            }
            Move-Item -LiteralPath $backupDir -Destination $serviceDir
        }
        if ($previousSettings) {
            $serviceToRestore = Get-CimInstance Win32_Service -Filter "Name='HearthHost'"
            $null = Invoke-CimMethod -InputObject $serviceToRestore -MethodName Change -Arguments @{ PathName=$previousSettings.PathName; StartName=$previousSettings.StartName }
        }
        # If even the first rename failed, the original executable is still in place.
        if ($wasRunning -and (Get-Service $serviceName).Status -ne 'Running') { Start-Service -Name $serviceName }
    } catch {
        Add-Content -LiteralPath (Join-Path $hearthRoot 'hosting-setup.log') -Value ('Rollback requires attention: ' + $_.Exception.ToString())
    }
    throw $setupFailure
} finally {
    if ($TokenFile -and (Test-Path -LiteralPath $TokenFile)) { Remove-Item -LiteralPath $TokenFile -Force }
    # Every recursively removed directory is constructed directly under this fixed root.
    $cleanup = @($stageDir)
    if ($succeeded) { $cleanup += $backupDir }
    foreach ($temporary in $cleanup) {
        $resolved = [IO.Path]::GetFullPath($temporary)
        if (-not $resolved.StartsWith($hearthRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the Hearth service root.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    Stop-Transcript | Out-Null
}

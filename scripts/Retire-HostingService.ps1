#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Retire')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Retire')]
    [ValidatePattern('^S-1-(5-21|12-1)-\d+-\d+-\d+-\d+$')][string]$OwnerSid,
    [Parameter(Mandatory = $true, ParameterSetName = 'Identity')][switch]$GetCurrentUserSid,
    [Parameter(Mandatory = $true, ParameterSetName = 'Identity')][string]$SidFile
)
$ErrorActionPreference = 'Stop'

# Setup runs this branch as the original, unelevated user before requesting UAC.
# Never infer the data owner from the account used to approve the UAC prompt.
if ($GetCurrentUserSid) {
    [IO.File]::WriteAllText($SidFile, [Security.Principal.WindowsIdentity]::GetCurrent().User.Value, [Text.Encoding]::ASCII)
    exit 0
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Retiring the old Hearth Windows service requires administrator permission.'
}
$owner = New-Object Security.Principal.SecurityIdentifier($OwnerSid)
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\HearthHost'
$root = Join-Path $env:ProgramData 'Hearth'
$descriptorPath = Join-Path $root 'retired-host.json'
if (-not (Test-Path -LiteralPath $serviceKey)) {
    Write-Output 'No independent Hearth service is registered. Existing data was left unchanged.'
    exit 0
}

function Assert-PlainPath([string]$Path) {
    $entry = Get-Item -LiteralPath $Path -Force
    while ($null -ne $entry) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Cannot migrate a reparse-point path: $Path" }
        $entry = $entry.Parent
    }
}
function Grant-Access([string]$Path, [Security.AccessControl.FileSystemRights]$Rights, [bool]$Inherit) {
    $existing = Get-Acl -LiteralPath $Path
    # Persist only the DACL. Get-Acl objects can include owner/audit state that
    # Set-Acl would otherwise try to rewrite when inheritance is protected.
    if ((Get-Item -LiteralPath $Path -Force).PSIsContainer) { $acl = New-Object Security.AccessControl.DirectorySecurity }
    else { $acl = New-Object Security.AccessControl.FileSecurity }
    $acl.SetSecurityDescriptorBinaryForm($existing.GetSecurityDescriptorBinaryForm(), [Security.AccessControl.AccessControlSections]::Access)
    $inheritance = [Security.AccessControl.InheritanceFlags]::None
    if ($Inherit) { $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' }
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($owner, $Rights, $inheritance, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    $item = Get-Item -LiteralPath $Path -Force
    if ($PSVersionTable.PSEdition -eq 'Desktop') { $item.SetAccessControl($acl) }
    else { [IO.FileSystemAclExtensions]::SetAccessControl($item, $acl) }
}

# Use Windows' own argument parser so a custom registered --data / --port is kept.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HearthServiceArguments {
    [DllImport("shell32.dll", SetLastError=true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argc);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public static string[] Parse(string commandLine) {
        int count; IntPtr memory = CommandLineToArgvW(commandLine, out count);
        if (memory == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size));
            return result;
        } finally { LocalFree(memory); }
    }
}
'@
$registered = Get-ItemProperty -LiteralPath $serviceKey
$arguments = [HearthServiceArguments]::Parse([Environment]::ExpandEnvironmentVariables($registered.ImagePath))
$dataDirectory = Join-Path $root 'Host'
$port = $null
for ($index = 1; $index -lt $arguments.Length; $index++) {
    if ($arguments[$index] -eq '--data' -or $arguments[$index] -eq '--port') {
        if ($index + 1 -ge $arguments.Length) { throw 'The registered Hearth service has incomplete arguments.' }
        if ($arguments[$index] -eq '--data') { $dataDirectory = $arguments[++$index] }
        else { $port = [int]$arguments[++$index] }
    }
}
if (-not [IO.Path]::IsPathRooted($dataDirectory)) { throw 'The registered host data path must be absolute.' }
$dataDirectory = [IO.Path]::GetFullPath($dataDirectory)
Assert-PlainPath $root
Assert-PlainPath $dataDirectory
$settingsPath = Join-Path $dataDirectory 'host.json'
Assert-PlainPath $settingsPath
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
if (-not $settings.hostId -or -not $settings.ownerTokenHash) { throw 'The existing host settings could not be verified. No data was changed.' }
if ($null -eq $port) { $port = if ($settings.port) { [int]$settings.port } else { 17842 } }
if ($port -lt 1024 -or $port -gt 65535) { throw 'The registered host port is invalid.' }
if (Test-Path -LiteralPath $descriptorPath) {
    Assert-PlainPath $descriptorPath
    $previous = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
    if ($previous.ownerSid -ne $OwnerSid -or $previous.dataDirectory -ne $dataDirectory) {
        throw 'An earlier migration belongs to another Windows account or host data folder. Resolve that migration before updating.'
    }
}

# Reject links inside the data before propagating permissions. No data is moved or removed.
$directories = New-Object 'Collections.Generic.Stack[string]'
$protectedEntries = New-Object 'Collections.Generic.List[IO.FileSystemInfo]'
$directories.Push($dataDirectory)
while ($directories.Count -gt 0) {
    foreach ($entry in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Cannot migrate linked host data: $($entry.FullName)" }
        if ((Get-Acl -LiteralPath $entry.FullName).AreAccessRulesProtected) { $protectedEntries.Add($entry) }
        if ($entry.PSIsContainer) { $directories.Push($entry.FullName) }
    }
}
$service = Get-Service -Name HearthHost
try { $wasRunning = $service.Status -ne 'Stopped' } finally { $service.Dispose() }
$wasEnabled = $registered.Start -ne 4
if ($previous) {
    $wasRunning = $wasRunning -or $previous.wasRunning
    $wasEnabled = $wasEnabled -or $previous.wasEnabled
}

# The current user receives only traversal of the protected Hearth root and access
# to this host's data. Service executables and other users' ACLs remain untouched.
Grant-Access $root ([Security.AccessControl.FileSystemRights]::ReadAndExecute) $false
Grant-Access $dataDirectory ([Security.AccessControl.FileSystemRights]::Modify) $true
foreach ($entry in $protectedEntries) {
    # A custom host may protect a child ACL. Add this user's permission without
    # re-enabling inheritance or dropping any existing owner/service access.
    Grant-Access $entry.FullName ([Security.AccessControl.FileSystemRights]::Modify) ([bool]$entry.PSIsContainer)
}

$descriptor = [ordered]@{
    formatVersion = 1
    ownerSid = $OwnerSid
    dataDirectory = $dataDirectory
    port = $port
    wasRunning = [bool]$wasRunning
    wasEnabled = [bool]$wasEnabled
    retiredAtUtc = [DateTime]::UtcNow.ToString('o')
}
$pending = Join-Path $root ('retired-host-' + [Guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllText($pending, ($descriptor | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$descriptorAcl = New-Object Security.AccessControl.FileSecurity
$descriptorAcl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'Allow')
    $descriptorAcl.AddAccessRule($rule)
}
$descriptorAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($owner, 'Read', 'Allow')))
Set-Acl -LiteralPath $pending -AclObject $descriptorAcl

# Save the original enabled state before changing the service. A retry after an
# interrupted retirement can then retain it. The launcher refuses adoption while
# a legacy service registration survives, even when this descriptor is present.
if (Test-Path -LiteralPath $descriptorPath) { [IO.File]::Replace($pending, $descriptorPath, $null) }
else { [IO.File]::Move($pending, $descriptorPath) }

# Disable first: a failed stop or interrupted installer must never re-enable an
# independent host at reboot. Cancellation before this helper runs makes no change.
& "$env:SystemRoot\System32\sc.exe" config HearthHost start= disabled | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Windows could not disable the legacy service ($LASTEXITCODE)." }
$service = Get-Service -Name HearthHost
try {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -InputObject $service -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
} finally { $service.Dispose() }
& "$env:SystemRoot\System32\sc.exe" delete HearthHost | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Windows could not remove the legacy service ($LASTEXITCODE). Hosting remains disabled; retry the update." }
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while ((Test-Path -LiteralPath $serviceKey) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
if (Test-Path -LiteralPath $serviceKey) { throw 'Windows has not finished removing the old service. Close Services, restart Windows if needed, and retry the update. Hosting is disabled.' }
Write-Output 'The independent host service was retired. Packs, members, releases, and owner credentials were preserved.'

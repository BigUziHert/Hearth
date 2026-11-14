#Requires -Version 5.1
[CmdletBinding()]
param([switch]$SkipTests, [switch]$SkipInstaller, [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.5', [string]$IsccPath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repo
$dotnetExe = Join-Path $repo 'tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $dotnetExe) { $env:DOTNET_ROOT = Split-Path -Parent $dotnetExe; $env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH }
else { $dotnetExe = 'dotnet' }
function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
Invoke-Dotnet @('build','Hearth.sln','-c','Release',('-p:Version=' + $Version))
if (-not $SkipTests) {
    Invoke-Dotnet @('run','--project','tests/Hearth.Tests','-c','Release','--no-build')
    Invoke-Dotnet @('run','--project','tests/Hearth.App.Tests','-c','Release','--no-build')
    Invoke-Dotnet @('run','--project','tests/Hearth.Lifecycle.Tests','-c','Release','--no-build')
}
$publish = Join-Path $repo 'artifacts\publish'
foreach ($parent in @((Join-Path $repo 'artifacts'),$publish)) {
    if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Publish output cannot be a reparse point.' }
}
# Publish into fresh task-owned directories so removed dependencies cannot leak into a release.
foreach ($component in @('app','host')) {
    $target = [IO.Path]::GetFullPath((Join-Path $publish $component))
    if (-not $target.StartsWith([IO.Path]::GetFullPath($publish) + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publish cleanup path.' }
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Publish component cannot be a reparse point.' }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
Invoke-Dotnet @('publish','src/Hearth.App','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=false',('-p:Version=' + $Version),'-o',(Join-Path $publish 'app'))
Invoke-Dotnet @('publish','src/Hearth.Host','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=false',('-p:Version=' + $Version),'-o',(Join-Path $publish 'host'))
if ($SkipInstaller) { exit 0 }
if (-not $IsccPath) {
    $candidates = @((Join-Path $repo 'tools\inno\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'))
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath) { throw 'Install Inno Setup from https://jrsoftware.org/isdl.php or pass -IsccPath.' }
& $IsccPath ('/DAppVersion=' + $Version) (Join-Path $repo 'scripts\Hearth.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compiler failed ($LASTEXITCODE)." }
$installer = Join-Path $repo ('artifacts\Hearth-Setup-' + $Version + '-win-x64.exe')
$checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$source = & (Join-Path $PSScriptRoot 'package-source.ps1') -Version $Version
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $repo 'artifacts\SHA256SUMS.txt') -Value @(($checksum + '  ' + [IO.Path]::GetFileName($installer)),($sourceHash + '  ' + [IO.Path]::GetFileName($source))) -Encoding ASCII
& (Join-Path $PSScriptRoot 'verify-release.ps1') -Version $Version
Write-Output ('Installer: ' + $installer)
Write-Output ('SHA-256: ' + $checksum)

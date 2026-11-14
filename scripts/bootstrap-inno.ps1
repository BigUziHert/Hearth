#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolRoot = Join-Path $repo 'tools'
$innoRoot = Join-Path $toolRoot 'inno'
$compiler = Join-Path $innoRoot 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }
# Pinned official compiler bootstrap. Updating these constants is an intentional build-tool change.
$uri = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe'
$expected = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
$download = Join-Path $toolRoot 'innosetup-7.1.1-x64.exe'
if (-not (Test-Path -LiteralPath $download) -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    Invoke-WebRequest -Uri $uri -OutFile $download -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'The Inno Setup bootstrap did not match its pinned SHA-256.' }
if ((Get-AuthenticodeSignature -LiteralPath $download).Status -ne 'Valid') { throw 'The official Inno Setup bootstrap signature could not be verified.' }
$setup = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + $innoRoot + '"')) -WindowStyle Hidden -PassThru -Wait
if ($setup.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw "Inno Setup bootstrap failed ($($setup.ExitCode))." }
Write-Output $compiler

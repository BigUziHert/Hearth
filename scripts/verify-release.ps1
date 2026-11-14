#Requires -Version 5.1
[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.10')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo 'artifacts'
$installerName = 'Hearth-Setup-' + $Version + '-win-x64.exe'
$sourceName = 'Hearth-Source-' + $Version + '.zip'
$sums = Get-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt')
foreach ($name in @($installerName,$sourceName)) {
    $path = Join-Path $output $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file missing: $name" }
    $expectedLine = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
    if (@($sums | Where-Object { $_ -eq $expectedLine }).Count -ne 1) { throw "Missing or inconsistent SHA256SUMS entry: $name" }
}
$peVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output $installerName)).ProductVersion
$parsed = [Version]($peVersion.Split('+')[0])
if ($parsed.ToString(3) -ne $Version) { throw "Installer version $peVersion does not match release $Version." }
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output $sourceName))
try {
    $names = @($archive.Entries | ForEach-Object FullName)
    $versionEntry = $archive.GetEntry('Directory.Build.props')
    if (-not $versionEntry) { throw 'Source archive lacks its version metadata.' }
    $versionReader = New-Object IO.StreamReader($versionEntry.Open())
    try { $sourceVersion = ([xml]($versionReader.ReadToEnd())).Project.PropertyGroup.Version } finally { $versionReader.Dispose() }
    if ($sourceVersion -ne $Version) { throw "Source version $sourceVersion does not match release $Version." }
    foreach ($required in @('README.md','Hearth.sln','global.json','src/Hearth.App/MainWindow.xaml','scripts/build.ps1','.github/workflows/windows-release.yml')) {
        if ($required -notin $names) { throw "Source archive is missing $required" }
    }
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        if ($name -match '(^|/)(bin|obj|artifacts|tools|test-data|\.git|\.vs|node_modules|__pycache__)(/|$)' -or $name -match '(^|/)(profile\.bin|host\.json|\.env[^/]*|[^/]+\.(key|pem|pfx|user|tmp|log|pyc))$' -or $name.Contains('..') -or $name.Contains(':')) { throw "Private or unsafe source archive entry: $name" }
        if ($entry.Length -le 2MB -and $name -match '\.(cs|xaml|json|md|txt|ps1|py|yml|yaml|props|csproj|iss)$') {
            $reader = New-Object IO.StreamReader($entry.Open())
            try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($content -match '(ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{70,}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----)' -or $content -match '(?i)[A-Z]:\\Users\\[^\\\s]+\\') { throw "Possible credential or personal local path found in $name" }
        }
    }
} finally { $archive.Dispose() }
Write-Output "Verified installer version, release hashes, and source-only archive for Hearth $Version."

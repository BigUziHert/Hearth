#Requires -Version 5.1
[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.4')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo 'artifacts'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$target = Join-Path $output ('Hearth-Source-' + $Version + '.zip')
$temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$files = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
foreach ($name in @('.gitignore','Directory.Build.props','global.json','Hearth.sln','README.md','LICENSE','LICENSE.md')) {
    $path = Join-Path $repo $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add((Get-Item -LiteralPath $path)) }
}
$excludedSegments = @('bin','obj','.git','artifacts','tools','test-data','.vs','node_modules','__pycache__')
foreach ($directory in @('src','scripts','docs','tests','.github')) {
    $path = Join-Path $repo $directory
    if (-not (Test-Path -LiteralPath $path)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $path -File -Recurse -Force) {
        $relative = $file.FullName.Substring($repo.Length + 1).Replace('\','/')
        if (@($relative.Split('/') | Where-Object { $_ -in $excludedSegments }).Count -gt 0) { continue }
        if ($file.Extension -in @('.user','.pyc','.tmp','.log','.bin','.key','.pfx','.pem','.env') -or $file.Name -like '.env*' -or $file.Name -in @('host.json','profile.bin')) { throw "Private or generated file found in a source directory: $relative" }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source packaging refuses reparse points: $relative" }
        for ($parent = $file.Directory; $null -ne $parent -and $parent.FullName.StartsWith($repo,[StringComparison]::OrdinalIgnoreCase); $parent = $parent.Parent) {
            if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source packaging refuses a linked parent directory: $relative" }
            if ($parent.FullName -eq $repo) { break }
        }
        $files.Add($file)
    }
}
try {
    $archive = [IO.Compression.ZipFile]::Open($temporary,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in ($files | Sort-Object FullName -Unique)) {
            $relative = $file.FullName.Substring($repo.Length + 1).Replace('\','/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $target -Force
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Write-Output $target

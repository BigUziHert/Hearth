#Requires -Version 5.1
# These checks exercise real Windows argument parsing and ACLs in an isolated
# directory. They do not install, stop, remove, or otherwise access an SCM service.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptPath = Join-Path $repo 'scripts\Retire-HostingService.ps1'
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    Invoke-Expression $function.Extent.Text
}
$parser = $ast.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-Type' }, $true)
Invoke-Expression $parser.Extent.Text
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Output "PASS: $Message"
}
$arguments = [HearthServiceArguments]::Parse('"C:\ProgramData\Hearth\Service\Hearth.Host.exe" --data "D:\Custom host data" --port 18888')
Check ($arguments.Length -eq 5 -and $arguments[2] -eq 'D:\Custom host data' -and $arguments[4] -eq '18888') 'Preserve custom registered data folder and port'

$fixture = Join-Path $repo ('artifacts\migration-helpers-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$sidFile = Join-Path $fixture 'sid.txt'
& $scriptPath -GetCurrentUserSid -SidFile $sidFile
Check ([IO.File]::ReadAllText($sidFile) -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) 'Capture original Windows user SID without elevation'

# Add permissions for a synthetic domain user SID, so the assertions cannot be
# satisfied accidentally by this test process's existing inherited FullControl.
$owner = New-Object Security.Principal.SecurityIdentifier('S-1-5-21-999999999-888888888-777777777-1234')
$data = Join-Path $fixture 'data'
$protected = Join-Path $data 'protected'
New-Item -ItemType Directory -Path $protected -Force | Out-Null
$sentinel = Join-Path $protected 'state.json'
[IO.File]::WriteAllText($sentinel, '{"member":"preserved"}')
$before = [IO.File]::ReadAllBytes($sentinel)
$acl = Get-Acl -LiteralPath $protected
$acl.SetAccessRuleProtection($true, $true)
Set-Acl -LiteralPath $protected -AclObject $acl
$originalRules = @((Get-Acl -LiteralPath $protected).Access | ForEach-Object { $_.IdentityReference.Value + ':' + $_.FileSystemRights + ':' + $_.AccessControlType })
Grant-Access $fixture ([Security.AccessControl.FileSystemRights]::ReadAndExecute) $false
Grant-Access $data ([Security.AccessControl.FileSystemRights]::Modify) $true
Grant-Access $protected ([Security.AccessControl.FileSystemRights]::Modify) $true
$afterAcl = Get-Acl -LiteralPath $protected
$afterRules = @($afterAcl.Access | ForEach-Object { $_.IdentityReference.Value + ':' + $_.FileSystemRights + ':' + $_.AccessControlType })
Check ($afterAcl.AreAccessRulesProtected) 'Keep protected inheritance on custom child folders'
Check (@($originalRules | Where-Object { $_ -notin $afterRules }).Count -eq 0) 'Keep all unrelated existing access rules'
$ownerRules = @($afterAcl.Access | Where-Object { $_.IdentityReference.Value -eq $owner.Value })
Check (@($ownerRules | Where-Object { ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify }).Count -gt 0) 'Grant the intended owner Modify on protected data'
$rootRule = @((Get-Acl -LiteralPath $fixture).Access | Where-Object { $_.IdentityReference.Value -eq $owner.Value })
Check ($rootRule.Count -eq 1 -and $rootRule[0].InheritanceFlags -eq 'None' -and ($rootRule[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -eq 0) 'Keep root access read-only and non-inheriting'
Check ([Convert]::ToBase64String($before) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($sentinel))) 'Preserve member-state bytes during ACL migration'
Assert-PlainPath $data
Assert-PlainPath $sentinel
Check $true 'Accept ordinary data directories and files'
$link = Join-Path $fixture 'linked-data'
New-Item -ItemType Junction -Path $link -Target $data | Out-Null
$rejected = $false
try { Assert-PlainPath $link } catch { $rejected = $_.Exception.Message -like '*reparse-point*' }
Check $rejected 'Reject linked migration paths'
# Remove only the created junction itself; keep fixture files as evidence.
[IO.Directory]::Delete($link)
Write-Output "$checks migration helper checks passed. Fixture: $fixture"

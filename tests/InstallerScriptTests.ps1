#Requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$installScript = Join-Path $root 'tools\Install-TinyHwBar.ps1'
$uninstallScript = Join-Path $root 'tools\Uninstall-TinyHwBar.ps1'
$iconScript = Join-Path $root 'tools\GenerateIcon.ps1'
$scriptAsts = @{}

foreach ($scriptPath in @($installScript, $uninstallScript, $iconScript)) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$errors)
    if (@($errors).Count -ne 0) {
        throw "PowerShell parser errors in ${scriptPath}: $($errors.Message -join ' | ')"
    }
    $scriptAsts[$scriptPath] = $ast
}

function Assert-CommandElementCount {
    param(
        [System.Management.Automation.Language.ScriptBlockAst]$Ast,
        [string]$CommandName,
        [int]$ExpectedCount)

    $calls = @($Ast.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq $CommandName
        },
        $true))
    if ($calls.Count -eq 0) {
        throw "No call was found for $CommandName."
    }
    foreach ($call in $calls) {
        if ($call.CommandElements.Count -ne $ExpectedCount) {
            throw ("Wrong argument count for $CommandName at line " +
                "$($call.Extent.StartLineNumber): expected $ExpectedCount command elements, " +
                "found $($call.CommandElements.Count).")
        }
    }
}

Assert-CommandElementCount $scriptAsts[$installScript] 'Set-UninstallRegistryEntry' 5
Assert-CommandElementCount $scriptAsts[$installScript] 'Restore-RegistryKeySnapshot' 6
Assert-CommandElementCount $scriptAsts[$installScript] 'Assert-UninstallKeyOwnedOrMissing' 5
Assert-CommandElementCount $scriptAsts[$uninstallScript] 'Remove-OwnedUninstallKey' 5
Assert-CommandElementCount $scriptAsts[$uninstallScript] 'Restore-RegistryKeySnapshot' 6

foreach ($scriptPath in @($installScript, $uninstallScript)) {
    $source = [IO.File]::ReadAllText($scriptPath)
    $maintenanceMutexIndex = $source.IndexOf(
        "'Global\TinyHwBar.Maintenance.'",
        [StringComparison]::Ordinal)
    $maintenanceAcquireIndex = $source.IndexOf(
        '[Threading.Mutex]::new($false, $maintenanceMutexName)',
        [StringComparison]::Ordinal)
    $singletonAcquireIndex = $source.IndexOf(
        '[Threading.Mutex]::new($false, $singletonMutexName)',
        [StringComparison]::Ordinal)
    $waitCount = [regex]::Matches(
        $source,
        [regex]::Escape('.WaitOne(0, $false)')).Count
    if ($source.IndexOf("'Local\TinyHwBar.Singleton'", [StringComparison]::Ordinal) -lt 0 -or
        $maintenanceMutexIndex -lt 0 -or
        $maintenanceAcquireIndex -lt 0 -or
        $singletonAcquireIndex -le $maintenanceAcquireIndex -or
        $waitCount -lt 2 -or
        $source.IndexOf("Name = 'TinyHwBar.exe'", [StringComparison]::Ordinal) -lt 0) {
        throw "The ordered cross-session maintenance lock, app lock, or post-lock process check is missing: $scriptPath"
    }
}

function Get-SelectedFunctionsScriptBlock {
    param(
        [System.Management.Automation.Language.ScriptBlockAst]$Ast,
        [string[]]$Names)

    $definitions = @($Ast.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $Names -contains $node.Name
        },
        $true) | Sort-Object { $Names.IndexOf($_.Name) })
    if ($definitions.Count -ne $Names.Count) {
        throw "Could not extract every required helper function."
    }
    return [ScriptBlock]::Create(($definitions.Extent.Text -join "`n"))
}

$registryFunctionNames = @(
    'Test-PathEquals',
    'Get-SnapshotValue',
    'Test-UninstallKeyOwned',
    'Test-RegistryValueEntryEquals',
    'Test-RegistrySnapshotsEqual',
    'Test-UninstallKeyTransitionOwned')
. (Get-SelectedFunctionsScriptBlock $scriptAsts[$installScript] $registryFunctionNames)
$uninstallValueNames = @(
    'DisplayName',
    'DisplayVersion',
    'Publisher',
    'InstallLocation',
    'UninstallString',
    'NoModify',
    'NoRepair')

function New-RegistryEntry {
    param(
        [string]$Name,
        $Value,
        [Microsoft.Win32.RegistryValueKind]$Kind)

    return [pscustomobject]@{ Name = $Name; Value = $Value; Kind = $Kind }
}

function New-UninstallValues {
    param([string]$Version, [string]$InstallRoot, [string]$Command)

    return @(
        (New-RegistryEntry 'DisplayName' 'TinyHwBar' String),
        (New-RegistryEntry 'DisplayVersion' $Version String),
        (New-RegistryEntry 'Publisher' 'TinyHwBar contributors' String),
        (New-RegistryEntry 'InstallLocation' $InstallRoot String),
        (New-RegistryEntry 'UninstallString' $Command String),
        (New-RegistryEntry 'NoModify' 1 DWord),
        (New-RegistryEntry 'NoRepair' 1 DWord))
}

$expectedRoot = 'C:\Owned\TinyHwBar'
$expectedCommand = 'expected-uninstall-command'
$oldValues = New-UninstallValues '1.0.0' $expectedRoot $expectedCommand
$newValues = New-UninstallValues '2.0.0' $expectedRoot $expectedCommand
$oldSnapshot = [pscustomobject]@{
    Exists = $true
    Values = $oldValues
    SubKeyNames = @()
}
$missingSnapshot = [pscustomobject]@{
    Exists = $false
    Values = @()
    SubKeyNames = @()
}

if (-not (Test-UninstallKeyOwned `
    $oldSnapshot `
    $expectedRoot `
    $expectedCommand `
    '1.0.0')) {
    throw 'The exact owned uninstall snapshot was rejected.'
}
$extraSnapshot = [pscustomobject]@{
    Exists = $true
    Values = @($oldValues + (New-RegistryEntry 'Extra' 'x' String))
    SubKeyNames = @()
}
if (Test-UninstallKeyOwned $extraSnapshot $expectedRoot $expectedCommand '1.0.0') {
    throw 'An uninstall snapshot with an extra value was accepted.'
}
$partialNewSnapshot = [pscustomobject]@{
    Exists = $true
    Values = @($newValues[0..3])
    SubKeyNames = @()
}
if (-not (Test-UninstallKeyTransitionOwned `
    $partialNewSnapshot `
    $missingSnapshot `
    $expectedRoot `
    $expectedCommand `
    '2.0.0')) {
    throw 'A safe partial new-key transition was rejected.'
}
$mixedValues = @()
for ($index = 0; $index -lt $newValues.Count; $index++) {
    if ($index -lt 3) {
        $mixedValues += $newValues[$index]
    }
    else {
        $mixedValues += $oldValues[$index]
    }
}
$mixedSnapshot = [pscustomobject]@{
    Exists = $true
    Values = $mixedValues
    SubKeyNames = @()
}
if (-not (Test-UninstallKeyTransitionOwned `
    $mixedSnapshot `
    $oldSnapshot `
    $expectedRoot `
    $expectedCommand `
    '2.0.0')) {
    throw 'A safe old/new uninstall-key transition was rejected.'
}

$fileFunctionNames = @(
    'Assert-NotReparsePoint',
    'Assert-RegularFileOrMissing',
    'Assert-FileHashMatchesExpected',
    'Write-Utf8FileNoClobber',
    'New-InstallFileState',
    'Move-ExpectedFileToBackupNoClobber',
    'Install-StagedFile',
    'Remove-InstalledFileToBackup',
    'Restore-InstallFileState')
. (Get-SelectedFunctionsScriptBlock $scriptAsts[$installScript] $fileFunctionNames)

function Get-TestFileHash {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-TestFile {
    param([string]$Path, [string]$Content)

    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

$temporaryRoot = Join-Path `
    $env:TEMP `
    ('TinyHwBar.InstallerScriptTests.' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $markerDestination = Join-Path $temporaryRoot 'marker.new'
    $markerContent = 'TinyHwBar.UserInstall|2'
    $markerExpectedHash = Write-Utf8FileNoClobber $markerDestination $markerContent
    if (-not [string]::Equals(
        $markerExpectedHash,
        (Get-TestFileHash $markerDestination),
        [StringComparison]::OrdinalIgnoreCase) -or
        [IO.File]::ReadAllText($markerDestination) -ne $markerContent) {
        throw 'The no-clobber marker writer did not return the hash of its expected UTF-8 bytes.'
    }
    $markerClobberBlocked = $false
    try {
        Write-Utf8FileNoClobber $markerDestination 'different' | Out-Null
    }
    catch {
        $markerClobberBlocked = $true
    }
    if (-not $markerClobberBlocked -or
        [IO.File]::ReadAllText($markerDestination) -ne $markerContent) {
        throw 'The marker writer overwrote an existing transaction file.'
    }

    $destination = Join-Path $temporaryRoot 'existing.txt'
    $stage = Join-Path $temporaryRoot 'existing.new'
    $backup = Join-Path $temporaryRoot 'existing.bak'
    Write-TestFile $destination 'old'
    Write-TestFile $stage 'new'
    $state = New-InstallFileState `
        'existing test file' `
        $destination `
        $stage `
        (Get-TestFileHash $stage) `
        $true `
        (Get-TestFileHash $destination) `
        $backup
    Install-StagedFile $state
    Restore-InstallFileState $state
    if ([IO.File]::ReadAllText($destination) -ne 'old') {
        throw 'An existing file did not return to its original content.'
    }

    $changedDestination = Join-Path $temporaryRoot 'changed-before-move.txt'
    $changedStage = Join-Path $temporaryRoot 'changed-before-move.new'
    $changedBackup = Join-Path $temporaryRoot 'changed-before-move.bak'
    Write-TestFile $changedDestination 'expected-original'
    $changedExpectedHash = Get-TestFileHash $changedDestination
    Write-TestFile $changedDestination 'concurrent-replacement'
    Write-TestFile $changedStage 'candidate'
    $changedState = New-InstallFileState `
        'changed-before-move test file' `
        $changedDestination `
        $changedStage `
        (Get-TestFileHash $changedStage) `
        $true `
        $changedExpectedHash `
        $changedBackup
    $changedBlocked = $false
    try {
        Install-StagedFile $changedState
    }
    catch {
        $changedBlocked = $true
    }
    if (-not $changedBlocked -or
        [IO.File]::ReadAllText($changedDestination) -ne 'concurrent-replacement' -or
        -not (Test-Path -LiteralPath $changedStage -PathType Leaf) -or
        (Test-Path -LiteralPath $changedBackup) -or
        $changedState.OriginalMoved -or
        $changedState.Installed) {
        throw 'A file changed before commit was not returned safely to its original path.'
    }

    $absentDestination = Join-Path $temporaryRoot 'absent.txt'
    $absentStage = Join-Path $temporaryRoot 'absent.new'
    $absentBackup = Join-Path $temporaryRoot 'absent.bak'
    Write-TestFile $absentStage 'new-only'
    $absentState = New-InstallFileState `
        'absent test file' `
        $absentDestination `
        $absentStage `
        (Get-TestFileHash $absentStage) `
        $false `
        $null `
        $absentBackup
    Install-StagedFile $absentState
    Restore-InstallFileState $absentState
    if (Test-Path -LiteralPath $absentDestination) {
        throw 'A destination that was originally absent remained after rollback.'
    }

    $tamperDestination = Join-Path $temporaryRoot 'tamper.txt'
    $tamperStage = Join-Path $temporaryRoot 'tamper.new'
    $tamperBackup = Join-Path $temporaryRoot 'tamper.bak'
    Write-TestFile $tamperDestination 'old-tamper'
    Write-TestFile $tamperStage 'new-tamper'
    $tamperState = New-InstallFileState `
        'tamper test file' `
        $tamperDestination `
        $tamperStage `
        (Get-TestFileHash $tamperStage) `
        $true `
        (Get-TestFileHash $tamperDestination) `
        $tamperBackup
    Install-StagedFile $tamperState
    Write-TestFile $tamperDestination 'unrelated'
    $tamperBlocked = $false
    try {
        Restore-InstallFileState $tamperState
    }
    catch {
        $tamperBlocked = $true
    }
    if (-not $tamperBlocked -or
        [IO.File]::ReadAllText($tamperDestination) -ne 'unrelated' -or
        -not (Test-Path -LiteralPath $tamperBackup -PathType Leaf)) {
        throw 'Rollback did not preserve a different file and the trusted backup.'
    }

    $removeDestination = Join-Path $temporaryRoot 'remove.txt'
    $removeBackup = Join-Path $temporaryRoot 'remove.bak'
    Write-TestFile $removeDestination 'preserve-me'
    $removeState = New-InstallFileState `
        'remove test file' `
        $removeDestination `
        $null `
        $null `
        $true `
        (Get-TestFileHash $removeDestination) `
        $removeBackup
    Remove-InstalledFileToBackup $removeState
    Restore-InstallFileState $removeState
    if ([IO.File]::ReadAllText($removeDestination) -ne 'preserve-me') {
        throw 'A removal transaction did not restore its original file.'
    }

    $changedRemoveDestination = Join-Path $temporaryRoot 'changed-remove.txt'
    $changedRemoveBackup = Join-Path $temporaryRoot 'changed-remove.bak'
    Write-TestFile $changedRemoveDestination 'expected-remove'
    $changedRemoveExpectedHash = Get-TestFileHash $changedRemoveDestination
    Write-TestFile $changedRemoveDestination 'concurrent-remove-replacement'
    $changedRemoveState = New-InstallFileState `
        'changed removal test file' `
        $changedRemoveDestination `
        $null `
        $null `
        $true `
        $changedRemoveExpectedHash `
        $changedRemoveBackup
    $changedRemoveBlocked = $false
    try {
        Remove-InstalledFileToBackup $changedRemoveState
    }
    catch {
        $changedRemoveBlocked = $true
    }
    if (-not $changedRemoveBlocked -or
        [IO.File]::ReadAllText($changedRemoveDestination) -ne 'concurrent-remove-replacement' -or
        (Test-Path -LiteralPath $changedRemoveBackup) -or
        $changedRemoveState.OriginalMoved) {
        throw 'A file changed before removal was not returned safely to its original path.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host 'PASS: installer and uninstaller script safety tests'

#Requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$installScript = Join-Path $root 'tools\Install-TinyHwBar.ps1'
$uninstallScript = Join-Path $root 'tools\Uninstall-TinyHwBar.ps1'
$iconScript = Join-Path $root 'tools\GenerateIcon.ps1'
$releaseScript = Join-Path $root 'tools\Prepare-Release.ps1'
$binaryReadmeTemplate = Join-Path $root 'tools\BinaryRelease-README.md'
$publicReadme = Join-Path $root 'README.md'
$securityPolicy = Join-Path $root 'SECURITY.md'
$v3Plan = Join-Path $root 'docs\v3-plan.md'
$testCommand = Join-Path $root 'test.cmd'
$scriptAsts = @{}

foreach ($scriptPath in @($installScript, $uninstallScript, $iconScript, $releaseScript)) {
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
Assert-CommandElementCount $scriptAsts[$installScript] 'Copy-FileWithHashVerification' 4
Assert-CommandElementCount $scriptAsts[$uninstallScript] 'Remove-OwnedUninstallKey' 5
Assert-CommandElementCount $scriptAsts[$uninstallScript] 'Restore-RegistryKeySnapshot' 6
foreach ($scriptPath in @($installScript, $uninstallScript)) {
    Assert-CommandElementCount $scriptAsts[$scriptPath] 'Get-TinyHwBarProcessBlockers' 4
    Assert-CommandElementCount $scriptAsts[$scriptPath] 'Write-TinyHwBarProcessOwnerWarning' 2
}
foreach ($scriptPath in @($installScript, $uninstallScript, $releaseScript)) {
    Assert-CommandElementCount $scriptAsts[$scriptPath] 'Assert-NonElevatedProcess' 2
}

$nonElevatedGateMarkers = @{
    $installScript = @(
        "Assert-NonElevatedProcess 'TinyHwBar installation'",
        'if ([string]::IsNullOrWhiteSpace($SourceExe))')
    $uninstallScript = @(
        "Assert-NonElevatedProcess 'TinyHwBar uninstallation'",
        '$ownershipMarkerName =')
    $releaseScript = @(
        "Assert-NonElevatedProcess 'TinyHwBar release preparation'",
        '$bundleAllowlist =')
}
foreach ($scriptPath in $nonElevatedGateMarkers.Keys) {
    $source = [IO.File]::ReadAllText($scriptPath)
    $gateIndex = $source.IndexOf(
        $nonElevatedGateMarkers[$scriptPath][0],
        [StringComparison]::Ordinal)
    $mainIndex = $source.IndexOf(
        $nonElevatedGateMarkers[$scriptPath][1],
        [StringComparison]::Ordinal)
    if ($source.IndexOf(
        '[Security.Principal.WindowsBuiltInRole]::Administrator',
        [StringComparison]::Ordinal) -lt 0 -or
        $gateIndex -lt 0 -or
        $mainIndex -le $gateIndex) {
        throw "The non-administrator entry gate is missing or runs too late: $scriptPath"
    }
}

foreach ($scriptPath in @($installScript, $uninstallScript)) {
    $source = [IO.File]::ReadAllText($scriptPath)
    $maintenanceMutexIndex = $source.IndexOf(
        "'Global\TinyHwBar.Maintenance.'",
        [StringComparison]::Ordinal)
    $maintenanceAcquireIndex = $source.IndexOf(
        '[Threading.Mutex]::new($false, $maintenanceMutexName)',
        [StringComparison]::Ordinal)
    $appSingletonAcquireIndex = $source.IndexOf(
        '[Threading.Mutex]::new($false, $appSingletonMutexName)',
        [StringComparison]::Ordinal)
    $legacySingletonAcquireIndex = $source.IndexOf(
        '[Threading.Mutex]::new($false, $legacySingletonMutexName)',
        [StringComparison]::Ordinal)
    $waitCount = [regex]::Matches(
        $source,
        [regex]::Escape('.WaitOne(0, $false)')).Count
    if ($source.IndexOf("'Global\TinyHwBar.Singleton.'", [StringComparison]::Ordinal) -lt 0 -or
        $source.IndexOf("'Local\TinyHwBar.Singleton'", [StringComparison]::Ordinal) -lt 0 -or
        $maintenanceMutexIndex -lt 0 -or
        $maintenanceAcquireIndex -lt 0 -or
        $appSingletonAcquireIndex -le $maintenanceAcquireIndex -or
        $legacySingletonAcquireIndex -le $appSingletonAcquireIndex -or
        $waitCount -lt 3 -or
        $source.IndexOf('Invoke-CimMethod', [StringComparison]::Ordinal) -lt 0 -or
        $source.IndexOf('-MethodName GetOwnerSid', [StringComparison]::Ordinal) -lt 0 -or
        $source.IndexOf('$runningAfterMutexAcquisition.Count', [StringComparison]::Ordinal) -ge 0 -or
        $source.IndexOf("Name = 'TinyHwBar.exe'", [StringComparison]::Ordinal) -lt 0) {
        throw "The ordered locks or current-user process-owner check is missing or unsafe: $scriptPath"
    }
}
if ([IO.File]::ReadAllText($uninstallScript).IndexOf(
    '$runningTinyHwBarProcesses.Count',
    [StringComparison]::Ordinal) -ge 0) {
    throw 'Uninstall -RemoveUserData still blocks on the machine-wide TinyHwBar.exe count.'
}

$testCommandSource = [IO.File]::ReadAllText($testCommand)
if ($testCommandSource.IndexOf(
    'if /i not "%~1"=="--compile-only" goto invalid_arguments',
    [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
    $testCommandSource.IndexOf(
        'if not "%~2"=="" goto invalid_arguments',
        [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
    $testCommandSource.IndexOf(':invalid_arguments', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'test.cmd no longer fails closed for unknown or extra arguments.'
}

$uninstallSource = [IO.File]::ReadAllText($uninstallScript)
$commitIndex = $uninstallSource.IndexOf('$uninstallCommitted = $true', [StringComparison]::Ordinal)
$userDataRemovalText = 'Remove-Item -LiteralPath $userDataRoot -Recurse -Force'
$userDataRemovalIndex = $uninstallSource.IndexOf($userDataRemovalText, [StringComparison]::Ordinal)
$userDataFailureThrowText = 'throw $userDataRemovalFailureMessage'
$userDataFailureThrowIndex = $uninstallSource.IndexOf(
    $userDataFailureThrowText,
    [StringComparison]::Ordinal)
$userDataPostconditionText = 'if (Test-Path -LiteralPath $userDataRoot)'
$userDataPostconditionIndex = $uninstallSource.IndexOf(
    $userDataPostconditionText,
    $userDataRemovalIndex,
    [StringComparison]::Ordinal)
$userDataSuccessIndex = $uninstallSource.IndexOf(
    '$userDataRemoved = $true',
    $userDataRemovalIndex,
    [StringComparison]::Ordinal)
if ($commitIndex -lt 0 -or
    $userDataRemovalIndex -le $commitIndex -or
    $userDataRemovalIndex -ne $uninstallSource.LastIndexOf(
        $userDataRemovalText,
        [StringComparison]::Ordinal) -or
    $userDataFailureThrowIndex -le $userDataRemovalIndex -or
    $userDataFailureThrowIndex -ne $uninstallSource.LastIndexOf(
        $userDataFailureThrowText,
        [StringComparison]::Ordinal) -or
    $userDataPostconditionIndex -le $userDataRemovalIndex -or
    $userDataSuccessIndex -le $userDataPostconditionIndex) {
    throw 'User-data removal must occur once after commit, verify absence, and terminate nonzero on failure.'
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

$processFunctionNames = @(
    'Test-PathEquals',
    'Get-TinyHwBarProcessOwnerSid',
    'Get-TinyHwBarProcessBlockers',
    'Write-TinyHwBarProcessOwnerWarning')
$installProcessFunctions = Get-SelectedFunctionsScriptBlock `
    $scriptAsts[$installScript] `
    $processFunctionNames
$uninstallProcessFunctions = Get-SelectedFunctionsScriptBlock `
    $scriptAsts[$uninstallScript] `
    $processFunctionNames
if (-not [string]::Equals(
    $installProcessFunctions.ToString(),
    $uninstallProcessFunctions.ToString(),
    [StringComparison]::Ordinal)) {
    throw 'Installer and uninstaller current-user process blocker helpers have drifted apart.'
}
. $installProcessFunctions

$currentProcessOwnerSid = 'S-1-5-21-100-200-300-1001'
$otherProcessOwnerSid = 'S-1-5-21-100-200-300-1002'
$currentUserDestinationExe =
    'C:\Users\Current\AppData\Local\Programs\TinyHwBar\TinyHwBar.exe'
$mockTinyHwBarProcesses = @(
    [pscustomobject]@{
        ProcessId = 101
        SessionId = 8
        ExecutablePath = $currentUserDestinationExe.ToUpperInvariant()
        OwnerSid = $null
        FailOwnerLookup = $true
    },
    [pscustomobject]@{
        ProcessId = 102
        SessionId = 99
        ExecutablePath = 'C:\Portable\TinyHwBar.exe'
        OwnerSid = $currentProcessOwnerSid
        FailOwnerLookup = $false
    },
    [pscustomobject]@{
        ProcessId = 103
        SessionId = 42
        ExecutablePath = 'D:\OtherUser\TinyHwBar.exe'
        OwnerSid = $otherProcessOwnerSid
        FailOwnerLookup = $false
    },
    [pscustomobject]@{
        ProcessId = 104
        SessionId = 43
        ExecutablePath = $null
        OwnerSid = $null
        FailOwnerLookup = $true
    })
$mockOwnerSidResolver = {
    param($Process)

    if ($Process.FailOwnerLookup) {
        throw 'Simulated GetOwnerSid failure.'
    }
    return $Process.OwnerSid
}
$mockProcessCheck = Get-TinyHwBarProcessBlockers `
    $mockTinyHwBarProcesses `
    $currentProcessOwnerSid `
    $currentUserDestinationExe `
    $mockOwnerSidResolver
$blockingProcessIds = @(
    $mockProcessCheck.BlockingProcesses | ForEach-Object { [int]$_.ProcessId })
$unresolvedProcessIds = @($mockProcessCheck.UnresolvedOwnerProcessIds)
if ($blockingProcessIds.Count -ne 2 -or
    $blockingProcessIds -notcontains 101 -or
    $blockingProcessIds -notcontains 102 -or
    $blockingProcessIds -contains 103 -or
    $blockingProcessIds -contains 104 -or
    $unresolvedProcessIds.Count -ne 1 -or
    $unresolvedProcessIds[0] -ne '104') {
    throw ('Current-user process selection must always block the installed path and current SID, ' +
        'ignore another SID, and report unresolved non-matching owners without machine-wide denial.')
}
$ownerWarningBecameBlocking = $false
$originalWarningPreference = $WarningPreference
try {
    $WarningPreference = 'Stop'
    Write-TinyHwBarProcessOwnerWarning $mockProcessCheck 3>$null
}
catch {
    $ownerWarningBecameBlocking = $true
}
finally {
    $WarningPreference = $originalWarningPreference
}
if ($ownerWarningBecameBlocking) {
    throw 'An unresolved owner warning became a maintenance blocker under WarningPreference Stop.'
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
    'Get-ValidatedLocalFixedFilePath',
    'Copy-FileWithHashVerification',
    'Assert-FileHashMatchesExpected',
    'Write-Utf8FileNoClobber',
    'New-InstallFileState',
    'Move-ExpectedFileToBackupNoClobber',
    'Install-StagedFile',
    'Remove-InstalledFileToBackup',
    'Restore-InstallFileState',
    'Get-TinyHwBarCliAssemblyMetadata',
    'Get-TinyHwBarExecutableMetadata')
. (Get-SelectedFunctionsScriptBlock $scriptAsts[$installScript] $fileFunctionNames)

$cliParserSources = @{}
foreach ($scriptPath in @($installScript, $releaseScript)) {
    $parserDefinitions = @($scriptAsts[$scriptPath].FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            [string]::Equals(
                $node.Name,
                'Get-TinyHwBarCliAssemblyMetadata',
                [StringComparison]::Ordinal)
    }, $true))
    if ($parserDefinitions.Count -ne 1) {
        throw "Expected exactly one non-loading CLI metadata parser: $scriptPath"
    }
    $parserSource = $parserDefinitions[0].Extent.Text
    $cliParserSources[$scriptPath] = $parserSource
    if ($parserSource.IndexOf('[IO.File]::Open(', [StringComparison]::Ordinal) -lt 0 -or
        $parserSource.IndexOf('New-Object IO.BinaryReader', [StringComparison]::Ordinal) -lt 0 -or
        $parserSource.IndexOf('GetAssemblyName', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $parserSource.IndexOf('ReflectionOnlyLoad', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $parserSource.IndexOf('Assembly]::Load', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $parserSource.IndexOf('Add-Type', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The CLI metadata parser stopped being a pure FileStream/BinaryReader reader: $scriptPath"
    }
}
if (-not [string]::Equals(
    $cliParserSources[$installScript],
    $cliParserSources[$releaseScript],
    [StringComparison]::Ordinal)) {
    throw 'Installer and release CLI metadata parsers have drifted apart.'
}
foreach ($scriptPath in @($installScript, $releaseScript)) {
    $productionSource = [IO.File]::ReadAllText($scriptPath)
    if ($productionSource.IndexOf('GetAssemblyName', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $productionSource.IndexOf('ReflectionOnlyLoad', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $productionSource.IndexOf('Assembly]::Load', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "A production executable gate still invokes a CLR assembly-loading API: $scriptPath"
    }
}

$remoteSourceBlocked = $false
try {
    Get-ValidatedLocalFixedFilePath `
        '\\invalid.example\share\TinyHwBar.exe' `
        'Test source executable' | Out-Null
}
catch {
    $remoteSourceBlocked = $true
}
if (-not $remoteSourceBlocked) {
    throw 'The installer source-path gate accepted a UNC path.'
}

$builtExecutable = Join-Path $root 'outputs\TinyHwBar.exe'
$builtExecutableMetadata = $null
$builtCliMetadata = $null
$builtExpectedReleaseVersion = $null
if (Test-Path -LiteralPath $builtExecutable -PathType Leaf) {
    $builtExecutableMetadata = Get-TinyHwBarExecutableMetadata $builtExecutable
    $builtCliMetadata = Get-TinyHwBarCliAssemblyMetadata `
        $builtExecutable `
        $builtExecutableMetadata.FileVersion
    $builtExpectedReleaseVersion = $builtExecutableMetadata.DisplayVersion
    if ([string]::IsNullOrWhiteSpace($builtExecutableMetadata.DisplayVersion) -or
        $null -eq $builtExecutableMetadata.FileVersion -or
        -not [string]::Equals(
            $builtCliMetadata.Name,
            'TinyHwBar',
            [StringComparison]::Ordinal) -or
        $builtCliMetadata.Version -ne $builtExecutableMetadata.FileVersion -or
        $builtCliMetadata.Machine -ne 0x8664 -or
        $builtCliMetadata.OptionalHeaderMagic -ne 0x020B -or
        $builtCliMetadata.Subsystem -ne 2 -or
        $builtCliMetadata.ClrFlags -ne 1) {
        throw 'The current TinyHwBar build did not pass the installer identity/version gate.'
    }
}

$releaseFunctionNames = @(
    'Test-PathEquals',
    'Test-IsDescendantPath',
    'Get-ValidatedLocalFixedPath',
    'Get-ValidatedStagingPath',
    'Get-CanonicalReleaseVersion',
    'Get-NormalizedMarkdownTarget',
    'Assert-BinaryReadmeLinks',
    'Get-Sha256Hex',
    'Get-GitForWindowsLayout',
    'Test-ReleaseAclRuleWriteRisk',
    'Get-ReleaseBoundaryParent',
    'Assert-TrustedFileSystemBoundary',
    'Assert-TrustedGitPathAcl',
    'Get-ApprovedGitExecutable',
    'Get-GitProcessEnvironmentSnapshot',
    'Clear-GitProcessEnvironment',
    'Restore-GitProcessEnvironment',
    'Assert-SafeLocalGitConfiguration',
    'Get-ValidatedGitObjectsDirectory',
    'Get-TinyHwBarCliAssemblyMetadata',
    'Assert-TinyHwBarExecutable')
. (Get-SelectedFunctionsScriptBlock $scriptAsts[$releaseScript] $releaseFunctionNames)

$releaseFileItem = Get-Item -LiteralPath $releaseScript -Force
$releaseFileBoundaryParent = Get-ReleaseBoundaryParent $releaseFileItem
$releaseDirectoryBoundaryParent = Get-ReleaseBoundaryParent $releaseFileBoundaryParent
if ($null -eq $releaseFileBoundaryParent -or
    -not (Test-PathEquals $releaseFileBoundaryParent.FullName (Split-Path -Parent $releaseScript)) -or
    $null -eq $releaseDirectoryBoundaryParent -or
    -not (Test-PathEquals $releaseDirectoryBoundaryParent.FullName $root)) {
    throw 'The release boundary parent helper did not use FileInfo.Directory and DirectoryInfo.Parent.'
}

if ($null -ne $builtExecutableMetadata) {
    Assert-TinyHwBarExecutable $builtExecutable $builtExpectedReleaseVersion
}

$originalGitEnvironmentSnapshot = Get-GitProcessEnvironmentSnapshot
try {
    Clear-GitProcessEnvironment
    $gitEnvironmentFixtureNames = @(
        'GIT_DIR',
        'GIT_WORK_TREE',
        'GIT_COMMON_DIR',
        'GIT_COMMONDIR',
        'GIT_OBJECT_DIRECTORY',
        'GIT_ALTERNATE_OBJECT_DIRECTORIES',
        'GIT_QUARANTINE_PATH',
        'GIT_CONFIG',
        'GIT_CONFIG_SYSTEM',
        'GIT_CONFIG_GLOBAL',
        'GIT_CONFIG_NOSYSTEM',
        'GIT_CONFIG_COUNT',
        'GIT_CONFIG_PARAMETERS',
        'GIT_CONFIG_KEY_73',
        'GIT_CONFIG_VALUE_73',
        'GIT_ATTR_SOURCE',
        'GIT_ATTR_GLOBAL',
        'GIT_ATTR_SYSTEM',
        'GIT_ATTR_NOSYSTEM',
        'GIT_REPLACE_REF_BASE',
        'GIT_GRAFT_FILE',
        'GIT_TRACE2_EVENT',
        'GIT_TEST_TINYHWBAR_FIXTURE')
    foreach ($fixtureName in $gitEnvironmentFixtureNames) {
        [Environment]::SetEnvironmentVariable(
            $fixtureName,
            ($fixtureName + '-value'),
            [EnvironmentVariableTarget]::Process)
    }
    $fixtureGitEnvironmentSnapshot = Get-GitProcessEnvironmentSnapshot
    if ($fixtureGitEnvironmentSnapshot.Count -ne $gitEnvironmentFixtureNames.Count) {
        throw 'The Git environment snapshot did not capture every fixed and wildcard fixture.'
    }

    Clear-GitProcessEnvironment
    if ((Get-GitProcessEnvironmentSnapshot).Count -ne 0) {
        throw 'The Git environment isolation helper left an inherited GIT_* override active.'
    }
    $env:GIT_CREATED_AFTER_SNAPSHOT = 'must-be-removed'
    Restore-GitProcessEnvironment $fixtureGitEnvironmentSnapshot
    $restoredGitEnvironmentSnapshot = Get-GitProcessEnvironmentSnapshot
    if ($restoredGitEnvironmentSnapshot.Count -ne $fixtureGitEnvironmentSnapshot.Count -or
        $restoredGitEnvironmentSnapshot.ContainsKey('GIT_CREATED_AFTER_SNAPSHOT')) {
        throw 'The Git environment restore helper retained a new override or lost a snapshot entry.'
    }
    foreach ($fixtureName in $gitEnvironmentFixtureNames) {
        if (-not [string]::Equals(
            [string]$restoredGitEnvironmentSnapshot[$fixtureName],
            ($fixtureName + '-value'),
            [StringComparison]::Ordinal)) {
            throw "The Git environment restore helper changed: $fixtureName"
        }
    }
}
finally {
    Restore-GitProcessEnvironment $originalGitEnvironmentSnapshot
}

$validVersion = Get-CanonicalReleaseVersion '2.0.0'
if ($validVersion.ToString(4) -ne '2.0.0.0') {
    throw 'The canonical release-version gate changed a valid version unexpectedly.'
}
foreach ($invalidVersion in @(
    '01.2.3',
    '1.02.3',
    '1.2.03',
    '65535.0.0',
    '2147483648.0.0',
    ([char]0xFF11 + '.2.3'))) {
    $invalidVersionBlocked = $false
    try {
        Get-CanonicalReleaseVersion $invalidVersion | Out-Null
    }
    catch {
        $invalidVersionBlocked = $true
    }
    if (-not $invalidVersionBlocked) {
        throw "The release-version gate accepted an unsafe or noncanonical version: $invalidVersion"
    }
}

$allowedStagingRoot = Join-Path $root 'release\staging'
$gitDirectory = Join-Path $root '.git'
$validStagingPath = Join-Path `
    $allowedStagingRoot `
    ('security-test-' + [Guid]::NewGuid().ToString('N'))
if (-not (Test-PathEquals `
    (Get-ValidatedStagingPath $validStagingPath $root $allowedStagingRoot $gitDirectory) `
    $validStagingPath)) {
    throw 'The release staging-path gate changed a valid repository staging child.'
}
foreach ($invalidStagingPath in @(
    (Join-Path $root 'outputs\outside-staging'),
    (Join-Path $allowedStagingRoot '.git\blocked'),
    '\\invalid.example\share\release-staging')) {
    $invalidStagingBlocked = $false
    try {
        Get-ValidatedStagingPath `
            $invalidStagingPath `
            $root `
            $allowedStagingRoot `
            $gitDirectory | Out-Null
    }
    catch {
        $invalidStagingBlocked = $true
    }
    if (-not $invalidStagingBlocked) {
        throw "The release staging-path gate accepted an unsafe path: $invalidStagingPath"
    }
}

$releaseSource = [IO.File]::ReadAllText($releaseScript)
$directProductionGitCommands = @($scriptAsts[$releaseScript].FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.InvocationOperator -eq [Management.Automation.Language.TokenKind]::Ampersand -and
        $node.CommandElements.Count -gt 0 -and
        $node.CommandElements[0] -is [Management.Automation.Language.VariableExpressionAst] -and
        [string]::Equals(
            $node.CommandElements[0].VariablePath.UserPath,
            'git',
            [StringComparison]::Ordinal)
}, $true))
if ($directProductionGitCommands.Count -ne 6) {
    throw 'The release tool changed its expected direct Git invocation count.'
}
$requiredGitGlobalOptions = @(
    '--no-replace-objects',
    '--no-lazy-fetch',
    '--no-optional-locks',
    '--no-pager')
foreach ($directGitCommand in $directProductionGitCommands) {
    if ($directGitCommand.CommandElements.Count -lt (1 + $requiredGitGlobalOptions.Count)) {
        throw "A production Git invocation omitted mandatory global options: $($directGitCommand.Extent.Text)"
    }
    for ($optionIndex = 0; $optionIndex -lt $requiredGitGlobalOptions.Count; $optionIndex++) {
        if (-not [string]::Equals(
            $directGitCommand.CommandElements[$optionIndex + 1].Extent.Text,
            $requiredGitGlobalOptions[$optionIndex],
            [StringComparison]::Ordinal)) {
            throw (
                "A production Git invocation changed mandatory global option order: " +
                $directGitCommand.Extent.Text)
        }
    }
    if ($directGitCommand.CommandElements.Count -lt 10 -or
        -not [string]::Equals(
            $directGitCommand.CommandElements[5].Extent.Text,
            '-c',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $directGitCommand.CommandElements[6].Extent.Text,
            '$trustedGitSafeDirectoryConfig',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $directGitCommand.CommandElements[7].Extent.Text,
            '-c',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $directGitCommand.CommandElements[8].Extent.Text,
            '$trustedGitAttributesConfig',
            [StringComparison]::Ordinal)) {
        throw (
            'A production Git invocation omitted the exact safe.directory or empty ' +
            "attributes configuration: $($directGitCommand.Extent.Text)")
    }
}
$archiveGitCommands = @($scriptAsts[$releaseScript].FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Invoke-ExternalCommand' -and
        $node.CommandElements.Count -eq 4 -and
        $node.CommandElements[1] -is [Management.Automation.Language.VariableExpressionAst] -and
        [string]::Equals(
            $node.CommandElements[1].VariablePath.UserPath,
            'git',
            [StringComparison]::Ordinal)
}, $true))
if ($archiveGitCommands.Count -ne 1 -or
    -not ($archiveGitCommands[0].CommandElements[2] -is
        [Management.Automation.Language.ArrayExpressionAst])) {
    throw 'The release tool changed its expected indirect Git archive invocation.'
}
$archiveGitArgumentAsts = @($archiveGitCommands[0].CommandElements[2].FindAll({
    param($node)
    $node -is [Management.Automation.Language.StringConstantExpressionAst] -or
        $node -is [Management.Automation.Language.ExpandableStringExpressionAst]
}, $true))
for ($optionIndex = 0; $optionIndex -lt $requiredGitGlobalOptions.Count; $optionIndex++) {
    if ($archiveGitArgumentAsts.Count -le $optionIndex -or
        -not [string]::Equals(
            $archiveGitArgumentAsts[$optionIndex].Value,
            $requiredGitGlobalOptions[$optionIndex],
            [StringComparison]::Ordinal)) {
        throw 'The Git archive invocation omitted or reordered a mandatory global option.'
    }
}
if ($archiveGitArgumentAsts.Count -lt 4) {
    throw 'The Git archive invocation omitted its mandatory global options.'
}
$archiveGitArgumentsText = $archiveGitCommands[0].CommandElements[2].Extent.Text
if ($archiveGitArgumentsText.IndexOf(
        '$trustedGitSafeDirectoryConfig',
        [StringComparison]::Ordinal) -lt 0 -or
    $archiveGitArgumentsText.IndexOf(
        '$trustedGitAttributesConfig',
        [StringComparison]::Ordinal) -lt 0 -or
    $archiveGitArgumentsText.IndexOf(
        "'--no-worktree-attributes'",
        [StringComparison]::Ordinal) -lt 0) {
    throw 'The Git archive invocation omitted a trusted config or tree-attribute safeguard.'
}
$zipAllowlistIndex = $releaseSource.IndexOf(
    'Assert-ZipAllowlist $zipPath $bundleAllowlist',
    [StringComparison]::Ordinal)
$finalZipHashIndex = $releaseSource.IndexOf(
    '$finalZipHash = Get-Sha256Hex $zipPath',
    [StringComparison]::Ordinal)
$publishedZipHashIndex = $releaseSource.IndexOf(
    '$publishedZipHash = Get-Sha256Hex $zipPath',
    [StringComparison]::Ordinal)
$gitApprovalIndex = $releaseSource.IndexOf(
    'if (-not $PSCmdlet.ShouldProcess(',
    [StringComparison]::Ordinal)
$gitLockIndex = $releaseSource.IndexOf(
    '$approvedGitLock = [IO.File]::Open(',
    [StringComparison]::Ordinal)
$gitRevalidationIndex = if ($gitLockIndex -ge 0) {
    $releaseSource.IndexOf(
        '$approvedGit = Get-ApprovedGitExecutable',
        $gitLockIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$gitInvocationIndex = $releaseSource.IndexOf(
    '$repoRootOutput = & $git',
    [StringComparison]::Ordinal)
$processTempCreationIndex = $releaseSource.IndexOf(
    "New-SafeDirectoryNoClobber `$processTempRoot 'Trusted child-process TEMP/TMP/TMPDIR directory'",
    [StringComparison]::Ordinal)
$tempRedirectFlagIndex = $releaseSource.IndexOf(
    '$processTempRedirected = $true',
    [StringComparison]::Ordinal)
$tempSetIndex = $releaseSource.IndexOf(
    '$env:TEMP = $processTempRoot',
    [StringComparison]::Ordinal)
$tmpSetIndex = $releaseSource.IndexOf(
    '$env:TMP = $processTempRoot',
    [StringComparison]::Ordinal)
$tmpDirSetIndex = $releaseSource.IndexOf(
    '$env:TMPDIR = $processTempRoot',
    [StringComparison]::Ordinal)
$gitEnvironmentSnapshotIndex = $releaseSource.IndexOf(
    '$gitEnvironmentSnapshot = Get-GitProcessEnvironmentSnapshot',
    [StringComparison]::Ordinal)
$emptyGitConfigCreationIndex = $releaseSource.IndexOf(
    "Write-Utf8NoBom `$emptyGitConfigPath ''",
    [StringComparison]::Ordinal)
$emptyGitAttributesCreationIndex = $releaseSource.IndexOf(
    "Write-Utf8NoBom `$emptyGitAttributesPath ''",
    [StringComparison]::Ordinal)
$preEmptyGitPolicyValidationIndex = $releaseSource.IndexOf(
    "        'Trusted release workspace before empty Git policy files'",
    [StringComparison]::Ordinal)
$emptyGitPolicyValidationIndex = $releaseSource.IndexOf(
    "        'Trusted release workspace and empty Git policy files before first Git execution'",
    [StringComparison]::Ordinal)
$gitEnvironmentHardenedIndex = $releaseSource.IndexOf(
    '$gitEnvironmentHardened = $true',
    [StringComparison]::Ordinal)
$gitEnvironmentClearIndex = if ($gitEnvironmentHardenedIndex -ge 0) {
    $releaseSource.IndexOf(
        'Clear-GitProcessEnvironment',
        $gitEnvironmentHardenedIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$gitNoReplaceSetIndex = $releaseSource.IndexOf(
    "`$env:GIT_NO_REPLACE_OBJECTS = '1'",
    [StringComparison]::Ordinal)
$gitExecPathSetIndex = $releaseSource.IndexOf(
    '$env:GIT_EXEC_PATH = $approvedGit.GitExecPath',
    [StringComparison]::Ordinal)
$firstLocalConfigValidationIndex = $releaseSource.IndexOf(
    'Assert-SafeLocalGitConfiguration $repoMetadataCandidate',
    [StringComparison]::Ordinal)
$secondLocalConfigValidationIndex = if ($firstLocalConfigValidationIndex -ge 0) {
    $releaseSource.IndexOf(
        'Assert-SafeLocalGitConfiguration $repoMetadataCandidate',
        $firstLocalConfigValidationIndex + 1,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$gitObjectsResolutionIndex = $releaseSource.IndexOf(
    '$gitObjectsOutput = & $git',
    [StringComparison]::Ordinal)
$gitFsckIndex = $releaseSource.IndexOf(
    'fsck --full --strict --no-dangling',
    [StringComparison]::Ordinal)
$resolvedCommitIndex = $releaseSource.IndexOf(
    '$resolvedCommitOutput = & $git',
    [StringComparison]::Ordinal)
$lastFinallyIndex = $releaseSource.LastIndexOf(
    'finally {',
    [StringComparison]::Ordinal)
$tempRestoreIndex = if ($lastFinallyIndex -ge 0) {
    $releaseSource.IndexOf(
        '$env:TEMP = $previousTemp',
        $lastFinallyIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$tmpRestoreIndex = if ($lastFinallyIndex -ge 0) {
    $releaseSource.IndexOf(
        '$env:TMP = $previousTmp',
        $lastFinallyIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$tmpDirRestoreIndex = if ($lastFinallyIndex -ge 0) {
    $releaseSource.IndexOf(
        '$env:TMPDIR = $previousTmpDir',
        $lastFinallyIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$tmpDirRemoveIndex = if ($lastFinallyIndex -ge 0) {
    $releaseSource.IndexOf(
        "Remove-Item -LiteralPath 'Env:TMPDIR' -ErrorAction Stop",
        $lastFinallyIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$gitEnvironmentRestoreIndex = if ($lastFinallyIndex -ge 0) {
    $releaseSource.IndexOf(
        'Restore-GitProcessEnvironment $gitEnvironmentSnapshot',
        $lastFinallyIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$gitLockDisposeIndex = if ($lastFinallyIndex -ge 0) {
    $releaseSource.IndexOf(
        '$approvedGitLock.Dispose()',
        $lastFinallyIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$temporaryCleanupIndex = if ($lastFinallyIndex -ge 0) {
    $releaseSource.IndexOf(
        'Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction Stop',
        $lastFinallyIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$finalizationAggregateIndex = if ($lastFinallyIndex -ge 0) {
    $releaseSource.IndexOf(
        '$finalizationFailures.Count -gt 0',
        $lastFinallyIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$workspaceRevalidationIndex = if ($gitRevalidationIndex -ge 0) {
    $releaseSource.IndexOf(
        "        'Trusted release workspace'",
        $gitRevalidationIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$archiveCreationIndex = $releaseSource.IndexOf(
    "        'Git source archive creation'",
    [StringComparison]::Ordinal)
$archiveValidationIndex = $releaseSource.IndexOf(
    "        'Trusted Git source archive'",
    [StringComparison]::Ordinal)
$expandArchiveIndex = $releaseSource.IndexOf(
    'Expand-Archive -LiteralPath $sourceArchive -DestinationPath $sourceRoot',
    [StringComparison]::Ordinal)
$sourceValidationIndex = $releaseSource.IndexOf(
    "        'Trusted extracted source tree'",
    [StringComparison]::Ordinal)
$provenanceWriteIndex = $releaseSource.IndexOf(
    'Write-Utf8NoBom $provenancePath',
    [StringComparison]::Ordinal)
$finalStagingValidationIndex = $releaseSource.IndexOf(
    "        'Trusted completed release staging tree with provenance'",
    [StringComparison]::Ordinal)

$releaseFinalizationTryAsts = @($scriptAsts[$releaseScript].FindAll({
    param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and
        $null -ne $node.Finally -and
        $node.Finally.Extent.Text.IndexOf(
            '$finalizationFailures',
            [StringComparison]::Ordinal) -ge 0
}, $true))
if ($releaseFinalizationTryAsts.Count -ne 1 -or
    $releaseFinalizationTryAsts[0].CatchClauses.Count -ne 1) {
    throw 'The release operation no longer records every primary failure before finalization.'
}
$segmentedFinalizationTryAsts = @($releaseFinalizationTryAsts[0].Finally.FindAll({
    param($node)
    $node -is [Management.Automation.Language.TryStatementAst]
}, $true))
if ($segmentedFinalizationTryAsts.Count -ne 4 -or
    @($segmentedFinalizationTryAsts | Where-Object {
        $_.CatchClauses.Count -ne 1
    }).Count -ne 0) {
    throw 'Git environment, process TEMP, Git lock, and temporary cleanup are not independently guarded.'
}

foreach ($requiredGitEnvironmentAssignment in @(
    "`$env:GIT_NO_REPLACE_OBJECTS = '1'",
    "`$env:GIT_NO_LAZY_FETCH = '1'",
    "`$env:GIT_OPTIONAL_LOCKS = '0'",
    "`$env:GIT_TERMINAL_PROMPT = '0'",
    '$env:GIT_CONFIG_SYSTEM = $emptyGitConfigPath',
    '$env:GIT_CONFIG_GLOBAL = $emptyGitConfigPath',
    "`$env:GIT_CONFIG_NOSYSTEM = '1'",
    "`$env:GIT_CONFIG_COUNT = '0'",
    '$env:GIT_ATTR_SYSTEM = $emptyGitAttributesPath',
    '$env:GIT_ATTR_GLOBAL = $emptyGitAttributesPath',
    "`$env:GIT_ATTR_NOSYSTEM = '1'",
    '$env:GIT_EXEC_PATH = $approvedGit.GitExecPath')) {
    if ($releaseSource.IndexOf(
        $requiredGitEnvironmentAssignment,
        [StringComparison]::Ordinal) -lt 0) {
        throw "The release tool omitted a controlled Git environment value: $requiredGitEnvironmentAssignment"
    }
}
foreach ($requiredGitMetadataFragment in @(
    "'commondir'",
    "'objects\info\alternates'",
    "'objects\info\http-alternates'",
    "'info\grafts'",
    "'info\attributes'",
    "'shallow'")) {
    if ($releaseSource.IndexOf(
        $requiredGitMetadataFragment,
        [StringComparison]::Ordinal) -lt 0) {
        throw "The release tool omitted a forbidden Git metadata path: $requiredGitMetadataFragment"
    }
}
if ($releaseSource.IndexOf("ConfirmImpact = 'High'", [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('[switch]$ApproveFrozenCommitScripts', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('[string]$GitExe', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('[string]$ApprovedGitSha256', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('[string]$ApprovedGitSignerThumbprint', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('[string]$TrustedWorkspaceRoot', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource -notmatch '(?s)\[Parameter\(Mandatory\s*=\s*\$true\)\]\s*\[string\]\$TrustedWorkspaceRoot' -or
    $releaseSource.IndexOf(
        'if (-not $ApproveFrozenCommitScripts -and -not $WhatIfPreference)',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('--absolute-git-dir', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('Get-Command git.exe', [StringComparison]::Ordinal) -ge 0 -or
    $releaseSource.IndexOf('Get-AuthenticodeSignature', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('function Get-GitForWindowsLayout', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '<root>\mingw64\bin\git.exe; cmd\git.exe shims and other layouts are rejected.',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('function Assert-TrustedFileSystemBoundary', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('function Get-ReleaseBoundaryParent', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('function Assert-TrustedGitPathAcl', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf("'S-1-5-18'", [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf("'S-1-5-32-544'", [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.FileSystemRights]::ChangePermissions',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.FileSystemRights]::TakeOwnership',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.FileSystemRights]::WriteData',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.FileSystemRights]::AppendData',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.FileSystemRights]::Write -bor',
        [StringComparison]::Ordinal) -ge 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.FileSystemRights]::Modify -bor',
        [StringComparison]::Ordinal) -ge 0 -or
    $releaseSource.IndexOf('0x10000000L', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('0x40000000L', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('.GetOwner(', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'Security.AccessControl.RawSecurityDescriptor(',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$acl.GetSecurityDescriptorBinaryForm()',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.ControlFlags]::DiscretionaryAclPresent',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$null -eq $rawSecurityDescriptor.DiscretionaryAcl',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'Get-ChildItem -LiteralPath $directory.FullName -Force',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$runtimeItems.Count -gt $MaxEntryCount',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$ancestorSubstitutionMask',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.PropagationFlags]::InheritOnly',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.InheritanceFlags]::ObjectInherit',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[Security.AccessControl.InheritanceFlags]::ContainerInherit',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'FutureChildRightsMask',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'CheckFutureChildren',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'Test-ReleaseAclRuleWriteRisk',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('IdentityReference.Translate(', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('Assert-TrustedGitPathAcl $layout', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'Assert-TrustedFileSystemBoundary `',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'The TinyHwBar source repository must be a strict descendant',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        "(Join-Path `$TrustedWorkspaceRoot 'work')",
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        "(Join-Path `$TrustedWorkspaceRoot 'staging')",
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$env:TEMP = $processTempRoot',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$env:TMP = $processTempRoot',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$previousTmpDirExists = Test-Path -LiteralPath ''Env:TMPDIR''',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$env:TMPDIR = $processTempRoot',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'function Get-GitProcessEnvironmentSnapshot',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'function Clear-GitProcessEnvironment',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'function Restore-GitProcessEnvironment',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'function Assert-SafeLocalGitConfiguration',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        "Join-Path `$GitMetadataDirectory 'config.worktree'",
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '(?:includeif|include|fsck|tar)',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        ".StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase)",
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '[EnvironmentVariableTarget]::Process',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '--git-common-dir',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '--git-path objects',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'fsck --full --strict --no-dangling',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        "'--no-worktree-attributes'",
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'safe.directory=*',
        [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $releaseSource.IndexOf(
        'Test-PathEquals $gitCommonDirectory $repoMetadataCandidate',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '"core.attributesFile=$emptyGitAttributesPath"',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$env:TEMP = $previousTemp',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$env:TMP = $previousTmp',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        '$env:TMPDIR = $previousTmpDir',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        "Remove-Item -LiteralPath 'Env:TMPDIR' -ErrorAction Stop",
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('[IO.Path]::GetTempPath()', [StringComparison]::Ordinal) -ge 0 -or
    $releaseSource.IndexOf('Set-Acl', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $releaseSource.IndexOf('[IO.FileShare]::Read', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('Get-Sha256Hex $git', [StringComparison]::Ordinal) -lt 0 -or
    $gitApprovalIndex -lt 0 -or
    $gitLockIndex -le $gitApprovalIndex -or
    $gitEnvironmentSnapshotIndex -le $gitApprovalIndex -or
    $processTempCreationIndex -le $gitApprovalIndex -or
    $preEmptyGitPolicyValidationIndex -le $processTempCreationIndex -or
    $emptyGitConfigCreationIndex -le $preEmptyGitPolicyValidationIndex -or
    $emptyGitAttributesCreationIndex -le $emptyGitConfigCreationIndex -or
    $emptyGitPolicyValidationIndex -le $emptyGitAttributesCreationIndex -or
    $tempRedirectFlagIndex -le $emptyGitPolicyValidationIndex -or
    $tempSetIndex -le $tempRedirectFlagIndex -or
    $tmpSetIndex -le $tempRedirectFlagIndex -or
    $tmpDirSetIndex -le $tempRedirectFlagIndex -or
    $gitEnvironmentHardenedIndex -le $tmpDirSetIndex -or
    $gitEnvironmentClearIndex -le $gitEnvironmentHardenedIndex -or
    $gitNoReplaceSetIndex -le $gitEnvironmentClearIndex -or
    $gitExecPathSetIndex -le $gitEnvironmentClearIndex -or
    $gitExecPathSetIndex -ge $gitInvocationIndex -or
    $firstLocalConfigValidationIndex -lt 0 -or
    $firstLocalConfigValidationIndex -ge $gitApprovalIndex -or
    $secondLocalConfigValidationIndex -le $gitApprovalIndex -or
    $secondLocalConfigValidationIndex -ge $gitInvocationIndex -or
    $gitInvocationIndex -le $tempSetIndex -or
    $gitInvocationIndex -le $tmpSetIndex -or
    $gitInvocationIndex -le $tmpDirSetIndex -or
    $gitInvocationIndex -le $gitEnvironmentClearIndex -or
    $gitInvocationIndex -le $gitNoReplaceSetIndex -or
    $gitObjectsResolutionIndex -le $gitInvocationIndex -or
    $gitFsckIndex -le $gitObjectsResolutionIndex -or
    $resolvedCommitIndex -le $gitFsckIndex -or
    $archiveCreationIndex -le $resolvedCommitIndex -or
    $gitRevalidationIndex -le $gitLockIndex -or
    $workspaceRevalidationIndex -le $gitRevalidationIndex -or
    $gitInvocationIndex -le $workspaceRevalidationIndex -or
    $archiveValidationIndex -le $archiveCreationIndex -or
    $sourceValidationIndex -le $expandArchiveIndex -or
    $finalStagingValidationIndex -le $provenanceWriteIndex -or
    $lastFinallyIndex -lt 0 -or
    $tempRestoreIndex -le $lastFinallyIndex -or
    $tmpRestoreIndex -le $lastFinallyIndex -or
    $tmpDirRestoreIndex -le $lastFinallyIndex -or
    $tmpDirRemoveIndex -le $lastFinallyIndex -or
    $gitEnvironmentRestoreIndex -le $lastFinallyIndex -or
    $tempRestoreIndex -le $gitEnvironmentRestoreIndex -or
    $gitLockDisposeIndex -le $tmpDirRemoveIndex -or
    $temporaryCleanupIndex -le $gitLockDisposeIndex -or
    $finalizationAggregateIndex -le $temporaryCleanupIndex -or
    $releaseSource.IndexOf("Stage = 'Git environment restoration'", [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf("Stage = 'Process TEMP/TMP/TMPDIR restoration'", [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf("Stage = 'Approved Git lock disposal'", [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf("Stage = 'Temporary release workspace cleanup'", [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('if ($null -eq $releaseFailure)', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('if ($finalizationFailures.Count -gt 0)', [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf(
        'Execution trust boundary: source, build, process TEMP/TMP/TMPDIR, and staging stayed beneath the validated trusted workspace root',
        [StringComparison]::Ordinal) -lt 0 -or
    $releaseSource.IndexOf('-ErrorAction SilentlyContinue', [StringComparison]::Ordinal) -ge 0 -or
    $releaseSource.IndexOf(
        'New-Item -ItemType Directory -Path $bundleRoot -Force',
        [StringComparison]::Ordinal) -ge 0 -or
    $zipAllowlistIndex -lt 0 -or
    $finalZipHashIndex -le $zipAllowlistIndex -or
    $publishedZipHashIndex -le $finalZipHashIndex) {
    throw 'The release tool lost its Git-path, trust-boundary, no-clobber, cleanup, or final ZIP-hash safeguard.'
}

foreach ($publicationBoundaryDocument in @(
    $publicReadme,
    $securityPolicy,
    $v3Plan,
    $binaryReadmeTemplate)) {
    if ([IO.File]::ReadAllText($publicationBoundaryDocument).IndexOf(
        '`TEMP`/`TMP`/`TMPDIR`',
        [StringComparison]::Ordinal) -lt 0) {
        throw "The publication boundary document omitted TMPDIR: $publicationBoundaryDocument"
    }
    $boundaryDocumentText = [IO.File]::ReadAllText($publicationBoundaryDocument)
    if ($boundaryDocumentText.IndexOf(
        '`--no-replace-objects`',
        [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`--no-lazy-fetch`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`--no-optional-locks`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`--no-pager`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`GIT_*`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('grafts', [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $boundaryDocumentText.IndexOf('.git/info/attributes', [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $boundaryDocumentText.IndexOf('alternates', [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $boundaryDocumentText.IndexOf('`GIT_EXEC_PATH`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`includeIf`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`tar.*`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`--git-path objects`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`git fsck --full --strict`', [StringComparison]::Ordinal) -lt 0 -or
        $boundaryDocumentText.IndexOf('`--no-worktree-attributes`', [StringComparison]::Ordinal) -lt 0) {
        throw "The publication boundary document omitted Git environment/object/config/attribute hardening: $publicationBoundaryDocument"
    }
}

$shimLayoutBlocked = $false
try {
    Get-GitForWindowsLayout (Join-Path $root 'test-git-root\cmd\git.exe') | Out-Null
}
catch {
    $shimLayoutBlocked = $true
}
if (-not $shimLayoutBlocked) {
    throw 'The release Git layout gate accepted a cmd\git.exe shim.'
}

$localGitConfigTestRoot = Join-Path `
    $env:TEMP `
    ('TinyHwBar.LocalGitConfigTests.' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($localGitConfigTestRoot)
try {
    $missingLocalConfigBlocked = $false
    try {
        Assert-SafeLocalGitConfiguration $localGitConfigTestRoot
    }
    catch {
        $missingLocalConfigBlocked = $true
    }
    if (-not $missingLocalConfigBlocked) {
        throw 'The release tool accepted a repository without .git/config.'
    }

    $localGitConfigPath = Join-Path $localGitConfigTestRoot 'config'
    $localGitWorktreeConfigPath = Join-Path $localGitConfigTestRoot 'config.worktree'
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText(
        $localGitConfigPath,
        "[core]`n`trepositoryformatversion = 0`n`tbare = false`n",
        $utf8NoBom)
    Assert-SafeLocalGitConfiguration $localGitConfigTestRoot

    $unsafeLocalConfigFixtures = @(
        "[InClUdE]`n`tpath = C:/outside/config`n",
        "[includeIf `"gitdir:C:/outside/**`"]`n`tpath = C:/outside/config`n",
        "[include.path]`n`tvalue = C:/outside/config`n",
        "[fsck]`n`tmissingEmail = ignore`n",
        "[tar]`n`tzip.command = C:/untrusted/tar-filter.exe`n",
        "[tar `"zip`"]`n`tcommand = C:/untrusted/tar-filter.exe`n",
        "[TaR.ZiP]`n`tcommand = C:/untrusted/tar-filter.exe`n",
        ([string]::Concat(
            "[core]`n`tcomment = continued",
            '\',
            "`n`toutside`n")))
    foreach ($unsafeLocalConfig in $unsafeLocalConfigFixtures) {
        [IO.File]::WriteAllText($localGitConfigPath, $unsafeLocalConfig, $utf8NoBom)
        $unsafeLocalConfigBlocked = $false
        try {
            Assert-SafeLocalGitConfiguration $localGitConfigTestRoot
        }
        catch {
            $unsafeLocalConfigBlocked = $true
        }
        if (-not $unsafeLocalConfigBlocked) {
            throw "The release tool accepted an unsafe local Git config: $unsafeLocalConfig"
        }
    }

    [IO.File]::WriteAllBytes($localGitConfigPath, [byte[]](0xFF))
    $invalidUtf8ConfigBlocked = $false
    try {
        Assert-SafeLocalGitConfiguration $localGitConfigTestRoot
    }
    catch {
        $invalidUtf8ConfigBlocked = $true
    }
    if (-not $invalidUtf8ConfigBlocked) {
        throw 'The release tool accepted invalid UTF-8 in local Git config.'
    }

    [IO.File]::WriteAllBytes($localGitConfigPath, [byte[]](0x5B, 0x63, 0x00, 0x5D))
    $nulConfigBlocked = $false
    try {
        Assert-SafeLocalGitConfiguration $localGitConfigTestRoot
    }
    catch {
        $nulConfigBlocked = $true
    }
    if (-not $nulConfigBlocked) {
        throw 'The release tool accepted a NUL byte in local Git config.'
    }

    [IO.File]::WriteAllText($localGitConfigPath, "[core]`n`tbare = false`n", $utf8NoBom)
    foreach ($unsafeWorktreeConfig in @(
        "[include]`n`tpath = C:/outside/worktree-config`n",
        "[tar `"zip`"]`n`tcommand = C:/untrusted/worktree-tar-filter.exe`n",
        "[tar.zip]`n`tcommand = C:/untrusted/worktree-tar-filter.exe`n")) {
        [IO.File]::WriteAllText(
            $localGitWorktreeConfigPath,
            $unsafeWorktreeConfig,
            $utf8NoBom)
        $unsafeWorktreeConfigBlocked = $false
        try {
            Assert-SafeLocalGitConfiguration $localGitConfigTestRoot
        }
        catch {
            $unsafeWorktreeConfigBlocked = $true
        }
        if (-not $unsafeWorktreeConfigBlocked) {
            throw "The release tool accepted an unsafe config.worktree section: $unsafeWorktreeConfig"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $localGitConfigTestRoot) {
        Remove-Item -LiteralPath $localGitConfigTestRoot -Recurse -Force
    }
}

$gitObjectsTestRoot = Join-Path `
    $env:TEMP `
    ('TinyHwBar.GitObjectsPathTests.' + [Guid]::NewGuid().ToString('N'))
$gitObjectsTestMetadata = Join-Path $gitObjectsTestRoot '.git'
$gitObjectsTestExpected = Join-Path $gitObjectsTestMetadata 'objects'
[void][IO.Directory]::CreateDirectory($gitObjectsTestExpected)
try {
    $relativeObjectsResult = Get-ValidatedGitObjectsDirectory `
        @('.git\objects') `
        $gitObjectsTestRoot `
        $gitObjectsTestMetadata
    if (-not (Test-PathEquals $relativeObjectsResult $gitObjectsTestExpected)) {
        throw 'The relative Git object-directory fixture did not resolve to .git\objects.'
    }

    $unsafeObjectsFixtures = @(
        [pscustomobject]@{ Output = [object[]]@('') },
        [pscustomobject]@{
            Output = [object[]]@((Join-Path $env:TEMP 'outside-git-objects'))
        },
        [pscustomobject]@{ Output = [object[]]@('.git\objects', '.git\objects') })
    foreach ($unsafeObjectsFixture in $unsafeObjectsFixtures) {
        $unsafeObjectsPathBlocked = $false
        try {
            Get-ValidatedGitObjectsDirectory `
                $unsafeObjectsFixture.Output `
                $gitObjectsTestRoot `
                $gitObjectsTestMetadata | Out-Null
        }
        catch {
            $unsafeObjectsPathBlocked = $true
        }
        if (-not $unsafeObjectsPathBlocked) {
            throw 'The release tool accepted an empty, external, or multi-line Git object path.'
        }
    }
}
finally {
    if (Test-Path -LiteralPath $gitObjectsTestRoot) {
        Remove-Item -LiteralPath $gitObjectsTestRoot -Recurse -Force
    }
}

[long]$writeAuthorityTestMask = (
    [long][Security.AccessControl.FileSystemRights]::WriteData -bor
    [long][Security.AccessControl.FileSystemRights]::AppendData -bor
    [long][Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
    [long][Security.AccessControl.FileSystemRights]::WriteAttributes -bor
    [long][Security.AccessControl.FileSystemRights]::Delete -bor
    [long][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
    [long][Security.AccessControl.FileSystemRights]::ChangePermissions -bor
    [long][Security.AccessControl.FileSystemRights]::TakeOwnership -bor
    0x10000000L -bor
    0x40000000L)
foreach ($genericSddl in @(
    'O:SYG:SYD:(A;;GA;;;WD)',
    'O:SYG:SYD:(A;;GW;;;WD)',
    'O:SYG:SYD:(A;;0x000301bf;;;S-1-5-21-111-222-333-444)')) {
    $genericSecurity = New-Object Security.AccessControl.DirectorySecurity
    $genericSecurity.SetSecurityDescriptorSddlForm($genericSddl)
    $genericRules = @($genericSecurity.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    if ($genericRules.Count -ne 1) {
        throw 'The generic-rights ACL regression fixture did not produce one access rule.'
    }
    [long]$genericRuleBits = (
        [long][int]$genericRules[0].FileSystemRights) -band 4294967295L
    if (($genericRuleBits -band $writeAuthorityTestMask) -eq 0) {
        throw "The release ACL mask does not detect generic write authority: $genericSddl"
    }
}

foreach ($nullDaclSddl in @(
    'O:SYG:SY',
    'O:SYG:SYD:NO_ACCESS_CONTROL')) {
    $nullDaclDescriptor = New-Object `
        Security.AccessControl.RawSecurityDescriptor($nullDaclSddl)
    $daclIsPresent = (
        $nullDaclDescriptor.ControlFlags -band
        [Security.AccessControl.ControlFlags]::DiscretionaryAclPresent) -ne 0
    if ($daclIsPresent -and $null -ne $nullDaclDescriptor.DiscretionaryAcl) {
        throw "The null/absent DACL regression fixture is not fail-closed: $nullDaclSddl"
    }
}
$emptyDaclDescriptor = New-Object `
    Security.AccessControl.RawSecurityDescriptor('O:SYG:SYD:')
if (($emptyDaclDescriptor.ControlFlags -band
        [Security.AccessControl.ControlFlags]::DiscretionaryAclPresent) -eq 0 -or
    $null -eq $emptyDaclDescriptor.DiscretionaryAcl -or
    $emptyDaclDescriptor.DiscretionaryAcl.Count -ne 0) {
    throw 'A present empty DACL was confused with a null or absent DACL.'
}

$trustedInheritanceTestSids = @{
    'S-1-5-18' = $true
}
function Test-InheritedSddlRuleRejected {
    param([string]$Sddl, [bool]$CheckFutureChildren)

    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetSecurityDescriptorSddlForm($Sddl)
    $rules = @($security.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 1) {
        throw "The inherited-rights SDDL fixture did not produce one rule: $Sddl"
    }
    if (-not (Test-ReleaseAclRuleWriteRisk `
        $rules[0] `
        $writeAuthorityTestMask `
        $writeAuthorityTestMask `
        $CheckFutureChildren)) {
        return $false
    }

    $ruleSid = $null
    try {
        $ruleSid = $rules[0].IdentityReference.Translate(
            [Security.Principal.SecurityIdentifier])
    }
    catch {
        return $true
    }
    return $null -eq $ruleSid -or
        -not $trustedInheritanceTestSids.ContainsKey($ruleSid.Value)
}

foreach ($untrustedInheritedSddl in @(
    'O:SYG:SYD:(A;OICIIO;GA;;;WD)',
    'O:SYG:SYD:(A;OICIIO;GW;;;WD)')) {
    if (-not (Test-InheritedSddlRuleRejected $untrustedInheritedSddl $true)) {
        throw "An untrusted future-child write ACE was accepted for a directory: $untrustedInheritedSddl"
    }
}
$trustedInheritedSddl = 'O:SYG:SYD:(A;OICIIO;GA;;;SY)'
if (Test-InheritedSddlRuleRejected $trustedInheritedSddl $true) {
    throw 'A trusted future-child write ACE was rejected for a directory.'
}
$fileOnlyInheritedSddl = 'O:SYG:SYD:(A;OICIIO;GA;;;WD)'
if (Test-InheritedSddlRuleRejected $fileOnlyInheritedSddl $false) {
    throw 'An InheritOnly ACE on a file was treated as a future-child write risk.'
}

$whatIfIntegrationRequired = Test-Path `
    -LiteralPath (Join-Path $root '.git') `
    -PathType Container
$candidateGitPaths = New-Object Collections.Generic.List[string]
$requiredBundledGitPath = $null
if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
    $requiredBundledGitPath = Join-Path `
        $env:USERPROFILE `
        '.cache\codex-runtimes\codex-primary-runtime\dependencies\native\git\mingw64\bin\git.exe'
    $candidateGitPaths.Add($requiredBundledGitPath)
}
if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
    $candidateGitPaths.Add((Join-Path $env:ProgramFiles 'Git\mingw64\bin\git.exe'))
}
$pathGitCommand = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $pathGitCommand) {
    $pathGitDirectory = [IO.Directory]::GetParent($pathGitCommand.Source)
    if ($null -ne $pathGitDirectory) {
        $pathGitRoot = if ([string]::Equals(
            $pathGitDirectory.Name,
            'cmd',
            [StringComparison]::OrdinalIgnoreCase)) {
            $pathGitDirectory.Parent
        }
        elseif ([string]::Equals(
            $pathGitDirectory.Name,
            'bin',
            [StringComparison]::OrdinalIgnoreCase) -and
            $null -ne $pathGitDirectory.Parent -and
            [string]::Equals(
                $pathGitDirectory.Parent.Name,
                'mingw64',
                [StringComparison]::OrdinalIgnoreCase)) {
            $pathGitDirectory.Parent.Parent
        }
        else {
            $null
        }
        if ($null -ne $pathGitRoot) {
            $candidateGitPaths.Add((Join-Path $pathGitRoot.FullName 'mingw64\bin\git.exe'))
        }
    }
}

$gitForWhatIf = $null
$whatIfGitHash = $null
$whatIfGitSignerThumbprint = $null
$candidateFailures = New-Object Collections.Generic.List[string]
foreach ($candidateGitPath in @($candidateGitPaths | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $candidateGitPath -PathType Leaf)) {
        continue
    }
    try {
        $candidateSignature = Get-AuthenticodeSignature -LiteralPath $candidateGitPath
        if ($candidateSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
            $null -eq $candidateSignature.SignerCertificate) {
            continue
        }
        $candidateHash = (Get-FileHash -LiteralPath $candidateGitPath -Algorithm SHA256).Hash
        $candidateSignerThumbprint = $candidateSignature.SignerCertificate.Thumbprint.
            Replace(' ', '').ToUpperInvariant()
        $approvedCandidate = Get-ApprovedGitExecutable `
            $candidateGitPath `
            $candidateHash `
            $candidateSignerThumbprint
        $candidateLock = $null
        try {
            $candidateLock = [IO.File]::Open(
                $approvedCandidate.Path,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::Read)
            $lockedCandidate = Get-ApprovedGitExecutable `
                $approvedCandidate.Path `
                $candidateHash `
                $candidateSignerThumbprint
        }
        finally {
            if ($null -ne $candidateLock) {
                $candidateLock.Dispose()
            }
        }
        $gitForWhatIf = $lockedCandidate.Path
        $whatIfGitHash = $lockedCandidate.Sha256
        $whatIfGitSignerThumbprint = $lockedCandidate.SignerThumbprint
        break
    }
    catch {
        $candidateFailures.Add(
            ($candidateGitPath + ': ' + $_.Exception.Message))
        continue
    }
}

if ($whatIfIntegrationRequired -and
    $null -ne $requiredBundledGitPath -and
    (Test-Path -LiteralPath $requiredBundledGitPath -PathType Leaf) -and
    $null -eq $gitForWhatIf) {
    throw (
        'A bundled real Git candidate exists, but none passed the release trust gate: ' +
        ($candidateFailures -join ' | '))
}

$whatIfIntegrationExecuted = $false
if ($whatIfIntegrationRequired -and $null -ne $gitForWhatIf) {
    $whatIfRepoOutput = & $gitForWhatIf -C $root rev-parse --show-toplevel 2>$null
    $whatIfRepoExit = $LASTEXITCODE
    if ($whatIfRepoExit -eq 0 -and @($whatIfRepoOutput).Count -eq 1 -and
        (Test-PathEquals ([string]@($whatIfRepoOutput)[0]) $root)) {
        $whatIfCommitOutput = & $gitForWhatIf -C $root rev-parse HEAD 2>$null
        if ($LASTEXITCODE -ne 0 -or @($whatIfCommitOutput).Count -ne 1) {
            throw 'Could not resolve the full commit required for the release WhatIf test.'
        }
        $whatIfCommit = ([string]@($whatIfCommitOutput)[0]).Trim()
        $whatIfWorkspaceRoot = Split-Path -Parent $root
        $whatIfStaging = Join-Path `
            $whatIfWorkspaceRoot `
            ('staging\installer-script-test-' + [Guid]::NewGuid().ToString('N'))

        $whatIfWorkspaceTrusted = $false
        try {
            if ((Test-IsDescendantPath $whatIfWorkspaceRoot $root) -and
                (Test-Path -LiteralPath (Join-Path $root '.git') -PathType Container)) {
                Assert-TrustedFileSystemBoundary `
                    $whatIfWorkspaceRoot `
                    'Prepare-Release WhatIf workspace fixture' `
                    50000
                $whatIfWorkspaceTrusted = $true
            }
        }
        catch {
            $whatIfWorkspaceTrusted = $false
        }

        $testIdentity = $null
        try {
            $testIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
            $testPrincipal = [Security.Principal.WindowsPrincipal]::new($testIdentity)
            $testProcessIsAdministrator = $testPrincipal.IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
        }
        finally {
            if ($null -ne $testIdentity) {
                $testIdentity.Dispose()
            }
        }

        $windowsPowerShell = Join-Path $PSHOME 'powershell.exe'
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $whatIfOutput = & $windowsPowerShell `
                -NoProfile `
                -NonInteractive `
                -ExecutionPolicy Bypass `
                -File $releaseScript `
                -Version '2.0.0' `
                -Commit $whatIfCommit `
                -GitExe $gitForWhatIf `
                -ApprovedGitSha256 $whatIfGitHash `
                -ApprovedGitSignerThumbprint $whatIfGitSignerThumbprint `
                -TrustedWorkspaceRoot $whatIfWorkspaceRoot `
                -StagingRoot $whatIfStaging `
                -WhatIf 2>&1
            $whatIfExit = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if (Test-Path -LiteralPath $whatIfStaging) {
            throw 'Prepare-Release -WhatIf created its staging directory.'
        }
        $whatIfShouldSucceed = -not $testProcessIsAdministrator -and $whatIfWorkspaceTrusted
        if (($whatIfShouldSucceed -and $whatIfExit -ne 0) -or
            (-not $whatIfShouldSucceed -and $whatIfExit -eq 0)) {
            throw (
                "Prepare-Release -WhatIf returned unexpected exit code $whatIfExit. " +
                ($whatIfOutput -join ' | '))
        }
        $whatIfIntegrationExecuted = $true
    }
}
if ($whatIfIntegrationRequired -and
    $null -ne $requiredBundledGitPath -and
    (Test-Path -LiteralPath $requiredBundledGitPath -PathType Leaf) -and
    -not $whatIfIntegrationExecuted) {
    throw 'The bundled real Git exists, but the Prepare-Release -WhatIf integration test did not run.'
}

$testCommit = '0123456789abcdef0123456789abcdef01234567'
$binaryReadme = [IO.File]::ReadAllText($binaryReadmeTemplate).
    Replace('{{VERSION}}', '2.0.0').
    Replace('{{COMMIT}}', $testCommit).
    Replace('{{ASSET_NAME}}', 'TinyHwBar-v2.0.0-win-x64.zip').
    Replace('{{SHA256_FILE}}', 'SHA256SUMS.txt').
    Replace('{{REPOSITORY_URL}}', 'https://github.com/Chuyu03/TinyHwBar')
Assert-BinaryReadmeLinks $binaryReadme @('LICENSE', 'README.md', 'TinyHwBar.exe')
if ($binaryReadme.IndexOf($testCommit, [StringComparison]::Ordinal) -lt 0 -or
    $binaryReadme.Contains('{{')) {
    throw 'The binary README did not bind its public source links to the frozen full commit ID.'
}
$missingLinkBlocked = $false
try {
    Assert-BinaryReadmeLinks '[missing](docs/not-in-bundle.md)' @('LICENSE', 'README.md', 'TinyHwBar.exe')
}
catch {
    $missingLinkBlocked = $true
}
if (-not $missingLinkBlocked) {
    throw 'The binary README link check accepted a file absent from the bundle allowlist.'
}
foreach ($unsafeReadme in @(
    '[external](https://example.com/not-approved)',
    "[reference][id]`n`n[id]: LICENSE",
    '<img src="LICENSE">',
    '<iframe src="//example.com/tracker"></iframe>',
    '<video src="//example.com/tracker.mp4"></video>',
    '<form action="//example.com/collect"></form>',
    'h&#116;tps://example.com/entity-obfuscated',
    '//example.com/protocol-relative',
    'https://github.com/Chuyu03/TinyHwBar')) {
    $unsafeReadmeBlocked = $false
    try {
        Assert-BinaryReadmeLinks `
            $unsafeReadme `
            @('LICENSE', 'README.md', 'TinyHwBar.exe')
    }
    catch {
        $unsafeReadmeBlocked = $true
    }
    if (-not $unsafeReadmeBlocked) {
        throw "The binary README link check accepted an unsupported link form: $unsafeReadme"
    }
}

function Get-TestFileHash {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-TestFile {
    param([string]$Path, [string]$Content)

    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

function Assert-CliMetadataByteMutationRejected {
    param(
        [string]$Source,
        [string]$Destination,
        [long]$Offset,
        [byte]$XorMask,
        [Version]$ExpectedVersion,
        [string]$Description)

    [IO.File]::Copy($Source, $Destination, $false)
    $bytes = [IO.File]::ReadAllBytes($Destination)
    if ($Offset -lt 0 -or $Offset -ge $bytes.LongLength -or $XorMask -eq 0) {
        throw "The CLI metadata mutation fixture has an invalid offset or mask: $Description"
    }
    $bytes[[int]$Offset] = [byte]($bytes[[int]$Offset] -bxor $XorMask)
    [IO.File]::WriteAllBytes($Destination, $bytes)

    $directParserBlocked = $false
    try {
        Get-TinyHwBarCliAssemblyMetadata $Destination $ExpectedVersion | Out-Null
    }
    catch {
        $directParserBlocked = $true
    }
    $installerGateBlocked = $false
    try {
        Get-TinyHwBarExecutableMetadata $Destination | Out-Null
    }
    catch {
        $installerGateBlocked = $true
    }
    $releaseGateBlocked = $false
    try {
        $releaseVersion = '{0}.{1}.{2}' -f
            $ExpectedVersion.Major,
            $ExpectedVersion.Minor,
            $ExpectedVersion.Build
        Assert-TinyHwBarExecutable $Destination $releaseVersion
    }
    catch {
        $releaseGateBlocked = $true
    }
    if (-not $directParserBlocked -or
        -not $installerGateBlocked -or
        -not $releaseGateBlocked) {
        throw "A critical PE/CLI metadata mutation was accepted: $Description"
    }
}

$temporaryRoot = Join-Path `
    $env:TEMP `
    ('TinyHwBar.InstallerScriptTests.' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $invalidExecutable = Join-Path $temporaryRoot 'TinyHwBar.exe'
    Write-TestFile $invalidExecutable 'not a Windows executable'
    $invalidExecutableBlocked = $false
    try {
        Get-TinyHwBarExecutableMetadata $invalidExecutable | Out-Null
    }
    catch {
        $invalidExecutableBlocked = $true
    }
    if (-not $invalidExecutableBlocked) {
        throw 'The installer identity gate accepted a non-PE source executable.'
    }

    if ($null -ne $builtCliMetadata) {
        $builtBytes = [IO.File]::ReadAllBytes($builtExecutable)
        if ($builtBytes.LongLength -lt 64) {
            throw 'The real outputs executable became too small for PE mutation tests.'
        }
        [long]$builtPeOffset = [BitConverter]::ToInt32($builtBytes, 0x3C)
        $cliMutations = @(
            [pscustomobject]@{
                Name = 'invalid-pe-signature'
                Offset = $builtPeOffset
                XorMask = [byte]0x01
            },
            [pscustomobject]@{
                Name = 'unsafe-clr-flags'
                Offset = $builtCliMetadata.ClrFlagsOffset
                XorMask = [byte]0x02
            },
            [pscustomobject]@{
                Name = 'invalid-bsjb-signature'
                Offset = $builtCliMetadata.MetadataRootOffset
                XorMask = [byte]0x01
            },
            [pscustomobject]@{
                Name = 'assembly-version-mismatch'
                Offset = $builtCliMetadata.AssemblyVersionOffset
                XorMask = [byte]0x01
            },
            [pscustomobject]@{
                Name = 'assembly-name-mismatch'
                Offset = $builtCliMetadata.AssemblyNameOffset
                XorMask = [byte]0x01
            })
        foreach ($cliMutation in $cliMutations) {
            $mutatedExecutable = Join-Path `
                $temporaryRoot `
                ($cliMutation.Name + '.exe')
            Assert-CliMetadataByteMutationRejected `
                $builtExecutable `
                $mutatedExecutable `
                $cliMutation.Offset `
                $cliMutation.XorMask `
                $builtExecutableMetadata.FileVersion `
                $cliMutation.Name
        }
    }

    $approvedSource = Join-Path $temporaryRoot 'approved-source.bin'
    $approvedDestination = Join-Path $temporaryRoot 'approved-destination.bin'
    Write-TestFile $approvedSource 'approved bytes'
    $approvedSourceHash = Get-TestFileHash $approvedSource
    Write-TestFile $approvedSource 'replacement bytes'
    $changedApprovedSourceBlocked = $false
    try {
        Copy-FileWithHashVerification `
            $approvedSource `
            $approvedDestination `
            $approvedSourceHash | Out-Null
    }
    catch {
        $changedApprovedSourceBlocked = $true
    }
    if (-not $changedApprovedSourceBlocked -or
        (Test-Path -LiteralPath $approvedDestination)) {
        throw 'A source changed after approval was copied into the installation transaction.'
    }

    $stableSource = Join-Path $temporaryRoot 'stable-source.bin'
    $stableDestination = Join-Path $temporaryRoot 'stable-destination.bin'
    Write-TestFile $stableSource 'stable approved bytes'
    $stableSourceHash = Get-TestFileHash $stableSource
    $stableDestinationHash = Copy-FileWithHashVerification `
        $stableSource `
        $stableDestination `
        $stableSourceHash
    if (-not [string]::Equals(
        $stableSourceHash,
        $stableDestinationHash,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A stable approved source did not preserve its trusted hash while staging.'
    }

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

Write-Host 'PASS: installer, uninstaller, and release script safety tests'

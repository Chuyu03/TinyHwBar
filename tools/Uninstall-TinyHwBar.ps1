#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$RemoveUserData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NonElevatedProcess {
    param([string]$Operation)

    $identity = $null
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw (
                "$Operation must run from a normal, non-administrator Windows PowerShell session. " +
                'Close the elevated shell and retry without Run as administrator.')
        }
    }
    finally {
        if ($null -ne $identity) {
            $identity.Dispose()
        }
    }
}

Assert-NonElevatedProcess 'TinyHwBar uninstallation'

$ownershipMarkerName = '.tinyhwbar-install-owner'
$ownershipMarkerContent = 'TinyHwBar.UserInstall|2'
$uninstallerName = 'Uninstall-TinyHwBar.ps1'
$uninstallRegistrySubKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\TinyHwBar'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TinyHwBar'
$startupRegistrySubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupName = 'TinyHwBar'
$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ([string]::IsNullOrWhiteSpace($currentUserSid)) {
    throw 'Windows did not return the current user SID required for cross-session maintenance locking.'
}
$maintenanceMutexName = 'Global\TinyHwBar.Maintenance.' + $currentUserSid
$appSingletonMutexName = 'Global\TinyHwBar.Singleton.' + $currentUserSid
$legacySingletonMutexName = 'Local\TinyHwBar.Singleton'

function Get-TrustedSpecialFolderPath {
    param([Environment+SpecialFolder]$Folder)

    $path = [Environment]::GetFolderPath($Folder)
    if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathRooted($path)) {
        throw "Windows did not return a trusted path for $Folder."
    }

    return [IO.Path]::GetFullPath($path).TrimEnd('\')
}

function Test-PathEquals {
    param([string]$Left, [string]$Right)

    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) {
        return $false
    }

    return [string]::Equals(
        [IO.Path]::GetFullPath($Left).TrimEnd('\'),
        [IO.Path]::GetFullPath($Right).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-TinyHwBarProcessOwnerSid {
    param($Process)

    try {
        $ownerResult = Invoke-CimMethod `
            -InputObject $Process `
            -MethodName GetOwnerSid `
            -ErrorAction Stop
        if ($null -eq $ownerResult -or
            $ownerResult.ReturnValue -ne 0 -or
            [string]::IsNullOrWhiteSpace([string]$ownerResult.Sid)) {
            return $null
        }
        return ([string]$ownerResult.Sid).Trim()
    }
    catch {
        return $null
    }
}

function Get-TinyHwBarProcessBlockers {
    param(
        [object[]]$Processes,
        [string]$CurrentUserSid,
        [string]$DestinationExe,
        [scriptblock]$OwnerSidResolver)

    if ([string]::IsNullOrWhiteSpace($CurrentUserSid) -or
        [string]::IsNullOrWhiteSpace($DestinationExe)) {
        throw 'Process blocker selection requires the current user SID and installed executable path.'
    }

    $blockingProcesses = @()
    $unresolvedOwnerProcessIds = @()
    foreach ($process in @($Processes)) {
        if ($null -eq $process) {
            continue
        }

        $executablePath = [string]$process.ExecutablePath
        if (-not [string]::IsNullOrWhiteSpace($executablePath) -and
            (Test-PathEquals $executablePath $DestinationExe)) {
            $blockingProcesses += ,$process
            continue
        }

        try {
            $ownerSidValues = @(
                if ($null -eq $OwnerSidResolver) {
                    Get-TinyHwBarProcessOwnerSid $process
                }
                else {
                    & $OwnerSidResolver $process
                })
        }
        catch {
            $ownerSidValues = @()
        }

        if ($ownerSidValues.Count -ne 1 -or
            [string]::IsNullOrWhiteSpace([string]$ownerSidValues[0])) {
            $processIdProperty = $process.PSObject.Properties['ProcessId']
            $unresolvedOwnerProcessIds += if ($null -eq $processIdProperty) {
                '<unknown>'
            }
            else {
                [string]$processIdProperty.Value
            }
            continue
        }

        $ownerSid = ([string]$ownerSidValues[0]).Trim()
        if ([string]::Equals(
            $ownerSid,
            $CurrentUserSid,
            [StringComparison]::OrdinalIgnoreCase)) {
            $blockingProcesses += ,$process
        }
    }

    return [pscustomobject]@{
        BlockingProcesses = @($blockingProcesses)
        UnresolvedOwnerProcessIds = @($unresolvedOwnerProcessIds)
    }
}

function Write-TinyHwBarProcessOwnerWarning {
    param($ProcessCheck)

    $unresolvedCount = @($ProcessCheck.UnresolvedOwnerProcessIds).Count
    if ($unresolvedCount -eq 0) {
        return
    }

    Write-Warning (
        "Windows could not resolve the owner SID for $unresolvedCount other normally named " +
        'TinyHwBar.exe process(es). They were not treated as blockers because their executable ' +
        'paths did not match this user''s installed TinyHwBar. New versions remain covered by the ' +
        'current-user cross-session mutex; the residual uncertainty is limited to a legacy copy in ' +
        'another session when Windows also denies its owner query. Ensure every TinyHwBar copy for ' +
        'this Windows user is closed before continuing.') -WarningAction Continue
}

function Assert-DirectChildPath {
    param([string]$Parent, [string]$Child)

    $parentPrefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $fullChild = [IO.Path]::GetFullPath($Child)
    if (-not $fullChild.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside the trusted root: $fullChild"
    }
}

function Assert-NotReparsePoint {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to use a reparse point: $Path"
    }
}

function Assert-NoReparseTree {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return
    }

    Assert-NotReparsePoint $Root
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively remove user data containing a reparse point: $($item.FullName)"
        }
    }
}

function Assert-RegularFileOrMissing {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected a regular file: $Path"
    }

    Assert-NotReparsePoint $Path
}

function Get-ShortcutInfo {
    param([string]$ShortcutPath)

    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
        return $null
    }

    Assert-NotReparsePoint $ShortcutPath
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        return [pscustomobject]@{
            TargetPath = $shortcut.TargetPath
            Arguments = $shortcut.Arguments
            WorkingDirectory = $shortcut.WorkingDirectory
            Description = $shortcut.Description
            IconLocation = $shortcut.IconLocation
            Hotkey = $shortcut.Hotkey
            WindowStyle = $shortcut.WindowStyle
        }
    }
    finally {
        if ($null -ne $shortcut -and [Runtime.InteropServices.Marshal]::IsComObject($shortcut)) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
        }
        if ($null -ne $shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
    }
}

function Test-TinyHwBarShortcutOwned {
    param($ShortcutInfo, [string]$ExecutablePath, [string]$WorkingDirectory)

    if ($null -eq $ShortcutInfo) {
        return $false
    }

    return (Test-PathEquals $ShortcutInfo.TargetPath $ExecutablePath) -and
        [string]::IsNullOrEmpty($ShortcutInfo.Arguments) -and
        (Test-PathEquals $ShortcutInfo.WorkingDirectory $WorkingDirectory) -and
        [string]::Equals(
            $ShortcutInfo.Description,
            'TinyHwBar hardware monitor',
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            $ShortcutInfo.IconLocation,
            ($ExecutablePath + ',0'),
            [StringComparison]::Ordinal) -and
        [string]::IsNullOrEmpty($ShortcutInfo.Hotkey) -and
        $ShortcutInfo.WindowStyle -eq 1
}

function Get-RegistryKeySnapshot {
    param([string]$SubKeyPath)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKeyPath, $false)
    if ($null -eq $key) {
        return [pscustomobject]@{ Exists = $false; Values = @(); SubKeyNames = @() }
    }

    try {
        $values = @()
        foreach ($name in $key.GetValueNames()) {
            $values += [pscustomobject]@{
                Name = $name
                Value = $key.GetValue(
                    $name,
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                Kind = $key.GetValueKind($name)
            }
        }

        return [pscustomobject]@{
            Exists = $true
            Values = $values
            SubKeyNames = @($key.GetSubKeyNames())
        }
    }
    finally {
        $key.Dispose()
    }
}

function Get-SnapshotValue {
    param($Snapshot, [string]$Name)

    foreach ($entry in $Snapshot.Values) {
        if ([string]::Equals($entry.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $entry
        }
    }

    return $null
}

function Test-UninstallKeyOwned {
    param(
        $Snapshot,
        [string]$ExpectedInstallRoot,
        [string]$ExpectedUninstallCommand,
        [string]$ExpectedDisplayVersion)

    if (-not $Snapshot.Exists -or
        $Snapshot.SubKeyNames.Count -ne 0 -or
        $Snapshot.Values.Count -ne 7 -or
        [string]::IsNullOrWhiteSpace($ExpectedDisplayVersion)) {
        return $false
    }

    $displayName = Get-SnapshotValue $Snapshot 'DisplayName'
    $displayVersion = Get-SnapshotValue $Snapshot 'DisplayVersion'
    $publisher = Get-SnapshotValue $Snapshot 'Publisher'
    $installLocation = Get-SnapshotValue $Snapshot 'InstallLocation'
    $uninstallString = Get-SnapshotValue $Snapshot 'UninstallString'
    $noModify = Get-SnapshotValue $Snapshot 'NoModify'
    $noRepair = Get-SnapshotValue $Snapshot 'NoRepair'
    return $null -ne $displayName -and
        $displayName.Kind -eq [Microsoft.Win32.RegistryValueKind]::String -and
        [string]::Equals([string]$displayName.Value, 'TinyHwBar', [StringComparison]::Ordinal) -and
        $null -ne $displayVersion -and
        $displayVersion.Kind -eq [Microsoft.Win32.RegistryValueKind]::String -and
        [string]::Equals(
            [string]$displayVersion.Value,
            $ExpectedDisplayVersion,
            [StringComparison]::Ordinal) -and
        $null -ne $publisher -and
        $publisher.Kind -eq [Microsoft.Win32.RegistryValueKind]::String -and
        [string]::Equals(
            [string]$publisher.Value,
            'TinyHwBar contributors',
            [StringComparison]::Ordinal) -and
        $null -ne $installLocation -and
        $installLocation.Kind -eq [Microsoft.Win32.RegistryValueKind]::String -and
        (Test-PathEquals ([string]$installLocation.Value) $ExpectedInstallRoot) -and
        $null -ne $uninstallString -and
        $uninstallString.Kind -eq [Microsoft.Win32.RegistryValueKind]::String -and
        [string]::Equals(
            [string]$uninstallString.Value,
            $ExpectedUninstallCommand,
            [StringComparison]::Ordinal) -and
        $null -ne $noModify -and
        $noModify.Kind -eq [Microsoft.Win32.RegistryValueKind]::DWord -and
        [int]$noModify.Value -eq 1 -and
        $null -ne $noRepair -and
        $noRepair.Kind -eq [Microsoft.Win32.RegistryValueKind]::DWord -and
        [int]$noRepair.Value -eq 1
}

function Restore-RegistryKeySnapshot {
    param(
        $Snapshot,
        [string]$SubKeyPath,
        [string]$ExpectedInstallRoot,
        [string]$ExpectedUninstallCommand,
        [string]$ExpectedDisplayVersion)

    $currentSnapshot = Get-RegistryKeySnapshot $SubKeyPath
    if ($currentSnapshot.Exists -and
        -not (Test-UninstallKeyOwned `
            $currentSnapshot `
            $ExpectedInstallRoot `
            $ExpectedUninstallCommand `
            $ExpectedDisplayVersion)) {
        throw 'An unrelated uninstall registry key appeared during rollback and was preserved.'
    }

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKeyPath)
    if ($null -eq $key) {
        throw 'Could not restore the uninstall registry key.'
    }

    try {
        foreach ($name in $key.GetValueNames()) {
            $key.DeleteValue($name, $false)
        }
        foreach ($entry in $Snapshot.Values) {
            $key.SetValue($entry.Name, $entry.Value, $entry.Kind)
        }
    }
    finally {
        $key.Dispose()
    }
}

function Remove-OwnedUninstallKey {
    param(
        [string]$SubKeyPath,
        [string]$ExpectedInstallRoot,
        [string]$ExpectedUninstallCommand,
        [string]$ExpectedDisplayVersion)

    $currentSnapshot = Get-RegistryKeySnapshot $SubKeyPath
    if (-not (Test-UninstallKeyOwned `
        $currentSnapshot `
        $ExpectedInstallRoot `
        $ExpectedUninstallCommand `
        $ExpectedDisplayVersion)) {
        throw 'The uninstall registry key changed and was not removed.'
    }

    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($SubKeyPath, $false)
}

function Get-RunValueSnapshot {
    param([string]$SubKeyPath, [string]$ValueName)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKeyPath, $false)
    if ($null -eq $key) {
        return [pscustomobject]@{ Exists = $false; Value = $null; Kind = $null }
    }

    try {
        if (-not ($key.GetValueNames() -contains $ValueName)) {
            return [pscustomobject]@{ Exists = $false; Value = $null; Kind = $null }
        }

        return [pscustomobject]@{
            Exists = $true
            Value = $key.GetValue(
                $ValueName,
                $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            Kind = $key.GetValueKind($ValueName)
        }
    }
    finally {
        $key.Dispose()
    }
}

function Remove-RunValueIfExact {
    param([string]$SubKeyPath, [string]$ValueName, [string]$ExpectedCommand)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKeyPath, $true)
    if ($null -eq $key) {
        throw 'The startup registration changed and was not removed.'
    }

    try {
        $current = $key.GetValue(
            $ValueName,
            $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if (-not ($current -is [string]) -or
            -not [string]::Equals(
                $current,
                $ExpectedCommand,
                [StringComparison]::Ordinal)) {
            throw 'The startup registration changed and was not removed.'
        }

        $key.DeleteValue($ValueName, $false)
    }
    finally {
        $key.Dispose()
    }
}

function Restore-RunValueSnapshot {
    param($Snapshot, [string]$SubKeyPath, [string]$ValueName, [string]$ExpectedCommand)

    $current = Get-RunValueSnapshot $SubKeyPath $ValueName
    if ($current.Exists -and
        (-not ($current.Value -is [string]) -or
         -not [string]::Equals(
             $current.Value,
             $ExpectedCommand,
             [StringComparison]::Ordinal))) {
        throw 'An unrelated startup registration appeared during rollback and was preserved.'
    }

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKeyPath)
    if ($null -eq $key) {
        throw 'Could not restore the startup registration.'
    }

    try {
        $key.SetValue($ValueName, $Snapshot.Value, $Snapshot.Kind)
    }
    finally {
        $key.Dispose()
    }
}

function Copy-FileWithHashVerification {
    param([string]$Source, [string]$Destination)

    Assert-RegularFileOrMissing $Source
    $destinationParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Destination))
    Assert-NotReparsePoint $destinationParent
    if (Test-Path -LiteralPath $Destination) {
        throw "Refusing to overwrite an existing transaction backup: $Destination"
    }

    [IO.File]::Copy($Source, $Destination, $false)
    Assert-FileHashesEqual $Source $Destination
    return (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
}

function Assert-FileHashesEqual {
    param([string]$ExpectedFile, [string]$ActualFile)

    $sourceHash = (Get-FileHash -LiteralPath $ExpectedFile -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $ActualFile -Algorithm SHA256).Hash
    if (-not [string]::Equals($sourceHash, $destinationHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 verification failed while copying: $ExpectedFile"
    }
}

function Assert-FileHashMatchesExpected {
    param([string]$Path, [string]$ExpectedHash, [string]$Description)

    if ([string]::IsNullOrWhiteSpace($ExpectedHash)) {
        throw "$Description does not have a trusted SHA-256 snapshot: $Path"
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
    Assert-RegularFileOrMissing $Path

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not [string]::Equals(
        $ExpectedHash,
        $actualHash,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description changed after it was created: $Path"
    }
}

function Remove-KnownTransactionDirectory {
    param([string]$TransactionRoot, [string[]]$KnownFiles)

    if (-not (Test-Path -LiteralPath $TransactionRoot -PathType Container)) {
        return
    }

    Assert-NotReparsePoint $TransactionRoot
    foreach ($path in $KnownFiles) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Assert-NotReparsePoint $path
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }

    if (@(Get-ChildItem -LiteralPath $TransactionRoot -Force).Count -eq 0) {
        Remove-Item -LiteralPath $TransactionRoot -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $TransactionRoot) {
        Write-Warning "Temporary uninstall transaction cleanup was incomplete; review this owned path: $TransactionRoot"
    }
}

$localAppData = Get-TrustedSpecialFolderPath ([Environment+SpecialFolder]::LocalApplicationData)
$roamingAppData = Get-TrustedSpecialFolderPath ([Environment+SpecialFolder]::ApplicationData)
$systemDirectory = Get-TrustedSpecialFolderPath ([Environment+SpecialFolder]::System)
$programsRoot = Join-Path $localAppData 'Programs'
$installRoot = Join-Path $programsRoot 'TinyHwBar'
$destinationExe = Join-Path $installRoot 'TinyHwBar.exe'
$destinationUninstaller = Join-Path $installRoot $uninstallerName
$markerPath = Join-Path $installRoot $ownershipMarkerName
$shortcutPath = Join-Path $roamingAppData 'Microsoft\Windows\Start Menu\Programs\TinyHwBar.lnk'
$shortcutDirectory = [IO.Path]::GetDirectoryName($shortcutPath)
$userDataRoot = Join-Path $localAppData 'TinyHwBar'
$powershellExe = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\powershell.exe'
$expectedStartupCommand = '"' + $destinationExe + '"'
$expectedUninstallCommand = '"' + $powershellExe + '" -NoProfile -ExecutionPolicy Bypass -File "' +
    $destinationUninstaller + '"'

Assert-DirectChildPath $localAppData $installRoot
Assert-DirectChildPath $localAppData $userDataRoot
Assert-NotReparsePoint $programsRoot

$installOwned = $false
if (Test-Path -LiteralPath $installRoot) {
    if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
        throw "The install path exists but is not a directory: $installRoot"
    }

    Assert-NotReparsePoint $installRoot
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Refusing to remove an unowned install directory: $installRoot"
    }
    Assert-NotReparsePoint $markerPath
    if (-not [string]::Equals(
        [IO.File]::ReadAllText($markerPath),
        $ownershipMarkerContent,
        [StringComparison]::Ordinal)) {
        throw "Refusing to remove an install directory with an invalid ownership marker: $installRoot"
    }
    $installOwned = $true
}

foreach ($knownPath in @($destinationExe, $destinationUninstaller, $markerPath)) {
    Assert-RegularFileOrMissing $knownPath
}

$runningTinyHwBarProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'TinyHwBar.exe'")
$runningInstalledProcess = $runningTinyHwBarProcesses |
    Where-Object {
        $_.ExecutablePath -and (Test-PathEquals $_.ExecutablePath $destinationExe)
    }
if ($runningInstalledProcess) {
    throw 'TinyHwBar is running. Exit it from the tray before uninstalling.'
}

$runSnapshot = Get-RunValueSnapshot $startupRegistrySubKey $startupName
$startupOwned = $installOwned -and
    $runSnapshot.Exists -and
    ($runSnapshot.Value -is [string]) -and
    [string]::Equals(
        $runSnapshot.Value,
        $expectedStartupCommand,
        [StringComparison]::Ordinal)

$shortcutExists = Test-Path -LiteralPath $shortcutPath -PathType Leaf
$shortcutOwned = $installOwned -and $shortcutExists -and
    (Test-TinyHwBarShortcutOwned `
        (Get-ShortcutInfo $shortcutPath) `
        $destinationExe `
        $installRoot)

$uninstallSnapshot = Get-RegistryKeySnapshot $uninstallRegistrySubKey
$expectedDisplayVersion = $null
if (Test-Path -LiteralPath $destinationExe -PathType Leaf) {
    $installedVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($destinationExe)
    $expectedDisplayVersion = $installedVersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($expectedDisplayVersion)) {
        $expectedDisplayVersion = $installedVersionInfo.FileVersion
    }
    if ([string]::IsNullOrWhiteSpace($expectedDisplayVersion)) {
        $expectedDisplayVersion = '3.0.0'
    }
}
$uninstallKeyOwned = $installOwned -and
    (Test-UninstallKeyOwned `
        $uninstallSnapshot `
        $installRoot `
        $expectedUninstallCommand `
        $expectedDisplayVersion)
$anyOwnedArtifact = $installOwned -or $startupOwned -or $shortcutOwned -or $uninstallKeyOwned

$removeUserDataRequested = [bool]$RemoveUserData
$maintenanceMutexRequired = $anyOwnedArtifact -or $removeUserDataRequested
$userDataPathExists = Test-Path -LiteralPath $userDataRoot
if ($removeUserDataRequested -and $userDataPathExists -and
    -not (Test-Path -LiteralPath $userDataRoot -PathType Container)) {
    throw "Refusing to remove a user-data path that is not a directory: $userDataRoot"
}
$removeUserDataNow = $false
if ($removeUserDataRequested) {
    $initialUserProcessCheck = Get-TinyHwBarProcessBlockers `
        $runningTinyHwBarProcesses `
        $currentUserSid `
        $destinationExe
    Write-TinyHwBarProcessOwnerWarning $initialUserProcessCheck
    if (@($initialUserProcessCheck.BlockingProcesses).Count -ne 0) {
        throw ('A TinyHwBar process for this Windows user, or a process using this user''s installed ' +
            'TinyHwBar path, is running. Exit every installed or portable copy for this Windows ' +
            'user before removing the shared user-data directory.')
    }
    if ($userDataPathExists) {
        Assert-NoReparseTree $userDataRoot
    }
}

$fileScopeApproved = $true
if ($installOwned) {
    $fileScopeApproved = $PSCmdlet.ShouldProcess(
        $installRoot,
        'Remove only TinyHwBar.exe, the uninstaller, and the ownership marker; remove the directory only if empty')
}
$startupScopeApproved = $true
if ($startupOwned) {
    $startupScopeApproved = $PSCmdlet.ShouldProcess(
        "$startupKey::$startupName",
        'Remove the exact TinyHwBar-owned startup command')
}
$shortcutScopeApproved = $true
if ($shortcutOwned) {
    $shortcutScopeApproved = $PSCmdlet.ShouldProcess(
        $shortcutPath,
        'Remove the Start Menu shortcut that exactly targets this installation')
}
$registryScopeApproved = $true
if ($uninstallKeyOwned) {
    $registryScopeApproved = $PSCmdlet.ShouldProcess(
        $uninstallKey,
        'Remove the TinyHwBar-owned current-user uninstall registration')
}
$userDataScopeApproved = $true
if ($removeUserDataRequested) {
    $userDataScopeApproved = $PSCmdlet.ShouldProcess(
        $userDataRoot,
        'Permanently remove the entire TinyHwBar user-data directory if present, including unknown files; this deletion has no rollback')
}

if (-not ($fileScopeApproved -and $startupScopeApproved -and $shortcutScopeApproved -and
    $registryScopeApproved -and $userDataScopeApproved)) {
    return
}

$maintenanceMutex = $null
$maintenanceMutexAcquired = $false
$appSingletonMutex = $null
$appSingletonMutexAcquired = $false
$legacySingletonMutex = $null
$legacySingletonMutexAcquired = $false
$userDataRemoved = $false
$userDataRemovalFailed = $false
$userDataRemovalFailureMessage = $null

try {
    if ($maintenanceMutexRequired) {
        $maintenanceMutex = [Threading.Mutex]::new($false, $maintenanceMutexName)
        try {
            $maintenanceMutexAcquired = $maintenanceMutex.WaitOne(0, $false)
        }
        catch [Threading.AbandonedMutexException] {
            $maintenanceMutexAcquired = $true
        }

        if (-not $maintenanceMutexAcquired) {
            throw 'Another TinyHwBar installation or uninstallation is active for this Windows user, possibly in another session. No installation artifacts or user data were changed; retry after it finishes.'
        }

        $appSingletonMutex = [Threading.Mutex]::new($false, $appSingletonMutexName)
        try {
            $appSingletonMutexAcquired = $appSingletonMutex.WaitOne(0, $false)
        }
        catch [Threading.AbandonedMutexException] {
            $appSingletonMutexAcquired = $true
        }

        if (-not $appSingletonMutexAcquired) {
            if ($removeUserDataRequested) {
                throw 'A TinyHwBar instance for this Windows user, possibly in another session, is using the shared user-data directory. No installation artifacts or user data were changed; exit every installed or portable copy and retry.'
            }
            throw 'A TinyHwBar instance is running for this Windows user, possibly in another session. No installation artifacts were changed; exit every installed or portable copy and retry.'
        }

        $legacySingletonMutex = [Threading.Mutex]::new($false, $legacySingletonMutexName)
        try {
            $legacySingletonMutexAcquired = $legacySingletonMutex.WaitOne(0, $false)
        }
        catch [Threading.AbandonedMutexException] {
            $legacySingletonMutexAcquired = $true
        }

        if (-not $legacySingletonMutexAcquired) {
            if ($removeUserDataRequested) {
                throw 'A legacy current-session TinyHwBar instance is using the shared user-data directory. No installation artifacts or user data were changed; exit every installed or portable copy and retry.'
            }
            throw 'A legacy current-session TinyHwBar instance is running. No installation artifacts were changed; exit every installed or portable copy and retry.'
        }

        $runningAfterMutexAcquisition = @(
            Get-CimInstance Win32_Process -Filter "Name = 'TinyHwBar.exe'")
        $postMutexProcessCheck = Get-TinyHwBarProcessBlockers `
            $runningAfterMutexAcquisition `
            $currentUserSid `
            $destinationExe
        Write-TinyHwBarProcessOwnerWarning $postMutexProcessCheck
        if (@($postMutexProcessCheck.BlockingProcesses).Count -ne 0) {
            if ($removeUserDataRequested) {
                throw ('A normally named TinyHwBar process for this Windows user, or a process using ' +
                    'this user''s installed TinyHwBar path, is running. No installation artifacts or ' +
                    'user data were changed; exit every installed or portable copy for this Windows ' +
                    'user and retry.')
            }
            throw ('A normally named TinyHwBar process for this Windows user, or a process using this ' +
                'user''s installed TinyHwBar path, is running. No installation artifacts were changed; ' +
                'exit every installed or portable copy for this Windows user and retry.')
        }
    }

    if ($removeUserDataRequested) {
        $userDataPathExists = Test-Path -LiteralPath $userDataRoot
        if ($userDataPathExists -and
            -not (Test-Path -LiteralPath $userDataRoot -PathType Container)) {
            throw "Refusing to remove a user-data path that is not a directory: $userDataRoot"
        }
        $removeUserDataNow = $userDataPathExists
    }

    if ($installOwned) {
        Assert-NotReparsePoint $programsRoot
        if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
            throw "The owned install directory changed before uninstall began: $installRoot"
        }
        Assert-NotReparsePoint $installRoot
        foreach ($knownPath in @($destinationExe, $destinationUninstaller)) {
            Assert-RegularFileOrMissing $knownPath
        }
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "The installation ownership marker changed before uninstall began: $markerPath"
        }
        Assert-NotReparsePoint $markerPath
        if (-not [string]::Equals(
            [IO.File]::ReadAllText($markerPath),
            $ownershipMarkerContent,
            [StringComparison]::Ordinal)) {
            throw "The installation ownership marker changed before uninstall began: $markerPath"
        }
    }

    if (-not $anyOwnedArtifact -and -not $removeUserDataRequested) {
        Write-Host 'No TinyHwBar-owned installation artifacts were found.'
        if ($runSnapshot.Exists) {
            Write-Warning "Startup-value ownership could not be proven, so it was preserved (the installation marker is missing or the command differs): $startupKey::$startupName"
        }
        if ($shortcutExists) {
            Write-Warning "Shortcut ownership could not be proven, so it was preserved (the installation marker is missing or the shortcut attributes differ): $shortcutPath"
        }
        if ($uninstallSnapshot.Exists) {
            Write-Warning "Installed Apps registration ownership could not be proven, so it was preserved (the installation marker is missing or the registration differs): $uninstallKey"
        }
        return
    }

$transactionRoot = $null
$backupExe = $null
$backupUninstaller = $null
$backupMarker = $null
$backupShortcut = $null
$backupExeExpectedHash = $null
$backupUninstallerExpectedHash = $null
$backupMarkerExpectedHash = $null
$backupShortcutExpectedHash = $null
$knownTransactionFiles = @()
$transactionCreated = $false
$filesChanged = $false
$destinationExeExistedForTransaction = $false
$destinationUninstallerExistedForTransaction = $false
$destinationExeRemoved = $false
$destinationUninstallerRemoved = $false
$markerRemoved = $false
$startupChanged = $false
$shortcutChanged = $false
$uninstallKeyChanged = $false
$uninstallCommitted = $false

if ($installOwned) {
    $transactionRoot = Join-Path $installRoot ('.tinyhwbar-uninstall-' + [Guid]::NewGuid().ToString('N'))
    $backupExe = Join-Path $transactionRoot 'TinyHwBar.exe.bak'
    $backupUninstaller = Join-Path $transactionRoot 'Uninstall-TinyHwBar.ps1.bak'
    $backupMarker = Join-Path $transactionRoot 'owner.bak'
    $backupShortcut = Join-Path $transactionRoot 'TinyHwBar.lnk.bak'
    $knownTransactionFiles = @($backupExe, $backupUninstaller, $backupMarker, $backupShortcut)
}

try {
    if ($installOwned) {
        Assert-NotReparsePoint $programsRoot
        if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
            throw "The owned install directory changed before its transaction began: $installRoot"
        }
        Assert-NotReparsePoint $installRoot
        foreach ($knownPath in @($destinationExe, $destinationUninstaller)) {
            Assert-RegularFileOrMissing $knownPath
        }
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "The installation ownership marker changed before its transaction began: $markerPath"
        }
        Assert-NotReparsePoint $markerPath
        if (-not [string]::Equals(
            [IO.File]::ReadAllText($markerPath),
            $ownershipMarkerContent,
            [StringComparison]::Ordinal)) {
            throw "The installation ownership marker changed before its transaction began: $markerPath"
        }

        $destinationExeExistedForTransaction =
            Test-Path -LiteralPath $destinationExe -PathType Leaf
        $destinationUninstallerExistedForTransaction =
            Test-Path -LiteralPath $destinationUninstaller -PathType Leaf

        New-Item -ItemType Directory -Path $transactionRoot | Out-Null
        $transactionCreated = $true
        Assert-NotReparsePoint $transactionRoot

        if ($destinationExeExistedForTransaction) {
            Assert-RegularFileOrMissing $destinationExe
            $backupExeExpectedHash = Copy-FileWithHashVerification $destinationExe $backupExe
        }
        if ($destinationUninstallerExistedForTransaction) {
            Assert-RegularFileOrMissing $destinationUninstaller
            $backupUninstallerExpectedHash =
                Copy-FileWithHashVerification $destinationUninstaller $backupUninstaller
        }
        Assert-RegularFileOrMissing $markerPath
        $backupMarkerExpectedHash = Copy-FileWithHashVerification $markerPath $backupMarker
        if ($shortcutOwned) {
            Assert-NotReparsePoint $shortcutDirectory
            Assert-RegularFileOrMissing $shortcutPath
            $backupShortcutExpectedHash =
                Copy-FileWithHashVerification $shortcutPath $backupShortcut
        }

        Assert-NotReparsePoint $programsRoot
        Assert-NotReparsePoint $installRoot
        Assert-NotReparsePoint $transactionRoot
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "The installation ownership marker changed during backup: $markerPath"
        }
        Assert-RegularFileOrMissing $markerPath
        if (-not [string]::Equals(
            [IO.File]::ReadAllText($markerPath),
            $ownershipMarkerContent,
            [StringComparison]::Ordinal)) {
            throw "The installation ownership marker changed during backup: $markerPath"
        }

        foreach ($snapshot in @(
            [pscustomobject]@{
                Current = $destinationExe
                Backup = $backupExe
                ExpectedHash = $backupExeExpectedHash
                Existed = $destinationExeExistedForTransaction
            },
            [pscustomobject]@{
                Current = $destinationUninstaller
                Backup = $backupUninstaller
                ExpectedHash = $backupUninstallerExpectedHash
                Existed = $destinationUninstallerExistedForTransaction
            },
            [pscustomobject]@{
                Current = $markerPath
                Backup = $backupMarker
                ExpectedHash = $backupMarkerExpectedHash
                Existed = $true
            })) {
            if ($snapshot.Existed) {
                if (-not (Test-Path -LiteralPath $snapshot.Backup -PathType Leaf) -or
                    -not (Test-Path -LiteralPath $snapshot.Current -PathType Leaf)) {
                    throw "An installation file changed during backup: $($snapshot.Current)"
                }
                Assert-RegularFileOrMissing $snapshot.Backup
                Assert-FileHashMatchesExpected `
                    $snapshot.Backup `
                    $snapshot.ExpectedHash `
                    'Installation transaction backup'
                Assert-RegularFileOrMissing $snapshot.Current
                Assert-FileHashesEqual $snapshot.Backup $snapshot.Current
            }
            elseif ((Test-Path -LiteralPath $snapshot.Current) -or
                (Test-Path -LiteralPath $snapshot.Backup)) {
                throw "A previously absent installation file appeared during backup: $($snapshot.Current)"
            }
        }

        if ($shortcutOwned) {
            Assert-NotReparsePoint $shortcutDirectory
            Assert-RegularFileOrMissing $backupShortcut
            Assert-FileHashMatchesExpected `
                $backupShortcut `
                $backupShortcutExpectedHash `
                'Shortcut transaction backup'
            Assert-RegularFileOrMissing $shortcutPath
            Assert-FileHashesEqual $backupShortcut $shortcutPath
        }

        if ($destinationExeExistedForTransaction) {
            Assert-RegularFileOrMissing $destinationExe
            Assert-FileHashMatchesExpected `
                $backupExe `
                $backupExeExpectedHash `
                'Executable transaction backup'
            Assert-FileHashesEqual $backupExe $destinationExe
            Remove-Item -LiteralPath $destinationExe -Force
            $destinationExeRemoved = $true
            $filesChanged = $true
        }
        if ($destinationUninstallerExistedForTransaction) {
            Assert-RegularFileOrMissing $destinationUninstaller
            Assert-FileHashMatchesExpected `
                $backupUninstaller `
                $backupUninstallerExpectedHash `
                'Uninstaller transaction backup'
            Assert-FileHashesEqual $backupUninstaller $destinationUninstaller
            Remove-Item -LiteralPath $destinationUninstaller -Force
            $destinationUninstallerRemoved = $true
            $filesChanged = $true
        }
        Assert-RegularFileOrMissing $markerPath
        Assert-FileHashMatchesExpected `
            $backupMarker `
            $backupMarkerExpectedHash `
            'Ownership-marker transaction backup'
        Assert-FileHashesEqual $backupMarker $markerPath
        Remove-Item -LiteralPath $markerPath -Force
        $markerRemoved = $true
        $filesChanged = $true
    }

    if ($startupOwned) {
        Remove-RunValueIfExact $startupRegistrySubKey $startupName $expectedStartupCommand
        $startupChanged = $true
    }

    if ($shortcutOwned) {
        Assert-NotReparsePoint $shortcutDirectory
        $currentShortcutInfo = Get-ShortcutInfo $shortcutPath
        if (-not (Test-TinyHwBarShortcutOwned $currentShortcutInfo $destinationExe $installRoot)) {
            throw 'The Start Menu shortcut changed and was not removed.'
        }
        Assert-FileHashMatchesExpected `
            $backupShortcut `
            $backupShortcutExpectedHash `
            'Shortcut transaction backup'
        Assert-FileHashesEqual $backupShortcut $shortcutPath
        Remove-Item -LiteralPath $shortcutPath -Force
        $shortcutChanged = $true
    }

    if ($uninstallKeyOwned) {
        Remove-OwnedUninstallKey `
            $uninstallRegistrySubKey `
            $installRoot `
            $expectedUninstallCommand `
            $expectedDisplayVersion
        $uninstallKeyChanged = $true
    }

    $uninstallCommitted = $true
}
catch {
    $originalError = $_.Exception
    $rollbackErrors = @()
    $uninstallMadeArtifactChanges = $filesChanged -or $startupChanged -or
        $shortcutChanged -or $uninstallKeyChanged

    if ($uninstallKeyChanged) {
        try {
            Restore-RegistryKeySnapshot `
                $uninstallSnapshot `
                $uninstallRegistrySubKey `
                $installRoot `
                $expectedUninstallCommand `
                $expectedDisplayVersion
        }
        catch {
            $rollbackErrors += $_.Exception.Message
        }
    }

    if ($shortcutChanged) {
        try {
            Assert-NotReparsePoint $programsRoot
            Assert-NotReparsePoint $installRoot
            Assert-NotReparsePoint $transactionRoot
            Assert-NotReparsePoint $shortcutDirectory
            if (-not (Test-Path -LiteralPath $backupShortcut -PathType Leaf)) {
                throw "The shortcut rollback backup is missing: $backupShortcut"
            }
            Assert-RegularFileOrMissing $backupShortcut
            Assert-FileHashMatchesExpected `
                $backupShortcut `
                $backupShortcutExpectedHash `
                'Shortcut rollback backup'
            Assert-RegularFileOrMissing $shortcutPath
            if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
                $backupShortcutHash = (Get-FileHash -LiteralPath $backupShortcut -Algorithm SHA256).Hash
                $currentShortcutHash = (Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash
                if (-not [string]::Equals(
                    $backupShortcutHash,
                    $currentShortcutHash,
                    [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'A different Start Menu shortcut appeared during rollback and was preserved.'
                }
            }
            else {
                [IO.File]::Copy($backupShortcut, $shortcutPath, $false)
                Assert-FileHashMatchesExpected `
                    $shortcutPath `
                    $backupShortcutExpectedHash `
                    'Restored Start Menu shortcut'
            }
        }
        catch {
            $rollbackErrors += $_.Exception.Message
        }
    }

    if ($startupChanged) {
        try {
            Restore-RunValueSnapshot `
                $runSnapshot `
                $startupRegistrySubKey `
                $startupName `
                $expectedStartupCommand
        }
        catch {
            $rollbackErrors += $_.Exception.Message
        }
    }

    if ($filesChanged) {
        $rollbackRootsValid = $true
        try {
            Assert-NotReparsePoint $programsRoot
            Assert-NotReparsePoint $installRoot
            Assert-NotReparsePoint $transactionRoot
        }
        catch {
            $rollbackErrors += $_.Exception.Message
            $rollbackRootsValid = $false
        }

        if ($rollbackRootsValid) {
            foreach ($state in @(
                [pscustomobject]@{
                    Destination = $destinationExe
                    Backup = $backupExe
                    ExpectedHash = $backupExeExpectedHash
                    Removed = $destinationExeRemoved
                },
                [pscustomobject]@{
                    Destination = $destinationUninstaller
                    Backup = $backupUninstaller
                    ExpectedHash = $backupUninstallerExpectedHash
                    Removed = $destinationUninstallerRemoved
                },
                [pscustomobject]@{
                    Destination = $markerPath
                    Backup = $backupMarker
                    ExpectedHash = $backupMarkerExpectedHash
                    Removed = $markerRemoved
                })) {
                if (-not $state.Removed) {
                    continue
                }

                try {
                    if (-not (Test-Path -LiteralPath $state.Backup -PathType Leaf)) {
                        throw "An installation rollback backup is missing: $($state.Backup)"
                    }
                    Assert-RegularFileOrMissing $state.Backup
                    Assert-FileHashMatchesExpected `
                        $state.Backup `
                        $state.ExpectedHash `
                        'Installation rollback backup'
                    Assert-RegularFileOrMissing $state.Destination
                    if (Test-Path -LiteralPath $state.Destination -PathType Leaf) {
                        $backupHash = (Get-FileHash -LiteralPath $state.Backup -Algorithm SHA256).Hash
                        $currentHash = (Get-FileHash -LiteralPath $state.Destination -Algorithm SHA256).Hash
                        if (-not [string]::Equals(
                            $backupHash,
                            $currentHash,
                            [StringComparison]::OrdinalIgnoreCase)) {
                            throw ("A different file appeared during rollback and was preserved: " +
                                $state.Destination)
                        }
                    }
                    else {
                        [IO.File]::Copy($state.Backup, $state.Destination, $false)
                        Assert-FileHashMatchesExpected `
                            $state.Destination `
                            $state.ExpectedHash `
                            'Restored installation file'
                    }
                }
                catch {
                    $rollbackErrors += $_.Exception.Message
                }
            }
        }
    }

    if ($rollbackErrors.Count -ne 0) {
        throw ($originalError.Message + ' Rollback was incomplete: ' + ($rollbackErrors -join ' | ') +
            " Backup files were preserved at: $transactionRoot")
    }

    if ($transactionCreated) {
        try {
            Remove-KnownTransactionDirectory $transactionRoot $knownTransactionFiles
        }
        catch {
            $rollbackState = if ($uninstallMadeArtifactChanges) {
                'Changes completed by this uninstall were rolled back'
            }
            else {
                'No installation-artifact changes were completed by this uninstall'
            }
            Write-Warning ($rollbackState + ", but temporary transaction cleanup failed; " +
                "review this owned path: $transactionRoot. " + $_.Exception.Message)
        }
    }
    throw $originalError
}

if ($uninstallCommitted -and $transactionCreated) {
    try {
        Remove-KnownTransactionDirectory $transactionRoot $knownTransactionFiles
    }
    catch {
        Write-Warning ("TinyHwBar-owned installation artifacts were removed, but temporary transaction cleanup failed; " +
            "review this owned path: $transactionRoot. " + $_.Exception.Message)
    }
}

if ($installOwned -and (Test-Path -LiteralPath $installRoot -PathType Container)) {
    try {
        Assert-NotReparsePoint $installRoot
        if (@(Get-ChildItem -LiteralPath $installRoot -Force).Count -eq 0) {
            Remove-Item -LiteralPath $installRoot -Force
        }
        else {
            Write-Warning "Unknown files were preserved in the former install directory: $installRoot"
        }
    }
    catch {
        Write-Warning ("TinyHwBar-owned installation artifacts were removed, but final install-directory cleanup failed; " +
            "review the remaining path: $installRoot. " + $_.Exception.Message)
    }
}

# User data is intentionally the final destructive step. It is attempted only after
# every rollback-capable installation-artifact operation has committed successfully.
if ($removeUserDataNow) {
    try {
        if (-not (Test-Path -LiteralPath $userDataRoot -PathType Container)) {
            throw 'The previously validated user-data directory is no longer a directory.'
        }
        Assert-NoReparseTree $userDataRoot
        Remove-Item -LiteralPath $userDataRoot -Recurse -Force
        if (Test-Path -LiteralPath $userDataRoot) {
            throw 'The user-data directory still exists after the requested removal.'
        }
        $userDataRemoved = $true
    }
    catch {
        $userDataRemovalFailed = $true
        $userDataRemovalFailureMessage = (
            "The rollback-capable TinyHwBar uninstall phase committed, but the explicitly requested " +
            "user-data removal failed and may be incomplete. The deletion cannot be rolled back; " +
            "review the remaining contents at: $userDataRoot. " + $_.Exception.Message)
        Write-Warning $userDataRemovalFailureMessage
    }
}

    if ($anyOwnedArtifact) {
        Write-Host 'TinyHwBar-owned installation artifacts were removed for the current user.'
    }
    else {
        Write-Host 'No TinyHwBar-owned installation artifacts were removed.'
    }
    if ($runSnapshot.Exists -and -not $startupOwned) {
        Write-Warning "Startup-value ownership could not be proven, so it was preserved (the installation marker is missing or the command differs): $startupKey::$startupName"
    }
    if ($shortcutExists -and -not $shortcutOwned) {
        Write-Warning "Shortcut ownership could not be proven, so it was preserved (the installation marker is missing or the shortcut attributes differ): $shortcutPath"
    }
    if ($uninstallSnapshot.Exists -and -not $uninstallKeyOwned) {
        Write-Warning "Installed Apps registration ownership could not be proven, so it was preserved (the installation marker is missing or the registration differs): $uninstallKey"
    }
    if ($userDataRemoved) {
        Write-Host "The TinyHwBar user-data directory was removed: $userDataRoot"
    }
    elseif ($userDataRemovalFailed) {
        Write-Warning "The TinyHwBar user-data directory still requires manual review: $userDataRoot"
    }
    elseif ($removeUserDataRequested) {
        Write-Host "No TinyHwBar user-data directory was present at removal time: $userDataRoot"
    }
    else {
        Write-Host "Settings and history were preserved: $userDataRoot"
    }
    if ($userDataRemovalFailed) {
        throw $userDataRemovalFailureMessage
    }
}
finally {
    if ($null -ne $legacySingletonMutex) {
        try {
            if ($legacySingletonMutexAcquired) {
                $legacySingletonMutex.ReleaseMutex()
            }
        }
        finally {
            $legacySingletonMutex.Dispose()
        }
    }
    if ($null -ne $appSingletonMutex) {
        try {
            if ($appSingletonMutexAcquired) {
                $appSingletonMutex.ReleaseMutex()
            }
        }
        finally {
            $appSingletonMutex.Dispose()
        }
    }
    if ($null -ne $maintenanceMutex) {
        try {
            if ($maintenanceMutexAcquired) {
                $maintenanceMutex.ReleaseMutex()
            }
        }
        finally {
            $maintenanceMutex.Dispose()
        }
    }
}

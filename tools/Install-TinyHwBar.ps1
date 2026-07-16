#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$SourceExe,
    [switch]$NoStartMenuShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceExe)) {
    $SourceExe = Join-Path (Split-Path -Parent $PSScriptRoot) 'outputs\TinyHwBar.exe'
}

$ownershipMarkerName = '.tinyhwbar-install-owner'
$ownershipMarkerContent = 'TinyHwBar.UserInstall|2'
$uninstallerName = 'Uninstall-TinyHwBar.ps1'
$uninstallRegistrySubKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\TinyHwBar'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TinyHwBar'
$singletonMutexName = 'Local\TinyHwBar.Singleton'
$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ([string]::IsNullOrWhiteSpace($currentUserSid)) {
    throw 'Windows did not return the current user SID required for cross-session maintenance locking.'
}
$maintenanceMutexName = 'Global\TinyHwBar.Maintenance.' + $currentUserSid
$uninstallValueNames = @(
    'DisplayName',
    'DisplayVersion',
    'Publisher',
    'InstallLocation',
    'UninstallString',
    'NoModify',
    'NoRepair')

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

function Assert-OwnedOrEmptyInstallDirectory {
    param([string]$InstallRoot, [string]$MarkerPath, [string]$ExpectedMarkerContent)

    if (-not (Test-Path -LiteralPath $InstallRoot)) {
        return
    }

    if (-not (Test-Path -LiteralPath $InstallRoot -PathType Container)) {
        throw "The install path exists but is not a directory: $InstallRoot"
    }

    Assert-NotReparsePoint $InstallRoot
    $entries = @(Get-ChildItem -LiteralPath $InstallRoot -Force)
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) {
        if ($entries.Count -eq 0) {
            return
        }

        throw "Refusing to modify an unowned non-empty install directory: $InstallRoot"
    }

    Assert-NotReparsePoint $MarkerPath
    $markerContent = [IO.File]::ReadAllText($MarkerPath)
    if (-not [string]::Equals(
        $markerContent,
        $ExpectedMarkerContent,
        [StringComparison]::Ordinal)) {
        throw "Refusing to modify an install directory with an invalid ownership marker: $InstallRoot"
    }
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

function Test-ShortcutInfoEquals {
    param($Left, $Right)

    if ($null -eq $Left -or $null -eq $Right) {
        return $false
    }

    return (Test-PathEquals $Left.TargetPath $Right.TargetPath) -and
        [string]::Equals($Left.Arguments, $Right.Arguments, [StringComparison]::Ordinal) -and
        (Test-PathEquals $Left.WorkingDirectory $Right.WorkingDirectory) -and
        [string]::Equals($Left.Description, $Right.Description, [StringComparison]::Ordinal) -and
        [string]::Equals($Left.IconLocation, $Right.IconLocation, [StringComparison]::Ordinal) -and
        [string]::Equals($Left.Hotkey, $Right.Hotkey, [StringComparison]::Ordinal) -and
        $Left.WindowStyle -eq $Right.WindowStyle
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

function New-TinyHwBarShortcut {
    param([string]$ShortcutPath, [string]$ExecutablePath, [string]$WorkingDirectory)

    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $ExecutablePath
        $shortcut.Arguments = ''
        $shortcut.WorkingDirectory = $WorkingDirectory
        $shortcut.Description = 'TinyHwBar hardware monitor'
        $shortcut.IconLocation = $ExecutablePath + ',0'
        $shortcut.WindowStyle = 1
        $shortcut.Save()
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

function Get-ExecutableDisplayVersion {
    param([string]$Path)

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $displayVersion = $versionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($displayVersion)) {
        $displayVersion = $versionInfo.FileVersion
    }
    if ([string]::IsNullOrWhiteSpace($displayVersion)) {
        $displayVersion = '2.0.0'
    }

    return $displayVersion
}

function Test-UninstallKeyOwned {
    param(
        $Snapshot,
        [string]$ExpectedInstallRoot,
        [string]$ExpectedUninstallCommand,
        [string]$ExpectedDisplayVersion)

    if (-not $Snapshot.Exists -or
        $Snapshot.SubKeyNames.Count -ne 0 -or
        $Snapshot.Values.Count -ne $uninstallValueNames.Count -or
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

    return (
        $null -ne $displayName -and
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
        [int]$noRepair.Value -eq 1)
}

function Test-RegistryValueEntryEquals {
    param($Left, $Right)

    if ($null -eq $Left -or $null -eq $Right -or $Left.Kind -ne $Right.Kind) {
        return $false
    }
    if ($Left.Value -is [string] -or $Right.Value -is [string]) {
        return ($Left.Value -is [string]) -and
            ($Right.Value -is [string]) -and
            [string]::Equals(
                [string]$Left.Value,
                [string]$Right.Value,
                [StringComparison]::Ordinal)
    }

    return [object]::Equals($Left.Value, $Right.Value)
}

function Test-RegistrySnapshotsEqual {
    param($Left, $Right)

    if ($Left.Exists -ne $Right.Exists) {
        return $false
    }
    if (-not $Left.Exists) {
        return $true
    }
    if ($Left.Values.Count -ne $Right.Values.Count -or
        $Left.SubKeyNames.Count -ne $Right.SubKeyNames.Count) {
        return $false
    }

    foreach ($entry in $Left.Values) {
        if (-not (Test-RegistryValueEntryEquals `
            $entry `
            (Get-SnapshotValue $Right $entry.Name))) {
            return $false
        }
    }
    foreach ($subKeyName in $Left.SubKeyNames) {
        if ($Right.SubKeyNames -notcontains $subKeyName) {
            return $false
        }
    }

    return $true
}

function Test-UninstallKeyTransitionOwned {
    param(
        $CurrentSnapshot,
        $OriginalSnapshot,
        [string]$ExpectedInstallRoot,
        [string]$ExpectedUninstallCommand,
        [string]$ExpectedDisplayVersion)

    if (-not $CurrentSnapshot.Exists -or
        $CurrentSnapshot.SubKeyNames.Count -ne 0 -or
        $CurrentSnapshot.Values.Count -gt $uninstallValueNames.Count -or
        [string]::IsNullOrWhiteSpace($ExpectedDisplayVersion)) {
        return $false
    }
    if ($OriginalSnapshot.Exists -and
        $CurrentSnapshot.Values.Count -ne $uninstallValueNames.Count) {
        return $false
    }

    $targetSnapshot = [pscustomobject]@{
        Exists = $true
        SubKeyNames = @()
        Values = @(
            [pscustomobject]@{
                Name = 'DisplayName'
                Value = 'TinyHwBar'
                Kind = [Microsoft.Win32.RegistryValueKind]::String
            },
            [pscustomobject]@{
                Name = 'DisplayVersion'
                Value = $ExpectedDisplayVersion
                Kind = [Microsoft.Win32.RegistryValueKind]::String
            },
            [pscustomobject]@{
                Name = 'Publisher'
                Value = 'TinyHwBar contributors'
                Kind = [Microsoft.Win32.RegistryValueKind]::String
            },
            [pscustomobject]@{
                Name = 'InstallLocation'
                Value = $ExpectedInstallRoot
                Kind = [Microsoft.Win32.RegistryValueKind]::String
            },
            [pscustomobject]@{
                Name = 'UninstallString'
                Value = $ExpectedUninstallCommand
                Kind = [Microsoft.Win32.RegistryValueKind]::String
            },
            [pscustomobject]@{
                Name = 'NoModify'
                Value = 1
                Kind = [Microsoft.Win32.RegistryValueKind]::DWord
            },
            [pscustomobject]@{
                Name = 'NoRepair'
                Value = 1
                Kind = [Microsoft.Win32.RegistryValueKind]::DWord
            })
    }

    foreach ($currentEntry in $CurrentSnapshot.Values) {
        $targetEntry = Get-SnapshotValue $targetSnapshot $currentEntry.Name
        $originalEntry = if ($OriginalSnapshot.Exists) {
            Get-SnapshotValue $OriginalSnapshot $currentEntry.Name
        }
        else { $null }
        if (-not (Test-RegistryValueEntryEquals $currentEntry $targetEntry) -and
            -not (Test-RegistryValueEntryEquals $currentEntry $originalEntry)) {
            return $false
        }
    }

    return $true
}

function Assert-UninstallKeyOwnedOrMissing {
    param(
        $Snapshot,
        [string]$ExpectedInstallRoot,
        [string]$ExpectedUninstallCommand,
        [string]$ExpectedDisplayVersion)

    if (-not $Snapshot.Exists) {
        return
    }

    if (-not (Test-UninstallKeyOwned `
        $Snapshot `
        $ExpectedInstallRoot `
        $ExpectedUninstallCommand `
        $ExpectedDisplayVersion)) {
        throw 'Refusing to overwrite an uninstall registry key that is not owned by this TinyHwBar installation.'
    }
}

function Set-UninstallRegistryEntry {
    param(
        [string]$SubKeyPath,
        [string]$InstallRoot,
        [string]$UninstallCommand,
        [string]$DisplayVersion
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKeyPath)
    if ($null -eq $key) {
        throw 'Could not create the current-user uninstall registry key.'
    }

    try {
        $key.SetValue('DisplayName', 'TinyHwBar', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('DisplayVersion', $DisplayVersion, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('Publisher', 'TinyHwBar contributors', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('InstallLocation', $InstallRoot, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('UninstallString', $UninstallCommand, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue('NoModify', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('NoRepair', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    }
    finally {
        $key.Dispose()
    }
}

function Restore-RegistryKeySnapshot {
    param(
        $Snapshot,
        [string]$SubKeyPath,
        [string]$ExpectedInstallRoot,
        [string]$ExpectedUninstallCommand,
        [string]$ExpectedDisplayVersion)

    $currentSnapshot = Get-RegistryKeySnapshot $SubKeyPath
    if (-not $currentSnapshot.Exists) {
        if (-not $Snapshot.Exists) {
            return
        }
        throw 'The uninstall key disappeared during installation; the original registration was not recreated.'
    }
    else {
        if (-not (Test-UninstallKeyTransitionOwned `
            $currentSnapshot `
            $Snapshot `
            $ExpectedInstallRoot `
            $ExpectedUninstallCommand `
            $ExpectedDisplayVersion)) {
            throw 'The uninstall key changed ownership during installation; it was preserved.'
        }
    }

    if (-not $Snapshot.Exists) {
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($SubKeyPath, $false)
        if ((Get-RegistryKeySnapshot $SubKeyPath).Exists) {
            throw 'The newly created uninstall registry key was not removed during rollback.'
        }
        return
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

    $restoredSnapshot = Get-RegistryKeySnapshot $SubKeyPath
    if (-not (Test-RegistrySnapshotsEqual $restoredSnapshot $Snapshot)) {
        throw 'The uninstall registry key did not match its original snapshot after rollback.'
    }
}

function Copy-FileWithHashVerification {
    param([string]$Source, [string]$Destination)

    Assert-RegularFileOrMissing $Source
    $destinationParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Destination))
    Assert-NotReparsePoint $destinationParent
    if (Test-Path -LiteralPath $Destination) {
        throw "Refusing to overwrite an existing transaction file: $Destination"
    }

    $sourceHashBefore = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    [IO.File]::Copy($Source, $Destination, $false)
    Assert-RegularFileOrMissing $Destination
    $sourceHashAfter = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if (-not [string]::Equals(
        $sourceHashBefore,
        $sourceHashAfter,
        [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $sourceHashBefore,
            $destinationHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The source changed or SHA-256 verification failed while staging: $Source"
    }

    return $destinationHash
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

function Write-Utf8FileNoClobber {
    param([string]$Destination, [string]$Content)

    $destinationParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Destination))
    Assert-NotReparsePoint $destinationParent
    if (Test-Path -LiteralPath $Destination) {
        throw "Refusing to overwrite an existing transaction file: $Destination"
    }

    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($Content)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $expectedHash = ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }

    $stream = [IO.File]::Open(
        $Destination,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }
    finally {
        $stream.Dispose()
    }

    Assert-RegularFileOrMissing $Destination
    Assert-FileHashMatchesExpected $Destination $expectedHash 'New transaction file'
    return $expectedHash
}

function New-InstallFileState {
    param(
        [string]$Description,
        [string]$Destination,
        [string]$StagedPath,
        [string]$StagedExpectedHash,
        [bool]$Existed,
        [string]$OriginalExpectedHash,
        [string]$BackupPath)

    return [pscustomobject]@{
        Description = $Description
        Destination = $Destination
        StagedPath = $StagedPath
        StagedExpectedHash = $StagedExpectedHash
        Existed = $Existed
        OriginalExpectedHash = $OriginalExpectedHash
        BackupPath = $BackupPath
        OriginalMoved = $false
        Installed = $false
    }
}

function Move-ExpectedFileToBackupNoClobber {
    param($State)

    if (Test-Path -LiteralPath $State.BackupPath) {
        throw "Refusing to overwrite an existing transaction backup: $($State.BackupPath)"
    }

    [IO.File]::Move($State.Destination, $State.BackupPath)
    $State.OriginalMoved = $true
    try {
        Assert-FileHashMatchesExpected `
            $State.BackupPath `
            $State.OriginalExpectedHash `
            ($State.Description + ' transaction backup')
    }
    catch {
        $verificationError = $_.Exception
        if (-not (Test-Path -LiteralPath $State.Destination) -and
            (Test-Path -LiteralPath $State.BackupPath -PathType Leaf)) {
            try {
                Assert-RegularFileOrMissing $State.BackupPath
                [IO.File]::Move($State.BackupPath, $State.Destination)
                $State.OriginalMoved = $false
            }
            catch {
                throw ($verificationError.Message +
                    ' The unexpected file could not be returned to its original path and was preserved at: ' +
                    $State.BackupPath + '. ' + $_.Exception.Message)
            }
        }

        if ($State.OriginalMoved) {
            throw ($verificationError.Message +
                ' The unexpected file and the newly appeared destination were both preserved; review: ' +
                $State.BackupPath)
        }
        throw $verificationError
    }
}

function Install-StagedFile {
    param($State)

    $destinationParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($State.Destination))
    $stagedParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($State.StagedPath))
    Assert-NotReparsePoint $destinationParent
    Assert-NotReparsePoint $stagedParent
    Assert-FileHashMatchesExpected `
        $State.StagedPath `
        $State.StagedExpectedHash `
        ($State.Description + ' staged file')
    Assert-RegularFileOrMissing $State.Destination

    if ($State.Existed) {
        Move-ExpectedFileToBackupNoClobber $State
    }
    elseif (Test-Path -LiteralPath $State.Destination) {
        throw "A previously absent destination appeared and was preserved: $($State.Destination)"
    }

    if (Test-Path -LiteralPath $State.Destination) {
        throw "The destination was not empty before installing: $($State.Destination)"
    }
    Assert-FileHashMatchesExpected `
        $State.StagedPath `
        $State.StagedExpectedHash `
        ($State.Description + ' staged file')
    [IO.File]::Move($State.StagedPath, $State.Destination)
    $State.Installed = $true
    Assert-FileHashMatchesExpected `
        $State.Destination `
        $State.StagedExpectedHash `
        ($State.Description + ' installed file')
}

function Remove-InstalledFileToBackup {
    param($State)

    if (-not $State.Existed) {
        return
    }

    $destinationParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($State.Destination))
    $backupParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($State.BackupPath))
    Assert-NotReparsePoint $destinationParent
    Assert-NotReparsePoint $backupParent
    Move-ExpectedFileToBackupNoClobber $State
}

function Restore-InstallFileState {
    param($State)

    if (-not $State.OriginalMoved -and -not $State.Installed) {
        return
    }

    $destinationParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($State.Destination))
    Assert-NotReparsePoint $destinationParent
    if (-not [string]::IsNullOrWhiteSpace($State.StagedPath)) {
        $stagedParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($State.StagedPath))
        Assert-NotReparsePoint $stagedParent
    }

    if ($State.Installed) {
        if (Test-Path -LiteralPath $State.Destination -PathType Leaf) {
            Assert-RegularFileOrMissing $State.Destination
            if (Test-Path -LiteralPath $State.StagedPath) {
                throw "The rollback staging path is not empty: $($State.StagedPath)"
            }

            [IO.File]::Move($State.Destination, $State.StagedPath)
            try {
                Assert-FileHashMatchesExpected `
                    $State.StagedPath `
                    $State.StagedExpectedHash `
                    ($State.Description + ' rollback candidate')
            }
            catch {
                if (-not (Test-Path -LiteralPath $State.Destination) -and
                    (Test-Path -LiteralPath $State.StagedPath -PathType Leaf)) {
                    [IO.File]::Move($State.StagedPath, $State.Destination)
                }
                throw
            }
        }
        elseif (Test-Path -LiteralPath $State.Destination) {
            throw "A non-file destination appeared during rollback and was preserved: $($State.Destination)"
        }
        $State.Installed = $false
    }

    if ($State.OriginalMoved) {
        Assert-FileHashMatchesExpected `
            $State.BackupPath `
            $State.OriginalExpectedHash `
            ($State.Description + ' rollback backup')
        if (Test-Path -LiteralPath $State.Destination) {
            throw "A destination appeared during rollback and was preserved: $($State.Destination)"
        }
        [IO.File]::Move($State.BackupPath, $State.Destination)
        Assert-FileHashMatchesExpected `
            $State.Destination `
            $State.OriginalExpectedHash `
            ($State.Description + ' restored original file')
        $State.OriginalMoved = $false
    }
}

function Remove-KnownTransactionDirectory {
    param([string]$TransactionRoot, [string[]]$KnownFiles)

    if (-not (Test-Path -LiteralPath $TransactionRoot -PathType Container)) {
        return
    }

    Assert-NotReparsePoint $TransactionRoot
    $children = @(Get-ChildItem -LiteralPath $TransactionRoot -Force)
    foreach ($child in $children) {
        $knownChild = $false
        foreach ($knownPath in $KnownFiles) {
            if ([string]::Equals(
                $child.FullName,
                [IO.Path]::GetFullPath($knownPath),
                [StringComparison]::OrdinalIgnoreCase)) {
                $knownChild = $true
                break
            }
        }
        if (-not $knownChild -or $child.PSIsContainer -or
            (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            Write-Warning "Temporary installation transaction cleanup preserved an unexpected entry: $($child.FullName)"
            return
        }
    }

    foreach ($child in $children) {
        Remove-Item -LiteralPath $child.FullName -Force -ErrorAction SilentlyContinue
    }

    if (@(Get-ChildItem -LiteralPath $TransactionRoot -Force).Count -eq 0) {
        Remove-Item -LiteralPath $TransactionRoot -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $TransactionRoot) {
        Write-Warning "Temporary installation transaction cleanup was incomplete; review this owned path: $TransactionRoot"
    }
}

$localAppData = Get-TrustedSpecialFolderPath ([Environment+SpecialFolder]::LocalApplicationData)
$roamingAppData = Get-TrustedSpecialFolderPath ([Environment+SpecialFolder]::ApplicationData)
$systemDirectory = Get-TrustedSpecialFolderPath ([Environment+SpecialFolder]::System)
$programsRoot = Join-Path $localAppData 'Programs'
$installRoot = Join-Path $programsRoot 'TinyHwBar'
$destinationExe = Join-Path $installRoot 'TinyHwBar.exe'
$sourceUninstaller = Join-Path $PSScriptRoot $uninstallerName
$destinationUninstaller = Join-Path $installRoot $uninstallerName
$markerPath = Join-Path $installRoot $ownershipMarkerName
$startMenuDirectory = Join-Path $roamingAppData 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenuDirectory 'TinyHwBar.lnk'
$powershellExe = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\powershell.exe'

Assert-DirectChildPath $localAppData $installRoot
Assert-DirectChildPath $roamingAppData $shortcutPath
Assert-NotReparsePoint $programsRoot
Assert-NotReparsePoint $startMenuDirectory
Assert-OwnedOrEmptyInstallDirectory $installRoot $markerPath $ownershipMarkerContent
Assert-RegularFileOrMissing $destinationExe
Assert-RegularFileOrMissing $destinationUninstaller

if (-not (Test-Path -LiteralPath $SourceExe -PathType Leaf)) {
    throw "TinyHwBar executable not found: $SourceExe"
}
if (-not (Test-Path -LiteralPath $sourceUninstaller -PathType Leaf)) {
    throw "Uninstaller script not found: $sourceUninstaller"
}
if (-not (Test-Path -LiteralPath $powershellExe -PathType Leaf)) {
    throw "Windows PowerShell 5.1 was not found: $powershellExe"
}

$sourceItem = Get-Item -LiteralPath $SourceExe
if ($sourceItem.Extension -ne '.exe' -or $sourceItem.Length -le 0) {
    throw 'Source must be a non-empty .exe file.'
}
$displayVersion = $null
$expectedUninstallCommand = '"' + $powershellExe + '" -NoProfile -ExecutionPolicy Bypass -File "' +
    $destinationUninstaller + '"'

$runningInstalledProcess = Get-CimInstance Win32_Process -Filter "Name = 'TinyHwBar.exe'" |
    Where-Object {
        $_.ExecutablePath -and (Test-PathEquals $_.ExecutablePath $destinationExe)
    }
if ($runningInstalledProcess) {
    throw 'The installed TinyHwBar is running. Exit it from the tray before installing or upgrading.'
}

$shortcutExists = Test-Path -LiteralPath $shortcutPath -PathType Leaf
$shortcutOwned = $false
$shortcutOriginalInfo = $null
if ($shortcutExists) {
    $shortcutOriginalInfo = Get-ShortcutInfo $shortcutPath
    $shortcutOwned = Test-TinyHwBarShortcutOwned `
        $shortcutOriginalInfo `
        $destinationExe `
        $installRoot
    if (-not $NoStartMenuShortcut -and -not $shortcutOwned) {
        throw "Refusing to overwrite a Start Menu shortcut that does not exactly match the TinyHwBar-owned shortcut. Preserve it by rerunning with -NoStartMenuShortcut, or review it manually: $shortcutPath"
    }
}

$uninstallSnapshot = Get-RegistryKeySnapshot $uninstallRegistrySubKey
$expectedExistingDisplayVersion = $null
if ($uninstallSnapshot.Exists) {
    if (Test-Path -LiteralPath $destinationExe -PathType Leaf) {
        $expectedExistingDisplayVersion = Get-ExecutableDisplayVersion $destinationExe
    }
}
Assert-UninstallKeyOwnedOrMissing `
    $uninstallSnapshot `
    $installRoot `
    $expectedUninstallCommand `
    $expectedExistingDisplayVersion

$fileScopeApproved = $PSCmdlet.ShouldProcess(
    $installRoot,
    'Install or upgrade TinyHwBar executable, uninstaller, and ownership marker')
$shortcutScopeApproved = $true
if (-not $NoStartMenuShortcut) {
    $shortcutScopeApproved = $PSCmdlet.ShouldProcess(
        $shortcutPath,
        'Create or update the TinyHwBar-owned Start Menu shortcut')
}
elseif ($shortcutOwned) {
    $shortcutScopeApproved = $PSCmdlet.ShouldProcess(
        $shortcutPath,
        'Remove the TinyHwBar-owned Start Menu shortcut')
}
$registryScopeApproved = $PSCmdlet.ShouldProcess(
    $uninstallKey,
    'Create or update the TinyHwBar-owned current-user uninstall registration')

if (-not ($fileScopeApproved -and $shortcutScopeApproved -and $registryScopeApproved)) {
    return
}

$installRootExisted = Test-Path -LiteralPath $installRoot -PathType Container
$startMenuDirectoryExisted = Test-Path -LiteralPath $startMenuDirectory -PathType Container
$destinationExeExisted = Test-Path -LiteralPath $destinationExe -PathType Leaf
$destinationUninstallerExisted = Test-Path -LiteralPath $destinationUninstaller -PathType Leaf
$markerExisted = Test-Path -LiteralPath $markerPath -PathType Leaf
$shortcutWillChange = (-not $NoStartMenuShortcut) -or $shortcutOwned
$transactionRoot = Join-Path $installRoot ('.tinyhwbar-install-' + [Guid]::NewGuid().ToString('N'))
$backupExe = Join-Path $transactionRoot 'TinyHwBar.exe.bak'
$backupUninstaller = Join-Path $transactionRoot 'Uninstall-TinyHwBar.ps1.bak'
$backupMarker = Join-Path $transactionRoot 'owner.bak'
$stagedExe = Join-Path $transactionRoot 'TinyHwBar.exe.new'
$stagedUninstaller = Join-Path $transactionRoot 'Uninstall-TinyHwBar.ps1.new'
$stagedMarker = Join-Path $transactionRoot 'owner.new'
$shortcutTransactionRoot = Join-Path `
    $startMenuDirectory `
    ('.tinyhwbar-shortcut-' + [Guid]::NewGuid().ToString('N'))
$backupShortcut = Join-Path $shortcutTransactionRoot 'TinyHwBar.lnk.bak'
$stagedShortcut = Join-Path $shortcutTransactionRoot 'TinyHwBar.lnk.new'
$stagedShortcutBuild = Join-Path `
    $shortcutTransactionRoot `
    ('TinyHwBar.' + [Guid]::NewGuid().ToString('N') + '.lnk')
$knownTransactionFiles = @(
    $backupExe,
    $backupUninstaller,
    $backupMarker,
    $stagedExe,
    $stagedUninstaller,
    $stagedMarker)
$knownShortcutTransactionFiles = @(
    $backupShortcut,
    $stagedShortcut,
    $stagedShortcutBuild)
$transactionCreated = $false
$shortcutTransactionCreated = $false
$preserveTransaction = $false
$installCompleted = $false
$registryModified = $false
$maintenanceMutex = $null
$maintenanceMutexAcquired = $false
$singletonMutex = $null
$singletonMutexAcquired = $false
$exeState = $null
$uninstallerState = $null
$markerState = $null
$shortcutState = $null

try {
    $maintenanceMutex = [Threading.Mutex]::new($false, $maintenanceMutexName)
    try {
        $maintenanceMutexAcquired = $maintenanceMutex.WaitOne(0, $false)
    }
    catch [Threading.AbandonedMutexException] {
        $maintenanceMutexAcquired = $true
    }

    if (-not $maintenanceMutexAcquired) {
        throw 'Another TinyHwBar installation or uninstallation is active for this Windows user, possibly in another session. No installation artifacts were changed; retry after it finishes.'
    }

    $singletonMutex = [Threading.Mutex]::new($false, $singletonMutexName)
    try {
        $singletonMutexAcquired = $singletonMutex.WaitOne(0, $false)
    }
    catch [Threading.AbandonedMutexException] {
        $singletonMutexAcquired = $true
    }

    if (-not $singletonMutexAcquired) {
        throw 'A current-session TinyHwBar instance is active. No installation artifacts were changed; exit TinyHwBar and retry.'
    }

    $runningAfterMutexAcquisition = @(Get-CimInstance Win32_Process -Filter "Name = 'TinyHwBar.exe'")
    if ($runningAfterMutexAcquisition.Count -ne 0) {
        throw 'A normally named TinyHwBar process is running. No installation artifacts were changed; exit every installed or portable copy and retry.'
    }

    Assert-NotReparsePoint $programsRoot
    Assert-NotReparsePoint $startMenuDirectory
    Assert-OwnedOrEmptyInstallDirectory $installRoot $markerPath $ownershipMarkerContent
    Assert-RegularFileOrMissing $destinationExe
    Assert-RegularFileOrMissing $destinationUninstaller

    $shortcutExists = Test-Path -LiteralPath $shortcutPath -PathType Leaf
    $shortcutOwned = $false
    $shortcutOriginalInfo = $null
    if ($shortcutExists) {
        $shortcutOriginalInfo = Get-ShortcutInfo $shortcutPath
        $shortcutOwned = Test-TinyHwBarShortcutOwned `
            $shortcutOriginalInfo `
            $destinationExe `
            $installRoot
        if (-not $NoStartMenuShortcut -and -not $shortcutOwned) {
            throw "The Start Menu shortcut changed before installation began and is not TinyHwBar-owned: $shortcutPath"
        }
    }

    $uninstallSnapshot = Get-RegistryKeySnapshot $uninstallRegistrySubKey
    $expectedExistingDisplayVersion = $null
    if ($uninstallSnapshot.Exists -and
        (Test-Path -LiteralPath $destinationExe -PathType Leaf)) {
        $expectedExistingDisplayVersion = Get-ExecutableDisplayVersion $destinationExe
    }
    Assert-UninstallKeyOwnedOrMissing `
        $uninstallSnapshot `
        $installRoot `
        $expectedUninstallCommand `
        $expectedExistingDisplayVersion

    $installRootExisted = Test-Path -LiteralPath $installRoot -PathType Container
    $startMenuDirectoryExisted = Test-Path -LiteralPath $startMenuDirectory -PathType Container
    $destinationExeExisted = Test-Path -LiteralPath $destinationExe -PathType Leaf
    $destinationUninstallerExisted = Test-Path -LiteralPath $destinationUninstaller -PathType Leaf
    $markerExisted = Test-Path -LiteralPath $markerPath -PathType Leaf
    $shortcutWillChange = (-not $NoStartMenuShortcut) -or $shortcutOwned

    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Assert-NotReparsePoint $programsRoot
    Assert-NotReparsePoint $installRoot
    New-Item -ItemType Directory -Path $transactionRoot | Out-Null
    Assert-NotReparsePoint $transactionRoot
    $transactionCreated = $true

    if ($shortcutWillChange) {
        New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
        Assert-NotReparsePoint $startMenuDirectory
        New-Item -ItemType Directory -Path $shortcutTransactionRoot | Out-Null
        Assert-NotReparsePoint $shortcutTransactionRoot
        $shortcutTransactionCreated = $true
    }

    $destinationExeOriginalHash = if ($destinationExeExisted) {
        (Get-FileHash -LiteralPath $destinationExe -Algorithm SHA256).Hash
    }
    else { $null }
    $destinationUninstallerOriginalHash = if ($destinationUninstallerExisted) {
        (Get-FileHash -LiteralPath $destinationUninstaller -Algorithm SHA256).Hash
    }
    else { $null }
    $markerOriginalHash = if ($markerExisted) {
        Assert-RegularFileOrMissing $markerPath
        $markerHashBefore = (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash
        $markerContent = [IO.File]::ReadAllText($markerPath)
        $markerHashAfter = (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash
        if (-not [string]::Equals(
            $markerHashBefore,
            $markerHashAfter,
            [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                $markerContent,
                $ownershipMarkerContent,
                [StringComparison]::Ordinal)) {
            throw "The installation ownership marker changed before it was snapshotted: $markerPath"
        }
        $markerHashAfter
    }
    else { $null }
    $shortcutOriginalHash = if ($shortcutWillChange -and $shortcutExists) {
        Assert-NotReparsePoint $startMenuDirectory
        Assert-RegularFileOrMissing $shortcutPath
        $shortcutHashBefore = (Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash
        $shortcutSnapshotInfo = Get-ShortcutInfo $shortcutPath
        $shortcutHashAfter = (Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash
        if (-not [string]::Equals(
            $shortcutHashBefore,
            $shortcutHashAfter,
            [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-ShortcutInfoEquals $shortcutSnapshotInfo $shortcutOriginalInfo)) {
            throw "The Start Menu shortcut changed before it was snapshotted: $shortcutPath"
        }
        $shortcutHashAfter
    }
    else { $null }

    $stagedExeExpectedHash = Copy-FileWithHashVerification $sourceItem.FullName $stagedExe
    $stagedExeItem = Get-Item -LiteralPath $stagedExe
    if ($stagedExeItem.Length -le 0) {
        throw 'The staged TinyHwBar executable is empty.'
    }
    $displayVersion = Get-ExecutableDisplayVersion $stagedExe
    Assert-FileHashMatchesExpected $stagedExe $stagedExeExpectedHash 'Staged executable'
    $stagedUninstallerExpectedHash =
        Copy-FileWithHashVerification $sourceUninstaller $stagedUninstaller
    $stagedUninstallerItem = Get-Item -LiteralPath $stagedUninstaller
    if ($stagedUninstallerItem.Length -le 0) {
        throw 'The staged TinyHwBar uninstaller is empty.'
    }
    $stagedUninstallerTokens = $null
    $stagedUninstallerErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $stagedUninstaller,
        [ref]$stagedUninstallerTokens,
        [ref]$stagedUninstallerErrors)
    if (@($stagedUninstallerErrors).Count -ne 0) {
        throw ('The staged TinyHwBar uninstaller has PowerShell syntax errors: ' +
            ($stagedUninstallerErrors.Message -join ' | '))
    }
    Assert-FileHashMatchesExpected `
        $stagedUninstaller `
        $stagedUninstallerExpectedHash `
        'Staged uninstaller'
    $stagedMarkerExpectedHash =
        Write-Utf8FileNoClobber $stagedMarker $ownershipMarkerContent

    $exeState = New-InstallFileState `
        'TinyHwBar executable' `
        $destinationExe `
        $stagedExe `
        $stagedExeExpectedHash `
        $destinationExeExisted `
        $destinationExeOriginalHash `
        $backupExe
    $uninstallerState = New-InstallFileState `
        'TinyHwBar uninstaller' `
        $destinationUninstaller `
        $stagedUninstaller `
        $stagedUninstallerExpectedHash `
        $destinationUninstallerExisted `
        $destinationUninstallerOriginalHash `
        $backupUninstaller
    $markerState = New-InstallFileState `
        'TinyHwBar ownership marker' `
        $markerPath `
        $stagedMarker `
        $stagedMarkerExpectedHash `
        $markerExisted `
        $markerOriginalHash `
        $backupMarker

    if (-not $NoStartMenuShortcut) {
        Assert-NotReparsePoint $startMenuDirectory
        Assert-NotReparsePoint $shortcutTransactionRoot
        if (Test-Path -LiteralPath $stagedShortcutBuild) {
            throw "Refusing to overwrite a shortcut staging path: $stagedShortcutBuild"
        }
        New-TinyHwBarShortcut $stagedShortcutBuild $destinationExe $installRoot
        Assert-RegularFileOrMissing $stagedShortcutBuild
        $stagedShortcutInfo = Get-ShortcutInfo $stagedShortcutBuild
        if (-not (Test-TinyHwBarShortcutOwned `
            $stagedShortcutInfo `
            $destinationExe `
            $installRoot)) {
            throw 'The staged Start Menu shortcut did not match the complete TinyHwBar-owned attributes.'
        }
        $stagedShortcutExpectedHash =
            (Get-FileHash -LiteralPath $stagedShortcutBuild -Algorithm SHA256).Hash
        if (Test-Path -LiteralPath $stagedShortcut) {
            throw "Refusing to overwrite a shortcut transaction file: $stagedShortcut"
        }
        [IO.File]::Move($stagedShortcutBuild, $stagedShortcut)
        Assert-FileHashMatchesExpected `
            $stagedShortcut `
            $stagedShortcutExpectedHash `
            'Staged Start Menu shortcut'
        $stagedShortcutInfo = Get-ShortcutInfo $stagedShortcut
        if (-not (Test-TinyHwBarShortcutOwned `
            $stagedShortcutInfo `
            $destinationExe `
            $installRoot)) {
            throw 'The staged Start Menu shortcut changed before it was committed.'
        }
        $shortcutState = New-InstallFileState `
            'TinyHwBar Start Menu shortcut' `
            $shortcutPath `
            $stagedShortcut `
            $stagedShortcutExpectedHash `
            $shortcutExists `
            $shortcutOriginalHash `
            $backupShortcut
    }
    elseif ($shortcutOwned) {
        $shortcutState = New-InstallFileState `
            'TinyHwBar Start Menu shortcut' `
            $shortcutPath `
            $null `
            $null `
            $shortcutExists `
            $shortcutOriginalHash `
            $backupShortcut
    }

    Install-StagedFile $exeState
    Install-StagedFile $uninstallerState
    Install-StagedFile $markerState

    if ($null -ne $shortcutState) {
        if ($NoStartMenuShortcut) {
            Remove-InstalledFileToBackup $shortcutState
        }
        else {
            Install-StagedFile $shortcutState
        }
    }

    $registryBeforeWrite = Get-RegistryKeySnapshot $uninstallRegistrySubKey
    if (-not (Test-RegistrySnapshotsEqual $registryBeforeWrite $uninstallSnapshot)) {
        throw 'An uninstall registration appeared after the locked snapshot and was preserved.'
    }

    $registryModified = $true
    Set-UninstallRegistryEntry `
        $uninstallRegistrySubKey `
        $installRoot `
        $expectedUninstallCommand `
        $displayVersion
    $writtenUninstallSnapshot = Get-RegistryKeySnapshot $uninstallRegistrySubKey
    if (-not (Test-UninstallKeyOwned `
        $writtenUninstallSnapshot `
        $installRoot `
        $expectedUninstallCommand `
        $displayVersion)) {
        throw 'The uninstall registration did not match the complete TinyHwBar-owned value set after writing.'
    }

    $installCompleted = $true
}
catch {
    $originalError = $_.Exception
    $rollbackErrors = @()

    if ($registryModified) {
        try {
            Restore-RegistryKeySnapshot `
                $uninstallSnapshot `
                $uninstallRegistrySubKey `
                $installRoot `
                $expectedUninstallCommand `
                $displayVersion
        }
        catch {
            $rollbackErrors += $_.Exception.Message
        }
    }

    if ($null -ne $shortcutState) {
        try {
            Restore-InstallFileState $shortcutState
            if ($shortcutExists) {
                Assert-FileHashMatchesExpected `
                    $shortcutPath `
                    $shortcutOriginalHash `
                    'Restored Start Menu shortcut'
                $restoredShortcutInfo = Get-ShortcutInfo $shortcutPath
                if (-not (Test-ShortcutInfoEquals `
                    $restoredShortcutInfo `
                    $shortcutOriginalInfo)) {
                    throw 'The restored Start Menu shortcut did not match its original attributes.'
                }
            }
            elseif (Test-Path -LiteralPath $shortcutPath) {
                throw 'A Start Menu shortcut remained after rollback even though none existed originally.'
            }
        }
        catch {
            $rollbackErrors += $_.Exception.Message
        }
    }

    foreach ($state in @($markerState, $uninstallerState, $exeState)) {
        if ($null -eq $state) {
            continue
        }
        try {
            Restore-InstallFileState $state
        }
        catch {
            $rollbackErrors += $_.Exception.Message
        }
    }

    if ($rollbackErrors.Count -ne 0) {
        $preserveTransaction = $true
        $preservedLocations = @()
        if ($transactionCreated) {
            $preservedLocations += $transactionRoot
        }
        if ($shortcutTransactionCreated) {
            $preservedLocations += $shortcutTransactionRoot
        }
        throw ($originalError.Message + ' Rollback was incomplete: ' + ($rollbackErrors -join ' | ') +
            ' Transaction files were preserved at: ' + ($preservedLocations -join ', '))
    }

    throw $originalError
}
finally {
    try {
        if ($shortcutTransactionCreated -and -not $preserveTransaction) {
            Remove-KnownTransactionDirectory `
                $shortcutTransactionRoot `
                $knownShortcutTransactionFiles
        }
        if ($transactionCreated -and -not $preserveTransaction) {
            Remove-KnownTransactionDirectory $transactionRoot $knownTransactionFiles
        }

        if (-not $installCompleted -and -not $installRootExisted -and
            (Test-Path -LiteralPath $installRoot -PathType Container)) {
            Assert-NotReparsePoint $installRoot
            if (@(Get-ChildItem -LiteralPath $installRoot -Force).Count -eq 0) {
                Remove-Item -LiteralPath $installRoot -Force -ErrorAction SilentlyContinue
            }
        }

        if (-not $installCompleted -and -not $startMenuDirectoryExisted -and
            (Test-Path -LiteralPath $startMenuDirectory -PathType Container)) {
            Assert-NotReparsePoint $startMenuDirectory
            if (@(Get-ChildItem -LiteralPath $startMenuDirectory -Force).Count -eq 0) {
                Remove-Item -LiteralPath $startMenuDirectory -Force -ErrorAction SilentlyContinue
            }
        }
    }
    finally {
        if ($null -ne $singletonMutex) {
            try {
                if ($singletonMutexAcquired) {
                    $singletonMutex.ReleaseMutex()
                }
            }
            finally {
                $singletonMutex.Dispose()
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
}

if (-not $installCompleted) {
    throw 'TinyHwBar installation did not complete.'
}

Write-Host "Installed TinyHwBar for the current user: $destinationExe"
Write-Host 'The installer did not change the startup registration; any existing state was preserved. TinyHwBar was not launched.'

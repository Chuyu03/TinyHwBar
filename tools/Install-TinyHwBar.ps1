#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$SourceExe,
    [switch]$NoStartMenuShortcut
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

Assert-NonElevatedProcess 'TinyHwBar installation'

if ([string]::IsNullOrWhiteSpace($SourceExe)) {
    $SourceExe = Join-Path (Split-Path -Parent $PSScriptRoot) 'outputs\TinyHwBar.exe'
}

$ownershipMarkerName = '.tinyhwbar-install-owner'
$ownershipMarkerContent = 'TinyHwBar.UserInstall|2'
$uninstallerName = 'Uninstall-TinyHwBar.ps1'
$uninstallRegistrySubKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\TinyHwBar'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TinyHwBar'
$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ([string]::IsNullOrWhiteSpace($currentUserSid)) {
    throw 'Windows did not return the current user SID required for cross-session maintenance locking.'
}
$maintenanceMutexName = 'Global\TinyHwBar.Maintenance.' + $currentUserSid
$appSingletonMutexName = 'Global\TinyHwBar.Singleton.' + $currentUserSid
$legacySingletonMutexName = 'Local\TinyHwBar.Singleton'
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

function Get-ValidatedLocalFixedFilePath {
    param([string]$Path, [string]$Description)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.StartsWith('\\', [StringComparison]::Ordinal) -or
        $Path.StartsWith('//', [StringComparison]::Ordinal) -or
        [Management.Automation.WildcardPattern]::ContainsWildcardCharacters($Path)) {
        throw "$Description must be a local fixed-drive path without wildcard or device syntax."
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($root) -or
        $fullPath.Substring($root.Length).Contains(':')) {
        throw "$Description does not have a trusted local filesystem path."
    }

    try {
        $drive = [IO.DriveInfo]::new($root)
    }
    catch {
        throw ("Could not verify the local fixed drive for $Description. " + $_.Exception.Message)
    }
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        throw "$Description must be on a local fixed drive; detected $($drive.DriveType)."
    }

    $candidate = $fullPath
    while (-not [string]::IsNullOrWhiteSpace($candidate)) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description has a reparse-point ancestor: $candidate"
            }
        }
        if (Test-PathEquals $candidate $root) {
            break
        }
        $parent = [IO.Directory]::GetParent($candidate)
        if ($null -eq $parent) {
            break
        }
        $candidate = $parent.FullName
    }

    return $fullPath
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
        $displayVersion = '3.0.0'
    }

    return $displayVersion
}

function Get-TinyHwBarCliAssemblyMetadata {
    param([string]$Path, [Version]$ExpectedVersion)

    if ($null -eq $ExpectedVersion) {
        throw 'Expected TinyHwBar assembly version is required.'
    }

    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $reader = New-Object IO.BinaryReader($stream)
        [long]$fileLength = $stream.Length
        if ($fileLength -lt 256 -or $fileLength -gt 536870912L) {
            throw 'TinyHwBar executable has an unsafe file size.'
        }

        $assertRange = {
            param([long]$Offset, [long]$Count, [long]$Limit, [string]$Description)

            if ($Offset -lt 0 -or $Count -lt 0 -or $Offset -gt $Limit -or
                $Count -gt ($Limit - $Offset)) {
                throw "$Description is outside the containing binary range."
            }
        }
        $readHeapIndex = {
            param([int]$Width)

            if ($Width -eq 2) {
                return [long]$reader.ReadUInt16()
            }
            if ($Width -eq 4) {
                return [long]$reader.ReadUInt32()
            }
            throw 'CLI metadata used an unsupported heap-index width.'
        }

        & $assertRange 0 64 $fileLength 'DOS header'
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw 'TinyHwBar executable has an invalid DOS signature.'
        }
        $stream.Position = 0x3C
        [long]$peOffset = $reader.ReadInt32()
        & $assertRange $peOffset 24 $fileLength 'PE and COFF headers'
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw 'TinyHwBar executable has an invalid PE signature.'
        }
        $machine = $reader.ReadUInt16()
        $sectionCount = $reader.ReadUInt16()
        $stream.Position += 12
        $optionalHeaderSize = $reader.ReadUInt16()
        $characteristics = $reader.ReadUInt16()
        if ($machine -ne 0x8664 -or $sectionCount -lt 1 -or $sectionCount -gt 96) {
            throw 'TinyHwBar executable is not a bounded AMD64 PE image.'
        }
        if (($characteristics -band 0x0002) -eq 0 -or
            ($characteristics -band 0x2000) -ne 0) {
            throw 'TinyHwBar executable must be an executable image and must not be a DLL.'
        }

        [long]$optionalHeaderOffset = $peOffset + 24
        if ($optionalHeaderSize -lt 240) {
            throw 'TinyHwBar executable has a truncated PE32+ optional header.'
        }
        & $assertRange $optionalHeaderOffset $optionalHeaderSize $fileLength 'PE32+ optional header'
        $stream.Position = $optionalHeaderOffset
        if ($reader.ReadUInt16() -ne 0x020B) {
            throw 'TinyHwBar executable is not a PE32+ image.'
        }
        $stream.Position = $optionalHeaderOffset + 60
        [long]$sizeOfHeaders = $reader.ReadUInt32()
        $stream.Position = $optionalHeaderOffset + 68
        $subsystem = $reader.ReadUInt16()
        if ($subsystem -ne 2) {
            throw 'TinyHwBar executable is not a Windows GUI image.'
        }
        $stream.Position = $optionalHeaderOffset + 108
        $directoryCount = $reader.ReadUInt32()
        if ($directoryCount -lt 15 -or
            (112L + ([long]$directoryCount * 8L)) -gt $optionalHeaderSize) {
            throw 'TinyHwBar executable has an invalid PE data-directory table.'
        }
        $stream.Position = $optionalHeaderOffset + 224
        [long]$clrRva = $reader.ReadUInt32()
        [long]$clrDirectorySize = $reader.ReadUInt32()
        if ($clrRva -eq 0 -or $clrDirectorySize -lt 72) {
            throw 'TinyHwBar executable does not contain a complete CLR header.'
        }

        [long]$sectionTableOffset = $optionalHeaderOffset + $optionalHeaderSize
        [long]$sectionTableSize = [long]$sectionCount * 40L
        & $assertRange $sectionTableOffset $sectionTableSize $fileLength 'PE section table'
        [long]$sectionTableEnd = $sectionTableOffset + $sectionTableSize
        if ($sizeOfHeaders -lt $sectionTableEnd -or $sizeOfHeaders -gt $fileLength) {
            throw 'TinyHwBar executable has an invalid SizeOfHeaders boundary.'
        }
        $sections = New-Object Collections.Generic.List[object]
        for ($sectionIndex = 0; $sectionIndex -lt $sectionCount; $sectionIndex++) {
            [long]$sectionOffset = $sectionTableOffset + ([long]$sectionIndex * 40L)
            $stream.Position = $sectionOffset + 8
            [long]$virtualSize = $reader.ReadUInt32()
            [long]$virtualAddress = $reader.ReadUInt32()
            [long]$rawSize = $reader.ReadUInt32()
            [long]$rawOffset = $reader.ReadUInt32()
            [long]$virtualSpan = [Math]::Max($virtualSize, $rawSize)
            if ($virtualSpan -le 0 -or
                $virtualAddress -gt 4294967295L -or
                $virtualSpan -gt (4294967296L - $virtualAddress) -or
                $rawOffset -gt $fileLength -or
                $rawSize -gt ($fileLength - $rawOffset)) {
                throw 'TinyHwBar executable contains an invalid PE section range.'
            }
            if ($rawSize -gt 0 -and $rawOffset -lt $sizeOfHeaders) {
                throw 'TinyHwBar executable has section data overlapping its PE headers.'
            }
            $sections.Add([pscustomobject]@{
                VirtualAddress = $virtualAddress
                VirtualSpan = $virtualSpan
                RawOffset = $rawOffset
                RawSize = $rawSize
            })
        }
        for ($leftIndex = 0; $leftIndex -lt $sections.Count; $leftIndex++) {
            for ($rightIndex = $leftIndex + 1; $rightIndex -lt $sections.Count; $rightIndex++) {
                $leftSection = $sections[$leftIndex]
                $rightSection = $sections[$rightIndex]
                $virtualOverlap =
                    $leftSection.VirtualAddress -lt
                        ($rightSection.VirtualAddress + $rightSection.VirtualSpan) -and
                    $rightSection.VirtualAddress -lt
                        ($leftSection.VirtualAddress + $leftSection.VirtualSpan)
                $rawOverlap = $leftSection.RawSize -gt 0 -and $rightSection.RawSize -gt 0 -and
                    $leftSection.RawOffset -lt ($rightSection.RawOffset + $rightSection.RawSize) -and
                    $rightSection.RawOffset -lt ($leftSection.RawOffset + $leftSection.RawSize)
                if ($virtualOverlap -or $rawOverlap) {
                    throw 'TinyHwBar executable contains overlapping PE sections.'
                }
            }
        }

        $mapRva = {
            param([long]$Rva, [long]$Size, [string]$Description)

            if ($Rva -le 0 -or $Size -le 0) {
                throw "$Description has an empty RVA range."
            }
            $matches = @()
            foreach ($section in $sections) {
                if ($Rva -lt $section.VirtualAddress -or
                    $Rva -ge ($section.VirtualAddress + $section.VirtualSpan)) {
                    continue
                }
                [long]$delta = $Rva - $section.VirtualAddress
                if ($Size -gt ($section.VirtualSpan - $delta) -or
                    $delta -gt $section.RawSize -or
                    $Size -gt ($section.RawSize - $delta)) {
                    continue
                }
                [long]$candidateOffset = $section.RawOffset + $delta
                & $assertRange $candidateOffset $Size $fileLength $Description
                $matches += $candidateOffset
            }
            if ($matches.Count -ne 1) {
                throw "$Description does not map to exactly one PE section."
            }
            return [long]$matches[0]
        }

        [long]$clrOffset = & $mapRva $clrRva 72 'CLR header'
        $stream.Position = $clrOffset
        [long]$clrHeaderSize = $reader.ReadUInt32()
        if ($clrHeaderSize -lt 72 -or $clrHeaderSize -gt $clrDirectorySize) {
            throw 'TinyHwBar executable has an invalid CLR header size.'
        }
        [long]$completeClrOffset = & $mapRva $clrRva $clrHeaderSize 'Complete CLR header'
        if ($completeClrOffset -ne $clrOffset) {
            throw 'TinyHwBar executable has an inconsistent CLR header mapping.'
        }
        [void]$reader.ReadUInt16()
        [void]$reader.ReadUInt16()
        [long]$metadataRva = $reader.ReadUInt32()
        [long]$metadataSize = $reader.ReadUInt32()
        [long]$clrFlagsOffset = $stream.Position
        [long]$clrFlags = $reader.ReadUInt32()
        if ($clrFlags -ne 1) {
            throw 'TinyHwBar executable has unsafe or non-AMD64 CLR image flags.'
        }
        if ($metadataSize -lt 32 -or $metadataSize -gt 134217728L) {
            throw 'TinyHwBar executable has an invalid CLR metadata size.'
        }
        [long]$metadataOffset = & $mapRva $metadataRva $metadataSize 'CLR metadata'
        [long]$metadataEnd = $metadataOffset + $metadataSize

        & $assertRange $metadataOffset 16 $metadataEnd 'CLI metadata root'
        $stream.Position = $metadataOffset
        if ($reader.ReadUInt32() -ne 0x424A5342) {
            throw 'TinyHwBar executable has an invalid BSJB metadata signature.'
        }
        [void]$reader.ReadUInt16()
        [void]$reader.ReadUInt16()
        if ($reader.ReadUInt32() -ne 0) {
            throw 'TinyHwBar executable has an invalid CLI metadata reserved value.'
        }
        [long]$versionLength = $reader.ReadUInt32()
        if ($versionLength -lt 1 -or $versionLength -gt 256) {
            throw 'TinyHwBar executable has an invalid CLI metadata version string length.'
        }
        & $assertRange $stream.Position $versionLength $metadataEnd 'CLI metadata version string'
        $versionBytes = $reader.ReadBytes([int]$versionLength)
        if ($versionBytes.Length -ne $versionLength -or $versionBytes[$versionBytes.Length - 1] -ne 0) {
            throw 'TinyHwBar executable has an unterminated CLI metadata version string.'
        }
        [long]$afterVersion = $stream.Position
        [long]$alignedAfterVersion = $metadataOffset +
            ([long][Math]::Ceiling(($afterVersion - $metadataOffset) / 4.0) * 4L)
        & $assertRange $alignedAfterVersion 4 $metadataEnd 'CLI metadata stream-count header'
        $stream.Position = $alignedAfterVersion
        [void]$reader.ReadUInt16()
        $streamCount = $reader.ReadUInt16()
        if ($streamCount -lt 2 -or $streamCount -gt 32) {
            throw 'TinyHwBar executable has an invalid CLI metadata stream count.'
        }

        $metadataStreams = New-Object Collections.Generic.List[object]
        $streamNames = @{}
        for ($metadataStreamIndex = 0; $metadataStreamIndex -lt $streamCount; $metadataStreamIndex++) {
            & $assertRange $stream.Position 8 $metadataEnd 'CLI metadata stream header'
            [long]$relativeStreamOffset = $reader.ReadUInt32()
            [long]$relativeStreamSize = $reader.ReadUInt32()
            [long]$streamNameOffset = $stream.Position
            $streamNameBytes = New-Object Collections.Generic.List[byte]
            $streamNameTerminated = $false
            for ($nameIndex = 0; $nameIndex -lt 32; $nameIndex++) {
                & $assertRange $stream.Position 1 $metadataEnd 'CLI metadata stream name'
                $nameByte = $reader.ReadByte()
                if ($nameByte -eq 0) {
                    $streamNameTerminated = $true
                    break
                }
                if ($nameByte -lt 0x21 -or $nameByte -gt 0x7E) {
                    throw 'TinyHwBar executable has a non-ASCII CLI metadata stream name.'
                }
                [void]$streamNameBytes.Add($nameByte)
            }
            if (-not $streamNameTerminated -or $streamNameBytes.Count -eq 0) {
                throw 'TinyHwBar executable has an invalid CLI metadata stream name.'
            }
            $streamName = [Text.Encoding]::ASCII.GetString($streamNameBytes.ToArray())
            if ($streamNames.ContainsKey($streamName)) {
                throw "TinyHwBar executable has a duplicate CLI metadata stream: $streamName"
            }
            $streamNames[$streamName] = $true
            [long]$nameFieldLength = $stream.Position - $streamNameOffset
            [long]$paddedNameLength = [long][Math]::Ceiling($nameFieldLength / 4.0) * 4L
            [long]$nextStreamHeader = $streamNameOffset + $paddedNameLength
            & $assertRange $nextStreamHeader 0 $metadataEnd 'CLI metadata stream-name padding'
            $stream.Position = $nextStreamHeader
            if ($relativeStreamSize -le 0 -or
                $relativeStreamOffset -gt $metadataSize -or
                $relativeStreamSize -gt ($metadataSize - $relativeStreamOffset)) {
                throw "TinyHwBar executable has an invalid CLI metadata stream range: $streamName"
            }
            $metadataStreams.Add([pscustomobject]@{
                Name = $streamName
                Offset = $metadataOffset + $relativeStreamOffset
                Size = $relativeStreamSize
            })
        }
        [long]$streamHeadersEnd = $stream.Position
        for ($leftIndex = 0; $leftIndex -lt $metadataStreams.Count; $leftIndex++) {
            $leftMetadataStream = $metadataStreams[$leftIndex]
            if ($leftMetadataStream.Offset -lt $streamHeadersEnd) {
                throw 'TinyHwBar executable has a metadata stream overlapping its headers.'
            }
            for ($rightIndex = $leftIndex + 1; $rightIndex -lt $metadataStreams.Count; $rightIndex++) {
                $rightMetadataStream = $metadataStreams[$rightIndex]
                if ($leftMetadataStream.Offset -lt
                        ($rightMetadataStream.Offset + $rightMetadataStream.Size) -and
                    $rightMetadataStream.Offset -lt
                        ($leftMetadataStream.Offset + $leftMetadataStream.Size)) {
                    throw 'TinyHwBar executable contains overlapping CLI metadata streams.'
                }
            }
        }

        $tablesStreams = @($metadataStreams | Where-Object {
            $_.Name -eq '#~' -or $_.Name -eq '#-'
        })
        $stringsStreams = @($metadataStreams | Where-Object { $_.Name -eq '#Strings' })
        if ($tablesStreams.Count -ne 1 -or $stringsStreams.Count -ne 1) {
            throw 'TinyHwBar executable must contain one tables stream and one #Strings stream.'
        }
        $tablesStream = $tablesStreams[0]
        $stringsStream = $stringsStreams[0]
        $stream.Position = $stringsStream.Offset
        if ($reader.ReadByte() -ne 0) {
            throw 'TinyHwBar executable has an invalid #Strings heap origin.'
        }

        [long]$tablesEnd = $tablesStream.Offset + $tablesStream.Size
        & $assertRange $tablesStream.Offset 24 $tablesEnd 'CLI tables stream header'
        $stream.Position = $tablesStream.Offset
        if ($reader.ReadUInt32() -ne 0) {
            throw 'TinyHwBar executable has an invalid CLI tables reserved value.'
        }
        $tablesMajor = $reader.ReadByte()
        $tablesMinor = $reader.ReadByte()
        $heapSizes = $reader.ReadByte()
        $tablesReserved = $reader.ReadByte()
        if ($tablesMajor -ne 2 -or $tablesMinor -ne 0 -or
            ($heapSizes -band 0xF8) -ne 0) {
            throw 'TinyHwBar executable has an unsupported CLI tables-stream header.'
        }
        [uint32]$validLow = $reader.ReadUInt32()
        [uint32]$validHigh = $reader.ReadUInt32()
        [void]$reader.ReadUInt32()
        [void]$reader.ReadUInt32()
        if (($validHigh -band 4294959104) -ne 0) {
            throw 'TinyHwBar executable uses a reserved CLI metadata table.'
        }

        $rowCounts = [long[]](@(0L) * 45)
        for ($tableIndex = 0; $tableIndex -le 44; $tableIndex++) {
            $bitIndex = if ($tableIndex -lt 32) { $tableIndex } else { $tableIndex - 32 }
            [uint32]$bitMask = [uint32]([uint64]1 -shl $bitIndex)
            $tablePresent = if ($tableIndex -lt 32) {
                ($validLow -band $bitMask) -ne 0
            }
            else {
                ($validHigh -band $bitMask) -ne 0
            }
            if ($tablePresent) {
                & $assertRange $stream.Position 4 $tablesEnd 'CLI metadata table row count'
                $rowCounts[$tableIndex] = [long]$reader.ReadUInt32()
            }
        }
        if ($rowCounts[0] -ne 1 -or $rowCounts[32] -ne 1) {
            throw 'TinyHwBar executable must contain exactly one Module row and one Assembly row.'
        }

        $stringIndexSize = if (($heapSizes -band 0x01) -ne 0) { 4 } else { 2 }
        $guidIndexSize = if (($heapSizes -band 0x02) -ne 0) { 4 } else { 2 }
        $blobIndexSize = if (($heapSizes -band 0x04) -ne 0) { 4 } else { 2 }
        $getTableIndexSize = {
            param([int]$Table)
            if ($rowCounts[$Table] -lt 65536L) { return 2 }
            return 4
        }
        $getCodedIndexSize = {
            param([int[]]$Tables, [int]$TagBits)

            [long]$maximumRows = 0
            foreach ($table in $Tables) {
                if ($rowCounts[$table] -gt $maximumRows) {
                    $maximumRows = $rowCounts[$table]
                }
            }
            [long]$smallIndexLimit = [long][Math]::Pow(2, 16 - $TagBits)
            if ($maximumRows -lt $smallIndexLimit) { return 2 }
            return 4
        }

        $rowSizes = [long[]](@(0L) * 45)
        for ($tableIndex = 0; $tableIndex -le 44; $tableIndex++) {
            [long]$rowSize = switch ($tableIndex) {
                0 { 2 + $stringIndexSize + (3 * $guidIndexSize) }
                1 { (& $getCodedIndexSize @(0, 26, 35, 1) 2) + (2 * $stringIndexSize) }
                2 { 4 + (2 * $stringIndexSize) + (& $getCodedIndexSize @(2, 1, 27) 2) + (& $getTableIndexSize 4) + (& $getTableIndexSize 6) }
                3 { & $getTableIndexSize 4 }
                4 { 2 + $stringIndexSize + $blobIndexSize }
                5 { & $getTableIndexSize 6 }
                6 { 8 + $stringIndexSize + $blobIndexSize + (& $getTableIndexSize 8) }
                7 { & $getTableIndexSize 8 }
                8 { 4 + $stringIndexSize }
                9 { (& $getTableIndexSize 2) + (& $getCodedIndexSize @(2, 1, 27) 2) }
                10 { (& $getCodedIndexSize @(2, 1, 26, 6, 27) 3) + $stringIndexSize + $blobIndexSize }
                11 { 2 + (& $getCodedIndexSize @(4, 8, 23) 2) + $blobIndexSize }
                12 { (& $getCodedIndexSize @(6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43) 5) + (& $getCodedIndexSize @(6, 10) 3) + $blobIndexSize }
                13 { (& $getCodedIndexSize @(4, 8) 1) + $blobIndexSize }
                14 { 2 + (& $getCodedIndexSize @(2, 6, 32) 2) + $blobIndexSize }
                15 { 6 + (& $getTableIndexSize 2) }
                16 { 4 + (& $getTableIndexSize 4) }
                17 { $blobIndexSize }
                18 { (& $getTableIndexSize 2) + (& $getTableIndexSize 20) }
                19 { & $getTableIndexSize 20 }
                20 { 2 + $stringIndexSize + (& $getCodedIndexSize @(2, 1, 27) 2) }
                21 { (& $getTableIndexSize 2) + (& $getTableIndexSize 23) }
                22 { & $getTableIndexSize 23 }
                23 { 2 + $stringIndexSize + $blobIndexSize }
                24 { 2 + (& $getTableIndexSize 6) + (& $getCodedIndexSize @(20, 23) 1) }
                25 { (& $getTableIndexSize 2) + (2 * (& $getCodedIndexSize @(6, 10) 1)) }
                26 { $stringIndexSize }
                27 { $blobIndexSize }
                28 { 2 + (& $getCodedIndexSize @(4, 6) 1) + $stringIndexSize + (& $getTableIndexSize 26) }
                29 { 4 + (& $getTableIndexSize 4) }
                30 { 8 }
                31 { 4 }
                32 { 16 + $blobIndexSize + (2 * $stringIndexSize) }
                33 { 4 }
                34 { 12 }
                35 { 12 + (2 * $blobIndexSize) + (2 * $stringIndexSize) }
                36 { 4 + (& $getTableIndexSize 35) }
                37 { 12 + (& $getTableIndexSize 35) }
                38 { 4 + $stringIndexSize + $blobIndexSize }
                39 { 8 + (2 * $stringIndexSize) + (& $getCodedIndexSize @(38, 35, 39) 2) }
                40 { 8 + $stringIndexSize + (& $getCodedIndexSize @(38, 35, 39) 2) }
                41 { 2 * (& $getTableIndexSize 2) }
                42 { 4 + (& $getCodedIndexSize @(2, 6) 1) + $stringIndexSize }
                43 { (& $getCodedIndexSize @(6, 10) 1) + $blobIndexSize }
                44 { (& $getTableIndexSize 42) + (& $getCodedIndexSize @(2, 1, 27) 2) }
            }
            if ($rowSize -le 0) {
                throw "TinyHwBar executable has an unsupported CLI metadata table: $tableIndex"
            }
            $rowSizes[$tableIndex] = $rowSize
        }

        $tableOffsets = [long[]](@(0L) * 45)
        [long]$tableDataOffset = $stream.Position
        for ($tableIndex = 0; $tableIndex -le 44; $tableIndex++) {
            if ($rowCounts[$tableIndex] -eq 0) {
                continue
            }
            [long]$rowSize = $rowSizes[$tableIndex]
            if ($rowCounts[$tableIndex] -gt ([long]::MaxValue / $rowSize)) {
                throw 'TinyHwBar executable has an overflowing CLI metadata table size.'
            }
            [long]$tableSize = $rowCounts[$tableIndex] * $rowSize
            & $assertRange $tableDataOffset $tableSize $tablesEnd "CLI metadata table $tableIndex"
            $tableOffsets[$tableIndex] = $tableDataOffset
            $tableDataOffset += $tableSize
        }
        [long]$trailingTableBytes = $tablesEnd - $tableDataOffset
        if ($trailingTableBytes -lt 0 -or $trailingTableBytes -gt 4) {
            throw 'TinyHwBar executable has unexpected data after its CLI metadata tables.'
        }
        $stream.Position = $tableDataOffset
        for ($paddingIndex = 0; $paddingIndex -lt $trailingTableBytes; $paddingIndex++) {
            if ($reader.ReadByte() -ne 0) {
                throw 'TinyHwBar executable has nonzero CLI metadata table padding.'
            }
        }

        [long]$assemblyOffset = $tableOffsets[32]
        & $assertRange $assemblyOffset $rowSizes[32] $tablesEnd 'CLI Assembly row'
        $stream.Position = $assemblyOffset
        [void]$reader.ReadUInt32()
        [long]$assemblyVersionOffset = $stream.Position
        $assemblyMajor = $reader.ReadUInt16()
        $assemblyMinor = $reader.ReadUInt16()
        $assemblyBuild = $reader.ReadUInt16()
        $assemblyRevision = $reader.ReadUInt16()
        $assemblyFlags = $reader.ReadUInt32()
        [long]$publicKeyIndex = & $readHeapIndex $blobIndexSize
        [long]$assemblyNameIndex = & $readHeapIndex $stringIndexSize
        [long]$assemblyCultureIndex = & $readHeapIndex $stringIndexSize
        if ($assemblyFlags -ne 0 -or $publicKeyIndex -ne 0 -or $assemblyCultureIndex -ne 0) {
            throw 'TinyHwBar assembly must be unsigned, neutral-culture, and use default assembly flags.'
        }

        $readString = {
            param([long]$Index, [string]$Description)

            if ($Index -le 0 -or $Index -ge $stringsStream.Size) {
                throw "$Description has an invalid #Strings index."
            }
            [long]$stringOffset = $stringsStream.Offset + $Index
            [long]$stringsEnd = $stringsStream.Offset + $stringsStream.Size
            $stream.Position = $stringOffset
            $bytes = New-Object Collections.Generic.List[byte]
            $terminated = $false
            while ($stream.Position -lt $stringsEnd -and $bytes.Count -le 1024) {
                $value = $reader.ReadByte()
                if ($value -eq 0) {
                    $terminated = $true
                    break
                }
                [void]$bytes.Add($value)
            }
            if (-not $terminated -or $bytes.Count -eq 0 -or $bytes.Count -gt 1024) {
                throw "$Description is missing a bounded NUL terminator."
            }
            try {
                $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
                $text = $strictUtf8.GetString($bytes.ToArray())
            }
            catch {
                throw "$Description is not valid UTF-8."
            }
            return [pscustomobject]@{
                Text = $text
                Offset = $stringOffset
                ByteLength = $bytes.Count
            }
        }
        $assemblyName = & $readString $assemblyNameIndex 'CLI Assembly name'
        $assemblyVersion = New-Object Version(
            $assemblyMajor,
            $assemblyMinor,
            $assemblyBuild,
            $assemblyRevision)
        if (-not [string]::Equals(
                $assemblyName.Text,
                'TinyHwBar',
                [StringComparison]::Ordinal) -or
            $assemblyVersion -ne $ExpectedVersion) {
            throw "TinyHwBar CLI Assembly identity does not match TinyHwBar $ExpectedVersion."
        }

        return [pscustomobject]@{
            Name = $assemblyName.Text
            Version = $assemblyVersion
            Machine = $machine
            OptionalHeaderMagic = 0x020B
            Subsystem = $subsystem
            Characteristics = $characteristics
            ClrFlags = $clrFlags
            ClrFlagsOffset = $clrFlagsOffset
            MetadataRootOffset = $metadataOffset
            AssemblyVersionOffset = $assemblyVersionOffset
            AssemblyNameOffset = $assemblyName.Offset
            AssemblyNameByteLength = $assemblyName.ByteLength
        }
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        elseif ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Get-TinyHwBarExecutableMetadata {
    param([string]$Path)

    Assert-RegularFileOrMissing $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "TinyHwBar executable not found: $Path"
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (-not [string]::Equals($item.Extension, '.exe', [StringComparison]::OrdinalIgnoreCase) -or
        $item.Length -lt 64) {
        throw 'Source must be a non-empty Windows executable.'
    }

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName)
    foreach ($identity in @(
        [pscustomobject]@{ Name = 'ProductName'; Value = $versionInfo.ProductName; Expected = 'TinyHwBar' },
        [pscustomobject]@{ Name = 'InternalName'; Value = $versionInfo.InternalName; Expected = 'TinyHwBar.exe' },
        [pscustomobject]@{ Name = 'OriginalFilename'; Value = $versionInfo.OriginalFilename; Expected = 'TinyHwBar.exe' })) {
        if (-not [string]::Equals(
            [string]$identity.Value,
            [string]$identity.Expected,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw ("Source executable identity mismatch for $($identity.Name); " +
                "expected '$($identity.Expected)'.")
        }
    }

    $productVersionText = [string]$versionInfo.ProductVersion
    $fileVersionText = [string]$versionInfo.FileVersion
    $productVersion = $null
    $fileVersion = $null
    if ([string]::IsNullOrWhiteSpace($productVersionText) -or
        -not [Version]::TryParse($productVersionText, [ref]$productVersion) -or
        [string]::IsNullOrWhiteSpace($fileVersionText) -or
        -not [Version]::TryParse($fileVersionText, [ref]$fileVersion)) {
        throw 'Source executable has missing or invalid product/file version metadata.'
    }
    $canonicalProductVersion = '{0}.{1}.{2}' -f `
        $productVersion.Major, $productVersion.Minor, $productVersion.Build
    $canonicalFileVersion = '{0}.{1}.{2}.{3}' -f `
        $fileVersion.Major, $fileVersion.Minor, $fileVersion.Build, $fileVersion.Revision
    if ($productVersion.Revision -ne -1 -or
        $fileVersion.Revision -ne 0 -or
        -not [string]::Equals(
            $productVersionText,
            $canonicalProductVersion,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $fileVersionText,
            $canonicalFileVersion,
            [StringComparison]::Ordinal) -or
        $productVersion.Major -ne $fileVersion.Major -or
        $productVersion.Minor -ne $fileVersion.Minor -or
        $productVersion.Build -ne $fileVersion.Build) {
        throw 'Source executable product and file versions are inconsistent.'
    }

    [void](Get-TinyHwBarCliAssemblyMetadata $item.FullName $fileVersion)

    return [pscustomobject]@{
        FullName = $item.FullName
        DisplayVersion = $canonicalProductVersion
        FileVersion = $fileVersion
    }
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
    param(
        [string]$Source,
        [string]$Destination,
        [string]$ExpectedSourceHash)

    Assert-RegularFileOrMissing $Source
    if ([string]::IsNullOrWhiteSpace($ExpectedSourceHash)) {
        throw "The approved source does not have a trusted SHA-256 snapshot: $Source"
    }
    $destinationParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Destination))
    Assert-NotReparsePoint $destinationParent
    if (Test-Path -LiteralPath $Destination) {
        throw "Refusing to overwrite an existing transaction file: $Destination"
    }

    $sourceHashBefore = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    if (-not [string]::Equals(
        $sourceHashBefore,
        $ExpectedSourceHash,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "The approved source changed before staging: $Source"
    }
    [IO.File]::Copy($Source, $Destination, $false)
    Assert-RegularFileOrMissing $Destination
    $sourceHashAfter = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if (-not [string]::Equals(
        $ExpectedSourceHash,
        $sourceHashAfter,
        [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $ExpectedSourceHash,
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

$SourceExe = Get-ValidatedLocalFixedFilePath `
    $SourceExe `
    'TinyHwBar source executable'
if (-not (Test-Path -LiteralPath $SourceExe -PathType Leaf)) {
    throw "TinyHwBar executable not found: $SourceExe"
}
if (-not (Test-Path -LiteralPath $sourceUninstaller -PathType Leaf)) {
    throw "Uninstaller script not found: $sourceUninstaller"
}
if (-not (Test-Path -LiteralPath $powershellExe -PathType Leaf)) {
    throw "Windows PowerShell 5.1 was not found: $powershellExe"
}

$sourceItem = Get-Item -LiteralPath $SourceExe -Force
$sourceExeHashBefore = (Get-FileHash -LiteralPath $sourceItem.FullName -Algorithm SHA256).Hash
$sourceMetadata = Get-TinyHwBarExecutableMetadata $sourceItem.FullName
$sourcePathAfterMetadata = Get-ValidatedLocalFixedFilePath `
    $sourceItem.FullName `
    'TinyHwBar source executable'
$sourceExeHashAfter = (Get-FileHash -LiteralPath $sourcePathAfterMetadata -Algorithm SHA256).Hash
if (-not (Test-PathEquals $sourcePathAfterMetadata $sourceItem.FullName) -or
    -not [string]::Equals(
        $sourceExeHashBefore,
        $sourceExeHashAfter,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'TinyHwBar source executable changed while its identity was being validated.'
}
$sourceExeExpectedHash = $sourceExeHashAfter
Assert-RegularFileOrMissing $sourceUninstaller
$sourceUninstallerExpectedHash = (
    Get-FileHash -LiteralPath $sourceUninstaller -Algorithm SHA256).Hash
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
$appSingletonMutex = $null
$appSingletonMutexAcquired = $false
$legacySingletonMutex = $null
$legacySingletonMutexAcquired = $false
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

    $appSingletonMutex = [Threading.Mutex]::new($false, $appSingletonMutexName)
    try {
        $appSingletonMutexAcquired = $appSingletonMutex.WaitOne(0, $false)
    }
    catch [Threading.AbandonedMutexException] {
        $appSingletonMutexAcquired = $true
    }

    if (-not $appSingletonMutexAcquired) {
        throw 'A TinyHwBar instance is active for this Windows user, possibly in another session. No installation artifacts were changed; exit every copy and retry.'
    }

    $legacySingletonMutex = [Threading.Mutex]::new($false, $legacySingletonMutexName)
    try {
        $legacySingletonMutexAcquired = $legacySingletonMutex.WaitOne(0, $false)
    }
    catch [Threading.AbandonedMutexException] {
        $legacySingletonMutexAcquired = $true
    }

    if (-not $legacySingletonMutexAcquired) {
        throw 'A legacy current-session TinyHwBar instance is active. No installation artifacts were changed; exit TinyHwBar and retry.'
    }

    $runningAfterMutexAcquisition = @(
        Get-CimInstance Win32_Process -Filter "Name = 'TinyHwBar.exe'")
    $postMutexProcessCheck = Get-TinyHwBarProcessBlockers `
        $runningAfterMutexAcquisition `
        $currentUserSid `
        $destinationExe
    Write-TinyHwBarProcessOwnerWarning $postMutexProcessCheck
    if (@($postMutexProcessCheck.BlockingProcesses).Count -ne 0) {
        throw ('A normally named TinyHwBar process for this Windows user, or a process using this ' +
            'user''s installed TinyHwBar path, is running. No installation artifacts were changed; ' +
            'exit every installed or portable copy for this Windows user and retry.')
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

    $sourcePathAtStaging = Get-ValidatedLocalFixedFilePath `
        $sourceItem.FullName `
        'TinyHwBar source executable'
    if (-not (Test-PathEquals $sourcePathAtStaging $sourceItem.FullName)) {
        throw 'TinyHwBar source executable path changed before staging.'
    }
    $stagedExeExpectedHash = Copy-FileWithHashVerification `
        $sourcePathAtStaging `
        $stagedExe `
        $sourceExeExpectedHash
    $stagedExeItem = Get-Item -LiteralPath $stagedExe
    if ($stagedExeItem.Length -le 0) {
        throw 'The staged TinyHwBar executable is empty.'
    }
    $stagedMetadata = Get-TinyHwBarExecutableMetadata $stagedExe
    if (-not [string]::Equals(
        $sourceMetadata.DisplayVersion,
        $stagedMetadata.DisplayVersion,
        [StringComparison]::Ordinal) -or
        $sourceMetadata.FileVersion -ne $stagedMetadata.FileVersion) {
        throw 'The staged executable metadata does not match the validated source executable.'
    }
    $displayVersion = $stagedMetadata.DisplayVersion
    Assert-FileHashMatchesExpected $stagedExe $stagedExeExpectedHash 'Staged executable'
    $stagedUninstallerExpectedHash =
        Copy-FileWithHashVerification `
            $sourceUninstaller `
            $stagedUninstaller `
            $sourceUninstallerExpectedHash
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
}

if (-not $installCompleted) {
    throw 'TinyHwBar installation did not complete.'
}

Write-Host "Installed TinyHwBar for the current user: $destinationExe"
Write-Host 'The installer did not change the startup registration; any existing state was preserved. TinyHwBar was not launched.'

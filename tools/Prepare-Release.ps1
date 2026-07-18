#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$Commit,

    [Parameter(Mandatory = $true)]
    [string]$GitExe,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ApprovedGitSha256,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ApprovedGitSignerThumbprint,

    [Parameter(Mandatory = $true)]
    [string]$TrustedWorkspaceRoot,

    [string]$StagingRoot,

    [switch]$ApproveFrozenCommitScripts
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

Assert-NonElevatedProcess 'TinyHwBar release preparation'

$bundleAllowlist = @('LICENSE', 'README.md', 'TinyHwBar.exe')
$repositoryUrl = 'https://github.com/Chuyu03/TinyHwBar'
$templateRelativePath = 'tools\BinaryRelease-README.md'

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

function Test-IsDescendantPath {
    param([string]$Parent, [string]$Child)

    if ([string]::IsNullOrWhiteSpace($Parent) -or
        [string]::IsNullOrWhiteSpace($Child)) {
        return $false
    }

    $parentPrefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $fullChild = [IO.Path]::GetFullPath($Child)
    return $fullChild.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-ValidatedLocalFixedPath {
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
        throw "$Description does not have a trusted filesystem root."
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

function Get-ValidatedStagingPath {
    param(
        [string]$Path,
        [string]$RepositoryRoot,
        [string]$AllowedStagingRoot,
        [string]$GitDirectory)

    $candidate = $Path
    if (-not [IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $AllowedStagingRoot $candidate
    }
    $fullPath = Get-ValidatedLocalFixedPath $candidate 'Release staging path'

    if (-not (Test-IsDescendantPath $AllowedStagingRoot $fullPath)) {
        throw "Release staging must be a new child of: $AllowedStagingRoot"
    }
    if ((Test-PathEquals $fullPath $RepositoryRoot) -or
        (Test-IsDescendantPath $RepositoryRoot $fullPath)) {
        $relativePath = $fullPath.Substring(
            [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\').Length).TrimStart('\')
        if (@($relativePath.Split([char[]]@('\', '/'))) -icontains '.git') {
            throw 'Release staging must not contain a .git path segment.'
        }
    }
    if ((Test-PathEquals $fullPath $GitDirectory) -or
        (Test-IsDescendantPath $GitDirectory $fullPath)) {
        throw 'Release staging must not write inside the Git metadata directory.'
    }

    return $fullPath
}

function New-SafeDirectoryNoClobber {
    param([string]$Path, [string]$Description)

    if (Test-Path -LiteralPath $Path) {
        throw "$Description already exists and will not be reused: $Path"
    }
    $parent = [IO.Directory]::GetParent($Path)
    if ($null -eq $parent -or
        -not (Test-Path -LiteralPath $parent.FullName -PathType Container)) {
        throw "$Description does not have an existing directory parent: $Path"
    }

    New-Item -ItemType Directory -Path $Path -ErrorAction Stop | Out-Null
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description was not created as a directory: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description became a reparse point during creation: $Path"
    }
}

function Assert-SafeDirectoryOrMissing {
    param([string]$Path, [string]$Description)

    [void](Get-ValidatedLocalFixedPath $Path $Description)
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            throw "$Description exists but is not a directory: $Path"
        }
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description is a reparse point: $Path"
        }
    }
}

function Get-CanonicalReleaseVersion {
    param([string]$Value)

    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'Version must use canonical ASCII major.minor.patch form without leading zeroes.'
    }

    $parsed = $null
    if (-not [Version]::TryParse(($Value + '.0'), [ref]$parsed) -or
        $parsed.Major -gt 65534 -or
        $parsed.Minor -gt 65534 -or
        $parsed.Build -gt 65534 -or
        -not [string]::Equals(
            $Value,
            ('{0}.{1}.{2}' -f $parsed.Major, $parsed.Minor, $parsed.Build),
            [StringComparison]::Ordinal)) {
        throw 'Version components must be canonical integers from 0 through 65534.'
    }

    return $parsed
}

function Invoke-ExternalCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$Description)

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-GitProcessEnvironmentSnapshot {
    $snapshot = @{}
    foreach ($entry in @(Get-ChildItem Env:)) {
        if ($entry.Name.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase)) {
            if ([string]::IsNullOrEmpty([string]$entry.Value)) {
                throw (
                    'An inherited empty Git environment variable cannot be restored exactly in ' +
                    "Windows PowerShell 5.1: $($entry.Name)")
            }
            $snapshot[[string]$entry.Name] = [string]$entry.Value
        }
    }

    return $snapshot
}

function Clear-GitProcessEnvironment {
    foreach ($entry in @(Get-ChildItem Env:)) {
        if ($entry.Name.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase)) {
            [Environment]::SetEnvironmentVariable(
                [string]$entry.Name,
                $null,
                [EnvironmentVariableTarget]::Process)
        }
    }
}

function Restore-GitProcessEnvironment {
    param([Collections.IDictionary]$Snapshot)

    Clear-GitProcessEnvironment
    foreach ($name in @($Snapshot.Keys)) {
        [Environment]::SetEnvironmentVariable(
            [string]$name,
            [string]$Snapshot[$name],
            [EnvironmentVariableTarget]::Process)
    }
}

function Assert-SafeLocalGitConfiguration {
    param([string]$GitMetadataDirectory)

    $configCandidates = @(
        [pscustomobject]@{
            Path = Join-Path $GitMetadataDirectory 'config'
            Required = $true
        },
        [pscustomobject]@{
            Path = Join-Path $GitMetadataDirectory 'config.worktree'
            Required = $false
        })
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    foreach ($candidate in $configCandidates) {
        if (-not (Test-Path -LiteralPath $candidate.Path)) {
            if ($candidate.Required) {
                throw "The trusted release repository is missing its local Git config: $($candidate.Path)"
            }
            continue
        }

        $item = Get-Item -LiteralPath $candidate.Path -Force
        if ($item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $item.Length -gt 1048576L) {
            throw "Local Git config must be a bounded regular file: $($candidate.Path)"
        }

        $bytes = [IO.File]::ReadAllBytes($item.FullName)
        if ($bytes -contains [byte]0) {
            throw "Local Git config must not contain NUL bytes: $($candidate.Path)"
        }
        try {
            $text = $strictUtf8.GetString($bytes)
        }
        catch {
            throw "Local Git config must be valid UTF-8: $($candidate.Path)"
        }
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
            $text = $text.Substring(1)
        }
        if ([Text.RegularExpressions.Regex]::IsMatch(
            $text,
            '\\(?:\r\n|\n|\r)')) {
            throw "Local Git config line continuations are not allowed for release preparation: $($candidate.Path)"
        }
        if ([Text.RegularExpressions.Regex]::IsMatch(
            $text,
            '(?im)^[\t ]*\[[\t ]*(?:includeif|include|fsck|tar)(?=[\t ."]|\])')) {
            throw (
                'Local Git include/includeIf, fsck, and tar override sections are not allowed for ' +
                "release preparation: $($candidate.Path)")
        }
    }
}

function Get-ValidatedGitObjectsDirectory {
    param(
        [object[]]$Output,
        [string]$RepositoryRoot,
        [string]$GitMetadataDirectory)

    if (@($Output).Count -ne 1) {
        throw 'Git must return exactly one object-directory path.'
    }
    $line = ([string](@($Output)[0])).Trim()
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw 'Git returned an invalid object directory.'
    }
    $candidate = if ([IO.Path]::IsPathRooted($line)) {
        $line
    }
    else {
        Join-Path $RepositoryRoot $line
    }
    $resolved = [IO.Path]::GetFullPath($candidate)
    $expected = [IO.Path]::GetFullPath((Join-Path $GitMetadataDirectory 'objects'))
    if (-not (Test-PathEquals $resolved $expected) -or
        -not (Test-Path -LiteralPath $expected -PathType Container)) {
        throw "The trusted release source must keep its object database in $expected"
    }

    return $expected
}

function Get-NormalizedMarkdownTarget {
    param([string]$RawTarget)

    $target = $RawTarget.Trim()
    if ($target.StartsWith('<') -and $target.EndsWith('>')) {
        $target = $target.Substring(1, $target.Length - 2)
    }
    else {
        $spaceIndex = $target.IndexOf(' ')
        if ($spaceIndex -ge 0) {
            $target = $target.Substring(0, $spaceIndex)
        }
    }

    return [Uri]::UnescapeDataString($target).Replace('\', '/')
}

function Assert-BinaryReadmeLinks {
    param(
        [string]$Markdown,
        [string[]]$AllowedBundleFiles)

    $matches = [regex]::Matches($Markdown, '!?(?:\[[^\]]*\])\(([^)]+)\)')
    $unparsedMarkdown = $Markdown
    for ($index = $matches.Count - 1; $index -ge 0; $index--) {
        $matchToMask = $matches[$index]
        $unparsedMarkdown = $unparsedMarkdown.Remove(
            $matchToMask.Index,
            $matchToMask.Length).Insert(
                $matchToMask.Index,
                (' ' * $matchToMask.Length))
    }
    if ([regex]::IsMatch(
        $unparsedMarkdown,
        '(?im)!?\[[^\]]+\]\s*\[[^\]]*\]|^\s*\[[^\]]+\]:|<[^>]*>|https?://|//')) {
        throw 'Binary README uses an unsupported reference, raw HTML, automatic, bare, or protocol-relative URL form.'
    }

    foreach ($match in $matches) {
        $target = Get-NormalizedMarkdownTarget $match.Groups[1].Value
        if ([string]::IsNullOrWhiteSpace($target) -or $target.StartsWith('#')) {
            continue
        }

        $absoluteUri = $null
        if ([Uri]::TryCreate($target, [UriKind]::Absolute, [ref]$absoluteUri)) {
            $allowedPath = '/Chuyu03/TinyHwBar'
            if ($absoluteUri.Scheme -ne 'https' -or
                -not $absoluteUri.IsDefaultPort -or
                -not [string]::IsNullOrEmpty($absoluteUri.UserInfo) -or
                -not [string]::IsNullOrEmpty($absoluteUri.Query) -or
                -not [string]::Equals(
                    $absoluteUri.Host,
                    'github.com',
                    [StringComparison]::OrdinalIgnoreCase) -or
                (-not [string]::Equals(
                    $absoluteUri.AbsolutePath.TrimEnd('/'),
                    $allowedPath,
                    [StringComparison]::Ordinal) -and
                -not $absoluteUri.AbsolutePath.StartsWith(
                    ($allowedPath + '/'),
                    [StringComparison]::Ordinal))) {
                throw "Binary README contains an external link outside the approved TinyHwBar repository: $target"
            }
            continue
        }

        $separatorIndex = $target.IndexOfAny([char[]]@('#', '?'))
        $relativeTarget = if ($separatorIndex -ge 0) {
            $target.Substring(0, $separatorIndex)
        }
        else {
            $target
        }
        if ($relativeTarget.StartsWith('./', [StringComparison]::Ordinal)) {
            $relativeTarget = $relativeTarget.Substring(2)
        }
        if ($relativeTarget.StartsWith('../', [StringComparison]::Ordinal) -or
            $relativeTarget.Contains('/../') -or
            $relativeTarget.Contains('/') -or
            $AllowedBundleFiles -cnotcontains $relativeTarget) {
            throw "Binary README references a file outside the release bundle allowlist: $target"
        }
    }
}

function Get-Sha256Hex {
    param([string]$Path)

    $stream = $null
    $sha256 = $null
    try {
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        return [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        if ($null -ne $sha256) {
            $sha256.Dispose()
        }
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Get-GitForWindowsLayout {
    param([string]$Path)

    $gitPath = Get-ValidatedLocalFixedPath $Path 'Git executable'
    $binDirectory = [IO.Directory]::GetParent($gitPath)
    $mingw64Directory = if ($null -ne $binDirectory) {
        $binDirectory.Parent
    }
    else {
        $null
    }
    $gitRootDirectory = if ($null -ne $mingw64Directory) {
        $mingw64Directory.Parent
    }
    else {
        $null
    }

    if ($null -eq $binDirectory -or
        $null -eq $mingw64Directory -or
        $null -eq $gitRootDirectory -or
        -not [string]::Equals(
            [IO.Path]::GetFileName($gitPath),
            'git.exe',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $binDirectory.Name,
            'bin',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $mingw64Directory.Name,
            'mingw64',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-PathEquals `
            $gitPath `
            (Join-Path $gitRootDirectory.FullName 'mingw64\bin\git.exe'))) {
        throw (
            'Git executable must be the real Git for Windows binary at ' +
            '<root>\mingw64\bin\git.exe; cmd\git.exe shims and other layouts are rejected.')
    }

    return [pscustomobject]@{
        Root = $gitRootDirectory.FullName
        Mingw64 = $mingw64Directory.FullName
        Bin = $binDirectory.FullName
        GitExecPath = Join-Path $mingw64Directory.FullName 'libexec\git-core'
        GitExe = $gitPath
    }
}

function Test-ReleaseAclRuleWriteRisk {
    param(
        [Security.AccessControl.FileSystemAccessRule]$Rule,
        [long]$CurrentObjectRightsMask,
        [long]$FutureChildRightsMask,
        [bool]$CheckFutureChildren)

    if ($Rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
        return $false
    }

    [long]$ruleRights = ([long][int]$Rule.FileSystemRights) -band 4294967295L
    $inheritOnly = (
        $Rule.PropagationFlags -band
        [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0
    $currentObjectRisk = -not $inheritOnly -and
        (($ruleRights -band $CurrentObjectRightsMask) -ne 0)
    $childInheritanceFlags = (
        [Security.AccessControl.InheritanceFlags]::ObjectInherit -bor
        [Security.AccessControl.InheritanceFlags]::ContainerInherit)
    $futureChildRisk = $CheckFutureChildren -and
        (($Rule.InheritanceFlags -band $childInheritanceFlags) -ne 0) -and
        (($ruleRights -band $FutureChildRightsMask) -ne 0)

    return $currentObjectRisk -or $futureChildRisk
}

function Get-ReleaseBoundaryParent {
    param([IO.FileSystemInfo]$Item)

    if ($null -eq $Item) {
        throw 'Could not resolve the filesystem item whose boundary parent must be checked.'
    }
    if ($Item -is [IO.DirectoryInfo]) {
        return $Item.Parent
    }
    if ($Item -is [IO.FileInfo]) {
        return $Item.Directory
    }

    throw "Unsupported filesystem item type in release boundary validation: $($Item.GetType().FullName)"
}

function Assert-TrustedFileSystemBoundary {
    param(
        [string]$RootPath,
        [string]$BoundaryName,
        [ValidateRange(1, 50000)]
        [int]$MaxEntryCount = 20000)

    $validatedRootPath = Get-ValidatedLocalFixedPath $RootPath $BoundaryName
    if (-not (Test-Path -LiteralPath $validatedRootPath)) {
        throw "$BoundaryName must already exist before it can be trusted: $validatedRootPath"
    }

    $identity = $null
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if ($null -eq $identity.User) {
            throw "Could not resolve the current Windows user SID for $BoundaryName validation."
        }
        $trustedSidValues = @{
            $identity.User.Value = $true
            'S-1-5-18' = $true
            'S-1-5-32-544' = $true
        }
        try {
            $trustedInstallerSid = ([Security.Principal.NTAccount]::new(
                'NT SERVICE',
                'TrustedInstaller')).Translate(
                    [Security.Principal.SecurityIdentifier])
            $trustedSidValues[$trustedInstallerSid.Value] = $true
        }
        catch {
            throw "Could not resolve the Windows TrustedInstaller SID for $BoundaryName validation."
        }
    }
    finally {
        if ($null -ne $identity) {
            $identity.Dispose()
        }
    }

    [long]$writeAuthorityMask = (
        [long][Security.AccessControl.FileSystemRights]::WriteData -bor
        [long][Security.AccessControl.FileSystemRights]::AppendData -bor
        [long][Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [long][Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [long][Security.AccessControl.FileSystemRights]::Delete -bor
        [long][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [long][Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [long][Security.AccessControl.FileSystemRights]::TakeOwnership -bor
        0x10000000L -bor # GENERIC_ALL
        0x40000000L)    # GENERIC_WRITE
    [long]$ancestorSubstitutionMask = (
        [long][Security.AccessControl.FileSystemRights]::Delete -bor
        [long][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [long][Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [long][Security.AccessControl.FileSystemRights]::TakeOwnership -bor
        0x10000000L)    # GENERIC_ALL

    $rootItem = Get-Item -LiteralPath $validatedRootPath -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$BoundaryName root must not be a reparse point: $validatedRootPath"
    }

    $runtimeItems = New-Object Collections.Generic.List[IO.FileSystemInfo]
    $runtimeItems.Add($rootItem)
    $directoryQueue = New-Object Collections.Generic.Queue[IO.DirectoryInfo]
    if ($rootItem.PSIsContainer) {
        $directoryQueue.Enqueue($rootItem)
    }
    while ($directoryQueue.Count -gt 0) {
        $directory = $directoryQueue.Dequeue()
        foreach ($child in @(Get-ChildItem -LiteralPath $directory.FullName -Force)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$BoundaryName tree must not contain reparse points: $($child.FullName)"
            }
            $runtimeItems.Add($child)
            if ($runtimeItems.Count -gt $MaxEntryCount) {
                throw "$BoundaryName tree exceeds the $MaxEntryCount-entry validation limit."
            }
            if ($child.PSIsContainer) {
                $directoryQueue.Enqueue($child)
            }
        }
    }

    $pathsToValidate = New-Object Collections.Generic.List[object]
    foreach ($runtimeItem in $runtimeItems) {
        $pathsToValidate.Add([pscustomobject]@{
            Path = $runtimeItem.FullName
            RightsMask = $writeAuthorityMask
            FutureChildRightsMask = if ($runtimeItem.PSIsContainer) {
                $writeAuthorityMask
            }
            else {
                0L
            }
            CheckFutureChildren = [bool]$runtimeItem.PSIsContainer
            Scope = "$BoundaryName tree"
        })
    }
    $ancestor = Get-ReleaseBoundaryParent $rootItem
    while ($null -ne $ancestor) {
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$BoundaryName ancestor must not be a reparse point: $($ancestor.FullName)"
        }
        $pathsToValidate.Add([pscustomobject]@{
            Path = $ancestor.FullName
            RightsMask = $ancestorSubstitutionMask
            FutureChildRightsMask = 0L
            CheckFutureChildren = $false
            Scope = "$BoundaryName ancestor"
        })
        $ancestor = $ancestor.Parent
    }

    foreach ($protectedPath in $pathsToValidate) {
        $path = $protectedPath.Path

        try {
            $acl = Get-Acl -LiteralPath $path
        }
        catch {
            throw (
                "Could not read the $BoundaryName ACL: $path. " +
                $_.Exception.Message)
        }

        try {
            $rawSecurityDescriptor = New-Object `
                Security.AccessControl.RawSecurityDescriptor(
                    $acl.GetSecurityDescriptorBinaryForm(),
                    0)
        }
        catch {
            throw "Could not inspect the $BoundaryName raw security descriptor: $path"
        }
        if (($rawSecurityDescriptor.ControlFlags -band
                [Security.AccessControl.ControlFlags]::DiscretionaryAclPresent) -eq 0 -or
            $null -eq $rawSecurityDescriptor.DiscretionaryAcl) {
            throw "$BoundaryName must have a present, non-null DACL: $path"
        }

        $ownerSid = $null
        try {
            $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier])
        }
        catch {
            throw "Could not resolve the $BoundaryName owner SID: $path"
        }
        if ($null -eq $ownerSid -or -not $trustedSidValues.ContainsKey($ownerSid.Value)) {
            $ownerValue = if ($null -eq $ownerSid) { 'unresolved' } else { $ownerSid.Value }
            throw (
                "$($protectedPath.Scope) has an unapproved owner that can change its DACL: " +
                "$path; owner: $ownerValue.")
        }

        foreach ($rule in @($acl.Access)) {
            if (-not (Test-ReleaseAclRuleWriteRisk `
                $rule `
                ([long]$protectedPath.RightsMask) `
                ([long]$protectedPath.FutureChildRightsMask) `
                ([bool]$protectedPath.CheckFutureChildren))) {
                continue
            }

            $ruleSid = $null
            try {
                $ruleSid = $rule.IdentityReference.Translate(
                    [Security.Principal.SecurityIdentifier])
            }
            catch {
                throw (
                    "$BoundaryName ACL contains an untranslatable identity with write authority: " +
                    "$path; identity: $($rule.IdentityReference.Value).")
            }
            if ($null -eq $ruleSid -or -not $trustedSidValues.ContainsKey($ruleSid.Value)) {
                $identityValue = if ($null -ne $ruleSid) {
                    $ruleSid.Value
                }
                else {
                    $rule.IdentityReference.Value
                }
                throw (
                    "$BoundaryName ACL grants write, modify, delete, permission-change, or " +
                    "ownership authority to an unapproved identity: $path; identity: " +
                    "$identityValue.")
            }
        }
    }
}

function Assert-TrustedGitPathAcl {
    param([pscustomobject]$Layout)

    Assert-TrustedFileSystemBoundary `
        $Layout.Root `
        'Git trust boundary' `
        20000
}

function Get-ApprovedGitExecutable {
    param(
        [string]$Path,
        [string]$ExpectedSha256,
        [string]$ExpectedSignerThumbprint)

    $layout = Get-GitForWindowsLayout $Path
    $gitPath = $layout.GitExe
    if (-not (Test-Path -LiteralPath $gitPath -PathType Leaf)) {
        throw "Git executable was not found at the approved local path: $gitPath"
    }

    $item = Get-Item -LiteralPath $gitPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [string]::Equals($item.Extension, '.exe', [StringComparison]::OrdinalIgnoreCase) -or
        $item.Length -le 0) {
        throw "Git must be a non-empty, non-reparse local executable: $gitPath"
    }

    foreach ($directoryPath in @(
        $layout.Root,
        $layout.Mingw64,
        $layout.Bin,
        $layout.GitExecPath)) {
        $directoryItem = Get-Item -LiteralPath $directoryPath -Force
        if (-not $directoryItem.PSIsContainer -or
            ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Git trust-boundary directory is missing, not a directory, or a reparse point: $directoryPath"
        }
    }
    Assert-TrustedGitPathAcl $layout

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($gitPath)
    if (-not [string]::Equals($versionInfo.ProductName, 'Git', [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $versionInfo.FileDescription,
            'Git for Windows',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $versionInfo.CompanyName,
            'The Git Development Community',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $versionInfo.OriginalFilename,
            'git.exe',
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $versionInfo.InternalName,
            'git',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The approved executable does not identify itself as Git for Windows: $gitPath"
    }

    $actualSha256 = Get-Sha256Hex $gitPath
    $expectedHash = $ExpectedSha256.ToUpperInvariant()
    if (-not [string]::Equals(
        $actualSha256,
        $expectedHash,
        [StringComparison]::Ordinal)) {
        throw "Git executable SHA-256 does not match the separately approved value: $gitPath"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $gitPath
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "Git executable does not have a valid Authenticode signature: $gitPath"
    }

    $actualSignerThumbprint = $signature.SignerCertificate.Thumbprint.
        Replace(' ', '').ToUpperInvariant()
    $expectedThumbprint = $ExpectedSignerThumbprint.ToUpperInvariant()
    if (-not [string]::Equals(
        $actualSignerThumbprint,
        $expectedThumbprint,
        [StringComparison]::Ordinal)) {
        throw "Git signer certificate does not match the separately approved thumbprint: $gitPath"
    }

    return [pscustomobject]@{
        Path = $gitPath
        Root = $layout.Root
        GitExecPath = $layout.GitExecPath
        Sha256 = $actualSha256
        SignerThumbprint = $actualSignerThumbprint
        SignerSubject = $signature.SignerCertificate.Subject
    }
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)

    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
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

function Assert-TinyHwBarExecutable {
    param([string]$Path, [string]$ExpectedVersion)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "TinyHwBar executable was not produced: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -lt 64) {
        throw 'Built TinyHwBar executable is empty or is a reparse point.'
    }

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName)
    foreach ($identity in @(
        [pscustomobject]@{ Actual = $versionInfo.ProductName; Expected = 'TinyHwBar' },
        [pscustomobject]@{ Actual = $versionInfo.InternalName; Expected = 'TinyHwBar.exe' },
        [pscustomobject]@{ Actual = $versionInfo.OriginalFilename; Expected = 'TinyHwBar.exe' })) {
        if (-not [string]::Equals(
            [string]$identity.Actual,
            [string]$identity.Expected,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Built executable identity does not match $($identity.Expected)."
        }
    }

    $expected = Get-CanonicalReleaseVersion $ExpectedVersion
    $productVersionText = [string]$versionInfo.ProductVersion
    $fileVersionText = [string]$versionInfo.FileVersion
    $productVersion = $null
    $fileVersion = $null
    if (-not [string]::Equals(
            $productVersionText,
            $ExpectedVersion,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $fileVersionText,
            $expected.ToString(4),
            [StringComparison]::Ordinal) -or
        -not [Version]::TryParse($productVersionText, [ref]$productVersion) -or
        -not [Version]::TryParse($fileVersionText, [ref]$fileVersion) -or
        $productVersion -ne [Version]::Parse($ExpectedVersion) -or
        $fileVersion -ne $expected) {
        throw "Built executable metadata does not match requested version $ExpectedVersion."
    }

    [void](Get-TinyHwBarCliAssemblyMetadata $item.FullName $expected)
}

function Assert-DirectoryAllowlist {
    param([string]$Directory, [string[]]$ExpectedNames)

    $items = @(Get-ChildItem -LiteralPath $Directory -Force)
    if ($items.Count -ne $ExpectedNames.Count) {
        throw "Release bundle contains an unexpected number of entries: $Directory"
    }
    foreach ($item in $items) {
        if ($item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $ExpectedNames -cnotcontains $item.Name -or
            $item.Length -le 0) {
            throw "Release bundle contains an unexpected, empty, or unsafe entry: $($item.FullName)"
        }
    }
}

function Assert-ZipAllowlist {
    param([string]$ZipPath, [string[]]$ExpectedNames)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @($archive.Entries)
        if ($entries.Count -ne $ExpectedNames.Count) {
            throw "Release ZIP contains $($entries.Count) entries; expected $($ExpectedNames.Count)."
        }
        $seen = @{}
        foreach ($entry in $entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name.Contains('/') -or
                $ExpectedNames -cnotcontains $name -or
                $seen.ContainsKey($name) -or
                $entry.Length -le 0) {
                throw "Release ZIP contains an unexpected, duplicate, nested, or empty entry: $name"
            }
            $seen[$name] = $true
        }
    }
    finally {
        $archive.Dispose()
    }
}

[void](Get-CanonicalReleaseVersion $Version)

$repoCandidate = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repoRoot = $repoCandidate
$approvedGit = Get-ApprovedGitExecutable `
    $GitExe `
    $ApprovedGitSha256 `
    $ApprovedGitSignerThumbprint
$git = $approvedGit.Path
$Commit = $Commit.ToLowerInvariant()
$shortCommit = $Commit.Substring(0, 7)

$TrustedWorkspaceRoot = Get-ValidatedLocalFixedPath `
    $TrustedWorkspaceRoot `
    'Trusted release workspace root'
if (-not (Test-Path -LiteralPath $TrustedWorkspaceRoot -PathType Container)) {
    throw "Trusted release workspace root must already exist as a directory: $TrustedWorkspaceRoot"
}
if (-not (Test-IsDescendantPath $TrustedWorkspaceRoot $repoRoot)) {
    throw (
        'The TinyHwBar source repository must be a strict descendant of the trusted release ' +
        "workspace root: $TrustedWorkspaceRoot")
}
$repoMetadataCandidate = Join-Path $repoRoot '.git'
if (-not (Test-Path -LiteralPath $repoMetadataCandidate -PathType Container)) {
    throw (
        'The trusted release source must use an in-tree .git directory; Git worktree files and ' +
        "external metadata directories are rejected: $repoMetadataCandidate")
}
$forbiddenGitMetadataRelativePaths = @(
    'commondir',
    'objects\info\alternates',
    'objects\info\http-alternates',
    'info\grafts',
    'info\attributes',
    'shallow')
foreach ($forbiddenGitMetadataRelativePath in $forbiddenGitMetadataRelativePaths) {
    $forbiddenGitMetadataPath = Join-Path `
        $repoMetadataCandidate `
        $forbiddenGitMetadataRelativePath
    if (Test-Path -LiteralPath $forbiddenGitMetadataPath) {
        throw (
            'Git common-directory, alternate-object, graft, metadata-attribute, and shallow ' +
            "overrides are not allowed for release preparation: $forbiddenGitMetadataPath")
    }
}
Assert-TrustedFileSystemBoundary `
    $TrustedWorkspaceRoot `
    'Trusted release workspace' `
    50000
Assert-SafeLocalGitConfiguration $repoMetadataCandidate

$temporaryBase = Get-ValidatedLocalFixedPath `
    (Join-Path $TrustedWorkspaceRoot 'work') `
    'Trusted release work directory'
$allowedStagingRoot = Get-ValidatedLocalFixedPath `
    (Join-Path $TrustedWorkspaceRoot 'staging') `
    'Trusted release staging directory'
foreach ($reservedRoot in @($temporaryBase, $allowedStagingRoot)) {
    if ((Test-PathEquals $reservedRoot $repoRoot) -or
        (Test-IsDescendantPath $repoRoot $reservedRoot) -or
        (Test-IsDescendantPath $reservedRoot $repoRoot)) {
        throw (
            'The source repository and trusted release work/staging directories must be ' +
            "non-overlapping siblings beneath $TrustedWorkspaceRoot.")
    }
}
Assert-SafeDirectoryOrMissing $temporaryBase 'Trusted release work directory'
Assert-SafeDirectoryOrMissing $allowedStagingRoot 'Trusted release staging directory'

if ([string]::IsNullOrWhiteSpace($StagingRoot)) {
    $StagingRoot = Join-Path $allowedStagingRoot ("v$Version-$shortCommit")
}
$StagingRoot = Get-ValidatedStagingPath `
    $StagingRoot `
    $repoRoot `
    $allowedStagingRoot `
    $null
if (Test-Path -LiteralPath $StagingRoot) {
    throw "Refusing to reuse an existing release staging path: $StagingRoot"
}

$temporaryRoot = Get-ValidatedLocalFixedPath `
    (Join-Path $temporaryBase ('TinyHwBar.Release.' + [Guid]::NewGuid().ToString('N'))) `
    'Trusted temporary release workspace'
$processTempRoot = Join-Path $temporaryRoot 'process-temp'
$emptyGitConfigPath = Join-Path $processTempRoot 'empty.gitconfig'
$emptyGitAttributesPath = Join-Path $processTempRoot 'empty.gitattributes'

Write-Warning (
    "Approved Git executable: $git; SHA-256: $($approvedGit.Sha256); " +
    "signer: $($approvedGit.SignerSubject); signer thumbprint: " +
    "$($approvedGit.SignerThumbprint). Trusted workspace: $TrustedWorkspaceRoot. This operation " +
    'keeps source, build, process TEMP/TMP/TMPDIR, and staging beneath that validated root, then executes ' +
    'test.cmd and build.cmd from ' +
    "frozen commit $Commit with the current user permissions. Continue only after separately " +
    'reviewing the Git identity, hash, signer, workspace boundary, and frozen scripts.')
if (-not $ApproveFrozenCommitScripts -and -not $WhatIfPreference) {
    throw (
        "Frozen-commit script execution was not approved. Review test.cmd and build.cmd at $Commit, " +
        'then rerun with -ApproveFrozenCommitScripts and respond to the high-impact confirmation.')
}
if (-not $PSCmdlet.ShouldProcess(
    $StagingRoot,
    "Use trusted workspace $TrustedWorkspaceRoot, execute approved Git $git and test.cmd/build.cmd from reviewed commit $Commit, then build TinyHwBar $Version assets")) {
    return
}

$approvedGitLock = $null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousTmpDirExists = Test-Path -LiteralPath 'Env:TMPDIR'
$previousTmpDir = if ($previousTmpDirExists) { $env:TMPDIR } else { $null }
$gitEnvironmentSnapshot = Get-GitProcessEnvironmentSnapshot
$processTempRedirected = $false
$gitEnvironmentHardened = $false
$releaseFailure = $null
try {
    if (-not (Test-Path -LiteralPath $temporaryBase)) {
        New-SafeDirectoryNoClobber $temporaryBase 'Trusted release work directory'
    }
    else {
        Assert-SafeDirectoryOrMissing $temporaryBase 'Trusted release work directory'
    }
    New-SafeDirectoryNoClobber $temporaryRoot 'Trusted temporary release workspace'
    New-SafeDirectoryNoClobber $processTempRoot 'Trusted child-process TEMP/TMP/TMPDIR directory'
    Assert-TrustedFileSystemBoundary `
        $TrustedWorkspaceRoot `
        'Trusted release workspace before empty Git policy files' `
        50000
    Write-Utf8NoBom $emptyGitConfigPath ''
    Write-Utf8NoBom $emptyGitAttributesPath ''
    Assert-TrustedFileSystemBoundary `
        $TrustedWorkspaceRoot `
        'Trusted release workspace and empty Git policy files before first Git execution' `
        50000
    $processTempRedirected = $true
    $env:TEMP = $processTempRoot
    $env:TMP = $processTempRoot
    $env:TMPDIR = $processTempRoot
    $gitEnvironmentHardened = $true
    Clear-GitProcessEnvironment
    $env:GIT_NO_REPLACE_OBJECTS = '1'
    $env:GIT_NO_LAZY_FETCH = '1'
    $env:GIT_OPTIONAL_LOCKS = '0'
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GIT_CONFIG_SYSTEM = $emptyGitConfigPath
    $env:GIT_CONFIG_GLOBAL = $emptyGitConfigPath
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:GIT_CONFIG_COUNT = '0'
    $env:GIT_ATTR_SYSTEM = $emptyGitAttributesPath
    $env:GIT_ATTR_GLOBAL = $emptyGitAttributesPath
    $env:GIT_ATTR_NOSYSTEM = '1'
    $env:GIT_EXEC_PATH = $approvedGit.GitExecPath
    $trustedGitSafeDirectoryConfig = "safe.directory=$repoCandidate"
    $trustedGitAttributesConfig = "core.attributesFile=$emptyGitAttributesPath"

    $approvedGitLock = [IO.File]::Open(
        $git,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $approvedGit = Get-ApprovedGitExecutable `
        $git `
        $ApprovedGitSha256 `
        $ApprovedGitSignerThumbprint
    Assert-TrustedFileSystemBoundary `
        $TrustedWorkspaceRoot `
        'Trusted release workspace' `
        50000
    Assert-SafeLocalGitConfiguration $repoMetadataCandidate

    $repoRootOutput = & $git --no-replace-objects --no-lazy-fetch --no-optional-locks --no-pager -c $trustedGitSafeDirectoryConfig -c $trustedGitAttributesConfig -C $repoCandidate rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or @($repoRootOutput).Count -ne 1) {
        throw 'Could not resolve the TinyHwBar Git repository root.'
    }
    $repoRootLine = ([string](@($repoRootOutput)[0])).Trim()
    $repoRoot = [IO.Path]::GetFullPath($repoRootLine)
    if (-not (Test-PathEquals $repoRoot $repoCandidate)) {
        throw "Prepare-Release.ps1 must run from the TinyHwBar repository tools directory: $repoCandidate"
    }

    $gitDirectoryOutput = & $git --no-replace-objects --no-lazy-fetch --no-optional-locks --no-pager -c $trustedGitSafeDirectoryConfig -c $trustedGitAttributesConfig -C $repoRoot rev-parse --absolute-git-dir
    if ($LASTEXITCODE -ne 0 -or @($gitDirectoryOutput).Count -ne 1) {
        throw 'Could not resolve the TinyHwBar Git metadata directory.'
    }
    $gitDirectoryLine = ([string](@($gitDirectoryOutput)[0])).Trim()
    if ([string]::IsNullOrWhiteSpace($gitDirectoryLine) -or
        -not [IO.Path]::IsPathRooted($gitDirectoryLine)) {
        throw 'Git returned an invalid metadata directory.'
    }
    $gitDirectory = [IO.Path]::GetFullPath($gitDirectoryLine)
    if (-not (Test-PathEquals $gitDirectory $repoMetadataCandidate)) {
        throw (
            'The trusted release source must keep its Git metadata in the validated in-tree ' +
            ".git directory: $repoMetadataCandidate")
    }
    $gitCommonDirectoryOutput = & $git --no-replace-objects --no-lazy-fetch --no-optional-locks --no-pager -c $trustedGitSafeDirectoryConfig -c $trustedGitAttributesConfig -C $repoRoot rev-parse --git-common-dir
    if ($LASTEXITCODE -ne 0 -or @($gitCommonDirectoryOutput).Count -ne 1) {
        throw 'Could not resolve the TinyHwBar Git common metadata directory.'
    }
    $gitCommonDirectoryLine = ([string](@($gitCommonDirectoryOutput)[0])).Trim()
    if ([string]::IsNullOrWhiteSpace($gitCommonDirectoryLine)) {
        throw 'Git returned an invalid common metadata directory.'
    }
    $gitCommonDirectoryCandidate = if ([IO.Path]::IsPathRooted($gitCommonDirectoryLine)) {
        $gitCommonDirectoryLine
    }
    else {
        Join-Path $repoRoot $gitCommonDirectoryLine
    }
    $gitCommonDirectory = [IO.Path]::GetFullPath($gitCommonDirectoryCandidate)
    if (-not (Test-PathEquals $gitCommonDirectory $repoMetadataCandidate)) {
        throw (
            'The trusted release source must not redirect its Git common metadata directory: ' +
            $gitCommonDirectory)
    }
    $gitObjectsOutput = & $git --no-replace-objects --no-lazy-fetch --no-optional-locks --no-pager -c $trustedGitSafeDirectoryConfig -c $trustedGitAttributesConfig -C $repoRoot rev-parse --git-path objects
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not resolve the TinyHwBar Git object directory.'
    }
    [void](Get-ValidatedGitObjectsDirectory `
        @($gitObjectsOutput) `
        $repoRoot `
        $repoMetadataCandidate)
    $StagingRoot = Get-ValidatedStagingPath `
        $StagingRoot `
        $repoRoot `
        $allowedStagingRoot `
        $gitDirectory
    if (Test-Path -LiteralPath $StagingRoot) {
        throw "Refusing to reuse an existing release staging path: $StagingRoot"
    }

    & $git --no-replace-objects --no-lazy-fetch --no-optional-locks --no-pager -c $trustedGitSafeDirectoryConfig -c $trustedGitAttributesConfig -C $repoRoot fsck --full --strict --no-dangling
    if ($LASTEXITCODE -ne 0) {
        throw "Strict Git object database verification failed with exit code $LASTEXITCODE."
    }

    $resolvedCommitOutput = & $git --no-replace-objects --no-lazy-fetch --no-optional-locks --no-pager -c $trustedGitSafeDirectoryConfig -c $trustedGitAttributesConfig -C $repoRoot rev-parse --verify ($Commit + '^{commit}')
    if ($LASTEXITCODE -ne 0 -or @($resolvedCommitOutput).Count -ne 1) {
        throw 'Commit must be the exact full ID of an existing Git commit.'
    }
    $resolvedCommit = ([string](@($resolvedCommitOutput)[0])).Trim()
    if (-not [string]::Equals(
        $resolvedCommit,
        $Commit,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Commit must be the exact full ID of an existing Git commit.'
    }

$sourceArchive = Join-Path $temporaryRoot 'source.zip'
$sourceRoot = Join-Path $temporaryRoot 'source'
$readbackRoot = Join-Path $temporaryRoot 'readback'
$bundleRoot = Join-Path $StagingRoot 'bundle'
$assetsRoot = Join-Path $StagingRoot 'assets'
$assetName = "TinyHwBar-v$Version-win-x64.zip"
$checksumName = 'SHA256SUMS.txt'
$zipPath = Join-Path $assetsRoot $assetName
$checksumPath = Join-Path $assetsRoot $checksumName
$provenancePath = Join-Path $StagingRoot 'PREPARATION.txt'

try {
    if (-not (Test-Path -LiteralPath $allowedStagingRoot)) {
        New-SafeDirectoryNoClobber $allowedStagingRoot 'Trusted release staging directory'
    }
    else {
        Assert-SafeDirectoryOrMissing $allowedStagingRoot 'Trusted release staging directory'
    }

    New-SafeDirectoryNoClobber $sourceRoot 'Trusted temporary source directory'
    New-SafeDirectoryNoClobber $readbackRoot 'Trusted temporary ZIP readback directory'
    New-SafeDirectoryNoClobber $StagingRoot 'Release staging directory'
    New-SafeDirectoryNoClobber $bundleRoot 'Release bundle directory'
    New-SafeDirectoryNoClobber $assetsRoot 'Release assets directory'
    Assert-TrustedFileSystemBoundary `
        $TrustedWorkspaceRoot `
        'Trusted release workspace after directory creation' `
        50000
    [void](Get-ValidatedStagingPath `
        $StagingRoot `
        $repoRoot `
        $allowedStagingRoot `
        $gitDirectory)

    Invoke-ExternalCommand `
        $git `
        @(
            '--no-replace-objects',
            '--no-lazy-fetch',
            '--no-optional-locks',
            '--no-pager',
            '-c',
            $trustedGitSafeDirectoryConfig,
            '-c',
            $trustedGitAttributesConfig,
            '-C',
            $repoRoot,
            'archive',
            '--no-worktree-attributes',
            '--format=zip',
            "--output=$sourceArchive",
            $Commit) `
        'Git source archive creation'
    Assert-TrustedFileSystemBoundary `
        $sourceArchive `
        'Trusted Git source archive' `
        1
    Expand-Archive -LiteralPath $sourceArchive -DestinationPath $sourceRoot
    Assert-TrustedFileSystemBoundary `
        $sourceRoot `
        'Trusted extracted source tree' `
        50000

    $isolatedTest = Join-Path $sourceRoot 'test.cmd'
    $isolatedBuild = Join-Path $sourceRoot 'build.cmd'
    $isolatedTemplate = Join-Path $sourceRoot $templateRelativePath
    foreach ($reviewedCommitFile in @($isolatedTest, $isolatedBuild, $isolatedTemplate)) {
        if (-not (Test-Path -LiteralPath $reviewedCommitFile -PathType Leaf)) {
            throw "The selected commit does not contain a required release input: $reviewedCommitFile"
        }
        $reviewedCommitItem = Get-Item -LiteralPath $reviewedCommitFile -Force
        if (($reviewedCommitItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The selected commit release input is a reparse point: $reviewedCommitFile"
        }
    }
    Invoke-ExternalCommand $isolatedTest @('--compile-only') 'Isolated compile-only test'
    Invoke-ExternalCommand $isolatedBuild @() 'Isolated application build'
    Assert-TrustedFileSystemBoundary `
        $TrustedWorkspaceRoot `
        'Trusted release workspace after frozen build' `
        50000

    $builtExe = Join-Path $sourceRoot 'outputs\TinyHwBar.exe'
    Assert-TinyHwBarExecutable $builtExe $Version

    [IO.File]::Copy($builtExe, (Join-Path $bundleRoot 'TinyHwBar.exe'), $false)
    [IO.File]::Copy((Join-Path $sourceRoot 'LICENSE'), (Join-Path $bundleRoot 'LICENSE'), $false)

    $readme = [IO.File]::ReadAllText($isolatedTemplate)
    $readme = $readme.Replace('{{VERSION}}', $Version).
        Replace('{{COMMIT}}', $Commit).
        Replace('{{ASSET_NAME}}', $assetName).
        Replace('{{SHA256_FILE}}', $checksumName).
        Replace('{{REPOSITORY_URL}}', $repositoryUrl)
    if ($readme.Contains('{{')) {
        throw 'Binary README contains an unresolved template placeholder.'
    }
    Assert-BinaryReadmeLinks $readme $bundleAllowlist
    Write-Utf8NoBom (Join-Path $bundleRoot 'README.md') $readme

    Assert-DirectoryAllowlist $bundleRoot $bundleAllowlist
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $bundleRoot,
        $zipPath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    $initialZipHash = Get-Sha256Hex $zipPath
    Assert-ZipAllowlist $zipPath $bundleAllowlist
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $readbackRoot)
    Assert-DirectoryAllowlist $readbackRoot $bundleAllowlist
    foreach ($name in $bundleAllowlist) {
        $bundleHash = Get-Sha256Hex (Join-Path $bundleRoot $name)
        $readbackHash = Get-Sha256Hex (Join-Path $readbackRoot $name)
        if (-not [string]::Equals($bundleHash, $readbackHash, [StringComparison]::Ordinal)) {
            throw "ZIP readback hash mismatch for $name."
        }
    }
    Assert-TinyHwBarExecutable (Join-Path $readbackRoot 'TinyHwBar.exe') $Version
    Assert-BinaryReadmeLinks `
        ([IO.File]::ReadAllText((Join-Path $readbackRoot 'README.md'))) `
        $bundleAllowlist

    $finalZipHash = Get-Sha256Hex $zipPath
    if (-not [string]::Equals(
        $initialZipHash,
        $finalZipHash,
        [StringComparison]::Ordinal)) {
        throw 'Release ZIP changed during validation.'
    }
    Write-Utf8NoBom $checksumPath ($finalZipHash + '  ' + $assetName + "`n")
    Assert-DirectoryAllowlist $assetsRoot @($assetName, $checksumName)

    $checksumLine = [IO.File]::ReadAllText($checksumPath).Trim()
    if (-not [string]::Equals(
        $checksumLine,
        ($finalZipHash + '  ' + $assetName),
        [StringComparison]::Ordinal)) {
        throw 'New SHA256SUMS.txt did not pass its own readback check.'
    }
    $publishedZipHash = Get-Sha256Hex $zipPath
    if (-not [string]::Equals(
        $publishedZipHash,
        $finalZipHash,
        [StringComparison]::Ordinal)) {
        throw 'Release ZIP changed after checksum generation.'
    }

    Assert-TrustedFileSystemBoundary `
        $StagingRoot `
        'Trusted completed release staging tree' `
        50000

    $provenance = @(
        'TinyHwBar local release preparation',
        "Version: $Version",
        "Commit: $Commit",
        "Git executable: $($approvedGit.Path)",
        "Git SHA256: $($approvedGit.Sha256)",
        "Git signer thumbprint: $($approvedGit.SignerThumbprint)",
        'Git invocation isolation: replace objects, lazy fetch, optional locks, paging, prompts, inherited GIT_* overrides, and external trace/test hooks disabled',
        'Git configuration boundary: empty system/global config and attributes; no redirected common directory, alternates, grafts, metadata attributes, or shallow repository',
        "Trusted workspace root: $TrustedWorkspaceRoot",
        "Asset: assets\$assetName",
        "SHA256: $publishedZipHash",
        'Validation: PowerShell script assertions + C# test compile only + isolated build + ZIP readback',
        'Execution trust boundary: source, build, process TEMP/TMP/TMPDIR, and staging stayed beneath the validated trusted workspace root',
        'Frozen-script boundary: frozen-commit test.cmd and build.cmd ran with current-user permissions',
        'Publication: this wrapper makes no direct Git write, upload, Release, or deployment request') -join "`r`n"
    Write-Utf8NoBom $provenancePath ($provenance + "`r`n")
    Assert-TrustedFileSystemBoundary `
        $StagingRoot `
        'Trusted completed release staging tree with provenance' `
        50000

    Write-Host "Prepared and verified local release staging: $StagingRoot"
    Write-Host "Asset: $zipPath"
    Write-Host "Checksum: $checksumPath"
    Write-Warning (
        'Prepare-Release.ps1 itself made no direct Git ref/index/remote write, upload, Release, or ' +
        'deployment request. The reviewed frozen-commit scripts ran with current-user permissions.')
}
catch {
    $releaseFailure = $_.Exception
    throw ("Release preparation failed. The incomplete staging path was preserved for inspection: " +
        "$StagingRoot. " + $releaseFailure.Message)
}
}
catch {
    if ($null -eq $releaseFailure) {
        $releaseFailure = $_.Exception
    }
    throw
}
finally {
    $finalizationFailures = New-Object Collections.Generic.List[object]

    try {
        if ($gitEnvironmentHardened) {
            Restore-GitProcessEnvironment $gitEnvironmentSnapshot
            $gitEnvironmentHardened = $false
        }
    }
    catch {
        $finalizationFailures.Add([pscustomobject]@{
            Stage = 'Git environment restoration'
            Exception = $_.Exception
        })
    }

    try {
        if ($processTempRedirected) {
            $env:TEMP = $previousTemp
            $env:TMP = $previousTmp
            if ($previousTmpDirExists) {
                $env:TMPDIR = $previousTmpDir
            }
            elseif (Test-Path -LiteralPath 'Env:TMPDIR') {
                Remove-Item -LiteralPath 'Env:TMPDIR' -ErrorAction Stop
            }
            $processTempRedirected = $false
        }
    }
    catch {
        $finalizationFailures.Add([pscustomobject]@{
            Stage = 'Process TEMP/TMP/TMPDIR restoration'
            Exception = $_.Exception
        })
    }

    try {
        if ($null -ne $approvedGitLock) {
            $approvedGitLock.Dispose()
        }
    }
    catch {
        $finalizationFailures.Add([pscustomobject]@{
            Stage = 'Approved Git lock disposal'
            Exception = $_.Exception
        })
    }

    try {
        if (Test-Path -LiteralPath $temporaryRoot) {
            if (-not (Test-IsDescendantPath $temporaryBase $temporaryRoot) -or
                -not (Test-IsDescendantPath $TrustedWorkspaceRoot $temporaryRoot)) {
                throw 'Temporary release workspace escaped the validated trusted work directory.'
            }
            Assert-TrustedFileSystemBoundary `
                $temporaryRoot `
                'Trusted temporary release workspace before cleanup' `
                50000
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction Stop
            if (Test-Path -LiteralPath $temporaryRoot) {
                throw 'Temporary release workspace still exists after cleanup.'
            }
        }
    }
    catch {
        $finalizationFailures.Add([pscustomobject]@{
            Stage = 'Temporary release workspace cleanup'
            Exception = $_.Exception
        })
    }

    if ($finalizationFailures.Count -gt 0) {
        $finalizationMessages = @(
            foreach ($finalizationFailure in $finalizationFailures) {
                "$($finalizationFailure.Stage): $($finalizationFailure.Exception.Message)"
            })
        $finalizationMessage = (
            'Release finalization encountered one or more failures: ' +
            ($finalizationMessages -join ' | '))
        if ($null -ne $releaseFailure) {
            Write-Warning $finalizationMessage -WarningAction Continue
        }
        else {
            throw $finalizationMessage
        }
    }
}

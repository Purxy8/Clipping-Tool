[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$releaseScript = Join-Path $PSScriptRoot "release.ps1"
$version = "0.0.0-ci-smoke.$PID"
$updateUrl = "https://github.com/Purxy8/Clipping-Tool"
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$workDirectory = Join-Path $artifactsRoot "release-work\$version"
$publishDirectory = Join-Path $workDirectory "publish"
$manifestPath = Join-Path $workDirectory "prepared-release.json"
$outputDirectory = Join-Path $artifactsRoot "release-preparation-smoke\$version"

function Get-PayloadEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($RootPath)
    $rootPrefix = $resolvedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    @(
        Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse |
            ForEach-Object {
                $fullPath = [System.IO.Path]::GetFullPath($_.FullName)
                [pscustomobject]@{
                    Path = $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
                    Length = [long]$_.Length
                    Sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object -Property Path
    )
}

function Assert-PayloadMatchesManifest {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$ExpectedFiles
    )

    $actualFiles = @(Get-PayloadEntries -RootPath $publishDirectory)
    if ($ExpectedFiles.Count -ne $actualFiles.Count) {
        throw "The restored payload contains $($actualFiles.Count) files; expected $($ExpectedFiles.Count)."
    }

    for ($index = 0; $index -lt $actualFiles.Count; $index++) {
        $expected = $ExpectedFiles[$index]
        $actual = $actualFiles[$index]
        if (-not [string]::Equals(
                [string]$expected.Path,
                [string]$actual.Path,
                [System.StringComparison]::Ordinal) -or
            [long]$expected.Length -ne [long]$actual.Length -or
            -not [string]::Equals(
                [string]$expected.Sha256,
                [string]$actual.Sha256,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The restored payload does not match the prepared manifest at index $index."
        }
    }
}

function Assert-PackOnlyRejected {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CaseName,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Mutate,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Restore,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage
    )

    $outputLines = @()
    $exitCode = $null
    try {
        & $Mutate

        # LICENSE is deliberately not a .NET assembly. If an integrity guard regresses,
        # dotnet will reject this sentinel instead of ever starting Velopack.
        $packArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $releaseScript,
            "-Version", $version,
            "-UpdateUrl", $updateUrl,
            "-OutputDirectory", $outputDirectory,
            "-VpkDllPath", (Join-Path $repositoryRoot "LICENSE"),
            "-NoRestore",
            "-PackOnly"
        )

        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $outputLines = @(& powershell @packArguments 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        $output = ($outputLines | Out-String)
        if ($exitCode -eq 0) {
            throw "$CaseName tampering unexpectedly passed PackOnly verification."
        }
        if ($output -notmatch [regex]::Escape($ExpectedMessage)) {
            throw "$CaseName tampering failed for an unexpected reason.`n$output"
        }

        Write-Host "Verified PackOnly rejects $CaseName tampering before packaging."
    }
    finally {
        & $Restore
    }
}

$prepareArguments = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", $releaseScript,
    "-Version", $version,
    "-UpdateUrl", $updateUrl,
    "-OutputDirectory", $outputDirectory,
    "-NoRestore",
    "-PrepareOnly"
)

& powershell @prepareArguments
if ($LASTEXITCODE -ne 0) {
    throw "Release preparation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Release preparation did not create $manifestPath."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$expectedFiles = @($manifest.Files)
if ($expectedFiles.Count -eq 0) {
    throw "The prepared release manifest is empty."
}

$manifestPaths = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in $expectedFiles) {
    [void]$manifestPaths.Add(([string]$file.Path).Replace('\', '/'))
}

$requiredFiles = @(
    "LICENSE",
    "SOURCE.md",
    "PRIVACY.md",
    "CODE_SIGNING_POLICY.md",
    "ASSET_PROVENANCE.md",
    "THIRD_PARTY_NOTICES.md"
)
foreach ($requiredFile in $requiredFiles) {
    if (-not $manifestPaths.Contains($requiredFile)) {
        throw "The prepared release manifest does not include required file '$requiredFile'."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
        throw "The prepared release payload does not include required file '$requiredFile'."
    }
}

Assert-PayloadMatchesManifest -ExpectedFiles $expectedFiles
Write-Host "Verified prepared manifest with $($expectedFiles.Count) files and all required notices."

$modifiedPath = Join-Path $publishDirectory "THIRD_PARTY_NOTICES.md"
$modifiedBytes = [System.IO.File]::ReadAllBytes($modifiedPath)
Assert-PackOnlyRejected `
    -CaseName "modified-file" `
    -ExpectedMessage "The prepared release payload changed at 'THIRD_PARTY_NOTICES.md' after verification." `
    -Mutate {
        [System.IO.File]::AppendAllText(
            $modifiedPath,
            "`nClipForge release integrity smoke-test mutation.",
            [System.Text.UTF8Encoding]::new($false))
    } `
    -Restore {
        [System.IO.File]::WriteAllBytes($modifiedPath, $modifiedBytes)
    }

$extraPayloadPath = Join-Path $publishDirectory "_clipforge-release-integrity-probe.txt"
$extraStagingPath = Join-Path $workDirectory "extra-file-probe-$PID.txt"
[System.IO.File]::WriteAllText(
    $extraStagingPath,
    "ClipForge release integrity smoke-test probe.",
    [System.Text.UTF8Encoding]::new($false))
Assert-PackOnlyRejected `
    -CaseName "extra-file" `
    -ExpectedMessage "The prepared release file set changed after verification." `
    -Mutate {
        if (Test-Path -LiteralPath $extraPayloadPath) {
            throw "Refusing to overwrite unexpected probe file $extraPayloadPath."
        }
        [System.IO.File]::Move($extraStagingPath, $extraPayloadPath)
    } `
    -Restore {
        if (Test-Path -LiteralPath $extraPayloadPath -PathType Leaf) {
            [System.IO.File]::Move($extraPayloadPath, $extraStagingPath)
        }
    }

$missingPath = Join-Path $publishDirectory "ASSET_PROVENANCE.md"
$missingBackupPath = Join-Path $workDirectory "missing-file-probe-$PID.md"
Assert-PackOnlyRejected `
    -CaseName "missing-file" `
    -ExpectedMessage "The prepared release file set changed after verification." `
    -Mutate {
        if (Test-Path -LiteralPath $missingBackupPath) {
            throw "Refusing to overwrite unexpected backup file $missingBackupPath."
        }
        [System.IO.File]::Move($missingPath, $missingBackupPath)
    } `
    -Restore {
        if (Test-Path -LiteralPath $missingBackupPath -PathType Leaf) {
            if (Test-Path -LiteralPath $missingPath) {
                throw "Refusing to overwrite $missingPath while restoring the smoke test."
            }
            [System.IO.File]::Move($missingBackupPath, $missingPath)
        }
    }

Assert-PayloadMatchesManifest -ExpectedFiles $expectedFiles
Write-Host "Release preparation and tamper smoke tests passed."

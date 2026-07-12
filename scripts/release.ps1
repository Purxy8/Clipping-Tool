[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$UpdateUrl,

    [string]$OutputDirectory,

    [string]$ReleaseNotes,

    [string]$SignParams,

    [string]$AzureTrustedSignFile,

    [string]$VpkDllPath,

    [switch]$NoRestore,

    [switch]$PrepareOnly,

    [switch]$PackOnly
)

$ErrorActionPreference = "Stop"
$packId = "ClipForge.Desktop"
$channel = "stable"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repositoryRoot "src\ClipForge\ClipForge.csproj"
$iconPath = Join-Path $repositoryRoot "assets\ClipForge.ico"
$splashPath = Join-Path $repositoryRoot "assets\InstallSplash.png"
$artifactsRoot = Join-Path $repositoryRoot "artifacts"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot "Releases"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$publishDirectory = Join-Path $artifactsRoot "release-work\$Version\publish"
$preparedManifestPath = Join-Path (Split-Path $publishDirectory -Parent) "prepared-release.json"
$releasePackage = Join-Path $OutputDirectory "$packId-$Version-$channel-full.nupkg"

function Get-PublishTreeManifestEntries {
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
                if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "Publish file escaped the prepared payload root: $fullPath"
                }
                $relativePath = $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
                [pscustomobject][ordered]@{
                    Path = $relativePath
                    Length = [long]$_.Length
                    Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object -Property Path
    )
}

if ($PrepareOnly -and $PackOnly) {
    throw "Choose either PrepareOnly or PackOnly, not both."
}

if ($PrepareOnly -and
    (-not [string]::IsNullOrWhiteSpace($SignParams) -or
     -not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile))) {
    throw "PrepareOnly never uses signing credentials. Provide signing only to the packaging stage."
}

if (-not $PrepareOnly -and (Test-Path -LiteralPath $releasePackage)) {
    throw "Release $Version already exists at $releasePackage. Increment the version instead of overwriting a release."
}

if (-not [string]::IsNullOrWhiteSpace($SignParams) -and
    -not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
    throw "Choose either SignParams or AzureTrustedSignFile, not both."
}

if (-not $PrepareOnly -and -not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes = (Resolve-Path $ReleaseNotes).Path
}

if (-not $PrepareOnly -and -not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
    $AzureTrustedSignFile = (Resolve-Path $AzureTrustedSignFile).Path
}

if (-not $PrepareOnly -and -not [string]::IsNullOrWhiteSpace($VpkDllPath)) {
    $VpkDllPath = (Resolve-Path $VpkDllPath).Path
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory((Split-Path $publishDirectory -Parent)) | Out-Null

if (-not $PackOnly) {
    if (Test-Path -LiteralPath $publishDirectory) {
        $fullPublishPath = [System.IO.Path]::GetFullPath($publishDirectory)
        $allowedRoot = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd('\') + '\'
        if (-not $fullPublishPath.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean publish directory outside artifacts: $fullPublishPath"
        }
        Remove-Item -LiteralPath $fullPublishPath -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($publishDirectory) | Out-Null

    $buildArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "build.ps1"), "-Configuration", "Release")
    if ($NoRestore) {
        $buildArguments += "-NoRestore"
    }
    & powershell @buildArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $numericParts = $Version.Split('-', 2)[0].Split('+', 2)[0].Split('.')
    $fileVersion = "$($numericParts[0]).$($numericParts[1]).$($numericParts[2]).0"
    $publishArguments = @(
        "publish", $projectPath,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $publishDirectory,
        "--no-restore",
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$fileVersion",
        "-p:FileVersion=$fileVersion",
        "-p:InformationalVersion=$Version",
        "-p:ClipForgeUpdateUrl=$UpdateUrl"
    )

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $dotnetNoticesPath = Join-Path (Split-Path $dotnetCommand.Source -Parent) "ThirdPartyNotices.txt"
    if (Test-Path -LiteralPath $dotnetNoticesPath) {
        Copy-Item -LiteralPath $dotnetNoticesPath `
            -Destination (Join-Path $publishDirectory "DOTNET_THIRD_PARTY_NOTICES.txt") `
            -Force
    }
    else {
        Write-Warning "The .NET SDK third-party notice file was not found beside dotnet.exe."
    }

    $preparedFiles = @(Get-PublishTreeManifestEntries -RootPath $publishDirectory)
    if ($preparedFiles.Count -eq 0) {
        throw "The prepared release payload is empty."
    }
    $preparedManifest = [ordered]@{
        FormatVersion = 1
        Version = $Version
        UpdateUrl = [string]$UpdateUrl
        Files = $preparedFiles
    }
    [System.IO.File]::WriteAllText(
        $preparedManifestPath,
        ($preparedManifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
}
else {
    $preparedExecutable = Join-Path $publishDirectory "ClipForge.exe"
    $preparedLibrary = Join-Path $publishDirectory "ClipForge.dll"
    foreach ($requiredPath in @($preparedExecutable, $preparedLibrary, $preparedManifestPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "PackOnly requires a complete prepared release payload at $publishDirectory. Run PrepareOnly first."
        }
    }

    $preparedManifest = Get-Content -LiteralPath $preparedManifestPath -Raw | ConvertFrom-Json
    if ([int]$preparedManifest.FormatVersion -ne 1 -or
        -not [string]::Equals(
            [string]$preparedManifest.Version,
            $Version,
            [System.StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$preparedManifest.UpdateUrl,
            [string]$UpdateUrl,
            [System.StringComparison]::Ordinal)) {
        throw "The prepared release identity does not match Version $Version and UpdateUrl $UpdateUrl. Run PrepareOnly again."
    }

    $expectedFiles = @($preparedManifest.Files)
    $actualFiles = @(Get-PublishTreeManifestEntries -RootPath $publishDirectory)
    if ($expectedFiles.Count -eq 0 -or $expectedFiles.Count -ne $actualFiles.Count) {
        throw "The prepared release file set changed after verification. Run PrepareOnly again."
    }

    for ($index = 0; $index -lt $actualFiles.Count; $index++) {
        $expected = $expectedFiles[$index]
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
            throw "The prepared release payload changed at '$($actual.Path)' after verification. Run PrepareOnly again."
        }
    }
}

if ($PrepareOnly) {
    Write-Host ""
    Write-Host "ClipForge $Version payload prepared without activating signing credentials: $publishDirectory"
    return
}

$packArguments = @(
    "pack",
    "--packId", $packId,
    "--packVersion", $Version,
    "--packDir", $publishDirectory,
    "--mainExe", "ClipForge.exe",
    "--packTitle", "ClipForge",
    "--packAuthors", "Purxy8",
    "--runtime", "win-x64",
    "--channel", $channel,
    "--icon", $iconPath,
    "--splashImage", $splashPath,
    "--splashProgressColor", "#7C6CF2",
    "--shortcuts", "Desktop,StartMenuRoot",
    "--outputDir", $OutputDirectory
)
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $packArguments += @("--releaseNotes", $ReleaseNotes)
}
if (-not [string]::IsNullOrWhiteSpace($SignParams)) {
    $packArguments += @("--signParams", $SignParams)
}
if (-not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
    $packArguments += @("--azureTrustedSignFile", $AzureTrustedSignFile)
}

if ([string]::IsNullOrWhiteSpace($VpkDllPath)) {
    & dotnet tool run vpk @packArguments
}
else {
    & dotnet $VpkDllPath @packArguments
}
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$setupPath = Join-Path $OutputDirectory "$packId-$channel-Setup.exe"
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Velopack did not produce the expected installer: $setupPath"
}

$publicSetupPath = Join-Path $OutputDirectory "ClipForge-Setup.exe"
Copy-Item -LiteralPath $setupPath -Destination $publicSetupPath -Force

$feedPath = Join-Path $OutputDirectory "releases.$channel.json"
if (-not (Test-Path -LiteralPath $feedPath)) {
    throw "Velopack did not produce the expected update feed: $feedPath"
}

$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
$feedAsset = @($feed.Assets) | Where-Object {
    $_.PackageId -eq $packId -and
    $_.Version -eq $Version -and
    $_.FileName -eq (Split-Path $releasePackage -Leaf)
} | Select-Object -First 1
if ($null -eq $feedAsset) {
    throw "The update feed does not contain the expected ClipForge $Version package."
}

$packageHash = (Get-FileHash -LiteralPath $releasePackage -Algorithm SHA256).Hash
if (-not $packageHash.Equals([string]$feedAsset.SHA256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The update feed checksum does not match the generated release package."
}

$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
$publicSetupHash = (Get-FileHash -LiteralPath $publicSetupPath -Algorithm SHA256).Hash
if (-not $setupHash.Equals($publicSetupHash, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The public installer copy does not match the Velopack installer."
}

$signature = Get-AuthenticodeSignature -LiteralPath $publicSetupPath
if ((-not [string]::IsNullOrWhiteSpace($SignParams) -or
     -not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) -and
    $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "The installer was configured for signing, but its Authenticode signature is $($signature.Status)."
}

$checksumPath = Join-Path $OutputDirectory "SHA256SUMS.txt"
$checksumLines = Get-ChildItem -LiteralPath $OutputDirectory -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
[System.IO.File]::WriteAllLines($checksumPath, $checksumLines, [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "ClipForge $Version release created in: $OutputDirectory"
Write-Host "Installer: $publicSetupPath"
if ([string]::IsNullOrWhiteSpace($UpdateUrl)) {
    Write-Warning "This installer has no embedded update URL. Rebuild with -UpdateUrl before public distribution."
}
if ([string]::IsNullOrWhiteSpace($SignParams) -and [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
    Write-Warning "Artifacts are unsigned. Provide a trusted signing configuration before calling this an official public release."
}

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputPath,

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repositoryRoot "src\ClipForge\ClipForge.csproj"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot "artifacts\ClipForge-win-x64"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot $OutputPath
}

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory($OutputPath) | Out-Null

if (-not $NoRestore) {
    & dotnet restore $projectPath --runtime win-x64 --locked-mode
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$publishArguments = @(
    "publish", $projectPath,
    "--configuration", $Configuration,
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--output", $OutputPath,
    "--no-restore",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

& dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "ClipForge was published to: $OutputPath"
Write-Host "FFmpeg is not bundled. ClipForge offers to install it on first run."
Write-Warning "This is a developer portable build and cannot self-update. Use scripts\release.ps1 for user distribution."

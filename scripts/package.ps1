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

$publishArguments = @(
    "publish", $projectPath,
    "--configuration", $Configuration,
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--output", $OutputPath,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)
if ($NoRestore) {
    $publishArguments += "--no-restore"
}

& dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "ClipForge was published to: $OutputPath"
Write-Host "FFmpeg is not bundled. ClipForge offers to install it on first run."
Write-Warning "This is a developer portable build and cannot self-update. Use scripts\release.ps1 for user distribution."

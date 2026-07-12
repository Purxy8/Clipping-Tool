[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$solutionPath = Join-Path $PSScriptRoot "..\ClipForge.slnx"
$projectPath = Join-Path $PSScriptRoot "..\src\ClipForge\ClipForge.csproj"
$testProjectPath = Join-Path $PSScriptRoot "..\tests\ClipForge.Tests\ClipForge.Tests.csproj"

if (-not $NoRestore) {
    & dotnet restore $solutionPath --locked-mode
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$appBuildArguments = @("build", $projectPath, "--configuration", $Configuration, "--no-restore")
& dotnet @appBuildArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$testBuildArguments = @("build", $testProjectPath, "--configuration", $Configuration, "--no-restore")
& dotnet @testBuildArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& dotnet run --project $testProjectPath --configuration $Configuration --no-build
exit $LASTEXITCODE

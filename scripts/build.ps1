[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "..\src\ClipForge\ClipForge.csproj"
$testProjectPath = Join-Path $PSScriptRoot "..\tests\ClipForge.Tests\ClipForge.Tests.csproj"

$appBuildArguments = @("build", $projectPath, "--configuration", $Configuration)
if ($NoRestore) {
    $appBuildArguments += "--no-restore"
}

& dotnet @appBuildArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$testBuildArguments = @("build", $testProjectPath, "--configuration", $Configuration)
if ($NoRestore) {
    $testBuildArguments += "--no-restore"
}

& dotnet @testBuildArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& dotnet run --project $testProjectPath --configuration $Configuration --no-build
exit $LASTEXITCODE

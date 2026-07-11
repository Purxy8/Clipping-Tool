[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ApplicationArguments
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "..\src\ClipForge\ClipForge.csproj"

& dotnet run --project $projectPath --configuration $Configuration -- @ApplicationArguments
exit $LASTEXITCODE

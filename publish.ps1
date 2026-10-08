param(
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\PromptScribe\PromptScribe.csproj"
$output = Join-Path $root "publish\$Runtime"

$args = @(
    "publish", $project,
    "-c", "Release",
    "-r", $Runtime,
    "-o", $output,
    "/p:PublishSingleFile=true",
    "--self-contained", ($(if ($SelfContained) { "true" } else { "false" }))
)

& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$promptTarget = Join-Path $output "promptsets"
New-Item -ItemType Directory -Force -Path $promptTarget | Out-Null
Copy-Item -Recurse -Force (Join-Path $root "promptsets\*") $promptTarget

Write-Host "Published PromptScribe to $output"
Write-Host "Run .\install.ps1 -PublishedPath '$output' to register it globally."

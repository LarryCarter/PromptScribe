param(
    [string]$PublishedPath = "",
    [string]$InstallPath = "$env:LOCALAPPDATA\PromptScribe\bin",
    [switch]$NoPathUpdate
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PublishedPath)) {
    $PublishedPath = Join-Path $root "publish\win-x64"
}

$exe = Join-Path $PublishedPath "PromptScribe.exe"
if (-not (Test-Path $exe)) {
    throw "PromptScribe.exe was not found at '$exe'. Run .\publish.ps1 first."
}

New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item -Force (Join-Path $PublishedPath "PromptScribe.exe") $InstallPath

$sourcePromptSets = Join-Path $PublishedPath "promptsets"
if (Test-Path $sourcePromptSets) {
    $targetPromptSets = Join-Path $InstallPath "promptsets"
    New-Item -ItemType Directory -Force -Path $targetPromptSets | Out-Null
    Copy-Item -Recurse -Force (Join-Path $sourcePromptSets "*") $targetPromptSets
}

if (-not $NoPathUpdate) {
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $parts = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($parts -notcontains $InstallPath) {
        $newPath = (($parts + $InstallPath) -join ';')
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Host "Added '$InstallPath' to the user PATH. Open a new terminal before using PromptScribe globally."
    }
}

Write-Host "Installed PromptScribe to $InstallPath"
Write-Host "Try: PromptScribe doctor"

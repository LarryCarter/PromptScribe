param(
    [string]$InstallPath = "$env:LOCALAPPDATA\PromptScribe\bin"
)

$ErrorActionPreference = "Stop"
if (Test-Path $InstallPath) {
    Remove-Item -Recurse -Force $InstallPath
}

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$parts = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne $InstallPath })
[Environment]::SetEnvironmentVariable("Path", ($parts -join ';'), "User")
Write-Host "PromptScribe removed from $InstallPath and the user PATH."

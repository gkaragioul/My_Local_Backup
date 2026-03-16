$ErrorActionPreference = "Stop"
$match = Select-String -Path "MyLocalBackup.UI/MyLocalBackup.UI.csproj" -Pattern "<Version>(.*)</Version>"
if (-not $match) { throw "Could not extract version from MyLocalBackup.UI.csproj — is the <Version> tag present?" }
$version = $match.Matches.Groups[1].Value
if ([string]::IsNullOrWhiteSpace($version)) { throw "Extracted version is empty" }
$stagingDir = "Staging\Portable"
$outputZip = "MyLocalBackup_v$($version)_Portable.zip"

if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

Write-Host "Publishing Portable version v$version..." -ForegroundColor Cyan

# Publish as Self-Contained to ensure it runs even if .NET isn't installed in the Sandbox
dotnet publish MyLocalBackup.UI/MyLocalBackup.UI.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $stagingDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# Clean up PDBs to save space
Get-ChildItem -Path $stagingDir -Filter "*.pdb" -Recurse | Remove-Item

Write-Host "Creating ZIP..." -ForegroundColor Cyan
if (Test-Path $outputZip) { Remove-Item $outputZip }
Compress-Archive -Path "$stagingDir\*" -DestinationPath $outputZip

Write-Host "Success! Created: $outputZip" -ForegroundColor Green

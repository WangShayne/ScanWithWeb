# Download TWAIN DSM DLLs for ScanWithWeb installer
# Run this script from the ScanWithWeb_setup directory

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Downloading TWAIN DSM DLLs" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Create dependencies directory
$depsDir = Join-Path $PSScriptRoot "dependencies"
if (!(Test-Path $depsDir)) {
    New-Item -ItemType Directory -Path $depsDir | Out-Null
}

# Download TWAIN DSM from official GitHub releases
# Note: File naming changed - use twain-dsm-X.X.X.zip format
$twainDsmVersion = "2.5.1"
$downloadUrl = "https://github.com/twain/twain-dsm/releases/download/v$twainDsmVersion/twain-dsm-$twainDsmVersion.zip"
$zipFile = Join-Path $env:TEMP "twaindsm.zip"
$extractDir = Join-Path $env:TEMP "twaindsm_extracted"

Write-Host "Downloading TWAIN DSM v$twainDsmVersion..." -ForegroundColor Yellow
Write-Host "URL: $downloadUrl"
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipFile -UseBasicParsing
    Write-Host "Download complete." -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to download TWAIN DSM!" -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}

# Extract the ZIP
Write-Host ""
Write-Host "Extracting..." -ForegroundColor Yellow
if (Test-Path $extractDir) {
    Remove-Item -Recurse -Force $extractDir
}
Expand-Archive -Path $zipFile -DestinationPath $extractDir -Force

# Find and copy the DLLs
Write-Host ""
Write-Host "Copying DLLs to dependencies folder..." -ForegroundColor Yellow

# 64-bit DLL
$dll64 = Get-ChildItem -Path $extractDir -Recurse -Filter "TWAINDSM.dll" |
    Where-Object { $_.DirectoryName -match "Win64|x64|64" } |
    Select-Object -First 1

if ($dll64) {
    Copy-Item $dll64.FullName (Join-Path $depsDir "twaindsm_x64.dll") -Force
    Write-Host "  64-bit: $($dll64.FullName) -> twaindsm_x64.dll" -ForegroundColor Green
} else {
    # Fallback: try to find any DLL
    $dllAny = Get-ChildItem -Path $extractDir -Recurse -Filter "TWAINDSM.dll" | Select-Object -First 1
    if ($dllAny) {
        Copy-Item $dllAny.FullName (Join-Path $depsDir "twaindsm_x64.dll") -Force
        Write-Host "  64-bit (fallback): $($dllAny.FullName) -> twaindsm_x64.dll" -ForegroundColor Yellow
    } else {
        Write-Host "  WARNING: 64-bit DLL not found!" -ForegroundColor Red
    }
}

# 32-bit DLL
$dll32 = Get-ChildItem -Path $extractDir -Recurse -Filter "TWAINDSM.dll" |
    Where-Object { $_.DirectoryName -match "Win32|x86|32" } |
    Select-Object -First 1

if ($dll32) {
    Copy-Item $dll32.FullName (Join-Path $depsDir "twaindsm_x86.dll") -Force
    Write-Host "  32-bit: $($dll32.FullName) -> twaindsm_x86.dll" -ForegroundColor Green
} else {
    Write-Host "  WARNING: 32-bit DLL not found!" -ForegroundColor Yellow
}

# Cleanup
Remove-Item $zipFile -Force -ErrorAction SilentlyContinue
Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue

# Verify
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Dependencies folder contents:" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Get-ChildItem $depsDir | Format-Table Name, Length -AutoSize

Write-Host ""
Write-Host "Done! You can now run build_installer.bat" -ForegroundColor Green

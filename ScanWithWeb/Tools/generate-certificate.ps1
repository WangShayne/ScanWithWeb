# ScanWithWeb Certificate Generator
# This script generates a self-signed SSL certificate for secure WebSocket (WSS) connections
# Run as Administrator for certificate installation

param(
    [string]$OutputPath = "..\certificate.pfx",
    [string]$Password = "ScanWithWeb",
    [switch]$InstallToStore
)

$ErrorActionPreference = "Stop"

Write-Host "ScanWithWeb Certificate Generator" -ForegroundColor Cyan
Write-Host "==============================" -ForegroundColor Cyan

# Certificate details
$certSubject = "CN=localhost"
$certFriendlyName = "ScanWithWeb WebSocket SSL Certificate"
$validYears = 10

# Generate certificate
Write-Host "`nGenerating self-signed certificate..." -ForegroundColor Yellow

$cert = New-SelfSignedCertificate `
    -Subject $certSubject `
    -DnsName "localhost", "127.0.0.1" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -NotBefore (Get-Date) `
    -NotAfter (Get-Date).AddYears($validYears) `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -FriendlyName $certFriendlyName `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1") `
    -KeyUsage DigitalSignature, KeyEncipherment

Write-Host "Certificate generated: $($cert.Thumbprint)" -ForegroundColor Green

# Export to PFX file
Write-Host "`nExporting certificate to PFX file..." -ForegroundColor Yellow

$securePassword = ConvertTo-SecureString -String $Password -Force -AsPlainText
$outputFullPath = Join-Path (Get-Location) $OutputPath
Export-PfxCertificate -Cert $cert -FilePath $outputFullPath -Password $securePassword | Out-Null

Write-Host "Certificate exported to: $outputFullPath" -ForegroundColor Green

# Install to Trusted Root (optional)
if ($InstallToStore) {
    Write-Host "`nInstalling certificate to Trusted Root store..." -ForegroundColor Yellow
    Write-Host "This requires Administrator privileges." -ForegroundColor Yellow

    try {
        # Export and import to Root store
        $certPath = "Cert:\LocalMachine\Root"
        $certBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)

        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
            [System.Security.Cryptography.X509Certificates.StoreName]::Root,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
        )
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($cert)
        $store.Close()

        Write-Host "Certificate installed to Trusted Root store." -ForegroundColor Green
        Write-Host "Browsers should now trust the certificate without warnings." -ForegroundColor Green
    }
    catch {
        Write-Host "Failed to install to Trusted Root. Try running as Administrator." -ForegroundColor Red
        Write-Host "Error: $_" -ForegroundColor Red
    }
}

# Cleanup from personal store (optional)
Write-Host "`nCleaning up temporary certificate from Personal store..." -ForegroundColor Yellow
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue

Write-Host "`n==============================" -ForegroundColor Cyan
Write-Host "Certificate generation complete!" -ForegroundColor Green
Write-Host "`nTo use with ScanWithWeb:" -ForegroundColor White
Write-Host "1. Copy 'certificate.pfx' to the ScanWithWeb folder" -ForegroundColor White
Write-Host "2. Update appsettings.json with the certificate path" -ForegroundColor White
Write-Host "3. Run with -InstallToStore to trust the certificate in browsers" -ForegroundColor White
Write-Host "`nExample:" -ForegroundColor Yellow
Write-Host "  .\generate-certificate.ps1 -InstallToStore" -ForegroundColor Yellow

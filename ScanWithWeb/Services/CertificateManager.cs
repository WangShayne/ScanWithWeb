using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace ScanWithWeb.Services;

/// <summary>
/// Manages SSL certificates for WebSocket server
/// Auto-generates self-signed certificates if not present
/// </summary>
public class CertificateManager
{
    private readonly ILogger<CertificateManager> _logger;
    private readonly string _certificatePath;
    private readonly string _certificatePassword;
    private readonly string _subjectName;
    private readonly int _validityDays;
    private readonly bool _autoInstallToStore;

    public CertificateManager(
        ILogger<CertificateManager> logger,
        string certificatePath = "scanwithweb.pfx",
        string certificatePassword = "scanwithweb",
        string subjectName = "localhost",
        int validityDays = 365,
        bool autoInstallToStore = true)
    {
        _logger = logger;
        _certificatePath = certificatePath;
        _certificatePassword = certificatePassword;
        _subjectName = subjectName;
        _validityDays = validityDays;
        _autoInstallToStore = autoInstallToStore;
    }

    /// <summary>
    /// Gets or creates the SSL certificate
    /// </summary>
    public X509Certificate2? GetOrCreateCertificate()
    {
        try
        {
            X509Certificate2? cert = null;
            bool isNewCert = false;

            // Try to load existing certificate
            if (File.Exists(_certificatePath))
            {
                cert = LoadCertificate();
                if (cert != null && IsCertificateValid(cert))
                {
                    _logger.LogInformation("Loaded existing SSL certificate from {Path}", _certificatePath);
                }
                else
                {
                    _logger.LogWarning("Existing certificate is invalid or expired, regenerating...");
                    cert?.Dispose();
                    cert = null;
                }
            }

            // Generate new certificate if needed
            if (cert == null)
            {
                cert = GenerateAndSaveCertificate();
                isNewCert = true;
            }

            // Auto-install to trusted store if enabled
            if (cert != null && _autoInstallToStore)
            {
                EnsureCertificateInStore(cert, isNewCert);
            }

            return cert;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get or create SSL certificate");
            return null;
        }
    }

    /// <summary>
    /// Ensures the certificate is installed in the trusted root store
    /// </summary>
    private void EnsureCertificateInStore(X509Certificate2 cert, bool isNewCert)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);

            // Check if already installed
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
            if (existing.Count > 0)
            {
                _logger.LogDebug("Certificate already trusted in store");
                return;
            }

            store.Close();

            // Only install if it's a new certificate or not found in store
            if (isNewCert || existing.Count == 0)
            {
                _logger.LogInformation("Installing certificate to trusted store for seamless WSS connections...");
                InstallToStore(cert);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check/install certificate in store");
        }
    }

    /// <summary>
    /// Loads certificate from file
    /// </summary>
    private X509Certificate2? LoadCertificate()
    {
        try
        {
            // UserKeySet is required on Windows for SSL/TLS to work properly
            return new X509Certificate2(_certificatePath, _certificatePassword,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load certificate from {Path}", _certificatePath);
            return null;
        }
    }

    /// <summary>
    /// Checks if certificate is valid and not expired
    /// </summary>
    private bool IsCertificateValid(X509Certificate2 cert)
    {
        var now = DateTime.Now;
        // Check if certificate expires within 30 days
        return cert.NotBefore <= now && cert.NotAfter > now.AddDays(30);
    }

    /// <summary>
    /// Generates a new self-signed certificate and saves it
    /// </summary>
    private X509Certificate2? GenerateAndSaveCertificate()
    {
        try
        {
            _logger.LogInformation("Generating new self-signed SSL certificate...");

            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest(
                $"CN={_subjectName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Add extensions
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    true));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                    false));

            // Add Subject Alternative Names (SAN)
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName(_subjectName);
            sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
            sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(sanBuilder.Build());

            // Create certificate
            var notBefore = DateTimeOffset.Now.AddDays(-1);
            var notAfter = DateTimeOffset.Now.AddDays(_validityDays);

            using var cert = request.CreateSelfSigned(notBefore, notAfter);

            // Export to PFX
            var pfxBytes = cert.Export(X509ContentType.Pfx, _certificatePassword);
            File.WriteAllBytes(_certificatePath, pfxBytes);

            _logger.LogInformation("SSL certificate generated and saved to {Path}", _certificatePath);
            _logger.LogInformation("Certificate valid until {ExpiryDate}", notAfter.DateTime.ToShortDateString());

            // Return a new instance loaded from the file with proper flags for SSL
            // UserKeySet is required on Windows for SSL/TLS to work properly
            return new X509Certificate2(_certificatePath, _certificatePassword,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate SSL certificate");
            return null;
        }
    }

    /// <summary>
    /// Installs certificate to Windows certificate store (optional, requires admin)
    /// </summary>
    public bool InstallToStore(X509Certificate2 cert)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            // Check if already installed
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
            if (existing.Count > 0)
            {
                _logger.LogInformation("Certificate already installed in store");
                return true;
            }

            store.Add(cert);
            store.Close();

            _logger.LogInformation("Certificate installed to CurrentUser\\Root store");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install certificate to store (may require admin rights)");
            return false;
        }
    }

    /// <summary>
    /// Gets instructions for manually trusting the certificate
    /// </summary>
    public string GetTrustInstructions()
    {
        return $@"
To trust the SSL certificate for browser access:

Option 1 - Browser (recommended):
  1. Open https://localhost:8181 in your browser
  2. Click 'Advanced' and 'Proceed to localhost'

Option 2 - Install to Windows Store:
  1. Double-click the file: {Path.GetFullPath(_certificatePath)}
  2. Click 'Install Certificate'
  3. Select 'Current User' -> 'Place all certificates in the following store'
  4. Browse and select 'Trusted Root Certification Authorities'
  5. Click 'Finish'

Option 3 - PowerShell (Admin):
  Import-PfxCertificate -FilePath ""{Path.GetFullPath(_certificatePath)}"" -CertStoreLocation Cert:\CurrentUser\Root -Password (ConvertTo-SecureString -String ""{_certificatePassword}"" -Force -AsPlainText)
";
    }
}

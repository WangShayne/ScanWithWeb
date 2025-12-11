using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Models;
using ScanWithWeb.Services.Escl;

namespace ScanWithWeb.Services.Protocols;

/// <summary>
/// ESCL (eSCL/AirScan) protocol implementation for network scanners
/// Supports scanners advertising _uscan._tcp and _uscans._tcp services
/// </summary>
public class EsclScannerProtocol : BaseScannerProtocol
{
    private readonly EsclDiscovery _discovery;
    private readonly Dictionary<string, EsclScannerEndpoint> _scanners = new();
    private EsclScannerEndpoint? _selectedScanner;
    private bool _stopRequested;
    private int _currentPageNumber;
    private string? _currentRequestId;

    // ESCL XML namespaces
    private static readonly XNamespace ScanNs = "http://schemas.hp.com/imaging/escl/2011/05/03";
    private static readonly XNamespace PwgNs = "http://www.pwg.org/schemas/2010/12/sm";

    public override string ProtocolName => "escl";

    public EsclScannerProtocol(ILogger<EsclScannerProtocol> logger) : base(logger)
    {
        _discovery = new EsclDiscovery(logger);
        _discovery.ScannerDiscovered += OnScannerDiscovered;
        _discovery.ScannerLost += OnScannerLost;
    }

    private void OnScannerDiscovered(object? sender, EsclScannerEndpoint endpoint)
    {
        var id = $"{endpoint.Host}:{endpoint.Port}";
        _scanners[id] = endpoint;
        Logger.LogInformation("[ESCL] Scanner discovered: {Name} at {Host}:{Port}",
            endpoint.Name, endpoint.Host, endpoint.Port);
    }

    private void OnScannerLost(object? sender, string scannerId)
    {
        _scanners.Remove(scannerId);
        Logger.LogInformation("[ESCL] Scanner lost: {Id}", scannerId);
    }

    public override bool Initialize(IntPtr windowHandle)
    {
        try
        {
            Logger.LogInformation("[ESCL] Initializing ESCL protocol...");

            // Start mDNS discovery in background
            _discovery.StartDiscovery();

            IsAvailable = true;
            Logger.LogInformation("[ESCL] Initialized successfully (network scanner support)");
            return true;
        }
        catch (Exception ex)
        {
            InitializationError = $"ESCL initialization failed: {ex.Message}";
            IsAvailable = false;
            Logger.LogError(ex, "[ESCL] Initialization failed");
            return false;
        }
    }

    public override async Task<List<ScannerInfo>> GetAvailableScannersAsync()
    {
        var scanners = new List<ScannerInfo>();

        // Include discovered scanners from mDNS
        foreach (var endpoint in _discovery.DiscoveredScanners.Values)
        {
            var id = $"{endpoint.Host}:{endpoint.Port}";
            _scanners[id] = endpoint;
        }

        foreach (var kvp in _scanners)
        {
            var endpoint = kvp.Value;
            var capabilities = await GetCapabilitiesFromEndpointAsync(endpoint);

            scanners.Add(new ScannerInfo
            {
                Id = kvp.Key,
                Name = endpoint.Name ?? $"ESCL Scanner ({endpoint.Host})",
                IsDefault = false,
                Capabilities = capabilities,
                Protocol = ProtocolName
            });
        }

        Logger.LogInformation("[ESCL] Found {Count} network scanner(s)", scanners.Count);
        return scanners;
    }

    /// <summary>
    /// Manually add a scanner by IP address
    /// </summary>
    public async Task<bool> AddManualScannerAsync(string host, int port = 80)
    {
        var endpoint = await _discovery.AddManualScannerAsync(host, port);
        if (endpoint != null)
        {
            var id = $"{host}:{port}";
            _scanners[id] = endpoint;
            return true;
        }
        return false;
    }

    public override bool SelectScanner(string scannerId)
    {
        if (!_scanners.TryGetValue(scannerId, out var endpoint))
        {
            Logger.LogWarning("[ESCL] Scanner not found: {Id}", scannerId);
            return false;
        }

        _selectedScanner = endpoint;
        CurrentScannerId = scannerId;
        Logger.LogInformation("[ESCL] Scanner selected: {Name} at {Host}:{Port}",
            endpoint.Name, endpoint.Host, endpoint.Port);
        return true;
    }

    public override ScannerCapabilities? GetCapabilities(string scannerId)
    {
        if (_scanners.TryGetValue(scannerId, out var endpoint))
        {
            return GetCapabilitiesFromEndpointAsync(endpoint).GetAwaiter().GetResult();
        }
        return null;
    }

    private async Task<ScannerCapabilities?> GetCapabilitiesFromEndpointAsync(EsclScannerEndpoint endpoint)
    {
        try
        {
            using var client = CreateHttpClient();
            var url = $"{endpoint.BaseUrl}/ScannerCapabilities";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return GetDefaultCapabilities();

            var xml = await response.Content.ReadAsStringAsync();
            return ParseCapabilities(xml);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ESCL] Failed to get capabilities from {Host}", endpoint.Host);
            return GetDefaultCapabilities();
        }
    }

    private ScannerCapabilities ParseCapabilities(string xml)
    {
        var caps = GetDefaultCapabilities();

        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root == null) return caps;

            // Parse supported resolutions
            var resolutions = root.Descendants()
                .Where(e => e.Name.LocalName == "DiscreteResolution" ||
                           e.Name.LocalName == "XResolution")
                .Select(e =>
                {
                    var xRes = e.Name.LocalName == "DiscreteResolution"
                        ? e.Elements().FirstOrDefault(x => x.Name.LocalName == "XResolution")?.Value
                        : e.Value;
                    return int.TryParse(xRes, out var r) ? r : 0;
                })
                .Where(r => r > 0)
                .Distinct()
                .OrderBy(r => r)
                .ToList();

            if (resolutions.Count > 0)
                caps.SupportedDpi = resolutions;

            // Parse color modes
            var colorModes = root.Descendants()
                .Where(e => e.Name.LocalName == "ColorMode")
                .Select(e => e.Value)
                .Distinct()
                .ToList();

            if (colorModes.Count > 0)
            {
                caps.SupportedPixelTypes = colorModes.Select(cm => cm switch
                {
                    "BlackAndWhite1" => "BW",
                    "Grayscale8" => "Gray8",
                    "RGB24" => "RGB",
                    _ => cm
                }).ToList();
            }

            // Check for ADF support
            var adfElement = root.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Adf");
            caps.SupportsAdf = adfElement != null;

            // Check for duplex support
            var duplexElement = root.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "AdfDuplexInputCaps");
            caps.SupportsDuplex = duplexElement != null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ESCL] Error parsing capabilities XML");
        }

        return caps;
    }

    private static ScannerCapabilities GetDefaultCapabilities()
    {
        return new ScannerCapabilities
        {
            SupportedDpi = new List<int> { 75, 100, 150, 200, 300, 600 },
            SupportedPixelTypes = new List<string> { "BW", "Gray8", "RGB" },
            SupportedPaperSizes = new List<string> { "A4", "Letter", "Legal" },
            SupportsDuplex = false,
            SupportsAdf = false,
            SupportsUIControl = false
        };
    }

    public override async Task<bool> StartScanAsync(string requestId)
    {
        if (_selectedScanner == null)
        {
            Logger.LogWarning("[ESCL] No scanner selected");
            return false;
        }

        if (IsScanning)
        {
            Logger.LogWarning("[ESCL] Scan already in progress");
            return false;
        }

        _currentRequestId = requestId;
        _currentPageNumber = 0;
        _stopRequested = false;
        IsScanning = true;

        try
        {
            Logger.LogInformation("[ESCL] Starting scan on {Host}:{Port}...",
                _selectedScanner.Host, _selectedScanner.Port);

            // Create scan job
            var jobUrl = await CreateScanJobAsync();
            if (string.IsNullOrEmpty(jobUrl))
            {
                RaiseScanError(requestId, "Failed to create scan job");
                IsScanning = false;
                return false;
            }

            Logger.LogDebug("[ESCL] Scan job created: {Url}", jobUrl);

            // Retrieve scanned image(s)
            await RetrieveScanResultsAsync(requestId, jobUrl);

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ESCL] Error during scan");
            RaiseScanError(requestId, ex.Message);
            IsScanning = false;
            return false;
        }
    }

    private async Task<string?> CreateScanJobAsync()
    {
        if (_selectedScanner == null) return null;

        try
        {
            using var client = CreateHttpClient();

            // Build scan settings XML
            var scanSettings = BuildScanSettingsXml();
            var content = new StringContent(scanSettings, Encoding.UTF8, "text/xml");

            var url = $"{_selectedScanner.BaseUrl}/ScanJobs";
            var response = await client.PostAsync(url, content);

            if (response.StatusCode == System.Net.HttpStatusCode.Created)
            {
                // Get job URL from Location header
                var location = response.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location))
                {
                    return location;
                }

                // Try to construct job URL from response
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(responseBody))
                {
                    // Parse job ID from response and construct URL
                    return $"{_selectedScanner.BaseUrl}/ScanJobs/1";
                }
            }

            Logger.LogWarning("[ESCL] Failed to create scan job: {Status}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ESCL] Error creating scan job");
            return null;
        }
    }

    private string BuildScanSettingsXml()
    {
        var colorMode = CurrentSettings?.PixelType?.ToLower() switch
        {
            "bw" or "blackwhite" => "BlackAndWhite1",
            "gray" or "gray8" or "grayscale" => "Grayscale8",
            _ => "RGB24"
        };

        var resolution = CurrentSettings?.Dpi > 0 ? CurrentSettings.Dpi : 200;

        // ESCL scan request XML
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<scan:ScanSettings xmlns:scan=""http://schemas.hp.com/imaging/escl/2011/05/03""
                   xmlns:pwg=""http://www.pwg.org/schemas/2010/12/sm"">
    <pwg:Version>2.0</pwg:Version>
    <scan:Intent>Document</scan:Intent>
    <pwg:ScanRegions>
        <pwg:ScanRegion>
            <pwg:ContentRegionUnits>escl:ThreeHundredthsOfInches</pwg:ContentRegionUnits>
            <pwg:XOffset>0</pwg:XOffset>
            <pwg:YOffset>0</pwg:YOffset>
            <pwg:Width>2550</pwg:Width>
            <pwg:Height>3300</pwg:Height>
        </pwg:ScanRegion>
    </pwg:ScanRegions>
    <pwg:InputSource>Platen</pwg:InputSource>
    <scan:ColorMode>{colorMode}</scan:ColorMode>
    <scan:XResolution>{resolution}</scan:XResolution>
    <scan:YResolution>{resolution}</scan:YResolution>
    <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>
</scan:ScanSettings>";

        return xml;
    }

    private async Task RetrieveScanResultsAsync(string requestId, string jobUrl)
    {
        const int maxRetries = 30;
        const int retryDelayMs = 1000;

        for (int i = 0; i < maxRetries && !_stopRequested; i++)
        {
            try
            {
                using var client = CreateHttpClient();

                // Try to get the next page
                var nextDocUrl = $"{jobUrl}/NextDocument";
                var response = await client.GetAsync(nextDocUrl);

                if (response.IsSuccessStatusCode)
                {
                    _currentPageNumber++;
                    var imageData = await response.Content.ReadAsByteArrayAsync();

                    // Determine format from content type
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    var format = contentType switch
                    {
                        "image/png" => "png",
                        "image/jpeg" => "jpg",
                        "image/tiff" => "tiff",
                        _ => "jpg"
                    };

                    var metadata = new ImageMetadata
                    {
                        Width = 0, // Will be determined by client
                        Height = 0,
                        Format = format,
                        SizeBytes = imageData.Length,
                        Dpi = CurrentSettings?.Dpi ?? 200
                    };

                    Logger.LogInformation("[ESCL] Page {Page} received: {Size} bytes",
                        _currentPageNumber, imageData.Length);

                    RaiseImageScanned(requestId, imageData, metadata, _currentPageNumber);

                    // Check if there are more pages
                    continue;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // No more pages or job completed
                    Logger.LogDebug("[ESCL] No more pages available");
                    break;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    // Scanner is still processing
                    Logger.LogDebug("[ESCL] Scanner processing, waiting...");
                    await Task.Delay(retryDelayMs);
                    continue;
                }
                else
                {
                    Logger.LogWarning("[ESCL] Unexpected response: {Status}", response.StatusCode);
                    break;
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "[ESCL] HTTP error retrieving scan results");
                await Task.Delay(retryDelayMs);
            }
        }

        // Delete the job
        await DeleteScanJobAsync(jobUrl);

        if (_currentPageNumber > 0)
        {
            RaiseScanCompleted(requestId, _currentPageNumber);
        }
        else if (!_stopRequested)
        {
            RaiseScanError(requestId, "No pages were scanned");
        }

        IsScanning = false;
    }

    private async Task DeleteScanJobAsync(string jobUrl)
    {
        try
        {
            using var client = CreateHttpClient();
            await client.DeleteAsync(jobUrl);
            Logger.LogDebug("[ESCL] Scan job deleted");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ESCL] Error deleting scan job");
        }
    }

    public override void StopScan()
    {
        _stopRequested = true;
        Logger.LogInformation("[ESCL] Scan stop requested");
    }

    public override void Shutdown()
    {
        try
        {
            _discovery.StopDiscovery();
            _discovery.Dispose();
            _scanners.Clear();
            Logger.LogInformation("[ESCL] Shutdown complete");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ESCL] Error during shutdown");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true // Accept self-signed certs
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/jpeg"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/png"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/pdf"));

        return client;
    }
}

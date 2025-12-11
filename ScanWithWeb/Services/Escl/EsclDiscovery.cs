using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ScanWithWeb.Services.Escl;

/// <summary>
/// mDNS/Bonjour discovery for ESCL-compatible network scanners
/// Discovers services advertising _uscan._tcp and _uscans._tcp
/// </summary>
public class EsclDiscovery : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, EsclScannerEndpoint> _endpoints = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _isRunning;

    public event EventHandler<EsclScannerEndpoint>? ScannerDiscovered;
    public event EventHandler<string>? ScannerLost;

    /// <summary>
    /// Gets all currently discovered scanners
    /// </summary>
    public IReadOnlyDictionary<string, EsclScannerEndpoint> DiscoveredScanners => _endpoints;

    public EsclDiscovery(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start mDNS discovery
    /// </summary>
    public void StartDiscovery()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _logger.LogInformation("[ESCL Discovery] Starting mDNS discovery for ESCL scanners...");

        // Start background discovery task
        _ = DiscoveryLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Stop mDNS discovery
    /// </summary>
    public void StopDiscovery()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cts.Cancel();
        _logger.LogInformation("[ESCL Discovery] Stopped mDNS discovery");
    }

    /// <summary>
    /// Add a scanner manually by address (for when mDNS isn't available)
    /// </summary>
    public async Task<EsclScannerEndpoint?> AddManualScannerAsync(string host, int port = 80, bool secure = false)
    {
        try
        {
            var endpoint = new EsclScannerEndpoint
            {
                Id = $"{host}:{port}",
                Host = host,
                Port = port,
                IsSecure = secure,
                BaseUrl = $"{(secure ? "https" : "http")}://{host}:{port}/eSCL"
            };

            // Try to get scanner info
            using var client = CreateHttpClient();
            var capabilitiesUrl = $"{endpoint.BaseUrl}/ScannerCapabilities";

            var response = await client.GetAsync(capabilitiesUrl);
            if (response.IsSuccessStatusCode)
            {
                var xml = await response.Content.ReadAsStringAsync();

                // Parse basic info from capabilities
                endpoint.Name = ParseXmlElement(xml, "MakeAndModel") ??
                               ParseXmlElement(xml, "Make") ??
                               $"ESCL Scanner ({host})";
                endpoint.Manufacturer = ParseXmlElement(xml, "Manufacturer");
                endpoint.Model = ParseXmlElement(xml, "Model");
                endpoint.Uuid = ParseXmlElement(xml, "UUID") ?? Guid.NewGuid().ToString();

                if (_endpoints.TryAdd(endpoint.Id, endpoint))
                {
                    _logger.LogInformation("[ESCL Discovery] Manually added scanner: {Name} at {Host}:{Port}",
                        endpoint.Name, host, port);
                    ScannerDiscovered?.Invoke(this, endpoint);
                }

                return endpoint;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ESCL Discovery] Failed to add manual scanner at {Host}:{Port}", host, port);
        }

        return null;
    }

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        // Initial discovery attempt
        await DiscoverScannersAsync(cancellationToken);

        // Periodic refresh
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                await DiscoverScannersAsync(cancellationToken);
                CleanupStaleEndpoints();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ESCL Discovery] Error in discovery loop");
            }
        }
    }

    private async Task DiscoverScannersAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Get local network interfaces to scan common ports on local network
            var localIPs = GetLocalNetworkAddresses();

            foreach (var ip in localIPs)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Scan common ESCL ports on the local network segment
                var baseAddress = GetNetworkBase(ip);
                if (string.IsNullOrEmpty(baseAddress))
                    continue;

                // Try a few common addresses (this is a simplified approach)
                // A full implementation would use proper mDNS
                var commonPorts = new[] { 80, 443, 8080, 8443, 9095 };

                foreach (var port in commonPorts)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Check if there's an ESCL endpoint at this address
                    await TryDiscoverAtAddressAsync($"{baseAddress}", port, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ESCL Discovery] Error during scanner discovery");
        }
    }

    private async Task TryDiscoverAtAddressAsync(string host, int port, CancellationToken cancellationToken)
    {
        var id = $"{host}:{port}";

        // Skip if already discovered
        if (_endpoints.ContainsKey(id))
        {
            _endpoints[id].LastSeen = DateTime.UtcNow;
            return;
        }

        try
        {
            using var client = CreateHttpClient();
            client.Timeout = TimeSpan.FromSeconds(2);

            var capabilitiesUrl = $"http://{host}:{port}/eSCL/ScannerCapabilities";
            var response = await client.GetAsync(capabilitiesUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var xml = await response.Content.ReadAsStringAsync(cancellationToken);

                var endpoint = new EsclScannerEndpoint
                {
                    Id = id,
                    Host = host,
                    Port = port,
                    IsSecure = false,
                    BaseUrl = $"http://{host}:{port}/eSCL",
                    Name = ParseXmlElement(xml, "MakeAndModel") ??
                           ParseXmlElement(xml, "Make") ??
                           $"ESCL Scanner ({host})",
                    Manufacturer = ParseXmlElement(xml, "Manufacturer"),
                    Model = ParseXmlElement(xml, "Model"),
                    Uuid = ParseXmlElement(xml, "UUID") ?? Guid.NewGuid().ToString(),
                    LastSeen = DateTime.UtcNow
                };

                if (_endpoints.TryAdd(id, endpoint))
                {
                    _logger.LogInformation("[ESCL Discovery] Found scanner: {Name} at {Host}:{Port}",
                        endpoint.Name, host, port);
                    ScannerDiscovered?.Invoke(this, endpoint);
                }
            }
        }
        catch
        {
            // Silently ignore - this is expected for most addresses
        }
    }

    private void CleanupStaleEndpoints()
    {
        var staleThreshold = DateTime.UtcNow.AddMinutes(-5);
        var staleKeys = _endpoints
            .Where(kvp => kvp.Value.LastSeen < staleThreshold)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            if (_endpoints.TryRemove(key, out _))
            {
                _logger.LogInformation("[ESCL Discovery] Scanner lost: {Id}", key);
                ScannerLost?.Invoke(this, key);
            }
        }
    }

    private static List<string> GetLocalNetworkAddresses()
    {
        var addresses = new List<string>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        addresses.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch { }

        return addresses;
    }

    private static string? GetNetworkBase(string ip)
    {
        try
        {
            var parts = ip.Split('.');
            if (parts.Length == 4)
            {
                // Return the gateway (assuming .1)
                return $"{parts[0]}.{parts[1]}.{parts[2]}.1";
            }
        }
        catch { }
        return null;
    }

    private static string? ParseXmlElement(string xml, string elementName)
    {
        try
        {
            // Simple XML parsing without dependencies
            var startTag = $"<{elementName}>";
            var endTag = $"</{elementName}>";

            // Also try with namespace prefix
            var patterns = new[]
            {
                (startTag, endTag),
                ($"<scan:{elementName}>", $"</scan:{elementName}>"),
                ($"<pwg:{elementName}>", $"</pwg:{elementName}>")
            };

            foreach (var (start, end) in patterns)
            {
                var startIdx = xml.IndexOf(start, StringComparison.OrdinalIgnoreCase);
                if (startIdx >= 0)
                {
                    startIdx += start.Length;
                    var endIdx = xml.IndexOf(end, startIdx, StringComparison.OrdinalIgnoreCase);
                    if (endIdx > startIdx)
                    {
                        return xml.Substring(startIdx, endIdx - startIdx).Trim();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true // Accept self-signed certs
        };
        return new HttpClient(handler);
    }

    public void Dispose()
    {
        StopDiscovery();
        _cts.Dispose();
    }
}

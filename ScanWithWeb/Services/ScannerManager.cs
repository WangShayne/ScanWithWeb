using Microsoft.Extensions.Logging;
using ScanWithWeb.Interfaces;
using ScanWithWeb.Models;

namespace ScanWithWeb.Services;

/// <summary>
/// Unified scanner manager that aggregates multiple protocol implementations
/// Provides a single entry point for scanner operations across all protocols
/// </summary>
public class ScannerManager : IDisposable
{
    private readonly ILogger<ScannerManager> _logger;
    private readonly Dictionary<string, IScannerProtocol> _protocols = new();
    private IScannerProtocol? _activeProtocol;
    private string? _activeScannerId;
    private ClientSession? _currentSession;
    private string? _currentRequestId;

    // Events that bubble up from protocols
    public event EventHandler<ProtocolImageScannedEventArgs>? ImageScanned;
    public event EventHandler<ProtocolScanCompletedEventArgs>? ScanCompleted;
    public event EventHandler<ProtocolScanErrorEventArgs>? ScanError;

    public bool IsScanning => _activeProtocol?.IsScanning ?? false;
    public string? CurrentScannerName => _activeScannerId;
    public string? CurrentScannerId => _activeScannerId;

    /// <summary>
    /// Gets all registered protocols
    /// </summary>
    public IEnumerable<IScannerProtocol> GetAvailableProtocols() => _protocols.Values;

    /// <summary>
    /// Gets status information about all registered protocols
    /// </summary>
    public IReadOnlyDictionary<string, (bool IsAvailable, string? Error)> ProtocolStatus =>
        _protocols.ToDictionary(
            p => p.Key,
            p => (p.Value.IsAvailable, p.Value.InitializationError));

    public ScannerManager(ILogger<ScannerManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Register a protocol implementation
    /// </summary>
    public void RegisterProtocol(IScannerProtocol protocol)
    {
        _protocols[protocol.ProtocolName] = protocol;

        // Subscribe to protocol events
        protocol.ImageScanned += OnProtocolImageScanned;
        protocol.ScanCompleted += OnProtocolScanCompleted;
        protocol.ScanError += OnProtocolScanError;

        _logger.LogInformation("Registered protocol: {Protocol}", protocol.ProtocolName);
    }

    /// <summary>
    /// Initialize all registered protocols
    /// </summary>
    public void Initialize(IntPtr windowHandle)
    {
        _logger.LogInformation("Initializing {Count} protocol(s)...", _protocols.Count);

        foreach (var protocol in _protocols.Values)
        {
            try
            {
                protocol.Initialize(windowHandle);
                _logger.LogInformation("Protocol {Name} initialized: Available={Available}",
                    protocol.ProtocolName, protocol.IsAvailable);

                if (!protocol.IsAvailable && protocol.InitializationError != null)
                {
                    _logger.LogWarning("Protocol {Name} error: {Error}",
                        protocol.ProtocolName, protocol.InitializationError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize protocol {Name}", protocol.ProtocolName);
            }
        }
    }

    /// <summary>
    /// Get aggregated list of scanners from all protocols
    /// Scanner IDs are prefixed with protocol name: "twain:ScannerName", "wia:ScannerName", etc.
    /// </summary>
    /// <param name="protocolFilter">Optional list of protocols to include (e.g., ["twain", "wia"]). If null/empty, all protocols are included.</param>
    public async Task<List<ScannerInfo>> GetAllScannersAsync(IEnumerable<string>? protocolFilter = null)
    {
        var allScanners = new List<ScannerInfo>();
        var filterSet = protocolFilter?.Select(p => p.ToLowerInvariant()).ToHashSet();
        var hasFilter = filterSet != null && filterSet.Count > 0;

        foreach (var protocol in _protocols.Values)
        {
            // Apply protocol filter if specified
            if (hasFilter && !filterSet!.Contains(protocol.ProtocolName.ToLowerInvariant()))
            {
                _logger.LogDebug("Skipping filtered protocol: {Protocol}", protocol.ProtocolName);
                continue;
            }

            if (!protocol.IsAvailable)
            {
                _logger.LogDebug("Skipping unavailable protocol: {Protocol}", protocol.ProtocolName);
                continue;
            }

            try
            {
                var scanners = await protocol.GetAvailableScannersAsync();
                foreach (var scanner in scanners)
                {
                    // Create a new ScannerInfo with prefixed ID and protocol field
                    var prefixedScanner = new ScannerInfo
                    {
                        Id = $"{protocol.ProtocolName}:{scanner.Id}",
                        Name = scanner.Name,
                        IsDefault = scanner.IsDefault,
                        Capabilities = scanner.Capabilities,
                        Protocol = protocol.ProtocolName
                    };
                    allScanners.Add(prefixedScanner);
                }

                _logger.LogDebug("Protocol {Name} returned {Count} scanner(s)",
                    protocol.ProtocolName, scanners.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting scanners from protocol {Name}", protocol.ProtocolName);
            }
        }

        _logger.LogInformation("Total scanners found: {Count} (filter: {Filter})",
            allScanners.Count, hasFilter ? string.Join(",", filterSet!) : "none");
        return allScanners;
    }

    /// <summary>
    /// Get scanners synchronously (for backward compatibility)
    /// </summary>
    public List<ScannerInfo> GetAvailableScanners()
    {
        return GetAllScannersAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Select a scanner by its prefixed ID (e.g., "wia:HP Scanner")
    /// </summary>
    public bool SelectScanner(string prefixedId)
    {
        var (protocolName, scannerId) = ParseScannerId(prefixedId);

        if (!_protocols.TryGetValue(protocolName, out var protocol))
        {
            _logger.LogWarning("Unknown protocol: {Protocol}", protocolName);
            return false;
        }

        if (!protocol.IsAvailable)
        {
            _logger.LogWarning("Protocol {Protocol} is not available", protocolName);
            return false;
        }

        if (protocol.SelectScanner(scannerId))
        {
            _activeProtocol = protocol;
            _activeScannerId = prefixedId;
            _logger.LogInformation("Selected scanner: {Id} via {Protocol}", scannerId, protocolName);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Get capabilities of currently selected scanner
    /// </summary>
    public ScannerCapabilities? GetCurrentScannerCapabilities()
    {
        if (_activeProtocol == null || _activeScannerId == null)
            return null;

        var (_, scannerId) = ParseScannerId(_activeScannerId);
        return _activeProtocol.GetCapabilities(scannerId);
    }

    /// <summary>
    /// Get capabilities of a scanner by its ID
    /// </summary>
    public ScannerCapabilities? GetCapabilities(string prefixedId)
    {
        var (protocolName, scannerId) = ParseScannerId(prefixedId);

        if (!_protocols.TryGetValue(protocolName, out var protocol))
            return null;

        return protocol.GetCapabilities(scannerId);
    }

    /// <summary>
    /// Apply scan settings
    /// </summary>
    public void ApplySettings(ScanSettings settings)
    {
        if (_activeProtocol == null)
        {
            _logger.LogWarning("ApplySettings called but no scanner/protocol is active");
            return;
        }

        _logger.LogDebug(
            "Applying settings via {Protocol} to {ScannerId}: dpi={Dpi}, pixelType={PixelType}, paperSize={PaperSize}, useAdf={UseAdf}, duplex={Duplex}, showUI={ShowUI}, continuousScan={ContinuousScan}, maxPages={MaxPages}",
            _activeProtocol.ProtocolName,
            _activeScannerId ?? "(none)",
            settings.Dpi,
            settings.PixelType,
            settings.PaperSize,
            settings.UseAdf,
            settings.Duplex,
            settings.ShowUI,
            settings.ContinuousScan,
            settings.MaxPages);

        _activeProtocol?.ApplySettings(settings);
    }

    /// <summary>
    /// Start scanning without session (events-based)
    /// </summary>
    public async Task<bool> StartScanAsync(string requestId)
    {
        if (_activeProtocol == null)
        {
            _logger.LogWarning("No scanner selected");
            return false;
        }

        _currentRequestId = requestId;

        _logger.LogInformation(
            "Starting scan via {Protocol}, Scanner={Scanner}, RequestId={RequestId}",
            _activeProtocol.ProtocolName,
            _activeScannerId ?? "(none)",
            requestId);

        return await _activeProtocol.StartScanAsync(requestId);
    }

    /// <summary>
    /// Start scanning for a specific session
    /// </summary>
    public async Task<bool> StartScanAsync(ClientSession session, string requestId)
    {
        _currentSession = session;
        return await StartScanAsync(requestId);
    }

    /// <summary>
    /// Stop scanning
    /// </summary>
    public void StopScan()
    {
        _activeProtocol?.StopScan();
        _logger.LogInformation("Scan stop requested");
    }

    /// <summary>
    /// Close and release resources
    /// </summary>
    public void Close()
    {
        foreach (var protocol in _protocols.Values)
        {
            try
            {
                protocol.Shutdown();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error shutting down protocol {Name}", protocol.ProtocolName);
            }
        }
    }

    /// <summary>
    /// Shutdown all protocols (alias for Close)
    /// </summary>
    public void Shutdown() => Close();

    private (string protocol, string id) ParseScannerId(string prefixedId)
    {
        var colonIndex = prefixedId.IndexOf(':');
        if (colonIndex <= 0)
        {
            // Legacy ID without prefix - assume TWAIN for backward compatibility
            return ("twain", prefixedId);
        }
        return (prefixedId[..colonIndex], prefixedId[(colonIndex + 1)..]);
    }

    private void OnProtocolImageScanned(object? sender, ProtocolImageScannedEventArgs e)
    {
        _logger.LogDebug("Image received from {Protocol}, page {Page}",
            e.ProtocolName, e.PageNumber);

        // Forward the protocol event directly
        ImageScanned?.Invoke(this, e);
    }

    private void OnProtocolScanCompleted(object? sender, ProtocolScanCompletedEventArgs e)
    {
        _logger.LogDebug("Scan completed from {Protocol}, {Pages} page(s)",
            e.ProtocolName, e.TotalPages);

        // Forward the protocol event directly
        ScanCompleted?.Invoke(this, e);

        _currentSession = null;
        _currentRequestId = null;
    }

    private void OnProtocolScanError(object? sender, ProtocolScanErrorEventArgs e)
    {
        _logger.LogError("Scan error from {Protocol}: {Error}",
            e.ProtocolName, e.ErrorMessage);

        // Forward the protocol event directly
        ScanError?.Invoke(this, e);
    }

    public void Dispose()
    {
        foreach (var protocol in _protocols.Values)
        {
            protocol.ImageScanned -= OnProtocolImageScanned;
            protocol.ScanCompleted -= OnProtocolScanCompleted;
            protocol.ScanError -= OnProtocolScanError;
            protocol.Dispose();
        }
        _protocols.Clear();

        GC.SuppressFinalize(this);
    }
}

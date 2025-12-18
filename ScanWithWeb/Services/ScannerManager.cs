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
    private ScanSettings _currentSettings = new();

    // Events that bubble up from protocols
    public event EventHandler<ProtocolImageScannedEventArgs>? ImageScanned;
    public event EventHandler<ProtocolScanCompletedEventArgs>? ScanCompleted;
    public event EventHandler<ProtocolScanErrorEventArgs>? ScanError;

    public bool IsScanning => _activeProtocol?.IsScanning ?? false;
    public string? CurrentScannerName => _activeScannerId;
    public string? CurrentScannerId => _activeScannerId;
    public ScanSettings CurrentSettings => CloneSettings(_currentSettings);

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
            _currentSettings = new ScanSettings();
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

        _currentSettings = CloneSettings(settings);

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

    public Task<(string? ScannerId, string? Protocol, List<DeviceCapability> Capabilities)> GetDeviceCapabilitiesAsync()
    {
        if (_activeProtocol == null || string.IsNullOrWhiteSpace(_activeScannerId))
        {
            return Task.FromResult<(string?, string?, List<DeviceCapability>)>((null, null, new List<DeviceCapability>()));
        }

        var caps = GetCapabilities(_activeScannerId) ?? new ScannerCapabilities();
        var protocol = _activeProtocol.ProtocolName;
        var settings = _currentSettings;

        var list = new List<DeviceCapability>();

        if (caps.SupportedDpi.Count > 0)
        {
            list.Add(new DeviceCapability
            {
                Key = "dpi",
                Label = "分辨率（DPI）",
                Type = "enum",
                IsReadable = true,
                IsWritable = true,
                Experimental = false,
                SupportedValues = caps.SupportedDpi.Select(d => d.ToString()).ToList(),
                CurrentValue = settings.Dpi
            });
        }

        if (caps.SupportedPixelTypes.Count > 0)
        {
            list.Add(new DeviceCapability
            {
                Key = "pixelType",
                Label = "颜色模式",
                Type = "enum",
                IsReadable = true,
                IsWritable = true,
                Experimental = false,
                SupportedValues = caps.SupportedPixelTypes.ToList(),
                CurrentValue = settings.PixelType
            });
        }

        if (caps.SupportedPaperSizes.Count > 0)
        {
            list.Add(new DeviceCapability
            {
                Key = "paperSize",
                Label = "纸张尺寸",
                Type = "enum",
                IsReadable = true,
                IsWritable = true,
                Experimental = false,
                SupportedValues = caps.SupportedPaperSizes.ToList(),
                CurrentValue = settings.PaperSize
            });
        }

        if (caps.SupportsAdf)
        {
            list.Add(new DeviceCapability
            {
                Key = "useAdf",
                Label = "使用自动进纸器（ADF）",
                Type = "bool",
                IsReadable = true,
                IsWritable = true,
                Experimental = false,
                CurrentValue = settings.UseAdf
            });
        }

        if (caps.SupportsDuplex)
        {
            list.Add(new DeviceCapability
            {
                Key = "duplex",
                Label = "双面扫描",
                Type = "bool",
                IsReadable = true,
                IsWritable = true,
                Experimental = false,
                CurrentValue = settings.Duplex
            });
        }

        // Note: maxPages maps to CapXferCount. Some drivers treat this differently, especially when ShowUI=true.
        list.Add(new DeviceCapability
        {
            Key = "maxPages",
            Label = "最大页数",
            Type = "int",
            IsReadable = true,
            IsWritable = true,
            Experimental = false,
            CurrentValue = settings.MaxPages,
            SupportedValues = new List<string> { "-1", "1", "5", "10", "20", "50" }
        });

        // Experimental / advanced toggles
        if (string.Equals(protocol, "twain", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new DeviceCapability
            {
                Key = "showUI",
                Label = "显示扫描仪驱动界面（TWAIN）",
                Description = "开启后将弹出厂商驱动界面；部分参数可能由驱动接管。",
                Type = "bool",
                IsReadable = true,
                IsWritable = true,
                Experimental = true,
                CurrentValue = settings.ShowUI
            });

            if (_activeProtocol is ScanWithWeb.Services.Protocols.TwainScannerProtocol twainProtocol)
            {
                list.AddRange(twainProtocol.GetAdvancedCapabilities());
            }
        }

        return Task.FromResult<(string?, string?, List<DeviceCapability>)>((_activeScannerId, protocol, list));
    }

    public Task<(string? ScannerId, string? Protocol, List<DeviceSettingResult> Results)> ApplyDeviceSettingsAsync(
        DeviceSettingsPatch patch,
        Dictionary<string, System.Text.Json.JsonElement>? advanced = null)
    {
        if (_activeProtocol == null || string.IsNullOrWhiteSpace(_activeScannerId))
        {
            return Task.FromResult<(string?, string?, List<DeviceSettingResult>)>((null, null, new List<DeviceSettingResult>()));
        }

        var protocol = _activeProtocol.ProtocolName;
        var caps = GetCapabilities(_activeScannerId) ?? new ScannerCapabilities();
        var updated = CloneSettings(_currentSettings);
        var results = new List<DeviceSettingResult>();

        if (IsScanning)
        {
            results.Add(new DeviceSettingResult
            {
                Key = "scan",
                Status = ResponseStatus.Error,
                Message = "正在扫描中，无法应用设置"
            });
            return Task.FromResult<(string?, string?, List<DeviceSettingResult>)>((_activeScannerId, protocol, results));
        }

        void AddError(string key, string message) =>
            results.Add(new DeviceSettingResult { Key = key, Status = ResponseStatus.Error, Message = message });

        void AddSuccess(string key, object? value, string? message = null) =>
            results.Add(new DeviceSettingResult { Key = key, Status = ResponseStatus.Success, AppliedValue = value, Message = message });

        // dpi
        if (patch.Dpi.HasValue)
        {
            var dpi = patch.Dpi.Value;
            if (caps.SupportedDpi.Count > 0 && caps.SupportedDpi.Contains(dpi))
            {
                updated.Dpi = dpi;
                AddSuccess("dpi", dpi);
            }
            else
            {
                AddError("dpi", $"不支持的 DPI：{dpi}");
            }
        }

        // pixelType
        if (!string.IsNullOrWhiteSpace(patch.PixelType))
        {
            var pt = patch.PixelType.Trim();
            var supported = caps.SupportedPixelTypes.FirstOrDefault(x => string.Equals(x, pt, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(supported))
            {
                updated.PixelType = supported;
                AddSuccess("pixelType", supported);
            }
            else
            {
                AddError("pixelType", $"不支持的颜色模式：{pt}");
            }
        }

        // paperSize
        if (!string.IsNullOrWhiteSpace(patch.PaperSize))
        {
            var ps = patch.PaperSize.Trim();
            var supported = caps.SupportedPaperSizes.FirstOrDefault(x => string.Equals(x, ps, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(supported))
            {
                updated.PaperSize = supported;
                AddSuccess("paperSize", supported);
            }
            else
            {
                AddError("paperSize", $"不支持的纸张尺寸：{ps}");
            }
        }

        // duplex
        if (patch.Duplex.HasValue)
        {
            if (caps.SupportsDuplex)
            {
                updated.Duplex = patch.Duplex.Value;
                AddSuccess("duplex", updated.Duplex);
            }
            else
            {
                AddError("duplex", "当前扫描仪不支持双面扫描");
            }
        }

        // useAdf
        if (patch.UseAdf.HasValue)
        {
            if (caps.SupportsAdf)
            {
                updated.UseAdf = patch.UseAdf.Value;
                AddSuccess("useAdf", updated.UseAdf);
            }
            else
            {
                AddError("useAdf", "当前扫描仪不支持自动进纸器（ADF）");
            }
        }

        // maxPages
        if (patch.MaxPages.HasValue)
        {
            var mp = patch.MaxPages.Value;
            if (mp == -1 || mp > 0)
            {
                updated.MaxPages = mp;
                AddSuccess("maxPages", mp);
            }
            else
            {
                AddError("maxPages", "maxPages 必须为 -1（不限）或大于 0");
            }
        }

        // showUI (experimental, TWAIN only)
        if (patch.ShowUI.HasValue)
        {
            if (string.Equals(protocol, "twain", StringComparison.OrdinalIgnoreCase))
            {
                updated.ShowUI = patch.ShowUI.Value;
                AddSuccess("showUI", updated.ShowUI, "实验项：不同驱动行为可能不同");
            }
            else
            {
                AddError("showUI", "showUI 仅适用于 TWAIN 扫描仪");
            }
        }

        // Advanced experimental settings (e.g., twain.duplexMode)
        if (advanced != null && advanced.Count > 0)
        {
            if (string.Equals(protocol, "twain", StringComparison.OrdinalIgnoreCase) &&
                _activeProtocol is ScanWithWeb.Services.Protocols.TwainScannerProtocol twainProtocol)
            {
                ApplyTwainAdvancedSettings(twainProtocol, advanced, results);
            }
            else
            {
                foreach (var key in advanced.Keys)
                {
                    AddError(key, "当前协议不支持该高级能力");
                }
            }
        }

        // Apply if we have at least one success.
        if (results.Any(r => r.Status == ResponseStatus.Success))
        {
            ApplySettings(updated);
        }

        return Task.FromResult<(string?, string?, List<DeviceSettingResult>)>((_activeScannerId, protocol, results));
    }

    private void ApplyTwainAdvancedSettings(
        ScanWithWeb.Services.Protocols.TwainScannerProtocol protocol,
        Dictionary<string, System.Text.Json.JsonElement> advanced,
        List<DeviceSettingResult> results)
    {
        foreach (var (key, value) in advanced)
        {
            try
            {
                var applied = protocol.TryApplyAdvancedSetting(key, value, out var message);
                results.Add(new DeviceSettingResult
                {
                    Key = key,
                    Status = applied ? ResponseStatus.Success : ResponseStatus.Error,
                    Message = message,
                    AppliedValue = applied ? value.ToString() : null
                });
            }
            catch (Exception ex)
            {
                results.Add(new DeviceSettingResult
                {
                    Key = key,
                    Status = ResponseStatus.Error,
                    Message = $"设置失败：{ex.Message}"
                });
            }
        }
    }

    private static ScanSettings CloneSettings(ScanSettings settings)
    {
        return new ScanSettings
        {
            Dpi = settings.Dpi,
            PixelType = settings.PixelType,
            PaperSize = settings.PaperSize,
            Duplex = settings.Duplex,
            ShowUI = settings.ShowUI,
            Source = settings.Source,
            UseAdf = settings.UseAdf,
            MaxPages = settings.MaxPages,
            ContinuousScan = settings.ContinuousScan,
            Rotation = settings.Rotation,
            Protocols = settings.Protocols == null ? null : settings.Protocols.ToList()
        };
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

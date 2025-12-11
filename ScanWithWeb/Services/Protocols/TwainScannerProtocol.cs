using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Models;
using NTwain;
using NTwain.Data;

namespace ScanWithWeb.Services.Protocols;

/// <summary>
/// TWAIN scanner protocol implementation
/// Refactored from original ScannerService to implement IScannerProtocol
/// </summary>
public class TwainScannerProtocol : BaseScannerProtocol
{
    private TwainSession? _twain;
    private IntPtr _windowHandle;
    private bool _stopScan;
    private int _currentPageNumber;
    private string? _currentRequestId;

    public override string ProtocolName => "twain";

    public TwainScannerProtocol(ILogger<TwainScannerProtocol> logger) : base(logger)
    {
    }

    public override bool Initialize(IntPtr windowHandle)
    {
        Logger.LogInformation("[TWAIN] Initializing with WindowHandle: {Handle}", windowHandle);
        _windowHandle = windowHandle;

        try
        {
            // Create TWIdentity manually to avoid issues with single-file publish
            var appId = TWIdentity.Create(
                DataGroups.Image,
                new Version(2, 0, 9),
                "ScanWithWeb Team",
                "ScanWithWeb",
                "ScanWithWeb Service",
                "ScanWithWeb Scanner Service");

            Logger.LogDebug("[TWAIN] TWIdentity created: {AppId}", appId.ProductName);

            _twain = new TwainSession(appId);
            Logger.LogDebug("[TWAIN] TwainSession created, initial state: {State}", _twain.State);

            _twain.StateChanged += (s, e) =>
            {
                Logger.LogDebug("[TWAIN] State changed to {State}", _twain.State);
            };

            _twain.TransferError += (s, e) =>
            {
                Logger.LogError("[TWAIN] Transfer error occurred");
                if (_currentRequestId != null)
                {
                    RaiseScanError(_currentRequestId, "Transfer error occurred");
                }
            };

            _twain.DataTransferred += OnDataTransferred;

            _twain.SourceDisabled += (s, e) =>
            {
                Logger.LogInformation("[TWAIN] Scan source disabled, scan complete");
                CompleteScan();
            };

            _twain.TransferReady += (s, e) =>
            {
                Logger.LogDebug("[TWAIN] TransferReady event, CancelAll: {Cancel}", _stopScan);
                e.CancelAll = _stopScan;
            };

            _twain.SynchronizationContext = SynchronizationContext.Current;
            Logger.LogDebug("[TWAIN] SynchronizationContext set: {HasContext}", SynchronizationContext.Current != null);

            if (_twain.State < 3)
            {
                Logger.LogDebug("[TWAIN] Opening TWAIN session...");
                _twain.Open();
                Logger.LogInformation("[TWAIN] Session opened successfully, state: {State}", _twain.State);
            }

            IsAvailable = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            Logger.LogError(ex, "[TWAIN] DLL not found - TWAIN Data Source Manager may not be installed");
            IsAvailable = false;
            InitializationError = "TWAIN not installed. Please install a TWAIN-compatible scanner driver or the TWAIN Data Source Manager.";
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[TWAIN] Failed to initialize session");
            IsAvailable = false;
            InitializationError = $"TWAIN initialization failed: {ex.Message}";
            return false;
        }
    }

    private void OnDataTransferred(object? sender, DataTransferredEventArgs e)
    {
        Logger.LogInformation("[TWAIN] Image data received - Page {Page}", _currentPageNumber + 1);
        _currentPageNumber++;

        try
        {
            if (e.NativeData != IntPtr.Zero)
            {
                var stream = e.GetNativeImageStream();
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var imageData = ms.ToArray();

                    // Get image dimensions
                    ms.Position = 0;
                    using var img = Image.FromStream(ms);
                    var metadata = new ImageMetadata
                    {
                        Width = img.Width,
                        Height = img.Height,
                        Format = "bmp",
                        SizeBytes = imageData.Length,
                        Dpi = (int)img.HorizontalResolution
                    };

                    if (_currentRequestId != null)
                    {
                        RaiseImageScanned(_currentRequestId, imageData, metadata, _currentPageNumber);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(e.FileDataPath))
            {
                var imageData = File.ReadAllBytes(e.FileDataPath);
                using var img = Image.FromFile(e.FileDataPath);
                var metadata = new ImageMetadata
                {
                    Width = img.Width,
                    Height = img.Height,
                    Format = Path.GetExtension(e.FileDataPath).TrimStart('.'),
                    SizeBytes = imageData.Length,
                    Dpi = (int)img.HorizontalResolution
                };

                if (_currentRequestId != null)
                {
                    RaiseImageScanned(_currentRequestId, imageData, metadata, _currentPageNumber);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[TWAIN] Error processing scanned image");
            if (_currentRequestId != null)
            {
                RaiseScanError(_currentRequestId, ex.Message);
            }
        }
    }

    public override Task<List<ScannerInfo>> GetAvailableScannersAsync()
    {
        return Task.FromResult(GetAvailableScanners());
    }

    private List<ScannerInfo> GetAvailableScanners()
    {
        var scanners = new List<ScannerInfo>();
        var bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";

        if (_twain == null || _twain.State < 3)
        {
            Logger.LogWarning("[TWAIN] Not initialized (State: {State}, {Bits})",
                _twain?.State ?? 0, bits);
            return scanners;
        }

        Logger.LogDebug("[TWAIN] Enumerating sources ({Bits} mode, State: {State})...", bits, _twain.State);

        foreach (var source in _twain)
        {
            Logger.LogDebug("[TWAIN] Found scanner: {Name}", source.Name);
            scanners.Add(new ScannerInfo
            {
                Name = source.Name,
                Id = source.Name,
                IsDefault = _twain.CurrentSource?.Name == source.Name
            });
        }

        if (scanners.Count == 0)
        {
            Logger.LogWarning("[TWAIN] No scanners found in {Bits} mode. " +
                "If your scanner has only 32-bit drivers, try using the 32-bit version of ScanWithWeb.", bits);
        }
        else
        {
            Logger.LogInformation("[TWAIN] Found {Count} scanner(s) in {Bits} mode", scanners.Count, bits);
        }

        return scanners;
    }

    public override bool SelectScanner(string scannerId)
    {
        if (_twain == null || _twain.State < 3)
            return false;

        // Close current source if open
        if (_twain.State == 4)
        {
            _twain.CurrentSource?.Close();
        }

        var source = _twain.FirstOrDefault(s => s.Name == scannerId);
        if (source == null)
        {
            Logger.LogWarning("[TWAIN] Scanner not found: {Name}", scannerId);
            return false;
        }

        if (source.Open() == ReturnCode.Success)
        {
            CurrentScannerId = scannerId;
            Logger.LogInformation("[TWAIN] Scanner selected: {Name}", scannerId);
            return true;
        }

        return false;
    }

    public override ScannerCapabilities? GetCapabilities(string scannerId)
    {
        if (_twain?.CurrentSource == null)
            return null;

        var caps = new ScannerCapabilities();
        var src = _twain.CurrentSource;

        try
        {
            // DPI values
            if (src.Capabilities.ICapXResolution.IsSupported)
            {
                caps.SupportedDpi = src.Capabilities.ICapXResolution
                    .GetValues()
                    .Where(dpi => dpi % 50 == 0)
                    .Select(dpi => (int)dpi.Whole)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();
            }

            // Pixel types (color modes)
            if (src.Capabilities.ICapPixelType.IsSupported)
            {
                caps.SupportedPixelTypes = src.Capabilities.ICapPixelType
                    .GetValues()
                    .Select(pt => pt.ToString())
                    .ToList();
            }

            // Paper sizes
            if (src.Capabilities.ICapSupportedSizes.IsSupported)
            {
                caps.SupportedPaperSizes = src.Capabilities.ICapSupportedSizes
                    .GetValues()
                    .Select(sz => sz.ToString())
                    .ToList();
            }

            // Duplex support
            caps.SupportsDuplex = src.Capabilities.CapDuplexEnabled.IsSupported;

            // ADF support
            caps.SupportsAdf = src.Capabilities.CapFeederEnabled.IsSupported;

            // UI control support
            caps.SupportsUIControl = src.Capabilities.CapUIControllable.IsSupported;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[TWAIN] Error getting scanner capabilities");
        }

        return caps;
    }

    public override void ApplySettings(ScanSettings settings)
    {
        base.ApplySettings(settings);

        if (_twain?.CurrentSource == null)
            return;

        var src = _twain.CurrentSource;

        try
        {
            // Set DPI
            if (settings.Dpi > 0 && src.Capabilities.ICapXResolution.IsSupported)
            {
                src.Capabilities.ICapXResolution.SetValue(new TWFix32 { Whole = (short)settings.Dpi });
                src.Capabilities.ICapYResolution.SetValue(new TWFix32 { Whole = (short)settings.Dpi });
                Logger.LogDebug("[TWAIN] DPI set to {Dpi}", settings.Dpi);
            }

            // Set pixel type (color mode)
            if (!string.IsNullOrEmpty(settings.PixelType) && src.Capabilities.ICapPixelType.IsSupported)
            {
                if (Enum.TryParse<PixelType>(settings.PixelType, true, out var pixelType))
                {
                    src.Capabilities.ICapPixelType.SetValue(pixelType);
                    Logger.LogDebug("[TWAIN] Pixel type set to {Type}", pixelType);
                }
            }

            // Set paper size
            if (!string.IsNullOrEmpty(settings.PaperSize) && src.Capabilities.ICapSupportedSizes.IsSupported)
            {
                if (Enum.TryParse<SupportedSize>(settings.PaperSize, true, out var paperSize))
                {
                    src.Capabilities.ICapSupportedSizes.SetValue(paperSize);
                    Logger.LogDebug("[TWAIN] Paper size set to {Size}", paperSize);
                }
            }

            // Set duplex
            if (src.Capabilities.CapDuplexEnabled.IsSupported)
            {
                src.Capabilities.CapDuplexEnabled.SetValue(settings.Duplex ? BoolType.True : BoolType.False);
                Logger.LogDebug("[TWAIN] Duplex set to {Duplex}", settings.Duplex);
            }

            // Enable/disable ADF
            if (src.Capabilities.CapFeederEnabled.IsSupported)
            {
                src.Capabilities.CapFeederEnabled.SetValue(settings.UseAdf ? BoolType.True : BoolType.False);
                Logger.LogDebug("[TWAIN] ADF enabled: {UseAdf}", settings.UseAdf);

                if (settings.UseAdf && src.Capabilities.CapAutoFeed.IsSupported)
                {
                    src.Capabilities.CapAutoFeed.SetValue(BoolType.True);
                }
            }

            // Set maximum pages
            if (src.Capabilities.CapXferCount.IsSupported)
            {
                src.Capabilities.CapXferCount.SetValue(settings.MaxPages);
                Logger.LogDebug("[TWAIN] Max pages set to {MaxPages}", settings.MaxPages);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[TWAIN] Error applying scan settings");
        }
    }

    public override async Task<bool> StartScanAsync(string requestId)
    {
        Logger.LogDebug("[TWAIN] StartScanAsync called - RequestId: {RequestId}", requestId);

        if (_twain?.CurrentSource == null)
        {
            Logger.LogWarning("[TWAIN] No scanner selected");
            return false;
        }

        if (IsScanning)
        {
            Logger.LogWarning("[TWAIN] Scan already in progress");
            return false;
        }

        _currentRequestId = requestId;
        _currentPageNumber = 0;
        _stopScan = false;
        IsScanning = true;

        try
        {
            var src = _twain.CurrentSource;

            // Check if scanner supports UI-less operation
            bool supportsNoUI = false;
            try
            {
                if (src.Capabilities.CapUIControllable.IsSupported)
                {
                    var canDisableUI = src.Capabilities.CapUIControllable.GetCurrent();
                    supportsNoUI = canDisableUI == BoolType.True;
                    Logger.LogDebug("[TWAIN] Scanner UI controllable: {Controllable}", supportsNoUI);
                }
            }
            catch (Exception capEx)
            {
                Logger.LogDebug("[TWAIN] Could not check CapUIControllable: {Message}", capEx.Message);
            }

            ReturnCode result;

            // Try NoUI mode first if supported
            if (supportsNoUI)
            {
                Logger.LogDebug("[TWAIN] Trying NoUI mode first");
                try
                {
                    result = src.Enable(SourceEnableMode.NoUI, false, _windowHandle);
                    if (result == ReturnCode.Success)
                    {
                        Logger.LogInformation("[TWAIN] NoUI mode scan started successfully");
                        return true;
                    }
                }
                catch (Exception noUIEx)
                {
                    Logger.LogWarning(noUIEx, "[TWAIN] NoUI mode failed, falling back to ShowUI mode");
                }
            }

            // Use ShowUI mode with modal=false
            Logger.LogDebug("[TWAIN] Enabling source with ShowUI mode (non-modal)");
            Logger.LogInformation("[TWAIN] Scanner UI will appear - please click 'Scan' in the scanner dialog");

            try
            {
                result = src.Enable(SourceEnableMode.ShowUI, false, _windowHandle);
            }
            catch (Exception enableEx)
            {
                Logger.LogError(enableEx, "[TWAIN] ShowUI non-modal mode failed, trying modal mode");

                try
                {
                    result = src.Enable(SourceEnableMode.ShowUI, true, _windowHandle);
                }
                catch (Exception fallbackEx)
                {
                    Logger.LogError(fallbackEx, "[TWAIN] All enable modes failed");
                    throw;
                }
            }

            if (result == ReturnCode.Success)
            {
                Logger.LogInformation("[TWAIN] Scan started successfully");
                return true;
            }
            else
            {
                Logger.LogWarning("[TWAIN] Failed to start scan: {Result}", result);
                IsScanning = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[TWAIN] Error starting scan");
            IsScanning = false;
            return false;
        }
    }

    public override void StopScan()
    {
        _stopScan = true;
        Logger.LogInformation("[TWAIN] Scan stop requested");
    }

    private void CompleteScan()
    {
        if (_currentRequestId != null)
        {
            RaiseScanCompleted(_currentRequestId, _currentPageNumber);
        }

        IsScanning = false;
        _currentRequestId = null;
        _currentPageNumber = 0;
    }

    public override void Shutdown()
    {
        if (_twain != null)
        {
            if (_twain.State == 4)
            {
                _twain.CurrentSource?.Close();
            }
            if (_twain.State == 3)
            {
                _twain.Close();
            }
            if (_twain.State > 2)
            {
                _twain.ForceStepDown(2);
            }
            Logger.LogInformation("[TWAIN] Session closed");
        }
    }
}

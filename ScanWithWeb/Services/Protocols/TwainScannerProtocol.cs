using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Models;
using NTwain;
using NTwain.Data;
using System.Text.Json;

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
    private readonly object _scanStateLock = new();
    private bool _scanTerminated;
    private Duplex? _duplexModeOverride;
    private bool? _autoFeedOverride;

    // Cache DataSource objects by ID for later selection
    // This helps handle scanners with empty names that get populated later
    private readonly Dictionary<string, DataSource> _sourceCache = new();

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
                new Version(3, 0, 7),
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
                HandleTransferError(e.Exception);
            };

            _twain.DataTransferred += OnDataTransferred;

            _twain.SourceDisabled += (s, e) =>
            {
                Logger.LogInformation("[TWAIN] Scan source disabled, scan complete");
                HandleSourceDisabled();
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
                Logger.LogDebug("[TWAIN] DataTransferred via NativeData (RequestId={RequestId}, Page={Page})",
                    _currentRequestId ?? "(none)",
                    _currentPageNumber);
                var stream = e.GetNativeImageStream();
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var rawImageData = ms.ToArray();

                    // Get image dimensions before compression
                    ms.Position = 0;
                    using var img = Image.FromStream(ms);
                    var width = img.Width;
                    var height = img.Height;
                    var dpi = (int)img.HorizontalResolution;

                    // Compress large images to JPEG to avoid WebSocket message size issues
                    var format = "bmp";
                    var imageData = ImageCompressor.CompressIfNeeded(rawImageData, ref format, Logger);

                    var metadata = new ImageMetadata
                    {
                        Width = width,
                        Height = height,
                        Format = format,
                        SizeBytes = imageData.Length,
                        Dpi = dpi
                    };

                    Logger.LogDebug("[TWAIN] Image processed: {Width}x{Height}, {Format}, {Size:N0} bytes",
                        width, height, format, imageData.Length);

                    if (_currentRequestId != null)
                    {
                        RaiseImageScanned(_currentRequestId, imageData, metadata, _currentPageNumber);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(e.FileDataPath))
            {
                Logger.LogDebug("[TWAIN] DataTransferred via FileDataPath={Path} (RequestId={RequestId}, Page={Page})",
                    e.FileDataPath,
                    _currentRequestId ?? "(none)",
                    _currentPageNumber);
                var rawImageData = File.ReadAllBytes(e.FileDataPath);
                using var img = Image.FromFile(e.FileDataPath);
                var width = img.Width;
                var height = img.Height;
                var dpi = (int)img.HorizontalResolution;

                // Compress large images to JPEG
                var format = Path.GetExtension(e.FileDataPath).TrimStart('.');
                var imageData = ImageCompressor.CompressIfNeeded(rawImageData, ref format, Logger);

                var metadata = new ImageMetadata
                {
                    Width = width,
                    Height = height,
                    Format = format,
                    SizeBytes = imageData.Length,
                    Dpi = dpi
                };

                Logger.LogDebug("[TWAIN] File image processed: {Width}x{Height}, {Format}, {Size:N0} bytes",
                    width, height, format, imageData.Length);

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

        // Clear the source cache before re-enumerating
        _sourceCache.Clear();

        Logger.LogDebug("[TWAIN] Enumerating sources ({Bits} mode, State: {State})...", bits, _twain.State);

        int index = 0;
        foreach (var source in _twain)
        {
            index++;

            // Generate a unique ID for this scanner
            // Use the name if available, otherwise use index-based ID
            string scannerId;
            string scannerName;

            if (!string.IsNullOrWhiteSpace(source.Name))
            {
                scannerId = source.Name;
                scannerName = source.Name;
            }
            else
            {
                // Scanner name is empty (might be loaded asynchronously by driver)
                // Use index-based ID and a placeholder name
                scannerId = $"TWAIN_Scanner_{index}";
                scannerName = $"TWAIN Scanner #{index}";
                Logger.LogDebug("[TWAIN] Scanner at index {Index} has empty name, using fallback ID: {Id}", index, scannerId);
            }

            // Cache the DataSource object for later selection
            _sourceCache[scannerId] = source;

            Logger.LogDebug("[TWAIN] Found scanner: {Name} (ID: {Id})", scannerName, scannerId);
            scanners.Add(new ScannerInfo
            {
                Name = scannerName,
                Id = scannerId,
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

        // First try to find the source in the cache
        DataSource? source = null;
        if (_sourceCache.TryGetValue(scannerId, out var cachedSource))
        {
            source = cachedSource;
            Logger.LogDebug("[TWAIN] Found scanner in cache: {Id}", scannerId);
        }
        else
        {
            // Fallback: search by name in case cache was cleared
            source = _twain.FirstOrDefault(s => s.Name == scannerId);
        }

        if (source == null)
        {
            Logger.LogWarning("[TWAIN] Scanner not found: {Id}", scannerId);
            return false;
        }

        if (source.Open() == ReturnCode.Success)
        {
            // After opening, the name might now be available
            var actualName = !string.IsNullOrWhiteSpace(source.Name) ? source.Name : scannerId;
            CurrentScannerId = scannerId;
            Logger.LogInformation("[TWAIN] Scanner selected: {Name} (ID: {Id})", actualName, scannerId);
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
        {
            Logger.LogWarning("[TWAIN] ApplySettings called but no current source is selected");
            return;
        }

        var src = _twain.CurrentSource;

        try
        {
            Logger.LogInformation(
                "[TWAIN] Applying settings: dpi={Dpi}, pixelType={PixelType}, paperSize={PaperSize}, useAdf={UseAdf}, duplex={Duplex}, showUI={ShowUI}, continuousScan={ContinuousScan}, maxPages={MaxPages}",
                settings.Dpi,
                settings.PixelType,
                settings.PaperSize,
                settings.UseAdf,
                settings.Duplex,
                settings.ShowUI,
                settings.ContinuousScan,
                settings.MaxPages);

            // Set DPI
            if (settings.Dpi > 0 && src.Capabilities.ICapXResolution.IsSupported)
            {
                var xrc = src.Capabilities.ICapXResolution.SetValue(new TWFix32 { Whole = (short)settings.Dpi });
                var yrc = src.Capabilities.ICapYResolution.SetValue(new TWFix32 { Whole = (short)settings.Dpi });
                Logger.LogInformation("[TWAIN] DPI set to {Dpi}, X={Xrc}, Y={Yrc}", settings.Dpi, xrc, yrc);
            }

            // Set pixel type (color mode)
            if (!string.IsNullOrEmpty(settings.PixelType) && src.Capabilities.ICapPixelType.IsSupported)
            {
                if (Enum.TryParse<PixelType>(settings.PixelType, true, out var pixelType))
                {
                    var rc = src.Capabilities.ICapPixelType.SetValue(pixelType);
                    Logger.LogInformation("[TWAIN] Pixel type set to {Type}, Result: {Result}", pixelType, rc);
                }
                else
                {
                    Logger.LogWarning("[TWAIN] Failed to parse pixelType: {PixelType}", settings.PixelType);
                }
            }

            // Set paper size
            if (!string.IsNullOrEmpty(settings.PaperSize) && src.Capabilities.ICapSupportedSizes.IsSupported)
            {
                // Get supported sizes for debugging
                var supportedSizes = src.Capabilities.ICapSupportedSizes.GetValues().ToList();
                Logger.LogDebug("[TWAIN] Requested paper size: {RequestedSize}, Supported sizes: {Supported}",
                    settings.PaperSize, string.Join(", ", supportedSizes));

                if (Enum.TryParse<SupportedSize>(settings.PaperSize, true, out var paperSize))
                {
                    // Check if this size is actually supported by the scanner
                    if (supportedSizes.Contains(paperSize))
                    {
                        var result = src.Capabilities.ICapSupportedSizes.SetValue(paperSize);
                        Logger.LogInformation("[TWAIN] Paper size set to {Size}, Result: {Result}", paperSize, result);
                    }
                    else
                    {
                        Logger.LogWarning("[TWAIN] Paper size {Size} parsed but not supported by scanner. Supported: {Supported}",
                            paperSize, string.Join(", ", supportedSizes));
                    }
                }
                else
                {
                    Logger.LogWarning("[TWAIN] Failed to parse paper size: {Size}. Valid enum values: {Values}",
                        settings.PaperSize, string.Join(", ", Enum.GetNames<SupportedSize>().Take(20)));
                }
            }

            // Set duplex
            if (src.Capabilities.CapDuplexEnabled.IsSupported)
            {
                var rc = src.Capabilities.CapDuplexEnabled.SetValue(settings.Duplex ? BoolType.True : BoolType.False);
                Logger.LogInformation("[TWAIN] Duplex enabled set to {Duplex}, Result: {Result}", settings.Duplex, rc);
            }

            // Prefer explicit duplex mode selection when available.
            // Some scanners/drivers default to TwoPass duplex, which yields output order:
            // 1F 2F 3F 1B 2B 3B. Selecting OnePass typically yields interleaved sides.
            if (src.Capabilities.CapDuplex.IsSupported)
            {
                var supported = src.Capabilities.CapDuplex.GetValues().ToList();
                var desired = _duplexModeOverride ?? (settings.Duplex ? Duplex.OnePass : Duplex.None);

                Logger.LogDebug("[TWAIN] Duplex mode requested: {Desired}, Supported: {Supported}",
                    desired, string.Join(", ", supported));

                if (supported.Contains(desired))
                {
                    if (src.Capabilities.CapDuplex is ICapWrapper<Duplex> writableDuplex && !src.Capabilities.CapDuplex.IsReadOnly)
                    {
                        var rc = writableDuplex.SetValue(desired);
                        Logger.LogInformation("[TWAIN] Duplex mode set to {Mode}, Result: {Result}", desired, rc);
                    }
                    else
                    {
                        Logger.LogWarning("[TWAIN] Duplex capability is read-only; cannot set duplex mode to {Mode}", desired);
                    }
                }
                else if (settings.Duplex && supported.Contains(Duplex.TwoPass))
                {
                    Logger.LogWarning("[TWAIN] OnePass duplex not supported; falling back to TwoPass duplex");
                    if (src.Capabilities.CapDuplex is ICapWrapper<Duplex> writableDuplex && !src.Capabilities.CapDuplex.IsReadOnly)
                    {
                        var rc = writableDuplex.SetValue(Duplex.TwoPass);
                        Logger.LogInformation("[TWAIN] Duplex mode set to {Mode}, Result: {Result}", Duplex.TwoPass, rc);
                    }
                    else
                    {
                        Logger.LogWarning("[TWAIN] Duplex capability is read-only; cannot set duplex mode to {Mode}", Duplex.TwoPass);
                    }
                }
            }

            // Enable/disable ADF
            if (src.Capabilities.CapFeederEnabled.IsSupported)
            {
                var rc = src.Capabilities.CapFeederEnabled.SetValue(settings.UseAdf ? BoolType.True : BoolType.False);
                Logger.LogInformation("[TWAIN] ADF enabled set to {UseAdf}, Result: {Result}", settings.UseAdf, rc);

                if (settings.UseAdf && src.Capabilities.CapAutoFeed.IsSupported)
                {
                    var desiredAutoFeed = _autoFeedOverride ?? true;
                    var autoRc = src.Capabilities.CapAutoFeed.SetValue(desiredAutoFeed ? BoolType.True : BoolType.False);
                    Logger.LogInformation("[TWAIN] AutoFeed set to {AutoFeed}, Result: {Result}", desiredAutoFeed, autoRc);
                }
            }

            // Set maximum pages
            if (src.Capabilities.CapXferCount.IsSupported)
            {
                // When showing the vendor TWAIN UI, some drivers (including Canon) can lock their UI controls
                // based on the app-set CapXferCount (often showing single-page only). In that mode, let the
                // scanner UI control transfer count unless the caller explicitly wants to limit it.
                //
                // The test page "continuous scan" mode intentionally sets MaxPages=1 for each continuation
                // request, so we still honor MaxPages when ShowUI=false (headless mode).
                if (settings.ShowUI)
                {
                    Logger.LogInformation("[TWAIN] Skipping CapXferCount because ShowUI=true (MaxPages={MaxPages})", settings.MaxPages);
                }
                else
                {
                    var rc = src.Capabilities.CapXferCount.SetValue(settings.MaxPages);
                    Logger.LogInformation("[TWAIN] Max pages set to {MaxPages}, Result: {Result}", settings.MaxPages, rc);
                }
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
        lock (_scanStateLock)
        {
            _scanTerminated = false;
        }

        try
        {
            var src = _twain.CurrentSource;
            var showUI = CurrentSettings?.ShowUI == true;

            Logger.LogInformation(
                "[TWAIN] Starting scan: RequestId={RequestId}, Scanner={Scanner}, ShowUI={ShowUI}, TwainState={State}, WindowHandle={Handle}",
                requestId,
                src.Name,
                showUI,
                _twain.State,
                _windowHandle);

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

            // When ShowUI is false (service/headless usage), do not pop scanner UI.
            // If the scanner/driver cannot scan without UI, the caller must request UI explicitly.
            if (!showUI)
            {
                Logger.LogDebug("[TWAIN] Starting scan with ShowUI=false (headless mode). SupportsNoUI={SupportsNoUI}", supportsNoUI);
                try
                {
                    result = src.Enable(SourceEnableMode.NoUI, false, _windowHandle);
                    if (result == ReturnCode.Success)
                    {
                        Logger.LogInformation("[TWAIN] NoUI mode scan started successfully");
                        return true;
                    }

                    Logger.LogWarning("[TWAIN] NoUI mode returned {Result}. To allow scanner UI, set settings.showUI=true.", result);
                    IsScanning = false;
                    return false;
                }
                catch (Exception noUiEx)
                {
                    Logger.LogWarning(noUiEx, "[TWAIN] NoUI mode threw an exception. To allow scanner UI, set settings.showUI=true.");
                    IsScanning = false;
                    return false;
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
        Logger.LogInformation(
            "[TWAIN] Scan stop requested (RequestId={RequestId}, PagesSoFar={Pages}, TwainState={State})",
            _currentRequestId ?? "(none)",
            _currentPageNumber,
            _twain?.State ?? 0);

        // Ensure we don't emit a "completed" event after a stop.
        // This also allows subsequent scans without needing to restart the service.
        lock (_scanStateLock)
        {
            _scanTerminated = true;
        }

        _currentRequestId = null;
        IsScanning = false;

        TryDisableAndStepDown("StopScan");
    }

    private void CompleteScan()
    {
        Logger.LogInformation("[TWAIN] Scan completed - Total pages scanned: {PageCount}", _currentPageNumber);

        if (_currentPageNumber == 0)
        {
            Logger.LogWarning("[TWAIN] No pages were scanned. This may indicate a paper size mismatch or empty feeder.");
        }

        if (_currentRequestId != null)
        {
            RaiseScanCompleted(_currentRequestId, _currentPageNumber);
        }

        IsScanning = false;
        _currentRequestId = null;
        _currentPageNumber = 0;
    }

    private void HandleSourceDisabled()
    {
        // SourceDisabled is raised for normal completion, but it may also fire after errors or manual stop.
        // Ensure we only report one terminal outcome for a given scan request.
        bool terminated;
        lock (_scanStateLock)
        {
            terminated = _scanTerminated;
        }

        Logger.LogDebug(
            "[TWAIN] SourceDisabled received (RequestId={RequestId}, Pages={Pages}, Terminated={Terminated}, TwainState={State})",
            _currentRequestId ?? "(none)",
            _currentPageNumber,
            terminated,
            _twain?.State ?? 0);

        if (terminated)
        {
            IsScanning = false;
            _currentRequestId = null;
            _currentPageNumber = 0;
            return;
        }

        CompleteScan();
    }

    private void HandleTransferError(Exception? ex)
    {
        var (conditionCode, errorMessage) = GetTwainErrorInfo(ex);

        // TransferError can fire repeatedly (e.g., paper jam loop / feeder empty polling).
        // Treat some conditions as a normal end-of-job once at least one page was scanned.
        var code = conditionCode ?? "Unknown";
        var isFeederEmpty = string.Equals(code, "FeederEmpty", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "NoMedia", StringComparison.OrdinalIgnoreCase);

        bool firstTerminal;
        lock (_scanStateLock)
        {
            firstTerminal = !_scanTerminated;
            _scanTerminated = true;
        }

        if (!firstTerminal)
        {
            Logger.LogDebug("[TWAIN] Suppressing repeated transfer error: {Code} - {Message}", code, errorMessage);
            return;
        }

        Logger.LogWarning(
            "[TWAIN] TransferError received (RequestId={RequestId}, Code={Code}, PagesSoFar={Pages}, TwainState={State}, Exception={ExType})",
            _currentRequestId ?? "(none)",
            code,
            _currentPageNumber,
            _twain?.State ?? 0,
            ex?.GetType().FullName ?? "(none)");

        // Stop the device from continuously raising the same condition.
        TryDisableAndStepDown($"TransferError:{code}");

        if (isFeederEmpty && _currentPageNumber > 0)
        {
            Logger.LogInformation("[TWAIN] Feeder empty after {Pages} page(s). Treating as scan completion.", _currentPageNumber);
            CompleteScan();
            return;
        }

        Logger.LogError("[TWAIN] Transfer error: {Code} - {Error}", code, errorMessage);

        if (_currentRequestId != null)
        {
            RaiseScanError(_currentRequestId, errorMessage);
        }

        _currentRequestId = null;
        IsScanning = false;
    }

    private static (string? ConditionCode, string ErrorMessage) GetTwainErrorInfo(Exception? ex)
    {
        var conditionCode = ex?.Data["ConditionCode"]?.ToString();

        if (!string.IsNullOrWhiteSpace(conditionCode))
        {
            var message = conditionCode switch
            {
                "PaperJam" => "Paper jam detected. Please clear the jam and try again.",
                "PaperDoubleFeed" => "Multiple pages fed. Please check the document feeder.",
                "CheckDeviceOnline" => "Scanner is offline. Please check the connection.",
                "NoMedia" => "No paper in the feeder. Please load paper and try again.",
                "FeederEmpty" => "Document feeder is empty.",
                "OperationError" => "Scanner operation error. Please try again.",
                _ => $"Scanner error: {conditionCode}"
            };
            return (conditionCode, message);
        }

        if (ex != null && !string.IsNullOrWhiteSpace(ex.Message))
        {
            return (null, $"Scan error: {ex.Message}");
        }

        return (null, "Scan transfer error");
    }

    private void TryDisableAndStepDown(string reason)
    {
        try
        {
            if (_twain != null && _twain.State > 4)
            {
                Logger.LogDebug("[TWAIN] ForceStepDown to source-open state due to: {Reason} (State={State})", reason, _twain.State);
                _twain.ForceStepDown(4);
                Logger.LogDebug("[TWAIN] ForceStepDown(4) complete (State={State})", _twain.State);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[TWAIN] ForceStepDown(4) failed (Reason={Reason})", reason);
            try
            {
                if (_twain != null && _twain.State > 3)
                {
                    _twain.ForceStepDown(3);
                    Logger.LogDebug("[TWAIN] ForceStepDown(3) complete (State={State})", _twain.State);
                }
            }
            catch (Exception ex2)
            {
                Logger.LogDebug(ex2, "[TWAIN] ForceStepDown(3) failed (Reason={Reason})", reason);
            }
        }
    }

    public List<DeviceCapability> GetAdvancedCapabilities()
    {
        var list = new List<DeviceCapability>();
        if (_twain?.CurrentSource == null)
        {
            return list;
        }

        var src = _twain.CurrentSource;

        // Duplex mode (TWAIN only)
        try
        {
            if (src.Capabilities.CapDuplex.IsSupported)
            {
                var supported = src.Capabilities.CapDuplex.GetValues().Select(v => v.ToString()).Distinct().ToList();
                Duplex? current = null;
                try
                {
                    current = src.Capabilities.CapDuplex.GetCurrent();
                }
                catch
                {
                    // ignore if driver doesn't support GetCurrent
                }

                list.Add(new DeviceCapability
                {
                    Key = "twain.duplexMode",
                    Label = "双面模式（TWAIN）",
                    Description = "实验项：部分驱动在 TwoPass 模式下会先出所有正面再出所有反面。",
                    Type = "enum",
                    IsReadable = true,
                    IsWritable = src.Capabilities.CapDuplex is ICapWrapper<Duplex> && !src.Capabilities.CapDuplex.IsReadOnly,
                    Experimental = true,
                    SupportedValues = supported.Prepend("Auto").Distinct().ToList(),
                    CurrentValue = _duplexModeOverride?.ToString() ?? current?.ToString() ?? "Auto"
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[TWAIN] Failed to enumerate CapDuplex");
        }

        // AutoFeed override
        try
        {
            if (src.Capabilities.CapAutoFeed.IsSupported)
            {
                BoolType? current = null;
                try
                {
                    current = src.Capabilities.CapAutoFeed.GetCurrent();
                }
                catch
                {
                    // ignore if driver doesn't support GetCurrent
                }

                list.Add(new DeviceCapability
                {
                    Key = "twain.autoFeed",
                    Label = "自动进纸（AutoFeed）",
                    Description = "实验项：仅在使用 ADF 时生效。",
                    Type = "enum",
                    IsReadable = true,
                    IsWritable = src.Capabilities.CapAutoFeed.CanSet && !src.Capabilities.CapAutoFeed.IsReadOnly,
                    Experimental = true,
                    SupportedValues = new List<string> { "Auto", "True", "False" },
                    CurrentValue = _autoFeedOverride.HasValue
                        ? (_autoFeedOverride.Value ? "True" : "False")
                        : (current.HasValue ? (current.Value == BoolType.True ? "True" : "False") : "Auto")
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[TWAIN] Failed to enumerate CapAutoFeed");
        }

        // UI controllable (read-only informational)
        try
        {
            if (src.Capabilities.CapUIControllable.IsSupported)
            {
                BoolType? current = null;
                try
                {
                    current = src.Capabilities.CapUIControllable.GetCurrent();
                }
                catch { }

                list.Add(new DeviceCapability
                {
                    Key = "twain.uiControllable",
                    Label = "驱动界面可控（CapUIControllable）",
                    Description = "只读信息：为 True 时通常可在 showUI=false 进行无界面扫描。",
                    Type = "bool",
                    IsReadable = true,
                    IsWritable = false,
                    Experimental = true,
                    CurrentValue = current == BoolType.True
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[TWAIN] Failed to read CapUIControllable");
        }

        return list;
    }

    public bool TryApplyAdvancedSetting(string key, JsonElement value, out string message)
    {
        message = string.Empty;

        if (_twain?.CurrentSource == null)
        {
            message = "未选择扫描仪";
            return false;
        }

        if (IsScanning)
        {
            message = "正在扫描中，无法应用设置";
            return false;
        }

        var src = _twain.CurrentSource;

        if (string.Equals(key, "twain.duplexMode", StringComparison.OrdinalIgnoreCase))
        {
            if (!src.Capabilities.CapDuplex.IsSupported || src.Capabilities.CapDuplex.IsReadOnly || src.Capabilities.CapDuplex is not ICapWrapper<Duplex> wrapper)
            {
                message = "CapDuplex 不支持或不可写";
                return false;
            }

            var str = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (string.Equals(str, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                _duplexModeOverride = null;
                message = "已恢复为自动";
                return true;
            }

            if (!Enum.TryParse<Duplex>(str, true, out var mode))
            {
                message = $"无效值：{str}";
                return false;
            }

            var supported = src.Capabilities.CapDuplex.GetValues().ToList();
            if (!supported.Contains(mode))
            {
                message = $"不支持的双面模式：{mode}";
                return false;
            }

            var rc = wrapper.SetValue(mode);
            _duplexModeOverride = mode;
            message = $"已设置为 {mode}（{rc}）";
            return rc == ReturnCode.Success;
        }

        if (string.Equals(key, "twain.autoFeed", StringComparison.OrdinalIgnoreCase))
        {
            if (!src.Capabilities.CapAutoFeed.IsSupported || src.Capabilities.CapAutoFeed.IsReadOnly)
            {
                message = "CapAutoFeed 不支持或不可写";
                return false;
            }

            var str = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (string.Equals(str, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                _autoFeedOverride = null;
                message = "已恢复为自动";
                return true;
            }

            bool desired;
            if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                desired = value.GetBoolean();
            }
            else if (string.Equals(str, "True", StringComparison.OrdinalIgnoreCase))
            {
                desired = true;
            }
            else if (string.Equals(str, "False", StringComparison.OrdinalIgnoreCase))
            {
                desired = false;
            }
            else
            {
                message = $"无效值：{str}";
                return false;
            }

            var rc = src.Capabilities.CapAutoFeed.SetValue(desired ? BoolType.True : BoolType.False);
            _autoFeedOverride = desired;
            message = $"已设置为 {desired}（{rc}）";
            return rc == ReturnCode.Success;
        }

        message = "未知的高级能力";
        return false;
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

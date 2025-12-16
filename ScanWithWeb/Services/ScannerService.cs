using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Models;
using NTwain;
using NTwain.Data;

namespace ScanWithWeb.Services;

/// <summary>
/// Scanner service for TWAIN scanner operations
/// </summary>
public class ScannerService : IDisposable
{
    private readonly ILogger<ScannerService> _logger;
    private TwainSession? _twain;
    private IntPtr _windowHandle;
    private bool _stopScan;
    private int _currentPageNumber;
    private ImageCodecInfo? _tiffCodecInfo;

    // Cache DataSource objects by ID for later selection
    // This helps handle scanners with empty names that get populated later
    private readonly Dictionary<string, DataSource> _sourceCache = new();

    // Events for scan results
    public event EventHandler<ImageScannedEventArgs>? ImageScanned;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;
    public event EventHandler<ScanErrorEventArgs>? ScanError;

    // Current state
    private ClientSession? _currentScanSession;
    private string? _currentRequestId;

    public string? CurrentScannerName { get; private set; }
    public bool IsScanning { get; private set; }
    public bool IsTwainAvailable { get; private set; }
    public string? TwainError { get; private set; }

    public ScannerService(ILogger<ScannerService> logger)
    {
        _logger = logger;

        // Find TIFF codec
        foreach (var enc in ImageCodecInfo.GetImageEncoders())
        {
            if (enc.MimeType == "image/tiff")
            {
                _tiffCodecInfo = enc;
                break;
            }
        }
    }

    /// <summary>
    /// Initializes the TWAIN session
    /// </summary>
    /// <returns>True if initialization succeeded, false if TWAIN is not available</returns>
    public bool Initialize(IntPtr windowHandle)
    {
        _logger.LogInformation("Initializing TWAIN session with WindowHandle: {Handle}", windowHandle);
        _windowHandle = windowHandle;

        try
        {
            // Create TWIdentity manually to avoid issues with single-file publish
            // (Assembly.Location is empty in single-file mode)
            var appId = TWIdentity.Create(
                DataGroups.Image,
                new Version(3, 0, 6),
                "ScanWithWeb Team",
                "ScanWithWeb",
                "ScanWithWeb Service",
                "ScanWithWeb Scanner Service");

            _logger.LogDebug("TWIdentity created: {AppId}", appId.ProductName);

            _twain = new TwainSession(appId);
            _logger.LogDebug("TwainSession created, initial state: {State}", _twain.State);

            _twain.StateChanged += (s, e) =>
            {
                _logger.LogDebug("TWAIN state changed to {State}", _twain.State);
            };

            _twain.TransferError += (s, e) =>
            {
                _logger.LogError("TWAIN transfer error occurred");
                ScanError?.Invoke(this, new ScanErrorEventArgs(
                    _currentScanSession,
                    _currentRequestId ?? string.Empty,
                    "Transfer error occurred"));
            };

            _twain.DataTransferred += OnDataTransferred;

            _twain.SourceDisabled += (s, e) =>
            {
                _logger.LogInformation("Scan source disabled, scan complete");
                CompleteScan();
            };

            _twain.TransferReady += (s, e) =>
            {
                _logger.LogDebug("TransferReady event, CancelAll: {Cancel}", _stopScan);
                e.CancelAll = _stopScan;
            };

            _twain.SynchronizationContext = SynchronizationContext.Current;
            _logger.LogDebug("SynchronizationContext set: {HasContext}", SynchronizationContext.Current != null);

            if (_twain.State < 3)
            {
                _logger.LogDebug("Opening TWAIN session...");
                _twain.Open();
                _logger.LogInformation("TWAIN session opened successfully, state: {State}", _twain.State);
            }
            else
            {
                _logger.LogDebug("TWAIN session already open, state: {State}", _twain.State);
            }

            IsTwainAvailable = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogError(ex, "TWAIN DLL not found - TWAIN Data Source Manager may not be installed");
            IsTwainAvailable = false;
            TwainError = "TWAIN not installed. Please install a TWAIN-compatible scanner driver or the TWAIN Data Source Manager.";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize TWAIN session");
            IsTwainAvailable = false;
            TwainError = $"TWAIN initialization failed: {ex.Message}";
            return false;
        }
    }

    private void OnDataTransferred(object? sender, DataTransferredEventArgs e)
    {
        _logger.LogInformation("Image data received - Page {Page}", _currentPageNumber + 1);
        _logger.LogDebug("DataTransferred - NativeData: {HasNative}, FileDataPath: {FilePath}",
            e.NativeData != IntPtr.Zero, e.FileDataPath ?? "null");
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
                    var rawImageData = ms.ToArray();

                    // Get image dimensions before compression
                    ms.Position = 0;
                    using var img = Image.FromStream(ms);
                    var width = img.Width;
                    var height = img.Height;
                    var dpi = (int)img.HorizontalResolution;

                    // Compress large images to JPEG to avoid WebSocket message size issues
                    var format = "bmp";
                    var imageData = ImageCompressor.CompressIfNeeded(rawImageData, ref format, _logger);

                    var metadata = new ImageMetadata
                    {
                        Width = width,
                        Height = height,
                        Format = format,
                        SizeBytes = imageData.Length,
                        Dpi = dpi
                    };

                    _logger.LogDebug("Image processed: {Width}x{Height}, {Format}, {Size:N0} bytes",
                        width, height, format, imageData.Length);

                    // Raise event - only to requesting session, not broadcast
                    ImageScanned?.Invoke(this, new ImageScannedEventArgs(
                        _currentScanSession,
                        _currentRequestId ?? string.Empty,
                        imageData,
                        metadata,
                        _currentPageNumber));
                }
            }
            else if (!string.IsNullOrEmpty(e.FileDataPath))
            {
                var rawImageData = File.ReadAllBytes(e.FileDataPath);
                using var img = Image.FromFile(e.FileDataPath);
                var width = img.Width;
                var height = img.Height;
                var dpi = (int)img.HorizontalResolution;

                // Compress large images to JPEG
                var format = Path.GetExtension(e.FileDataPath).TrimStart('.');
                var imageData = ImageCompressor.CompressIfNeeded(rawImageData, ref format, _logger);

                var metadata = new ImageMetadata
                {
                    Width = width,
                    Height = height,
                    Format = format,
                    SizeBytes = imageData.Length,
                    Dpi = dpi
                };

                _logger.LogDebug("File image processed: {Width}x{Height}, {Format}, {Size:N0} bytes",
                    width, height, format, imageData.Length);

                ImageScanned?.Invoke(this, new ImageScannedEventArgs(
                    _currentScanSession,
                    _currentRequestId ?? string.Empty,
                    imageData,
                    metadata,
                    _currentPageNumber));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing scanned image");
            ScanError?.Invoke(this, new ScanErrorEventArgs(
                _currentScanSession,
                _currentRequestId ?? string.Empty,
                ex.Message));
        }
    }

    /// <summary>
    /// Gets list of available scanners
    /// </summary>
    public List<ScannerInfo> GetAvailableScanners()
    {
        var scanners = new List<ScannerInfo>();
        var bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";

        if (_twain == null || _twain.State < 3)
        {
            _logger.LogWarning("TWAIN not initialized (State: {State}, {Bits})",
                _twain?.State ?? 0, bits);
            return scanners;
        }

        // Clear the source cache before re-enumerating
        _sourceCache.Clear();

        _logger.LogDebug("Enumerating TWAIN sources ({Bits} mode, State: {State})...", bits, _twain.State);

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
                _logger.LogDebug("Scanner at index {Index} has empty name, using fallback ID: {Id}", index, scannerId);
            }

            // Cache the DataSource object for later selection
            _sourceCache[scannerId] = source;

            _logger.LogDebug("Found scanner: {Name} (ID: {Id})", scannerName, scannerId);
            scanners.Add(new ScannerInfo
            {
                Name = scannerName,
                Id = scannerId,
                IsDefault = _twain.CurrentSource?.Name == source.Name
            });
        }

        if (scanners.Count == 0)
        {
            _logger.LogWarning("No TWAIN scanners found in {Bits} mode. " +
                "If your scanner has only 32-bit drivers, try using the 32-bit version of ScanWithWeb.", bits);
        }
        else
        {
            _logger.LogInformation("Found {Count} scanner(s) in {Bits} mode", scanners.Count, bits);
        }

        return scanners;
    }

    /// <summary>
    /// Selects a scanner by ID
    /// </summary>
    public bool SelectScanner(string scannerId)
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
            _logger.LogDebug("Found scanner in cache: {Id}", scannerId);
        }
        else
        {
            // Fallback: search by name in case cache was cleared
            source = _twain.FirstOrDefault(s => s.Name == scannerId);
        }

        if (source == null)
        {
            _logger.LogWarning("Scanner not found: {Id}", scannerId);
            return false;
        }

        if (source.Open() == ReturnCode.Success)
        {
            // After opening, the name might now be available
            var actualName = !string.IsNullOrWhiteSpace(source.Name) ? source.Name : scannerId;
            CurrentScannerName = actualName;
            _logger.LogInformation("Scanner selected: {Name} (ID: {Id})", actualName, scannerId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets capabilities of currently selected scanner
    /// </summary>
    public ScannerCapabilities? GetCurrentScannerCapabilities()
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

            // ADF (Automatic Document Feeder) support
            caps.SupportsAdf = src.Capabilities.CapFeederEnabled.IsSupported;

            // UI control support
            caps.SupportsUIControl = src.Capabilities.CapUIControllable.IsSupported;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scanner capabilities");
        }

        return caps;
    }

    /// <summary>
    /// Applies scan settings
    /// </summary>
    public void ApplySettings(ScanSettings settings)
    {
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
                _logger.LogDebug("DPI set to {Dpi}", settings.Dpi);
            }

            // Set pixel type (color mode)
            if (!string.IsNullOrEmpty(settings.PixelType) && src.Capabilities.ICapPixelType.IsSupported)
            {
                if (Enum.TryParse<PixelType>(settings.PixelType, true, out var pixelType))
                {
                    src.Capabilities.ICapPixelType.SetValue(pixelType);
                    _logger.LogDebug("Pixel type set to {Type}", pixelType);
                }
            }

            // Set paper size
            if (!string.IsNullOrEmpty(settings.PaperSize) && src.Capabilities.ICapSupportedSizes.IsSupported)
            {
                if (Enum.TryParse<SupportedSize>(settings.PaperSize, true, out var paperSize))
                {
                    src.Capabilities.ICapSupportedSizes.SetValue(paperSize);
                    _logger.LogDebug("Paper size set to {Size}", paperSize);
                }
            }

            // Set duplex
            if (src.Capabilities.CapDuplexEnabled.IsSupported)
            {
                src.Capabilities.CapDuplexEnabled.SetValue(settings.Duplex ? BoolType.True : BoolType.False);
                _logger.LogDebug("Duplex set to {Duplex}", settings.Duplex);
            }

            // Enable/disable ADF (Automatic Document Feeder)
            if (src.Capabilities.CapFeederEnabled.IsSupported)
            {
                src.Capabilities.CapFeederEnabled.SetValue(settings.UseAdf ? BoolType.True : BoolType.False);
                _logger.LogDebug("ADF enabled: {UseAdf}", settings.UseAdf);

                // If ADF is enabled, also enable auto feed
                if (settings.UseAdf && src.Capabilities.CapAutoFeed.IsSupported)
                {
                    src.Capabilities.CapAutoFeed.SetValue(BoolType.True);
                    _logger.LogDebug("Auto feed enabled");
                }
            }

            // Set maximum number of pages to scan (-1 means all pages in feeder)
            if (src.Capabilities.CapXferCount.IsSupported)
            {
                // TWAIN uses -1 for unlimited pages
                src.Capabilities.CapXferCount.SetValue(settings.MaxPages);
                _logger.LogDebug("Max pages set to {MaxPages}", settings.MaxPages);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying scan settings");
        }
    }

    /// <summary>
    /// Starts scanning for a specific session
    /// </summary>
    public Task<bool> StartScanAsync(ClientSession session, string requestId)
    {
        _logger.LogDebug("StartScanAsync called - RequestId: {RequestId}, ClientId: {ClientId}",
            requestId, session?.ClientId ?? "null");

        if (_twain?.CurrentSource == null)
        {
            _logger.LogWarning("No scanner selected - TWAIN: {TwainNull}, CurrentSource: {SourceNull}",
                _twain == null, _twain?.CurrentSource == null);
            return Task.FromResult(false);
        }

        if (IsScanning)
        {
            _logger.LogWarning("Scan already in progress");
            return Task.FromResult(false);
        }

        _currentScanSession = session;
        _currentRequestId = requestId;
        _currentPageNumber = 0;
        _stopScan = false;
        IsScanning = true;

        try
        {
            var src = _twain.CurrentSource;
            _logger.LogDebug("Current scanner: {ScannerName}, TWAIN State: {State}, WindowHandle: {Handle}",
                src.Name, _twain.State, _windowHandle);

            ReturnCode result;

            // Check if scanner supports UI-less operation
            bool supportsNoUI = false;
            try
            {
                if (src.Capabilities.CapUIControllable.IsSupported)
                {
                    var canDisableUI = src.Capabilities.CapUIControllable.GetCurrent();
                    supportsNoUI = canDisableUI == BoolType.True;
                    _logger.LogDebug("Scanner UI controllable: {Controllable}", supportsNoUI);
                }
            }
            catch (Exception capEx)
            {
                _logger.LogDebug("Could not check CapUIControllable: {Message}", capEx.Message);
            }

            // Try NoUI mode first if supported (for programmatic control)
            if (supportsNoUI)
            {
                _logger.LogDebug("Trying NoUI mode first (scanner supports UI control)");
                try
                {
                    result = src.Enable(SourceEnableMode.NoUI, false, _windowHandle);
                    _logger.LogDebug("Source.Enable (NoUI) returned: {Result}", result);

                    if (result == ReturnCode.Success)
                    {
                        _logger.LogInformation("NoUI mode scan started successfully for session {ClientId}", session.ClientId);
                        return Task.FromResult(true);
                    }
                }
                catch (Exception noUIEx)
                {
                    _logger.LogWarning(noUIEx, "NoUI mode failed, falling back to ShowUI mode");
                }
            }

            // Use ShowUI mode with modal=false so window appears and user can interact
            // modal=false allows the scanner UI to appear without blocking the message loop
            _logger.LogDebug("Enabling source with ShowUI mode (non-modal), WindowHandle: {Handle}", _windowHandle);
            _logger.LogInformation("Scanner UI will appear - please click 'Scan' in the scanner dialog to start scanning");

            try
            {
                result = src.Enable(SourceEnableMode.ShowUI, false, _windowHandle);
                _logger.LogDebug("Source.Enable (ShowUI, non-modal) returned: {Result}", result);
            }
            catch (Exception enableEx)
            {
                _logger.LogError(enableEx, "ShowUI non-modal mode failed, trying modal mode");

                // Try modal mode as fallback
                try
                {
                    result = src.Enable(SourceEnableMode.ShowUI, true, _windowHandle);
                    _logger.LogDebug("Source.Enable (ShowUI, modal) returned: {Result}", result);
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx, "All enable modes failed");
                    throw;
                }
            }

            if (result == ReturnCode.Success)
            {
                _logger.LogInformation("Scan started successfully for session {ClientId}", session.ClientId);
                return Task.FromResult(true);
            }
            else
            {
                _logger.LogWarning("Failed to start scan: {Result}", result);
                IsScanning = false;
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting scan - Exception type: {ExType}, Message: {Message}",
                ex.GetType().FullName, ex.Message);
            IsScanning = false;
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Stops the current scan
    /// </summary>
    public void StopScan()
    {
        _stopScan = true;
        _logger.LogInformation("Scan stop requested");
    }

    private void CompleteScan()
    {
        if (_currentScanSession != null)
        {
            ScanCompleted?.Invoke(this, new ScanCompletedEventArgs(
                _currentScanSession,
                _currentRequestId ?? string.Empty,
                _currentPageNumber));
        }

        IsScanning = false;
        _currentScanSession = null;
        _currentRequestId = null;
        _currentPageNumber = 0;
    }

    /// <summary>
    /// Closes the TWAIN session
    /// </summary>
    public void Close()
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
            _logger.LogInformation("TWAIN session closed");
        }
    }

    public void Dispose()
    {
        Close();
    }
}

#region Event Args

public class ImageScannedEventArgs : EventArgs
{
    public ClientSession? Session { get; }
    public string RequestId { get; }
    public byte[] ImageData { get; }
    public ImageMetadata Metadata { get; }
    public int PageNumber { get; }

    public ImageScannedEventArgs(ClientSession? session, string requestId, byte[] imageData, ImageMetadata metadata, int pageNumber)
    {
        Session = session;
        RequestId = requestId;
        ImageData = imageData;
        Metadata = metadata;
        PageNumber = pageNumber;
    }
}

public class ScanCompletedEventArgs : EventArgs
{
    public ClientSession? Session { get; }
    public string RequestId { get; }
    public int TotalPages { get; }

    public ScanCompletedEventArgs(ClientSession? session, string requestId, int totalPages)
    {
        Session = session;
        RequestId = requestId;
        TotalPages = totalPages;
    }
}

public class ScanErrorEventArgs : EventArgs
{
    public ClientSession? Session { get; }
    public string RequestId { get; }
    public string ErrorMessage { get; }

    public ScanErrorEventArgs(ClientSession? session, string requestId, string errorMessage)
    {
        Session = session;
        RequestId = requestId;
        ErrorMessage = errorMessage;
    }
}

#endregion

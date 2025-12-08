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
    private bool _stopScan;
    private int _currentPageNumber;
    private ImageCodecInfo? _tiffCodecInfo;

    // Events for scan results
    public event EventHandler<ImageScannedEventArgs>? ImageScanned;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;
    public event EventHandler<ScanErrorEventArgs>? ScanError;

    // Current state
    private ClientSession? _currentScanSession;
    private string? _currentRequestId;

    public string? CurrentScannerName { get; private set; }
    public bool IsScanning { get; private set; }

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
    public void Initialize(IntPtr windowHandle)
    {
        // Create TWIdentity manually to avoid issues with single-file publish
        // (Assembly.Location is empty in single-file mode)
        var appId = TWIdentity.Create(
            DataGroups.Image,
            new Version(2, 0, 3),
            "ScanWithWeb Team",
            "ScanWithWeb",
            "ScanWithWeb Service",
            "ScanWithWeb Scanner Service");

        _twain = new TwainSession(appId);

        _twain.StateChanged += (s, e) =>
        {
            _logger.LogDebug("TWAIN state changed to {State}", _twain.State);
        };

        _twain.TransferError += (s, e) =>
        {
            _logger.LogError("TWAIN transfer error");
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
            e.CancelAll = _stopScan;
        };

        _twain.SynchronizationContext = SynchronizationContext.Current;

        if (_twain.State < 3)
        {
            _twain.Open();
            _logger.LogInformation("TWAIN session opened");
        }
    }

    private void OnDataTransferred(object? sender, DataTransferredEventArgs e)
    {
        _logger.LogInformation("Image data received");
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

        if (_twain == null || _twain.State < 3)
        {
            _logger.LogWarning("TWAIN not initialized");
            return scanners;
        }

        foreach (var source in _twain)
        {
            scanners.Add(new ScannerInfo
            {
                Name = source.Name,
                Id = source.Name,
                IsDefault = _twain.CurrentSource?.Name == source.Name
            });
        }

        _logger.LogInformation("Found {Count} scanners", scanners.Count);
        return scanners;
    }

    /// <summary>
    /// Selects a scanner by name
    /// </summary>
    public bool SelectScanner(string scannerName)
    {
        if (_twain == null || _twain.State < 3)
            return false;

        // Close current source if open
        if (_twain.State == 4)
        {
            _twain.CurrentSource?.Close();
        }

        var source = _twain.FirstOrDefault(s => s.Name == scannerName);
        if (source == null)
        {
            _logger.LogWarning("Scanner not found: {Name}", scannerName);
            return false;
        }

        if (source.Open() == ReturnCode.Success)
        {
            CurrentScannerName = scannerName;
            _logger.LogInformation("Scanner selected: {Name}", scannerName);
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
        if (_twain?.CurrentSource == null)
        {
            _logger.LogWarning("No scanner selected");
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
            ReturnCode result;

            // Try to scan without UI if supported
            if (src.Capabilities.CapUIControllable.IsSupported)
            {
                result = src.Enable(SourceEnableMode.NoUI, false, IntPtr.Zero);
            }
            else
            {
                result = src.Enable(SourceEnableMode.ShowUI, true, IntPtr.Zero);
            }

            if (result == ReturnCode.Success)
            {
                _logger.LogInformation("Scan started for session {ClientId}", session.ClientId);
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
            _logger.LogError(ex, "Error starting scan");
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

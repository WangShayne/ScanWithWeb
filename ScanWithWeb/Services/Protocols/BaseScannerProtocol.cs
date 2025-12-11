using Microsoft.Extensions.Logging;
using ScanWithWeb.Interfaces;
using ScanWithWeb.Models;

namespace ScanWithWeb.Services.Protocols;

/// <summary>
/// Abstract base class for scanner protocol implementations
/// Provides common functionality and event handling
/// </summary>
public abstract class BaseScannerProtocol : IScannerProtocol
{
    protected readonly ILogger Logger;
    protected ScanSettings? CurrentSettings;

    public abstract string ProtocolName { get; }
    public bool IsAvailable { get; protected set; }
    public string? InitializationError { get; protected set; }
    public bool IsScanning { get; protected set; }
    public string? CurrentScannerId { get; protected set; }

    public event EventHandler<ProtocolImageScannedEventArgs>? ImageScanned;
    public event EventHandler<ProtocolScanCompletedEventArgs>? ScanCompleted;
    public event EventHandler<ProtocolScanErrorEventArgs>? ScanError;

    protected BaseScannerProtocol(ILogger logger)
    {
        Logger = logger;
    }

    public abstract bool Initialize(IntPtr windowHandle);
    public abstract void Shutdown();
    public abstract Task<List<ScannerInfo>> GetAvailableScannersAsync();
    public abstract bool SelectScanner(string scannerId);
    public abstract ScannerCapabilities? GetCapabilities(string scannerId);
    public abstract Task<bool> StartScanAsync(string requestId);
    public abstract void StopScan();

    public virtual void ApplySettings(ScanSettings settings)
    {
        CurrentSettings = settings;
        Logger.LogDebug("[{Protocol}] Settings applied: DPI={Dpi}, PixelType={PixelType}",
            ProtocolName, settings.Dpi, settings.PixelType);
    }

    /// <summary>
    /// Raises the ImageScanned event
    /// </summary>
    protected void RaiseImageScanned(string requestId, byte[] imageData, ImageMetadata metadata, int pageNumber)
    {
        Logger.LogInformation("[{Protocol}] Image scanned: Page {Page}, Size={Size} bytes",
            ProtocolName, pageNumber, imageData.Length);

        ImageScanned?.Invoke(this, new ProtocolImageScannedEventArgs(
            requestId, imageData, metadata, pageNumber, ProtocolName));
    }

    /// <summary>
    /// Raises the ScanCompleted event
    /// </summary>
    protected void RaiseScanCompleted(string requestId, int totalPages)
    {
        Logger.LogInformation("[{Protocol}] Scan completed: {Pages} page(s)", ProtocolName, totalPages);
        IsScanning = false;

        ScanCompleted?.Invoke(this, new ProtocolScanCompletedEventArgs(
            requestId, totalPages, ProtocolName));
    }

    /// <summary>
    /// Raises the ScanError event
    /// </summary>
    protected void RaiseScanError(string requestId, string errorMessage)
    {
        Logger.LogError("[{Protocol}] Scan error: {Error}", ProtocolName, errorMessage);
        IsScanning = false;

        ScanError?.Invoke(this, new ProtocolScanErrorEventArgs(
            requestId, errorMessage, ProtocolName));
    }

    public virtual void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }
}

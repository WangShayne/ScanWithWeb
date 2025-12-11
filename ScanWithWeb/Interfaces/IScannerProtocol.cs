using ScanWithWeb.Models;

namespace ScanWithWeb.Interfaces;

/// <summary>
/// Interface for scanner protocol implementations (TWAIN, WIA, ESCL)
/// </summary>
public interface IScannerProtocol : IDisposable
{
    /// <summary>
    /// Protocol identifier (e.g., "twain", "wia", "escl")
    /// </summary>
    string ProtocolName { get; }

    /// <summary>
    /// Whether this protocol is available on the system
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Error message if initialization failed
    /// </summary>
    string? InitializationError { get; }

    /// <summary>
    /// Whether a scan is currently in progress
    /// </summary>
    bool IsScanning { get; }

    /// <summary>
    /// Currently selected scanner ID (without protocol prefix)
    /// </summary>
    string? CurrentScannerId { get; }

    /// <summary>
    /// Initialize the protocol. Returns true if successful.
    /// </summary>
    /// <param name="windowHandle">Window handle for UI operations (may be IntPtr.Zero for headless protocols)</param>
    bool Initialize(IntPtr windowHandle);

    /// <summary>
    /// Shutdown the protocol and release resources
    /// </summary>
    void Shutdown();

    /// <summary>
    /// Get list of available scanners for this protocol
    /// </summary>
    Task<List<ScannerInfo>> GetAvailableScannersAsync();

    /// <summary>
    /// Select a scanner by its protocol-local ID
    /// </summary>
    bool SelectScanner(string scannerId);

    /// <summary>
    /// Get capabilities of a specific scanner
    /// </summary>
    ScannerCapabilities? GetCapabilities(string scannerId);

    /// <summary>
    /// Apply scan settings before starting scan
    /// </summary>
    void ApplySettings(ScanSettings settings);

    /// <summary>
    /// Start scanning asynchronously
    /// </summary>
    Task<bool> StartScanAsync(string requestId);

    /// <summary>
    /// Stop the current scan
    /// </summary>
    void StopScan();

    // Events
    event EventHandler<ProtocolImageScannedEventArgs>? ImageScanned;
    event EventHandler<ProtocolScanCompletedEventArgs>? ScanCompleted;
    event EventHandler<ProtocolScanErrorEventArgs>? ScanError;
}

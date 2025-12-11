using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Models;

namespace ScanWithWeb.Services.Protocols;

/// <summary>
/// WIA (Windows Image Acquisition) protocol implementation
/// Works in both 32-bit and 64-bit mode, solving the TWAIN 32-bit driver limitation
/// Uses COM interop with WIA Automation Library
/// </summary>
public class WiaScannerProtocol : BaseScannerProtocol
{
    private dynamic? _deviceManager;
    private dynamic? _selectedDevice;
    private readonly Dictionary<string, dynamic> _deviceInfoCache = new();
    private bool _stopRequested;
    private int _currentPageNumber;
    private string? _currentRequestId;

    // WIA Device Type constants
    private const int WiaDeviceTypeScannerDeviceType = 1;

    // WIA Property IDs
    private const int WIA_IPS_CUR_INTENT = 6146;
    private const int WIA_IPS_XRES = 6147;
    private const int WIA_IPS_YRES = 6148;
    private const int WIA_IPS_XEXTENT = 6151;
    private const int WIA_IPS_YEXTENT = 6152;
    private const int WIA_IPS_PAGES = 3096;
    private const int WIA_IPS_PAGE_SIZE = 3097;
    private const int WIA_DPS_DOCUMENT_HANDLING_SELECT = 3088;
    private const int WIA_DPS_DOCUMENT_HANDLING_STATUS = 3087;

    // WIA Intent constants
    private const int WIA_INTENT_IMAGE_TYPE_COLOR = 1;
    private const int WIA_INTENT_IMAGE_TYPE_GRAYSCALE = 2;
    private const int WIA_INTENT_IMAGE_TYPE_TEXT = 4;

    // WIA Format GUIDs
    private static readonly string FormatBMP = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    private static readonly string FormatPNG = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private static readonly string FormatJPEG = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    public override string ProtocolName => "wia";

    public WiaScannerProtocol(ILogger<WiaScannerProtocol> logger) : base(logger)
    {
    }

    public override bool Initialize(IntPtr windowHandle)
    {
        try
        {
            Logger.LogInformation("[WIA] Initializing WIA Device Manager...");

            // Create WIA Device Manager via COM (late binding)
            var wiaType = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (wiaType == null)
            {
                InitializationError = "WIA is not available on this system";
                IsAvailable = false;
                Logger.LogWarning("[WIA] WIA.DeviceManager not found");
                return false;
            }

            _deviceManager = Activator.CreateInstance(wiaType);
            if (_deviceManager == null)
            {
                InitializationError = "Failed to create WIA Device Manager";
                IsAvailable = false;
                Logger.LogWarning("[WIA] Failed to create WIA.DeviceManager instance");
                return false;
            }

            IsAvailable = true;
            Logger.LogInformation("[WIA] Initialized successfully (64-bit compatible)");
            return true;
        }
        catch (COMException ex)
        {
            InitializationError = $"WIA COM error: {ex.Message}";
            IsAvailable = false;
            Logger.LogWarning(ex, "[WIA] COM initialization failed");
            return false;
        }
        catch (Exception ex)
        {
            InitializationError = $"WIA initialization failed: {ex.Message}";
            IsAvailable = false;
            Logger.LogError(ex, "[WIA] Initialization failed");
            return false;
        }
    }

    public override async Task<List<ScannerInfo>> GetAvailableScannersAsync()
    {
        return await Task.Run(() =>
        {
            var scanners = new List<ScannerInfo>();

            if (_deviceManager == null)
            {
                Logger.LogWarning("[WIA] Device manager not initialized");
                return scanners;
            }

            try
            {
                _deviceInfoCache.Clear();
                var deviceInfos = _deviceManager.DeviceInfos;
                int count = deviceInfos.Count;

                Logger.LogDebug("[WIA] Found {Count} WIA device(s)", count);

                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        dynamic deviceInfo = deviceInfos.Item(i);
                        int deviceType = deviceInfo.Type;

                        // Filter to only scanner devices
                        if (deviceType != WiaDeviceTypeScannerDeviceType)
                        {
                            Logger.LogDebug("[WIA] Skipping non-scanner device at index {Index}", i);
                            continue;
                        }

                        string deviceId = deviceInfo.DeviceID;
                        string name = GetDeviceProperty(deviceInfo, "Name") ?? $"WIA Scanner {i}";

                        _deviceInfoCache[deviceId] = deviceInfo;

                        scanners.Add(new ScannerInfo
                        {
                            Id = deviceId,
                            Name = name,
                            IsDefault = false,
                            Capabilities = GetDeviceCapabilities(deviceInfo)
                        });

                        Logger.LogDebug("[WIA] Found scanner: {Name} (ID: {Id})", name, deviceId);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "[WIA] Error reading device at index {Index}", i);
                    }
                }

                Logger.LogInformation("[WIA] Found {Count} scanner(s)", scanners.Count);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[WIA] Error enumerating devices");
            }

            return scanners;
        });
    }

    private string? GetDeviceProperty(dynamic deviceInfo, string propertyName)
    {
        try
        {
            var properties = deviceInfo.Properties;
            foreach (dynamic prop in properties)
            {
                if (prop.Name == propertyName)
                {
                    return prop.Value?.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    private ScannerCapabilities GetDeviceCapabilities(dynamic deviceInfo)
    {
        var caps = new ScannerCapabilities
        {
            SupportedDpi = new List<int> { 75, 100, 150, 200, 300, 600 },
            SupportedPixelTypes = new List<string> { "BW", "Gray8", "RGB" },
            SupportedPaperSizes = new List<string> { "A4", "Letter", "Legal" },
            SupportsDuplex = false,
            SupportsAdf = false,
            SupportsUIControl = true
        };

        try
        {
            // Try to query actual capabilities from device
            // WIA capabilities are typically queried after connecting to the device
            // For now, return default capabilities
        }
        catch (Exception ex)
        {
            Logger.LogDebug("[WIA] Error reading device capabilities: {Error}", ex.Message);
        }

        return caps;
    }

    public override bool SelectScanner(string scannerId)
    {
        if (_deviceManager == null)
        {
            Logger.LogWarning("[WIA] Device manager not initialized");
            return false;
        }

        try
        {
            if (!_deviceInfoCache.TryGetValue(scannerId, out var deviceInfo))
            {
                Logger.LogWarning("[WIA] Scanner not found in cache: {Id}", scannerId);
                return false;
            }

            // Connect to the device
            _selectedDevice = deviceInfo.Connect();
            CurrentScannerId = scannerId;

            Logger.LogInformation("[WIA] Scanner selected and connected: {Id}", scannerId);
            return true;
        }
        catch (COMException ex)
        {
            Logger.LogError(ex, "[WIA] Failed to connect to scanner: {Id}", scannerId);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[WIA] Error selecting scanner: {Id}", scannerId);
            return false;
        }
    }

    public override ScannerCapabilities? GetCapabilities(string scannerId)
    {
        if (_deviceInfoCache.TryGetValue(scannerId, out var deviceInfo))
        {
            return GetDeviceCapabilities(deviceInfo);
        }
        return null;
    }

    public override async Task<bool> StartScanAsync(string requestId)
    {
        if (_selectedDevice == null)
        {
            Logger.LogWarning("[WIA] No scanner selected");
            return false;
        }

        if (IsScanning)
        {
            Logger.LogWarning("[WIA] Scan already in progress");
            return false;
        }

        _currentRequestId = requestId;
        _currentPageNumber = 0;
        _stopRequested = false;
        IsScanning = true;

        return await Task.Run(() =>
        {
            try
            {
                Logger.LogInformation("[WIA] Starting scan...");

                // Get the first item (scanner)
                dynamic items = _selectedDevice.Items;
                if (items.Count == 0)
                {
                    Logger.LogWarning("[WIA] No scan items available");
                    RaiseScanError(requestId, "No scan items available");
                    IsScanning = false;
                    return false;
                }

                dynamic scannerItem = items.Item(1);

                // Apply settings if available
                if (CurrentSettings != null)
                {
                    ApplyWiaSettings(scannerItem);
                }

                // Perform the scan
                dynamic imageFile = scannerItem.Transfer(FormatBMP);

                if (_stopRequested)
                {
                    Logger.LogInformation("[WIA] Scan was cancelled");
                    IsScanning = false;
                    return false;
                }

                _currentPageNumber++;

                // Get image data
                byte[] imageData = imageFile.FileData.BinaryData;

                // Get image dimensions
                int width = imageFile.Width;
                int height = imageFile.Height;
                double horizontalResolution = imageFile.HorizontalResolution;

                var metadata = new ImageMetadata
                {
                    Width = width,
                    Height = height,
                    Format = "bmp",
                    SizeBytes = imageData.Length,
                    Dpi = (int)horizontalResolution
                };

                Logger.LogInformation("[WIA] Scan completed: {Width}x{Height}, {Size} bytes",
                    width, height, imageData.Length);

                RaiseImageScanned(requestId, imageData, metadata, _currentPageNumber);
                RaiseScanCompleted(requestId, _currentPageNumber);

                IsScanning = false;
                return true;
            }
            catch (COMException ex)
            {
                Logger.LogError(ex, "[WIA] COM error during scan");
                RaiseScanError(requestId, $"WIA scan error: {ex.Message}");
                IsScanning = false;
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[WIA] Error during scan");
                RaiseScanError(requestId, ex.Message);
                IsScanning = false;
                return false;
            }
        });
    }

    private void ApplyWiaSettings(dynamic scannerItem)
    {
        if (CurrentSettings == null)
            return;

        try
        {
            var properties = scannerItem.Properties;

            // Set resolution (DPI)
            if (CurrentSettings.Dpi > 0)
            {
                SetWiaProperty(properties, WIA_IPS_XRES, CurrentSettings.Dpi);
                SetWiaProperty(properties, WIA_IPS_YRES, CurrentSettings.Dpi);
                Logger.LogDebug("[WIA] Set resolution to {Dpi} DPI", CurrentSettings.Dpi);
            }

            // Set color mode (intent)
            int intent = CurrentSettings.PixelType?.ToLower() switch
            {
                "bw" or "blackwhite" => WIA_INTENT_IMAGE_TYPE_TEXT,
                "gray" or "gray8" or "grayscale" => WIA_INTENT_IMAGE_TYPE_GRAYSCALE,
                _ => WIA_INTENT_IMAGE_TYPE_COLOR
            };
            SetWiaProperty(properties, WIA_IPS_CUR_INTENT, intent);
            Logger.LogDebug("[WIA] Set intent to {Intent}", intent);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[WIA] Error applying settings");
        }
    }

    private void SetWiaProperty(dynamic properties, int propertyId, object value)
    {
        try
        {
            foreach (dynamic prop in properties)
            {
                if (prop.PropertyID == propertyId)
                {
                    prop.Value = value;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("[WIA] Failed to set property {Id}: {Error}", propertyId, ex.Message);
        }
    }

    public override void StopScan()
    {
        _stopRequested = true;
        Logger.LogInformation("[WIA] Scan stop requested");
    }

    public override void Shutdown()
    {
        try
        {
            if (_selectedDevice != null)
            {
                Marshal.ReleaseComObject(_selectedDevice);
                _selectedDevice = null;
            }

            foreach (var deviceInfo in _deviceInfoCache.Values)
            {
                try
                {
                    Marshal.ReleaseComObject(deviceInfo);
                }
                catch { }
            }
            _deviceInfoCache.Clear();

            if (_deviceManager != null)
            {
                Marshal.ReleaseComObject(_deviceManager);
                _deviceManager = null;
            }

            Logger.LogInformation("[WIA] Shutdown complete");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[WIA] Error during shutdown");
        }
    }
}

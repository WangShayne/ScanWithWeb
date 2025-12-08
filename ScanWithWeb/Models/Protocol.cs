using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScanWithWeb.Models;

/// <summary>
/// V2 Protocol - JSON-based WebSocket communication
/// </summary>

#region Request Models

/// <summary>
/// Base request message from client
/// </summary>
public class ScanRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("settings")]
    public ScanSettings? Settings { get; set; }
}

/// <summary>
/// Scan settings for configuration
/// </summary>
public class ScanSettings
{
    [JsonPropertyName("dpi")]
    public int Dpi { get; set; } = 200;

    [JsonPropertyName("pixelType")]
    public string PixelType { get; set; } = "RGB";

    [JsonPropertyName("paperSize")]
    public string PaperSize { get; set; } = "A4";

    [JsonPropertyName("duplex")]
    public bool Duplex { get; set; } = false;

    [JsonPropertyName("showUI")]
    public bool ShowUI { get; set; } = false;

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Use ADF (Automatic Document Feeder) for continuous scanning
    /// </summary>
    [JsonPropertyName("useAdf")]
    public bool UseAdf { get; set; } = true;

    /// <summary>
    /// Maximum number of pages to scan (-1 for unlimited/all pages in feeder)
    /// </summary>
    [JsonPropertyName("maxPages")]
    public int MaxPages { get; set; } = -1;

    /// <summary>
    /// Continue scanning even if feeder is empty (for flatbed batch scanning)
    /// </summary>
    [JsonPropertyName("continuousScan")]
    public bool ContinuousScan { get; set; } = false;
}

#endregion

#region Response Models

/// <summary>
/// Base response message to client
/// </summary>
public class ScanResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "success";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Response for authentication
/// </summary>
public class AuthResponse : ScanResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Response containing scanner list
/// </summary>
public class ScannersResponse : ScanResponse
{
    [JsonPropertyName("scanners")]
    public List<ScannerInfo> Scanners { get; set; } = new();
}

/// <summary>
/// Scanner information
/// </summary>
public class ScannerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("capabilities")]
    public ScannerCapabilities? Capabilities { get; set; }
}

/// <summary>
/// Scanner capabilities
/// </summary>
public class ScannerCapabilities
{
    [JsonPropertyName("supportedDpi")]
    public List<int> SupportedDpi { get; set; } = new();

    [JsonPropertyName("supportedPixelTypes")]
    public List<string> SupportedPixelTypes { get; set; } = new();

    [JsonPropertyName("supportedPaperSizes")]
    public List<string> SupportedPaperSizes { get; set; } = new();

    [JsonPropertyName("supportsDuplex")]
    public bool SupportsDuplex { get; set; }

    [JsonPropertyName("supportsAdf")]
    public bool SupportsAdf { get; set; }

    [JsonPropertyName("supportsUIControl")]
    public bool SupportsUIControl { get; set; }
}

/// <summary>
/// Response for scan image data
/// </summary>
public class ImageResponse : ScanResponse
{
    [JsonPropertyName("metadata")]
    public ImageMetadata? Metadata { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("totalPages")]
    public int? TotalPages { get; set; }
}

/// <summary>
/// Image metadata
/// </summary>
public class ImageMetadata
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("format")]
    public string Format { get; set; } = "bmp";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("dpi")]
    public int Dpi { get; set; }
}

/// <summary>
/// Error response
/// </summary>
public class ErrorResponse : ScanResponse
{
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; } = string.Empty;

    [JsonPropertyName("errorDetails")]
    public string? ErrorDetails { get; set; }

    public ErrorResponse()
    {
        Status = "error";
    }
}

#endregion

#region Protocol Actions

/// <summary>
/// Supported protocol actions
/// </summary>
public static class ProtocolActions
{
    public const string Authenticate = "authenticate";
    public const string ListScanners = "list_scanners";
    public const string SelectScanner = "select_scanner";
    public const string GetCapabilities = "get_capabilities";
    public const string Scan = "scan";
    public const string StopScan = "stop_scan";
    public const string Ping = "ping";
    public const string Pong = "pong";

    // Legacy support
    public const string LegacyWakeUp = "1100";
}

/// <summary>
/// Response status codes
/// </summary>
public static class ResponseStatus
{
    public const string Success = "success";
    public const string Error = "error";
    public const string Scanning = "scanning";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Error codes
/// </summary>
public static class ErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string TokenExpired = "TOKEN_EXPIRED";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string ScannerNotFound = "SCANNER_NOT_FOUND";
    public const string ScannerBusy = "SCANNER_BUSY";
    public const string ScanFailed = "SCAN_FAILED";
    public const string NoScannersAvailable = "NO_SCANNERS_AVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
}

#endregion

#region JSON Helpers

public static class ProtocolSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T obj) => JsonSerializer.Serialize(obj, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static ScanRequest? ParseRequest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ScanRequest>(json, Options);
        }
        catch
        {
            return null;
        }
    }
}

#endregion

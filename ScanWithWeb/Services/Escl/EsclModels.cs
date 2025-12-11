namespace ScanWithWeb.Services.Escl;

/// <summary>
/// Represents an ESCL scanner endpoint discovered via mDNS
/// </summary>
public class EsclScannerEndpoint
{
    /// <summary>
    /// Unique identifier (host:port)
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Scanner display name from TXT record
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Host address (IP or hostname)
    /// </summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// Port number
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Whether to use HTTPS
    /// </summary>
    public bool IsSecure { get; set; }

    /// <summary>
    /// Base path for ESCL endpoints (default: /eSCL)
    /// </summary>
    public string BasePath { get; set; } = "/eSCL";

    /// <summary>
    /// Full base URL for ESCL requests
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Scanner UUID from TXT record
    /// </summary>
    public string Uuid { get; set; } = "";

    /// <summary>
    /// Manufacturer from TXT record
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Model from TXT record
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Last seen timestamp
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// ESCL Scanner Status from ScannerStatus endpoint
/// </summary>
public class EsclScannerStatus
{
    public string State { get; set; } = "Idle";
    public string? AdfState { get; set; }
}

/// <summary>
/// ESCL Job info
/// </summary>
public class EsclScanJob
{
    public string JobUri { get; set; } = "";
    public string JobUuid { get; set; } = "";
    public int Age { get; set; }
    public int ImagesCompleted { get; set; }
    public int ImagesToTransfer { get; set; }
    public string JobState { get; set; } = "";
    public string JobStateReasons { get; set; } = "";
}

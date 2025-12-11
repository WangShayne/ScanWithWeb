namespace ScanWithWeb.Models;

/// <summary>
/// Event args for protocol image scanned event
/// </summary>
public class ProtocolImageScannedEventArgs : EventArgs
{
    public string RequestId { get; }
    public byte[] ImageData { get; }
    public ImageMetadata Metadata { get; }
    public int PageNumber { get; }
    public string ProtocolName { get; }

    public ProtocolImageScannedEventArgs(
        string requestId,
        byte[] imageData,
        ImageMetadata metadata,
        int pageNumber,
        string protocolName)
    {
        RequestId = requestId;
        ImageData = imageData;
        Metadata = metadata;
        PageNumber = pageNumber;
        ProtocolName = protocolName;
    }
}

/// <summary>
/// Event args for protocol scan completed event
/// </summary>
public class ProtocolScanCompletedEventArgs : EventArgs
{
    public string RequestId { get; }
    public int TotalPages { get; }
    public string ProtocolName { get; }

    public ProtocolScanCompletedEventArgs(string requestId, int totalPages, string protocolName)
    {
        RequestId = requestId;
        TotalPages = totalPages;
        ProtocolName = protocolName;
    }
}

/// <summary>
/// Event args for protocol scan error event
/// </summary>
public class ProtocolScanErrorEventArgs : EventArgs
{
    public string RequestId { get; }
    public string ErrorMessage { get; }
    public string ProtocolName { get; }

    public ProtocolScanErrorEventArgs(string requestId, string errorMessage, string protocolName)
    {
        RequestId = requestId;
        ErrorMessage = errorMessage;
        ProtocolName = protocolName;
    }
}

using System.Security.Cryptography.X509Certificates;
using Fleck;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Models;

namespace ScanWithWeb.Services;

/// <summary>
/// Secure WebSocket service with WSS support
/// </summary>
public class WebSocketService : IDisposable
{
    private readonly ILogger<WebSocketService> _logger;
    private readonly SessionManager _sessionManager;
    private readonly ScannerService _scannerService;
    private WebSocketServer? _server;
    private readonly int _port;
    private readonly bool _useSsl;
    private readonly string? _certificatePath;
    private readonly string? _certificatePassword;

    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    public WebSocketService(
        ILogger<WebSocketService> logger,
        SessionManager sessionManager,
        ScannerService scannerService,
        int port = 8181,
        bool useSsl = true,
        string? certificatePath = null,
        string? certificatePassword = null)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _scannerService = scannerService;
        _port = port;
        _useSsl = useSsl;
        _certificatePath = certificatePath;
        _certificatePassword = certificatePassword;
    }

    /// <summary>
    /// Starts the WebSocket server
    /// </summary>
    public void Start()
    {
        var protocol = _useSsl ? "wss" : "ws";
        var location = $"{protocol}://0.0.0.0:{_port}";

        _server = new WebSocketServer(location);

        if (_useSsl && !string.IsNullOrEmpty(_certificatePath))
        {
            try
            {
                _server.Certificate = new X509Certificate2(_certificatePath, _certificatePassword);
                _logger.LogInformation("SSL certificate loaded from {Path}", _certificatePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load SSL certificate, falling back to WS");
                // Fallback to non-SSL
                _server = new WebSocketServer($"ws://0.0.0.0:{_port}");
            }
        }

        _server.Start(ConfigureSocket);
        _logger.LogInformation("WebSocket server started on {Location}", location);
    }

    private void ConfigureSocket(IWebSocketConnection socket)
    {
        socket.OnOpen = () => OnSocketOpen(socket);
        socket.OnClose = () => OnSocketClose(socket);
        socket.OnMessage = message => OnSocketMessage(socket, message);
        socket.OnBinary = data => OnSocketBinary(socket, data);
        socket.OnError = ex => OnSocketError(socket, ex);
    }

    private void OnSocketOpen(IWebSocketConnection socket)
    {
        _logger.LogInformation("Client connected: {ClientIp}:{Port}",
            socket.ConnectionInfo.ClientIpAddress,
            socket.ConnectionInfo.ClientPort);

        ClientConnected?.Invoke(this, new ClientConnectedEventArgs(socket));
    }

    private void OnSocketClose(IWebSocketConnection socket)
    {
        _sessionManager.RemoveSessionBySocket(socket);
        _logger.LogInformation("Client disconnected: {SocketId}", socket.ConnectionInfo.Id);

        ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(socket));
    }

    private async void OnSocketMessage(IWebSocketConnection socket, string message)
    {
        _logger.LogDebug("Received message from {SocketId}: {Message}", socket.ConnectionInfo.Id, message);

        try
        {
            // Handle legacy protocol (backwards compatibility)
            if (message == ProtocolActions.LegacyWakeUp)
            {
                await HandleLegacyWakeUp(socket);
                return;
            }

            // Parse V2 protocol message
            var request = ProtocolSerializer.ParseRequest(message);
            if (request == null)
            {
                await SendError(socket, null, ErrorCodes.InvalidRequest, "Invalid JSON format");
                return;
            }

            await HandleRequest(socket, request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            await SendError(socket, null, ErrorCodes.InternalError, ex.Message);
        }

        MessageReceived?.Invoke(this, new MessageReceivedEventArgs(socket, message));
    }

    private void OnSocketBinary(IWebSocketConnection socket, byte[] data)
    {
        _logger.LogDebug("Received binary data from {SocketId}: {Length} bytes",
            socket.ConnectionInfo.Id, data.Length);
    }

    private void OnSocketError(IWebSocketConnection socket, Exception ex)
    {
        _logger.LogError(ex, "WebSocket error for {SocketId}", socket.ConnectionInfo.Id);
    }

    private async Task HandleRequest(IWebSocketConnection socket, ScanRequest request)
    {
        switch (request.Action.ToLowerInvariant())
        {
            case ProtocolActions.Authenticate:
                await HandleAuthenticate(socket, request);
                break;

            case ProtocolActions.Ping:
                await HandlePing(socket, request);
                break;

            case ProtocolActions.ListScanners:
                await HandleListScanners(socket, request);
                break;

            case ProtocolActions.SelectScanner:
                await HandleSelectScanner(socket, request);
                break;

            case ProtocolActions.GetCapabilities:
                await HandleGetCapabilities(socket, request);
                break;

            case ProtocolActions.GetDeviceCapabilities:
                await SendError(socket, request.RequestId, ErrorCodes.InvalidRequest,
                    "get_device_capabilities requires DualWebSocketService (multi-protocol mode)");
                break;

            case ProtocolActions.ApplyDeviceSettings:
                await SendError(socket, request.RequestId, ErrorCodes.InvalidRequest,
                    "apply_device_settings requires DualWebSocketService (multi-protocol mode)");
                break;

            case ProtocolActions.Scan:
                await HandleScan(socket, request);
                break;

            case ProtocolActions.StopScan:
                await HandleStopScan(socket, request);
                break;

            default:
                await SendError(socket, request.RequestId, ErrorCodes.InvalidRequest, $"Unknown action: {request.Action}");
                break;
        }
    }

    #region Request Handlers

    private async Task HandleAuthenticate(IWebSocketConnection socket, ScanRequest request)
    {
        var session = _sessionManager.CreateSession(socket, request.RequestId);
        if (session == null)
        {
            await SendError(socket, request.RequestId, ErrorCodes.InternalError, "Failed to create session");
            return;
        }

        var response = new AuthResponse
        {
            Action = ProtocolActions.Authenticate,
            RequestId = request.RequestId,
            Token = session.Token,
            ExpiresAt = session.ExpiresAt,
            Message = "Authentication successful"
        };

        await SendResponse(socket, response);
    }

    private async Task HandlePing(IWebSocketConnection socket, ScanRequest request)
    {
        var response = new ScanResponse
        {
            Action = ProtocolActions.Pong,
            RequestId = request.RequestId,
            Message = "pong"
        };
        await SendResponse(socket, response);
    }

    private async Task HandleListScanners(IWebSocketConnection socket, ScanRequest request)
    {
        if (!await ValidateSession(socket, request)) return;

        var scanners = _scannerService.GetAvailableScanners();
        var response = new ScannersResponse
        {
            Action = ProtocolActions.ListScanners,
            RequestId = request.RequestId,
            Scanners = scanners
        };

        await SendResponse(socket, response);
    }

    private async Task HandleSelectScanner(IWebSocketConnection socket, ScanRequest request)
    {
        if (!await ValidateSession(socket, request)) return;

        var session = _sessionManager.GetSessionBySocket(socket);
        var scannerName = request.Settings?.Source;

        if (string.IsNullOrEmpty(scannerName))
        {
            await SendError(socket, request.RequestId, ErrorCodes.InvalidRequest, "Scanner name required in settings.source");
            return;
        }

        var success = _scannerService.SelectScanner(scannerName);
        if (success && session != null)
        {
            session.SelectedScanner = scannerName;
            var response = new ScanResponse
            {
                Action = ProtocolActions.SelectScanner,
                RequestId = request.RequestId,
                Message = $"Scanner '{scannerName}' selected"
            };
            await SendResponse(socket, response);
        }
        else
        {
            await SendError(socket, request.RequestId, ErrorCodes.ScannerNotFound, $"Scanner '{scannerName}' not found");
        }
    }

    private async Task HandleGetCapabilities(IWebSocketConnection socket, ScanRequest request)
    {
        if (!await ValidateSession(socket, request)) return;

        var capabilities = _scannerService.GetCurrentScannerCapabilities();
        if (capabilities == null)
        {
            await SendError(socket, request.RequestId, ErrorCodes.ScannerNotFound, "No scanner selected");
            return;
        }

        var scannerInfo = new ScannerInfo
        {
            Name = _scannerService.CurrentScannerName ?? "Unknown",
            Capabilities = capabilities
        };

        var response = new ScannersResponse
        {
            Action = ProtocolActions.GetCapabilities,
            RequestId = request.RequestId,
            Scanners = new List<ScannerInfo> { scannerInfo }
        };

        await SendResponse(socket, response);
    }

    private async Task HandleScan(IWebSocketConnection socket, ScanRequest request)
    {
        if (!await ValidateSession(socket, request)) return;

        var session = _sessionManager.GetSessionBySocket(socket);
        if (session == null) return;

        if (session.IsScanning)
        {
            await SendError(socket, request.RequestId, ErrorCodes.ScannerBusy, "Scan already in progress");
            return;
        }

        session.IsScanning = true;
        session.CurrentScanRequestId = request.RequestId;

        try
        {
            // Apply scan settings if provided
            if (request.Settings != null)
            {
                _scannerService.ApplySettings(request.Settings);
            }

            // Start scan - images will be sent via ImageScanned event
            var success = await _scannerService.StartScanAsync(session, request.RequestId);
            if (!success)
            {
                await SendError(socket, request.RequestId, ErrorCodes.ScanFailed, "Failed to start scan");
                session.IsScanning = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            await SendError(socket, request.RequestId, ErrorCodes.ScanFailed, ex.Message);
            session.IsScanning = false;
        }
    }

    private async Task HandleStopScan(IWebSocketConnection socket, ScanRequest request)
    {
        if (!await ValidateSession(socket, request)) return;

        var session = _sessionManager.GetSessionBySocket(socket);
        if (session == null) return;

        _scannerService.StopScan();
        session.IsScanning = false;

        var response = new ScanResponse
        {
            Action = ProtocolActions.StopScan,
            RequestId = request.RequestId,
            Status = ResponseStatus.Cancelled,
            Message = "Scan stopped"
        };

        await SendResponse(socket, response);
    }

    private Task HandleLegacyWakeUp(IWebSocketConnection socket)
    {
        _logger.LogInformation("Legacy wake-up request received from {SocketId}", socket.ConnectionInfo.Id);

        // Create session for legacy client
        var session = _sessionManager.GetSessionBySocket(socket);
        if (session == null)
        {
            session = _sessionManager.CreateSession(socket, "legacy-client");
        }

        // Trigger UI wake-up event
        MessageReceived?.Invoke(this, new MessageReceivedEventArgs(socket, ProtocolActions.LegacyWakeUp));
        return Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private async Task<bool> ValidateSession(IWebSocketConnection socket, ScanRequest request)
    {
        // Allow authenticate action without token
        if (request.Action == ProtocolActions.Authenticate)
            return true;

        // Check for token in request
        if (!string.IsNullOrEmpty(request.Token))
        {
            var session = _sessionManager.ValidateToken(request.Token);
            if (session != null)
                return true;

            await SendError(socket, request.RequestId, ErrorCodes.InvalidToken, "Invalid or expired token");
            return false;
        }

        // Check for existing session by socket
        var existingSession = _sessionManager.GetSessionBySocket(socket);
        if (existingSession != null)
            return true;

        await SendError(socket, request.RequestId, ErrorCodes.Unauthorized, "Authentication required");
        return false;
    }

    public async Task SendResponse<T>(IWebSocketConnection socket, T response) where T : ScanResponse
    {
        var json = ProtocolSerializer.Serialize(response);
        await Task.Run(() => socket.Send(json));
    }

    public async Task SendError(IWebSocketConnection socket, string? requestId, string errorCode, string message)
    {
        var response = new ErrorResponse
        {
            RequestId = requestId ?? string.Empty,
            ErrorCode = errorCode,
            Message = message,
            ErrorDetails = message
        };

        await SendResponse(socket, response);
    }

    /// <summary>
    /// Sends binary image data to a specific session (not broadcast)
    /// </summary>
    public async Task SendImageToSession(ClientSession session, byte[] imageData, ImageMetadata metadata, string requestId, int pageNumber)
    {
        if (session.Socket == null || !session.Socket.IsAvailable)
        {
            _logger.LogWarning("Cannot send image - socket not available for session {ClientId}", session.ClientId);
            return;
        }

        // Send as JSON response with base64 data
        var response = new ImageResponse
        {
            Action = ProtocolActions.Scan,
            RequestId = requestId,
            Status = ResponseStatus.Scanning,
            Metadata = metadata,
            Data = Convert.ToBase64String(imageData),
            PageNumber = pageNumber
        };

        await SendResponse(session.Socket, response);
        _logger.LogInformation("Image sent to session {ClientId}, page {Page}", session.ClientId, pageNumber);
    }

    /// <summary>
    /// Sends scan completion notification to a specific session
    /// </summary>
    public async Task SendScanComplete(ClientSession session, string requestId, int totalPages)
    {
        if (session.Socket == null || !session.Socket.IsAvailable) return;

        session.IsScanning = false;
        session.CurrentScanRequestId = null;

        var response = new ImageResponse
        {
            Action = ProtocolActions.Scan,
            RequestId = requestId,
            Status = ResponseStatus.Completed,
            Message = $"Scan completed. Total pages: {totalPages}",
            TotalPages = totalPages
        };

        await SendResponse(session.Socket, response);
    }

    #endregion

    /// <summary>
    /// Stops the WebSocket server
    /// </summary>
    public void Stop()
    {
        _server?.Dispose();
        _logger.LogInformation("WebSocket server stopped");
    }

    public void Dispose()
    {
        Stop();
    }
}

#region Event Args

public class ClientConnectedEventArgs : EventArgs
{
    public IWebSocketConnection Socket { get; }
    public ClientConnectedEventArgs(IWebSocketConnection socket) => Socket = socket;
}

public class ClientDisconnectedEventArgs : EventArgs
{
    public IWebSocketConnection Socket { get; }
    public ClientDisconnectedEventArgs(IWebSocketConnection socket) => Socket = socket;
}

public class MessageReceivedEventArgs : EventArgs
{
    public IWebSocketConnection Socket { get; }
    public string Message { get; }
    public MessageReceivedEventArgs(IWebSocketConnection socket, string message)
    {
        Socket = socket;
        Message = message;
    }
}

#endregion

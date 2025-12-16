using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Fleck;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Models;

namespace ScanWithWeb.Services;

/// <summary>
/// Dual WebSocket service that supports both WS and WSS simultaneously
/// - WSS for secure HTTPS pages (default port 8181)
/// - WS for HTTP pages (default port 8180)
/// </summary>
public class DualWebSocketService : IDisposable
{
    private readonly ILogger<DualWebSocketService> _logger;
    private readonly SessionManager _sessionManager;
    private readonly ScannerService _scannerService;
    private readonly ScannerManager? _scannerManager;
    private readonly CertificateManager _certificateManager;

    private WebSocketServer? _wssServer;
    private WebSocketServer? _wsServer;

    private readonly int _wssPort;
    private readonly int _wsPort;
    private X509Certificate2? _certificate;

    private readonly object _scanHandlersLock = new();
    private readonly Dictionary<string, (EventHandler<ProtocolImageScannedEventArgs> Image, EventHandler<ProtocolScanCompletedEventArgs> Completed, EventHandler<ProtocolScanErrorEventArgs> Error)> _scanHandlersByRequestId = new();

    public bool IsWssRunning { get; private set; }
    public bool IsWsRunning { get; private set; }

    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    public DualWebSocketService(
        ILogger<DualWebSocketService> logger,
        SessionManager sessionManager,
        ScannerService scannerService,
        CertificateManager certificateManager,
        int wssPort = 8181,
        int wsPort = 8180)
        : this(logger, sessionManager, scannerService, null, certificateManager, wssPort, wsPort)
    {
    }

    public DualWebSocketService(
        ILogger<DualWebSocketService> logger,
        SessionManager sessionManager,
        ScannerService scannerService,
        ScannerManager? scannerManager,
        CertificateManager certificateManager,
        int wssPort = 8181,
        int wsPort = 8180)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _scannerService = scannerService;
        _scannerManager = scannerManager;
        _certificateManager = certificateManager;
        _wssPort = wssPort;
        _wsPort = wsPort;
    }

    /// <summary>
    /// Starts both WS and WSS servers
    /// </summary>
    public void Start()
    {
        // Start WS server (non-secure)
        StartWsServer();

        // Start WSS server (secure)
        StartWssServer();

        LogConnectionInfo();
    }

    private void StartWsServer()
    {
        try
        {
            var location = $"ws://0.0.0.0:{_wsPort}";
            _wsServer = new WebSocketServer(location);
            _wsServer.Start(ConfigureSocket);
            IsWsRunning = true;
            _logger.LogInformation("WS server started on port {Port}", _wsPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start WS server on port {Port}", _wsPort);
            IsWsRunning = false;
        }
    }

    private void StartWssServer()
    {
        try
        {
            // Get or create SSL certificate
            _certificate = _certificateManager.GetOrCreateCertificate();

            if (_certificate == null)
            {
                _logger.LogWarning("No SSL certificate available, WSS server will not start");
                IsWssRunning = false;
                return;
            }

            var location = $"wss://0.0.0.0:{_wssPort}";
            _wssServer = new WebSocketServer(location)
            {
                Certificate = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            _wssServer.Start(ConfigureSocket);
            IsWssRunning = true;
            _logger.LogInformation("WSS server started on port {Port}", _wssPort);

            // Log certificate trust instructions
            _logger.LogInformation(_certificateManager.GetTrustInstructions());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start WSS server on port {Port}", _wssPort);
            IsWssRunning = false;
        }
    }

    private void LogConnectionInfo()
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("ScanWithWeb Service Started");
        _logger.LogInformation("========================================");

        if (IsWsRunning)
        {
            _logger.LogInformation("WS  (HTTP pages):  ws://localhost:{Port}", _wsPort);
        }

        if (IsWssRunning)
        {
            _logger.LogInformation("WSS (HTTPS pages): wss://localhost:{Port}", _wssPort);
        }

        _logger.LogInformation("========================================");

        if (!IsWsRunning && !IsWssRunning)
        {
            _logger.LogError("No WebSocket servers are running!");
        }
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
        var isSecure = socket.ConnectionInfo.Path?.StartsWith("wss") ?? false;
        _logger.LogInformation("Client connected ({Protocol}): {ClientIp}:{Port}",
            isSecure ? "WSS" : "WS",
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
                await HandleGetDeviceCapabilities(socket, request);
                break;

            case ProtocolActions.ApplyDeviceSettings:
                await HandleApplyDeviceSettings(socket, request);
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

        List<ScannerInfo> scanners;

        // Get protocol filter from request settings
        var protocolFilter = request.Settings?.Protocols;

        // Use ScannerManager if available (multi-protocol support)
        if (_scannerManager != null)
        {
            scanners = await _scannerManager.GetAllScannersAsync(protocolFilter);
        }
        else
        {
            scanners = _scannerService.GetAvailableScanners();
        }

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

        bool success;

        // Use ScannerManager if available (multi-protocol support)
        if (_scannerManager != null)
        {
            success = _scannerManager.SelectScanner(scannerName);
        }
        else
        {
            success = _scannerService.SelectScanner(scannerName);
        }

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

        ScannerCapabilities? capabilities;
        string scannerName;

        // Use ScannerManager if available (multi-protocol support)
        if (_scannerManager != null)
        {
            var currentId = _scannerManager.CurrentScannerId;
            if (string.IsNullOrEmpty(currentId))
            {
                await SendError(socket, request.RequestId, ErrorCodes.ScannerNotFound, "No scanner selected");
                return;
            }
            capabilities = _scannerManager.GetCapabilities(currentId);
            scannerName = currentId;
        }
        else
        {
            capabilities = _scannerService.GetCurrentScannerCapabilities();
            scannerName = _scannerService.CurrentScannerName ?? "Unknown";
        }

        if (capabilities == null)
        {
            await SendError(socket, request.RequestId, ErrorCodes.ScannerNotFound, "No scanner selected");
            return;
        }

        var scannerInfo = new ScannerInfo
        {
            Name = scannerName,
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

        _logger.LogInformation(
            "Scan requested: RequestId={RequestId}, ClientId={ClientId}, SocketId={SocketId}, IP={IP}, SelectedScanner={Scanner}, Settings={Settings}",
            request.RequestId,
            session.ClientId,
            socket.ConnectionInfo.Id,
            socket.ConnectionInfo.ClientIpAddress,
            session.SelectedScanner ?? "(none)",
            request.Settings == null
                ? "(none)"
                : $"dpi={request.Settings.Dpi}, pixelType={request.Settings.PixelType}, paperSize={request.Settings.PaperSize}, useAdf={request.Settings.UseAdf}, duplex={request.Settings.Duplex}, showUI={request.Settings.ShowUI}, continuousScan={request.Settings.ContinuousScan}, maxPages={request.Settings.MaxPages}");

        if (session.IsScanning)
        {
            _logger.LogWarning(
                "Rejecting scan: already in progress for ClientId={ClientId}, CurrentRequestId={CurrentRequestId}",
                session.ClientId,
                session.CurrentScanRequestId ?? "(none)");
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
                _logger.LogDebug("Applying scan settings for RequestId={RequestId}", request.RequestId);
                if (_scannerManager != null)
                {
                    _scannerManager.ApplySettings(request.Settings);
                }
                else
                {
                    _scannerService.ApplySettings(request.Settings);
                }
            }

            bool success;

            // Use ScannerManager if available (multi-protocol support)
            if (_scannerManager != null)
            {
                // Wire up event handlers for this scan
                WireUpScannerManagerEvents(session, request.RequestId);
                success = await _scannerManager.StartScanAsync(request.RequestId);
            }
            else
            {
                // Start scan - images will be sent via ImageScanned event
                success = await _scannerService.StartScanAsync(session, request.RequestId);
            }

            if (!success)
            {
                var hint = request.Settings?.ShowUI == true
                    ? string.Empty
                    : " If you're using a TWAIN scanner that requires driver UI, try settings.showUI=true.";
                await SendError(socket, request.RequestId, ErrorCodes.ScanFailed, $"Failed to start scan.{hint}");
                session.IsScanning = false;
                if (_scannerManager != null)
                {
                    UnwireScannerManagerEvents(request.RequestId);
                }
            }
            else
            {
                _logger.LogInformation("Scan started: RequestId={RequestId}, ClientId={ClientId}", request.RequestId, session.ClientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            await SendError(socket, request.RequestId, ErrorCodes.ScanFailed, ex.Message);
            session.IsScanning = false;
            if (_scannerManager != null)
            {
                UnwireScannerManagerEvents(request.RequestId);
            }
        }
    }

    private void WireUpScannerManagerEvents(ClientSession session, string requestId)
    {
        if (_scannerManager == null) return;

        lock (_scanHandlersLock)
        {
            if (_scanHandlersByRequestId.ContainsKey(requestId))
            {
                return;
            }
        }

        // Handle image scanned event
        void onImageScanned(object? sender, Models.ProtocolImageScannedEventArgs e)
        {
            if (e.RequestId != requestId) return;

            _ = Task.Run(async () =>
            {
                await SendImageToSession(session, e.ImageData, e.Metadata, e.RequestId, e.PageNumber);
            });
        }

        // Handle scan completed event
        void onScanCompleted(object? sender, Models.ProtocolScanCompletedEventArgs e)
        {
            if (e.RequestId != requestId) return;

            _ = Task.Run(async () =>
            {
                await SendScanComplete(session, e.RequestId, e.TotalPages);
            });

            UnwireScannerManagerEvents(requestId);
        }

        // Handle scan error event
        void onScanError(object? sender, Models.ProtocolScanErrorEventArgs e)
        {
            if (e.RequestId != requestId) return;

            _ = Task.Run(async () =>
            {
                if (session.Socket != null && session.Socket.IsAvailable)
                {
                    await SendError(session.Socket, e.RequestId, ErrorCodes.ScanFailed, e.ErrorMessage);
                }
                session.IsScanning = false;
            });

            UnwireScannerManagerEvents(requestId);
        }

        lock (_scanHandlersLock)
        {
            _scanHandlersByRequestId[requestId] = (onImageScanned, onScanCompleted, onScanError);
        }

        _scannerManager.ImageScanned += onImageScanned;
        _scannerManager.ScanCompleted += onScanCompleted;
        _scannerManager.ScanError += onScanError;
    }

    private void UnwireScannerManagerEvents(string requestId)
    {
        if (_scannerManager == null) return;

        (EventHandler<ProtocolImageScannedEventArgs> Image, EventHandler<ProtocolScanCompletedEventArgs> Completed, EventHandler<ProtocolScanErrorEventArgs> Error) handlers;
        lock (_scanHandlersLock)
        {
            if (!_scanHandlersByRequestId.TryGetValue(requestId, out handlers))
            {
                return;
            }
            _scanHandlersByRequestId.Remove(requestId);
        }

        _scannerManager.ImageScanned -= handlers.Image;
        _scannerManager.ScanCompleted -= handlers.Completed;
        _scannerManager.ScanError -= handlers.Error;
    }

    private async Task HandleStopScan(IWebSocketConnection socket, ScanRequest request)
    {
        if (!await ValidateSession(socket, request)) return;

        var session = _sessionManager.GetSessionBySocket(socket);
        if (session == null) return;

        _logger.LogInformation(
            "Stop requested: RequestId={RequestId}, ClientId={ClientId}, CurrentScanRequestId={CurrentRequestId}",
            request.RequestId,
            session.ClientId,
            session.CurrentScanRequestId ?? "(none)");

        // Use ScannerManager if available (multi-protocol support)
        if (_scannerManager != null)
        {
            // Detach scan-specific handlers to avoid leaking and to prevent late "error/complete" messages
            // after we've acknowledged cancellation.
            if (!string.IsNullOrEmpty(session.CurrentScanRequestId))
            {
                UnwireScannerManagerEvents(session.CurrentScanRequestId);
            }

            _scannerManager.StopScan();
        }
        else
        {
            _scannerService.StopScan();
        }

        session.IsScanning = false;
        session.CurrentScanRequestId = null;

        var response = new ScanResponse
        {
            Action = ProtocolActions.StopScan,
            RequestId = request.RequestId,
            Status = ResponseStatus.Cancelled,
            Message = "Scan stopped"
        };

        await SendResponse(socket, response);
    }

    private async Task HandleGetDeviceCapabilities(IWebSocketConnection socket, ScanRequest request)
    {
        if (!await ValidateSession(socket, request)) return;

        if (_scannerManager == null)
        {
            await SendError(socket, request.RequestId, ErrorCodes.InternalError, "ScannerManager not available");
            return;
        }

        var (scannerId, protocol, capabilities) = await _scannerManager.GetDeviceCapabilitiesAsync();
        if (scannerId == null || protocol == null)
        {
            await SendError(socket, request.RequestId, ErrorCodes.ScannerNotFound, "No scanner selected");
            return;
        }

        var response = new DeviceCapabilitiesResponse
        {
            Action = ProtocolActions.GetDeviceCapabilities,
            RequestId = request.RequestId,
            ScannerId = scannerId,
            Protocol = protocol,
            Capabilities = capabilities
        };

        await SendResponse(socket, response);
    }

    private async Task HandleApplyDeviceSettings(IWebSocketConnection socket, ScanRequest request)
    {
        if (!await ValidateSession(socket, request)) return;

        if (_scannerManager == null)
        {
            await SendError(socket, request.RequestId, ErrorCodes.InternalError, "ScannerManager not available");
            return;
        }

        if (request.Patch == null && (request.Advanced == null || request.Advanced.Count == 0))
        {
            await SendError(socket, request.RequestId, ErrorCodes.InvalidRequest, "patch or advanced is required");
            return;
        }

        var patch = request.Patch ?? new DeviceSettingsPatch();
        var (scannerId, protocol, results) = await _scannerManager.ApplyDeviceSettingsAsync(patch, request.Advanced);
        if (scannerId == null || protocol == null)
        {
            await SendError(socket, request.RequestId, ErrorCodes.ScannerNotFound, "No scanner selected");
            return;
        }

        var response = new ApplyDeviceSettingsResponse
        {
            Action = ProtocolActions.ApplyDeviceSettings,
            RequestId = request.RequestId,
            ScannerId = scannerId,
            Protocol = protocol,
            Results = results
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
    /// Gets connection information for display
    /// </summary>
    public string GetConnectionInfo()
    {
        var info = new System.Text.StringBuilder();
        info.AppendLine("Connection URLs:");

        if (IsWsRunning)
        {
            info.AppendLine($"  WS:  ws://localhost:{_wsPort}");
        }

        if (IsWssRunning)
        {
            info.AppendLine($"  WSS: wss://localhost:{_wssPort}");
        }

        return info.ToString();
    }

    /// <summary>
    /// Stops both WebSocket servers
    /// </summary>
    public void Stop()
    {
        _wsServer?.Dispose();
        _wssServer?.Dispose();
        _certificate?.Dispose();
        IsWsRunning = false;
        IsWssRunning = false;
        _logger.LogInformation("WebSocket servers stopped");
    }

    public void Dispose()
    {
        Stop();
    }
}

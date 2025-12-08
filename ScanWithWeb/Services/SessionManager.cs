using System.Collections.Concurrent;
using System.Security.Cryptography;
using Fleck;
using Microsoft.Extensions.Logging;

namespace ScanWithWeb.Services;

/// <summary>
/// Manages client sessions with token-based authentication
/// </summary>
public class SessionManager
{
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _socketToToken = new();
    private readonly ILogger<SessionManager> _logger;
    private readonly int _tokenExpirationMinutes;
    private readonly int _maxConcurrentSessions;
    private readonly System.Threading.Timer _cleanupTimer;

    public SessionManager(ILogger<SessionManager> logger, int tokenExpirationMinutes = 60, int maxConcurrentSessions = 10)
    {
        _logger = logger;
        _tokenExpirationMinutes = tokenExpirationMinutes;
        _maxConcurrentSessions = maxConcurrentSessions;

        // Cleanup expired sessions every 5 minutes
        _cleanupTimer = new System.Threading.Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Creates a new session for a WebSocket connection
    /// </summary>
    public ClientSession? CreateSession(IWebSocketConnection socket, string? clientId = null)
    {
        if (_sessions.Count >= _maxConcurrentSessions)
        {
            _logger.LogWarning("Maximum concurrent sessions reached ({Max})", _maxConcurrentSessions);
            return null;
        }

        var token = GenerateToken();
        var session = new ClientSession
        {
            Token = token,
            SocketId = socket.ConnectionInfo.Id,
            Socket = socket,
            ClientId = clientId ?? socket.ConnectionInfo.Id.ToString(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_tokenExpirationMinutes),
            LastActivity = DateTime.UtcNow
        };

        if (_sessions.TryAdd(token, session))
        {
            _socketToToken[socket.ConnectionInfo.Id] = token;
            _logger.LogInformation("Session created for client {ClientId}, socket {SocketId}", session.ClientId, session.SocketId);
            return session;
        }

        return null;
    }

    /// <summary>
    /// Validates a token and returns the associated session
    /// </summary>
    public ClientSession? ValidateToken(string token)
    {
        if (_sessions.TryGetValue(token, out var session))
        {
            if (session.ExpiresAt > DateTime.UtcNow)
            {
                session.LastActivity = DateTime.UtcNow;
                return session;
            }
            else
            {
                _logger.LogInformation("Token expired for client {ClientId}", session.ClientId);
                RemoveSession(token);
            }
        }
        return null;
    }

    /// <summary>
    /// Gets session by WebSocket connection
    /// </summary>
    public ClientSession? GetSessionBySocket(IWebSocketConnection socket)
    {
        if (_socketToToken.TryGetValue(socket.ConnectionInfo.Id, out var token))
        {
            return ValidateToken(token);
        }
        return null;
    }

    /// <summary>
    /// Gets session by socket ID
    /// </summary>
    public ClientSession? GetSessionBySocketId(Guid socketId)
    {
        if (_socketToToken.TryGetValue(socketId, out var token))
        {
            return ValidateToken(token);
        }
        return null;
    }

    /// <summary>
    /// Removes a session
    /// </summary>
    public void RemoveSession(string token)
    {
        if (_sessions.TryRemove(token, out var session))
        {
            _socketToToken.TryRemove(session.SocketId, out _);
            _logger.LogInformation("Session removed for client {ClientId}", session.ClientId);
        }
    }

    /// <summary>
    /// Removes session by socket
    /// </summary>
    public void RemoveSessionBySocket(IWebSocketConnection socket)
    {
        if (_socketToToken.TryRemove(socket.ConnectionInfo.Id, out var token))
        {
            _sessions.TryRemove(token, out _);
            _logger.LogInformation("Session removed for socket {SocketId}", socket.ConnectionInfo.Id);
        }
    }

    /// <summary>
    /// Renews a session token
    /// </summary>
    public ClientSession? RenewSession(string token)
    {
        if (_sessions.TryGetValue(token, out var session))
        {
            session.ExpiresAt = DateTime.UtcNow.AddMinutes(_tokenExpirationMinutes);
            session.LastActivity = DateTime.UtcNow;
            _logger.LogInformation("Session renewed for client {ClientId}", session.ClientId);
            return session;
        }
        return null;
    }

    /// <summary>
    /// Gets all active sessions
    /// </summary>
    public IEnumerable<ClientSession> GetActiveSessions()
    {
        return _sessions.Values.Where(s => s.ExpiresAt > DateTime.UtcNow);
    }

    /// <summary>
    /// Gets session count
    /// </summary>
    public int SessionCount => _sessions.Count;

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private void CleanupExpiredSessions(object? state)
    {
        var expiredTokens = _sessions
            .Where(kvp => kvp.Value.ExpiresAt <= DateTime.UtcNow)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var token in expiredTokens)
        {
            RemoveSession(token);
        }

        if (expiredTokens.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired sessions", expiredTokens.Count);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}

/// <summary>
/// Represents a client session
/// </summary>
public class ClientSession
{
    public string Token { get; set; } = string.Empty;
    public Guid SocketId { get; set; }
    public IWebSocketConnection? Socket { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastActivity { get; set; }

    /// <summary>
    /// Currently selected scanner for this session
    /// </summary>
    public string? SelectedScanner { get; set; }

    /// <summary>
    /// Current scan request ID being processed
    /// </summary>
    public string? CurrentScanRequestId { get; set; }

    /// <summary>
    /// Whether this session is currently scanning
    /// </summary>
    public bool IsScanning { get; set; }
}

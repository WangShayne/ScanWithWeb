using Microsoft.Extensions.Logging;
using ScanWithWeb.Services;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ScanWithWeb;

/// <summary>
/// Main application form with system tray integration
/// Modern, clean UI design focused on service status
/// </summary>
public partial class MainForm : Form
{
    private readonly ILogger<MainForm> _logger;
    private readonly DualWebSocketService _webSocketService;
    private readonly ScannerService _scannerService;
    private readonly ScannerManager? _scannerManager;
    private readonly SessionManager _sessionManager;

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _trayMenu;

    // UI Controls
    private Panel? _headerPanel;
    private Label? _titleLabel;
    private Label? _versionLabel;
    private Icon? _appIcon;

    private Panel? _statusPanel;
    private Label? _wsStatusIcon;
    private Label? _wsStatusLabel;
    private Label? _wssStatusIcon;
    private Label? _wssStatusLabel;
    private Label? _scannerStatusIcon;
    private Label? _scannerStatusLabel;

    private ListView? _listConnections;
    private Label? _lblStatus;

    // Colors
    private readonly Color _primaryColor = Color.FromArgb(41, 128, 185);    // Blue
    private readonly Color _successColor = Color.FromArgb(39, 174, 96);     // Green
    private readonly Color _errorColor = Color.FromArgb(192, 57, 43);       // Red
    private readonly Color _warningColor = Color.FromArgb(243, 156, 18);    // Orange
    private readonly Color _bgColor = Color.FromArgb(248, 249, 250);        // Light gray
    private readonly Color _cardColor = Color.White;

    public MainForm(
        ILogger<MainForm> logger,
        DualWebSocketService webSocketService,
        ScannerService scannerService,
        ScannerManager scannerManager,
        SessionManager sessionManager)
    {
        _logger = logger;
        _webSocketService = webSocketService;
        _scannerService = scannerService;
        _scannerManager = scannerManager;
        _sessionManager = sessionManager;

        InitializeComponent();
        SetupTrayIcon();
        SetupEventHandlers();
    }

    private void InitializeComponent()
    {
        // Form settings - larger size for 4K displays
        this.Text = "ScanWithWeb Service";
        this.Size = new Size(650, 700);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.BackColor = _bgColor;
        this.Font = new Font("Segoe UI", 10F);

        // Set form icon from PNG (since .ico file is actually PNG format)
        try
        {
            var pngPath = Path.Combine(Application.StartupPath, "Resources", "ScanWithWeb.png");
            if (File.Exists(pngPath))
            {
                using var bitmap = new Bitmap(pngPath);
                _appIcon = Icon.FromHandle(bitmap.GetHicon());
                this.Icon = _appIcon;
            }
        }
        catch { /* Use default icon */ }

        // Main layout
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(20),
            BackColor = _bgColor
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));   // Header
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));  // Status cards (single line per row)
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // Connections (takes remaining space)
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // Status bar

        // Header panel
        CreateHeaderPanel();
        mainPanel.Controls.Add(_headerPanel!, 0, 0);

        // Service status panel
        CreateStatusPanel();
        mainPanel.Controls.Add(_statusPanel!, 0, 1);

        // Connections list
        var connectionsCard = CreateConnectionsCard();
        mainPanel.Controls.Add(connectionsCard, 0, 2);

        // Status bar
        _lblStatus = new Label
        {
            Text = "Starting service...",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(100, 100, 100),
            Font = new Font("Segoe UI", 9F)
        };
        mainPanel.Controls.Add(_lblStatus, 0, 3);

        this.Controls.Add(mainPanel);
    }

    private void CreateHeaderPanel()
    {
        _headerPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _bgColor
        };

        // Title (no logo)
        var bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";
        _titleLabel = new Label
        {
            Text = "ScanWithWeb Service",
            Font = new Font("Segoe UI Semibold", 18F),
            ForeColor = Color.FromArgb(33, 37, 41),
            AutoSize = true,
            Location = new Point(0, 5)
        };

        _versionLabel = new Label
        {
            Text = $"v3.0.8 ({bits})",
            Font = new Font("Segoe UI", 10F),
            ForeColor = Color.FromArgb(108, 117, 125),
            AutoSize = true,
            Location = new Point(2, 45)
        };

        // Actions (right aligned)
        var actionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            // Right-to-left prevents the left-most button from being clipped on smaller widths / high DPI scaling.
            // We want the most important actions to remain visible (e.g., Test Page).
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 18, 0, 0),
            BackColor = _bgColor
        };

        Button CreateHeaderButton(string text, Color backColor)
        {
            var btn = new Button
            {
                Text = text,
                Font = new Font("Segoe UI", 9F),
                Size = new Size(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 0, 8, 0),
                Padding = new Padding(0)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        var btnTestPage = CreateHeaderButton("Test Page", _primaryColor);
        btnTestPage.Click += (s, e) => OpenTestPage();

        var btnScanner = CreateHeaderButton("ShowUI", _successColor);
        btnScanner.Click += async (s, e) => await OpenShowUIAsync();

        _headerPanel.Controls.Add(_titleLabel);
        _headerPanel.Controls.Add(_versionLabel);
        // With RightToLeft flow, add in priority order (highest priority first) so it stays visible if clipped.
        actionsPanel.Controls.Add(btnTestPage);
        actionsPanel.Controls.Add(btnScanner);
        _headerPanel.Controls.Add(actionsPanel);
    }

    private void CreateStatusPanel()
    {
        _statusPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 5, 0, 5)
        };

        // Use a simple Panel with fixed-position child panels instead of TableLayoutPanel
        var statusCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _cardColor,
            Padding = new Padding(15, 10, 15, 10)
        };

        // Round corners for card
        statusCard.Paint += (s, e) =>
        {
            using var path = GetRoundedRectPath(statusCard.ClientRectangle, 8);
            using var pen = new Pen(Color.FromArgb(222, 226, 230), 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        };

        // Create three status rows with fixed positions (top to bottom)
        const int rowHeight = 30;
        const int startY = 10;

        // WS Status Row (top)
        var wsRow = CreateStatusRow(
            out _wsStatusIcon, out _wsStatusLabel,
            "WS Service (HTTP)",
            "ws://localhost:8180",
            startY
        );
        statusCard.Controls.Add(wsRow);

        // WSS Status Row (middle)
        var wssRow = CreateStatusRow(
            out _wssStatusIcon, out _wssStatusLabel,
            "WSS Service (HTTPS)",
            "wss://localhost:8181",
            startY + rowHeight
        );
        statusCard.Controls.Add(wssRow);

        // Scanner Status Row (bottom)
        var scannerRow = CreateStatusRow(
            out _scannerStatusIcon, out _scannerStatusLabel,
            "Scanner Driver",
            "Checking...",
            startY + rowHeight * 2
        );
        statusCard.Controls.Add(scannerRow);

        _statusPanel.Controls.Add(statusCard);
    }

    private Panel CreateStatusRow(out Label iconLabel, out Label statusLabel, string title, string initialStatus, int yPosition)
    {
        var row = new Panel
        {
            Location = new Point(15, yPosition),
            Size = new Size(580, 26),
            BackColor = _cardColor
        };

        // Status icon (circle indicator)
        iconLabel = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 10F),
            ForeColor = Color.FromArgb(200, 200, 200),
            AutoSize = false,
            Size = new Size(20, 20),
            Location = new Point(0, 3),
            TextAlign = ContentAlignment.MiddleCenter
        };

        // Title
        var titleLbl = new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = Color.FromArgb(33, 37, 41),
            AutoSize = false,
            Size = new Size(140, 20),
            Location = new Point(22, 3),
            TextAlign = ContentAlignment.MiddleLeft
        };

        // Status/URL - positioned to the right of title
        statusLabel = new Label
        {
            Text = initialStatus,
            Font = new Font("Consolas", 9F),
            ForeColor = Color.FromArgb(108, 117, 125),
            AutoSize = false,
            Size = new Size(400, 20),
            Location = new Point(165, 3),
            TextAlign = ContentAlignment.MiddleLeft
        };

        row.Controls.Add(iconLabel);
        row.Controls.Add(titleLbl);
        row.Controls.Add(statusLabel);

        return row;
    }

    private Panel CreateConnectionsCard()
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 10, 0, 0)
        };

        var innerCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _cardColor,
            Padding = new Padding(1)
        };

        // Title
        var titlePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = _cardColor,
            Padding = new Padding(15, 10, 15, 5)
        };

        var titleLabel = new Label
        {
            Text = "Active Connections",
            Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = Color.FromArgb(33, 37, 41),
            AutoSize = true
        };
        titlePanel.Controls.Add(titleLabel);

        // Connection list - larger font
        _listConnections = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            BackColor = _cardColor,
            Font = new Font("Segoe UI", 10F)
        };
        _listConnections.Columns.Add("Client", 120);
        _listConnections.Columns.Add("IP Address", 130);
        _listConnections.Columns.Add("Protocol", 80);
        _listConnections.Columns.Add("Status", 100);
        _listConnections.Columns.Add("Time", 90);

        innerCard.Controls.Add(_listConnections);
        innerCard.Controls.Add(titlePanel);

        // Border
        innerCard.Paint += (s, e) =>
        {
            using var pen = new Pen(Color.FromArgb(222, 226, 230), 1);
            e.Graphics.DrawRectangle(pen, 0, 0, innerCard.Width - 1, innerCard.Height - 1);
        };

        card.Controls.Add(innerCard);
        return card;
    }

    private static GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void SetupTrayIcon()
    {
        // Create context menu
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Open Dashboard", null, (s, e) => ShowWindow());
        _trayMenu.Items.Add("Test Page", null, (s, e) => OpenTestPage());
        _trayMenu.Items.Add("Scanner Settings", null, (s, e) => OpenScannerSettings());
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

        // Create notify icon
        _notifyIcon = new NotifyIcon
        {
            Text = "ScanWithWeb Service",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };

        // Use app icon for tray (already loaded in InitializeComponent)
        if (_appIcon != null)
        {
            _notifyIcon.Icon = _appIcon;
        }
        else
        {
            // Fallback: try to load from PNG
            try
            {
                var pngPath = Path.Combine(Application.StartupPath, "Resources", "ScanWithWeb.png");
                if (File.Exists(pngPath))
                {
                    using var bitmap = new Bitmap(pngPath);
                    _notifyIcon.Icon = Icon.FromHandle(bitmap.GetHicon());
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }

        _notifyIcon.DoubleClick += (s, e) => ShowWindow();
    }

    private void SetupEventHandlers()
    {
        // WebSocket events
        _webSocketService.ClientConnected += OnClientConnected;
        _webSocketService.ClientDisconnected += OnClientDisconnected;
        _webSocketService.MessageReceived += OnMessageReceived;

        // Scanner events
        _scannerService.ImageScanned += OnImageScanned;
        _scannerService.ScanCompleted += OnScanCompleted;
        _scannerService.ScanError += OnScanError;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        bool scannersAvailable = false;

        // Initialize ScannerManager (multi-protocol) if available
        if (_scannerManager != null)
        {
            _scannerManager.Initialize(this.Handle);
            scannersAvailable = _scannerManager.GetAvailableProtocols().Any(p => p.IsAvailable);

            if (!scannersAvailable)
            {
                _logger.LogWarning("No scanner protocols available");
                UpdateScannerStatus(false, "No scanner protocols available");
            }
        }
        else
        {
            // Fallback: Initialize TWAIN only
            var twainInitialized = _scannerService.Initialize(this.Handle);

            if (!twainInitialized)
            {
                _logger.LogWarning("TWAIN initialization failed: {Error}", _scannerService.TwainError);
                UpdateScannerStatus(false, _scannerService.TwainError ?? "TWAIN not available");
            }
            scannersAvailable = twainInitialized;
        }

        // Start WebSocket service (both WS and WSS)
        _webSocketService.Start();

        // Update status display
        UpdateServiceStatus();

        UpdateStatus(scannersAvailable ? "Service running - waiting for connections" : "Service running - Scanner not available");
    }

    private void UpdateServiceStatus()
    {
        // WS Status
        if (_wsStatusIcon != null && _wsStatusLabel != null)
        {
            if (_webSocketService.IsWsRunning)
            {
                _wsStatusIcon.ForeColor = _successColor;
                _wsStatusLabel.Text = "ws://localhost:8180";
                _wsStatusLabel.ForeColor = _successColor;
            }
            else
            {
                _wsStatusIcon.ForeColor = _errorColor;
                _wsStatusLabel.Text = "Not available";
                _wsStatusLabel.ForeColor = _errorColor;
            }
        }

        // WSS Status
        if (_wssStatusIcon != null && _wssStatusLabel != null)
        {
            if (_webSocketService.IsWssRunning)
            {
                _wssStatusIcon.ForeColor = _successColor;
                _wssStatusLabel.Text = "wss://localhost:8181";
                _wssStatusLabel.ForeColor = _successColor;
            }
            else
            {
                _wssStatusIcon.ForeColor = _errorColor;
                _wssStatusLabel.Text = "Not available - check certificate";
                _wssStatusLabel.ForeColor = _errorColor;
            }
        }

        // Scanner Status - use ScannerManager if available
        if (_scannerStatusIcon != null && _scannerStatusLabel != null)
        {
            if (_scannerManager != null)
            {
                // Multi-protocol scanner status
                // Use synchronous method to avoid deadlock on UI thread
                var protocols = _scannerManager.GetAvailableProtocols()
                    .Where(p => p.IsAvailable)
                    .Select(p => p.ProtocolName.ToUpper())
                    .ToList();

                if (protocols.Count > 0)
                {
                    // Show protocols first, then load scanner count async
                    _scannerStatusIcon.ForeColor = _successColor;
                    var bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";
                    _scannerStatusLabel.Text = $"Loading... ({bits}, {string.Join("/", protocols)})";
                    _scannerStatusLabel.ForeColor = _successColor;

                    // Load scanner count asynchronously to avoid UI deadlock
                    _ = LoadScannerCountAsync(protocols);
                }
                else
                {
                    _scannerStatusIcon.ForeColor = _errorColor;
                    _scannerStatusLabel.Text = "No scanner protocols available";
                    _scannerStatusLabel.ForeColor = _errorColor;
                }
            }
            else
            {
                // Legacy TWAIN-only status
                if (!_scannerService.IsTwainAvailable)
                {
                    _scannerStatusIcon.ForeColor = _errorColor;
                    _scannerStatusLabel.Text = _scannerService.TwainError ?? "TWAIN not available";
                    _scannerStatusLabel.ForeColor = _errorColor;
                }
                else
                {
                    var scanners = _scannerService.GetAvailableScanners();
                    if (scanners.Count > 0)
                    {
                        _scannerStatusIcon.ForeColor = _successColor;
                        _scannerStatusLabel.Text = $"{scanners.Count} scanner(s) available";
                        _scannerStatusLabel.ForeColor = _successColor;
                    }
                    else
                    {
                        _scannerStatusIcon.ForeColor = _warningColor;
                        var bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";
                        _scannerStatusLabel.Text = $"No scanners detected ({bits} mode)";
                        _scannerStatusLabel.ForeColor = _warningColor;
                    }
                }
            }
        }
    }

    private async Task LoadScannerCountAsync(List<string> protocols)
    {
        if (_scannerManager == null) return;

        try
        {
            // Run scanner discovery off the UI thread to avoid deadlock
            var scanners = await Task.Run(() => _scannerManager.GetAllScannersAsync());

            // Update UI on the UI thread
            if (InvokeRequired)
            {
                Invoke(() => UpdateScannerCountUI(scanners.Count, protocols));
            }
            else
            {
                UpdateScannerCountUI(scanners.Count, protocols);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading scanner count");
        }
    }

    private void UpdateScannerCountUI(int scannerCount, List<string> protocols)
    {
        if (_scannerStatusIcon == null || _scannerStatusLabel == null) return;

        var bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";

        if (scannerCount > 0)
        {
            _scannerStatusIcon.ForeColor = _successColor;
            var protocolInfo = protocols.Count > 0 ? $" ({string.Join(", ", protocols)})" : "";
            _scannerStatusLabel.Text = $"{scannerCount} scanner(s) available{protocolInfo}";
            _scannerStatusLabel.ForeColor = _successColor;
        }
        else
        {
            _scannerStatusIcon.ForeColor = _warningColor;
            // Show helpful message for 64-bit mode - TWAIN requires 32-bit
            if (Environment.Is64BitProcess)
            {
                _scannerStatusLabel.Text = $"No scanners - TWAIN needs 32-bit app";
            }
            else
            {
                _scannerStatusLabel.Text = $"No scanners detected ({bits})";
            }
            _scannerStatusLabel.ForeColor = _warningColor;
        }
    }

    private void UpdateScannerStatus(bool available, string message)
    {
        if (_scannerStatusIcon != null && _scannerStatusLabel != null)
        {
            _scannerStatusIcon.ForeColor = available ? _successColor : _errorColor;
            _scannerStatusLabel.Text = message;
            _scannerStatusLabel.ForeColor = available ? _successColor : _errorColor;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Minimize to tray instead of closing
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        // Actually closing - cleanup
        _webSocketService.Stop();
        _scannerManager?.Shutdown();
        _scannerService.Close();

        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (WindowState == FormWindowState.Minimized)
        {
            MinimizeToTray();
        }
    }

    #region Event Handlers

    private void OnClientConnected(object? sender, ClientConnectedEventArgs e)
    {
        this.BeginInvoke(() =>
        {
            var item = new ListViewItem(new[]
            {
                e.Socket.ConnectionInfo.Id.ToString()[..8],
                e.Socket.ConnectionInfo.ClientIpAddress,
                "WS",
                "Connected",
                DateTime.Now.ToString("HH:mm:ss")
            });
            item.Tag = e.Socket.ConnectionInfo.Id;
            _listConnections?.Items.Add(item);

            UpdateStatus($"Client connected: {e.Socket.ConnectionInfo.ClientIpAddress}");
            ShowNotification("Client Connected", $"New connection from {e.Socket.ConnectionInfo.ClientIpAddress}");
        });
    }

    private void OnClientDisconnected(object? sender, ClientDisconnectedEventArgs e)
    {
        this.BeginInvoke(() =>
        {
            if (_listConnections != null)
            {
                foreach (ListViewItem item in _listConnections.Items)
                {
                    if (item.Tag is Guid id && id == e.Socket.ConnectionInfo.Id)
                    {
                        _listConnections.Items.Remove(item);
                        break;
                    }
                }
            }

            UpdateStatus("Client disconnected");
        });
    }

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        // Handle legacy wake-up message
        if (e.Message == Models.ProtocolActions.LegacyWakeUp)
        {
            this.BeginInvoke(() =>
            {
                ShowWindow();
            });
        }
    }

    private async void OnImageScanned(object? sender, ImageScannedEventArgs e)
    {
        _logger.LogInformation("Image scanned: Page {Page}", e.PageNumber);

        // Send to requesting client only
        if (e.Session?.Socket != null)
        {
            await _webSocketService.SendImageToSession(e.Session, e.ImageData, e.Metadata, e.RequestId, e.PageNumber);
        }

        this.BeginInvoke(() =>
        {
            UpdateStatus($"Scanned page {e.PageNumber}");
        });
    }

    private async void OnScanCompleted(object? sender, ScanCompletedEventArgs e)
    {
        if (e.Session?.Socket != null)
        {
            await _webSocketService.SendScanComplete(e.Session, e.RequestId, e.TotalPages);
        }

        this.BeginInvoke(() =>
        {
            UpdateStatus($"Scan completed - {e.TotalPages} page(s)");
            ShowNotification("Scan Complete", $"Scanned {e.TotalPages} page(s)");
        });
    }

    private void OnScanError(object? sender, ScanErrorEventArgs e)
    {
        _logger.LogError("Scan error: RequestId={RequestId}, ClientId={ClientId}, Message={Error}",
            e.RequestId,
            e.Session?.ClientId ?? "(unknown)",
            e.ErrorMessage);

        this.BeginInvoke(() =>
        {
            UpdateStatus($"Scan error: {e.ErrorMessage}");
        });
    }

    #endregion

    #region UI Helpers

    private void UpdateStatus(string message)
    {
        if (_lblStatus != null)
        {
            _lblStatus.Text = message;
        }
        _logger.LogInformation(message);
    }

    private void ShowWindow()
    {
        this.Show();
        this.WindowState = FormWindowState.Normal;
        this.ShowInTaskbar = true;
        this.Activate();
    }

    private void MinimizeToTray()
    {
        this.Hide();
        this.ShowInTaskbar = false;
        ShowNotification("ScanWithWeb", "Running in background. Double-click to open.");
    }

    private void ShowNotification(string title, string message)
    {
        _notifyIcon?.ShowBalloonTip(2000, title, message, ToolTipIcon.Info);
    }

    private void OpenTestPage()
    {
        try
        {
            // Get the test page path
            var testPagePath = Path.Combine(Application.StartupPath, "Resources", "TestPage.html");

            if (File.Exists(testPagePath))
            {
                // Open in default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = testPagePath,
                    UseShellExecute = true
                });
                _logger.LogInformation("Opened test page: {Path}", testPagePath);
            }
            else
            {
                // If not found, try to extract from resources or show error
                _logger.LogWarning("Test page not found at: {Path}", testPagePath);
                MessageBox.Show(
                    $"Test page not found at:\n{testPagePath}",
                    "Test Page",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open test page");
            MessageBox.Show(
                $"Failed to open test page:\n{ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OpenScannerSettings()
    {
        try
        {
            if (_scannerManager == null)
            {
                MessageBox.Show(
                    "Scanner Manager is not available in this build.\n\nUse the Test Page to select scanners.",
                    "Scanner Settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using var form = new ScannerSettingsForm(_logger, _scannerManager);
            var result = form.ShowDialog(this);
            if (result == DialogResult.OK)
            {
                ShowNotification("Scanner Settings", "Scanner preference saved");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open scanner settings");
            MessageBox.Show(
                $"Failed to open scanner settings:\n{ex.Message}",
                "Scanner Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool EnsureScannerSelectedForUi()
    {
        if (_scannerManager == null) return false;

        if (!string.IsNullOrWhiteSpace(_scannerManager.CurrentScannerId))
            return true;

        try
        {
            // Try to auto-select the persisted default scanner (if any).
            var prefs = UserPreferences.Load(_logger);
            if (!string.IsNullOrWhiteSpace(prefs.DefaultScannerId))
            {
                if (_scannerManager.SelectScanner(prefs.DefaultScannerId))
                {
                    _logger.LogInformation("Auto-selected default scanner for ShowUI: {Id}", prefs.DefaultScannerId);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-select default scanner for ShowUI");
        }

        var result = MessageBox.Show(
            "No scanner is currently selected.\n\nOpen Scanner Settings to select one now?",
            "ShowUI",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
        {
            OpenScannerSettings();
            return !string.IsNullOrWhiteSpace(_scannerManager.CurrentScannerId);
        }

        return false;
    }

    private async Task OpenShowUIAsync()
    {
        try
        {
            if (_scannerManager == null)
            {
                MessageBox.Show(
                    "Scanner Manager is not available in this build.\n\nUse the Test Page to interact with scanners.",
                    "ShowUI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (_scannerManager.IsScanning)
            {
                MessageBox.Show(
                    "A scan is already in progress.\n\nStop the current scan before opening the driver UI.",
                    "ShowUI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!EnsureScannerSelectedForUi())
                return;

            // Force vendor driver UI for this one-off "configure" run.
            var settings = _scannerManager.CurrentSettings;
            settings.ShowUI = true;

            // Safety: if unlimited, default to a single transfer so users don't accidentally scan an entire stack
            // when they only want to open the UI to adjust settings.
            if (settings.MaxPages <= 0)
                settings.MaxPages = 1;

            _scannerManager.ApplySettings(settings);

            var requestId = Guid.NewGuid().ToString("N");
            var ok = await _scannerManager.StartScanAsync(requestId);
            if (!ok)
            {
                MessageBox.Show(
                    "Failed to open the scanner driver UI.\n\nIf your TWAIN driver requires UI, ensure it supports ShowUI mode and try again.",
                    "ShowUI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open ShowUI");
            MessageBox.Show(
                $"Failed to open scanner driver UI:\n{ex.Message}",
                "ShowUI",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ExitApplication()
    {
        _notifyIcon?.Dispose();
        Application.Exit();
    }

    #endregion
}

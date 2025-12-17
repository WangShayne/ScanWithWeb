using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Models;
using ScanWithWeb.Services;

namespace ScanWithWeb;

public sealed class ScannerSettingsForm : Form
{
    private readonly ILogger _logger;
    private readonly ScannerManager _scannerManager;

    private readonly ComboBox _scannersCombo;
    private readonly Label _statusLabel;
    private readonly Button _btnSave;

    private List<ScannerInfo> _scanners = new();

    public ScannerSettingsForm(ILogger logger, ScannerManager scannerManager)
    {
        _logger = logger;
        _scannerManager = scannerManager;

        Text = "Scanner Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Width = 640;
        Height = 280;
        Font = new Font("Segoe UI", 10F);

        var prefs = UserPreferences.Load(logger);

        _btnSave = new Button
        {
            Text = "Save Default",
            DialogResult = DialogResult.OK,
            Width = 120,
            Enabled = false,
        };
        _btnSave.Click += (_, _) =>
        {
            var selected = GetSelectedScanner();
            if (selected == null)
            {
                DialogResult = DialogResult.None;
                return;
            }

            prefs.DefaultScannerId = selected.Id;
            prefs.DefaultScannerProtocol = selected.Protocol;
            prefs.Save(_logger);
        };

        _scannersCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _scannersCombo.SelectedIndexChanged += (_, _) =>
        {
            _btnSave.Enabled = _scannersCombo.SelectedIndex >= 0;
            UpdateStatus();
        };

        var btnRefresh = new Button
        {
            Text = "Refresh",
            Width = 90,
        };
        btnRefresh.Click += async (_, _) => await RefreshScannersAsync(prefs);

        var btnSelectNow = new Button
        {
            Text = "Select Now",
            Width = 110,
        };
        btnSelectNow.Click += (_, _) =>
        {
            var selected = GetSelectedScanner();
            if (selected == null)
            {
                return;
            }

            var ok = _scannerManager.SelectScanner(selected.Id);
            if (ok)
            {
                _logger.LogInformation("Selected scanner via UI: {Id}", selected.Id);
                MessageBox.Show(
                    $"Selected scanner:\n{selected.Name}\n({selected.Id})",
                    "Scanner Selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    $"Failed to select scanner:\n{selected.Name}\n({selected.Id})",
                    "Scanner Selection Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };

        var btnWindows = new Button
        {
            Text = "Windows Devices…",
            Width = 150,
        };
        btnWindows.Click += (_, _) => OpenWindowsDevices();

        _statusLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 90, 90),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Select a scanner device to use by default for the service.",
            AutoSize = true,
            ForeColor = Color.FromArgb(60, 60, 60),
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(title, 0, 0);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        row.Controls.Add(new Label
        {
            Text = "Scanner:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 6, 6),
        }, 0, 0);
        row.Controls.Add(_scannersCombo, 1, 0);
        layout.Controls.Add(row, 0, 1);

        layout.Controls.Add(_statusLabel, 0, 2);

        var tip = new Label
        {
            Text = "Tip: If your Canon DR-6050C TWAIN UI shows single-page only, try disabling “Continuous Scan (Auto)” in the test page.",
            AutoSize = true,
            ForeColor = Color.FromArgb(120, 120, 120),
            Margin = new Padding(0, 10, 0, 0),
        };
        layout.Controls.Add(tip, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
        };

        var btnClose = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Width = 90,
        };

        buttons.Controls.Add(btnClose);
        buttons.Controls.Add(_btnSave);
        buttons.Controls.Add(btnSelectNow);
        buttons.Controls.Add(btnRefresh);
        buttons.Controls.Add(btnWindows);

        layout.Controls.Add(buttons, 0, 4);

        Controls.Add(layout);

        Shown += async (_, _) => await RefreshScannersAsync(prefs);
    }

    private ScannerInfo? GetSelectedScanner()
    {
        var idx = _scannersCombo.SelectedIndex;
        if (idx < 0 || idx >= _scanners.Count) return null;
        return _scanners[idx];
    }

    private void UpdateStatus()
    {
        var selected = GetSelectedScanner();
        if (selected == null)
        {
            _statusLabel.Text = "No scanner selected.";
            return;
        }

        _statusLabel.Text = $"Selected: {selected.Name} ({selected.Id})";
    }

    private async Task RefreshScannersAsync(UserPreferences prefs)
    {
        try
        {
            _statusLabel.Text = "Refreshing scanners...";
            _scannersCombo.Items.Clear();
            _scanners.Clear();

            // ScannerManager provides protocol-prefixed IDs, which is what the Web protocol expects.
            _scanners = await _scannerManager.GetAllScannersAsync();

            foreach (var s in _scanners)
            {
                var protocolLabel = string.IsNullOrWhiteSpace(s.Protocol) ? string.Empty : $"[{s.Protocol.ToUpperInvariant()}] ";
                _scannersCombo.Items.Add(protocolLabel + s.Name);
            }

            if (_scanners.Count == 0)
            {
                _statusLabel.Text = "No scanners found. Ensure you are running the correct (x86/x64) build for your TWAIN driver.";
                _btnSave.Enabled = false;
                return;
            }

            // Restore saved default selection if present.
            var targetId = prefs.DefaultScannerId;
            if (!string.IsNullOrWhiteSpace(targetId))
            {
                var foundIndex = _scanners.FindIndex(s => string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase));
                if (foundIndex >= 0)
                {
                    _scannersCombo.SelectedIndex = foundIndex;
                    UpdateStatus();
                    return;
                }
            }

            // Otherwise pick current selected scanner if any.
            var currentId = _scannerManager.CurrentScannerId;
            if (!string.IsNullOrWhiteSpace(currentId))
            {
                var foundIndex = _scanners.FindIndex(s => string.Equals(s.Id, currentId, StringComparison.OrdinalIgnoreCase));
                if (foundIndex >= 0)
                {
                    _scannersCombo.SelectedIndex = foundIndex;
                    UpdateStatus();
                    return;
                }
            }

            _scannersCombo.SelectedIndex = 0;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh scanners");
            _statusLabel.Text = $"Failed to refresh scanners: {ex.Message}";
        }
    }

    private void OpenWindowsDevices()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to open ms-settings, trying control.exe fallback");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "control.exe",
                    Arguments = "/name Microsoft.DevicesAndPrinters",
                    UseShellExecute = true
                });
            }
            catch (Exception ex2)
            {
                _logger.LogWarning(ex2, "Failed to open Windows devices UI");
            }
        }
    }
}

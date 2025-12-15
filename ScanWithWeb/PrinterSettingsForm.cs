using System.Diagnostics;
using System.Drawing.Printing;
using Microsoft.Extensions.Logging;
using ScanWithWeb.Services;

namespace ScanWithWeb;

public sealed class PrinterSettingsForm : Form
{
    private readonly ILogger _logger;
    private readonly ComboBox _printersCombo;
    private readonly Label _currentValueLabel;
    private readonly Button _btnSave;

    private string? _selectedPrinter;

    public PrinterSettingsForm(ILogger logger)
    {
        _logger = logger;

        Text = "Printer Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Width = 520;
        Height = 240;
        Font = new Font("Segoe UI", 10F);

        var prefs = UserPreferences.Load(logger);

        _btnSave = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Width = 90,
            Enabled = false,
        };
        _btnSave.Click += (_, _) =>
        {
            var current = _selectedPrinter;
            if (string.IsNullOrWhiteSpace(current))
            {
                DialogResult = DialogResult.None;
                return;
            }

            prefs.DefaultPrinter = current;
            prefs.Save(_logger);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Default printer used by ScanWithWeb (when printing is not configured via Web).",
            AutoSize = true,
            ForeColor = Color.FromArgb(60, 60, 60),
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(title, 0, 0);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        row.Controls.Add(new Label
        {
            Text = "Printer:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 6, 6),
        }, 0, 0);

        _printersCombo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 3, 0, 6),
        };
        _printersCombo.SelectedIndexChanged += (_, _) =>
        {
            _selectedPrinter = _printersCombo.SelectedItem as string;
            UpdateCurrentValueLabel();
            _btnSave.Enabled = !string.IsNullOrWhiteSpace(_selectedPrinter);
        };
        row.Controls.Add(_printersCombo, 1, 0);

        row.Controls.Add(new Label
        {
            Text = "Current:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 6, 0),
        }, 0, 1);

        _currentValueLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(80, 80, 80),
            Margin = new Padding(0, 6, 0, 0),
        };
        row.Controls.Add(_currentValueLabel, 1, 1);

        layout.Controls.Add(row, 0, 1);

        var hint = new Label
        {
            Text = "Tip: If your driver requires TWAIN UI, enable “Show Scanner UI (TWAIN)” on the test page.",
            AutoSize = true,
            ForeColor = Color.FromArgb(120, 120, 120),
            Margin = new Padding(0, 10, 0, 0),
        };
        layout.Controls.Add(hint, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = 90,
        };

        var btnWindows = new Button
        {
            Text = "Windows Printers…",
            Width = 160,
        };
        btnWindows.Click += (_, _) => OpenWindowsPrinterSettings();

        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(_btnSave);
        buttons.Controls.Add(btnWindows);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);

        LoadPrinters(prefs.DefaultPrinter);
    }

    private void LoadPrinters(string? savedPrinter)
    {
        _printersCombo.Items.Clear();

        foreach (string printer in PrinterSettings.InstalledPrinters)
        {
            _printersCombo.Items.Add(printer);
        }

        var systemDefault = new PrinterSettings().PrinterName;

        var initial = !string.IsNullOrWhiteSpace(savedPrinter) ? savedPrinter : systemDefault;
        _selectedPrinter = null;

        if (!string.IsNullOrWhiteSpace(initial))
        {
            for (var i = 0; i < _printersCombo.Items.Count; i++)
            {
                if (string.Equals(_printersCombo.Items[i] as string, initial, StringComparison.OrdinalIgnoreCase))
                {
                    _printersCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        if (_printersCombo.SelectedIndex < 0 && _printersCombo.Items.Count > 0)
        {
            _printersCombo.SelectedIndex = 0;
        }

        UpdateCurrentValueLabel();
    }

    private void UpdateCurrentValueLabel()
    {
        if (string.IsNullOrWhiteSpace(_selectedPrinter))
        {
            _currentValueLabel.Text = "(not set)";
        }
        else
        {
            _currentValueLabel.Text = _selectedPrinter;
        }
    }

    private void OpenWindowsPrinterSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:printers",
                UseShellExecute = true
            });
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to open ms-settings:printers, trying control.exe fallback");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "control.exe",
                Arguments = "printers",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open Windows printer settings");
            MessageBox.Show(
                $"Failed to open Windows printer settings:\n{ex.Message}",
                "Printer Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}

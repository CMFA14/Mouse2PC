using System.Net;
using System.Net.Sockets;
using Mouse2PC.Core;

namespace Mouse2PC.UI;

public class MainForm : Form
{
    // Mesma paleta escura do painel de layout
    private static readonly Color BackDark = Color.FromArgb(32, 32, 32);
    private static readonly Color CardBack = Color.FromArgb(43, 43, 43);
    private static readonly Color TextDim = Color.FromArgb(170, 170, 170);

    private readonly AppConfig _config = AppConfig.Load();

    private ControllerEngine? _engine;
    private ControlledEndpoint? _endpoint;

    private readonly RadioButton _rbController;
    private readonly RadioButton _rbControlled;
    private readonly TextBox _txtHost;
    private readonly NumericUpDown _numPort;
    private readonly Button _btnStartStop;
    private readonly Button _btnLayout;
    private readonly Label _lblStatus;
    private readonly Label _lblLocalIps;

    private bool _running;

    public MainForm()
    {
        Text = "Mouse2PC";
        ClientSize = new Size(480, 360);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = BackDark;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);

        var lblTitle = new Label
        {
            Text = "Mouse2PC",
            Font = new Font("Segoe UI Semibold", 15f),
            AutoSize = true,
            Location = new Point(20, 14),
        };

        var lblMode = new Label
        {
            Text = "Papel deste computador",
            ForeColor = TextDim,
            Left = 20, Top = 52, AutoSize = true,
        };

        _rbController = new RadioButton
        {
            Text = "Controlador — o mouse e o teclado físicos estão aqui",
            Left = 28, Top = 76, AutoSize = true,
        };
        _rbControlled = new RadioButton
        {
            Text = "Controlado — recebe o mouse vindo do outro PC",
            Left = 28, Top = 102, AutoSize = true,
        };

        var lblHost = new Label { Text = "IP do PC controlado:", Left = 20, Top = 142, AutoSize = true };
        _txtHost = new TextBox
        {
            Left = 160, Top = 138, Width = 160,
            BackColor = CardBack, ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var lblPort = new Label { Text = "Porta:", Left = 335, Top = 142, AutoSize = true };
        _numPort = new NumericUpDown
        {
            Left = 385, Top = 138, Width = 75,
            Minimum = 1024, Maximum = 65535, Value = 24801,
            BackColor = CardBack, ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _lblLocalIps = new Label
        {
            Left = 20, Top = 172, Width = 440, Height = 20,
            ForeColor = TextDim,
        };

        _btnStartStop = MakeButton("Iniciar", accent: true);
        _btnStartStop.Location = new Point(20, 202);
        _btnStartStop.Click += (_, _) => ToggleStartStop();

        _btnLayout = MakeButton("Configurar telas...", accent: false);
        _btnLayout.Location = new Point(250, 202);
        _btnLayout.Click += (_, _) => OpenLayout();

        _lblStatus = new Label
        {
            Left = 20, Top = 256, Width = 440, Height = 84,
            BackColor = CardBack,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0),
            Text = "Parado.",
        };

        Controls.AddRange(new Control[]
        {
            lblTitle, lblMode, _rbController, _rbControlled,
            lblHost, _txtHost, lblPort, _numPort,
            _lblLocalIps, _btnStartStop, _btnLayout, _lblStatus,
        });

        // Estado salvo
        _rbController.Checked = _config.Mode == "controller";
        _rbControlled.Checked = _config.Mode == "controlled";
        _txtHost.Text = _config.RemoteHost;
        _numPort.Value = Math.Clamp(_config.Port, 1024, 65535);

        _rbController.CheckedChanged += (_, _) => UpdateUiForMode();
        UpdateUiForMode();
        FormClosing += (_, _) => StopAll();
    }

    private static Button MakeButton(string text, bool accent)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(210, 38),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = accent ? Color.FromArgb(0, 110, 200) : Color.FromArgb(55, 55, 55),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
        b.FlatAppearance.BorderSize = accent ? 0 : 1;
        return b;
    }

    private void UpdateUiForMode()
    {
        bool controller = _rbController.Checked;
        _txtHost.Enabled = controller && !_running;
        _btnLayout.Visible = controller;
        _lblLocalIps.Text = controller
            ? ""
            : "IPs deste PC (use no controlador): " + GetLocalIps();
    }

    private static string GetLocalIps()
    {
        try
        {
            var ips = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString());
            return string.Join(", ", ips);
        }
        catch { return "(não detectado)"; }
    }

    private void ToggleStartStop()
    {
        if (_running) { StopAll(); return; }

        _config.Mode = _rbController.Checked ? "controller" : "controlled";
        _config.RemoteHost = _txtHost.Text.Trim();
        _config.Port = (int)_numPort.Value;
        _config.Save();

        if (_config.Mode == "controller")
        {
            if (string.IsNullOrWhiteSpace(_config.RemoteHost))
            {
                MessageBox.Show(this, "Informe o IP do PC controlado.", "Mouse2PC",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _engine = new ControllerEngine(_config);
            _engine.StatusChanged += SetStatus; // engine posta na thread de UI
            _engine.Start();
        }
        else
        {
            _endpoint = new ControlledEndpoint(_config.Port);
            _endpoint.StatusChanged += s => BeginInvoke(() => SetStatus(s));
            _endpoint.IdentifyRequested += nums => BeginInvoke(() => ShowIdentify(nums));
            _endpoint.Start();
        }

        _running = true;
        _btnStartStop.Text = "Parar";
        _rbController.Enabled = _rbControlled.Enabled = false;
        _numPort.Enabled = false;
        UpdateUiForMode();
    }

    // O controlador pediu "Identificar": pisca o número em cada tela deste PC
    // (mesma ordem de monitores enviada no hello).
    private void ShowIdentify(int[] numbers)
    {
        var screens = Screen.AllScreens
            .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .Select((s, i) => (s.Bounds, i < numbers.Length ? numbers[i] : i + 1));
        IdentifyOverlay.ShowNumbers(screens);
    }

    private void StopAll()
    {
        _engine?.Dispose();
        _engine = null;
        _endpoint?.Dispose();
        _endpoint = null;

        _running = false;
        _btnStartStop.Text = "Iniciar";
        _rbController.Enabled = _rbControlled.Enabled = true;
        _numPort.Enabled = true;
        UpdateUiForMode();
        SetStatus("Parado.");
    }

    private void SetStatus(string s) => _lblStatus.Text = s;

    private void OpenLayout()
    {
        if (_config.RemoteMonitorsCache.Count == 0)
        {
            MessageBox.Show(this,
                "As telas do PC remoto ainda não são conhecidas.\n" +
                "Inicie e conecte ao PC controlado ao menos uma vez; depois volte aqui.",
                "Mouse2PC", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var form = new LayoutForm(_config, _engine);
        if (form.ShowDialog(this) == DialogResult.OK)
            _engine?.ReloadLayout();
    }
}

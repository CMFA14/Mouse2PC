using System.Net;
using System.Net.Sockets;
using MouseLink.Core;

namespace MouseLink.UI;

public class MainForm : Form
{
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
        Text = "MouseLink";
        ClientSize = new Size(460, 320);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        var lblMode = new Label { Text = "Papel deste computador:", Left = 20, Top = 18, AutoSize = true };

        _rbController = new RadioButton
        {
            Text = "Controlador — o mouse e o teclado físicos estão aqui",
            Left = 30, Top = 42, AutoSize = true,
        };
        _rbControlled = new RadioButton
        {
            Text = "Controlado — recebe o mouse vindo do outro PC",
            Left = 30, Top = 66, AutoSize = true,
        };

        var lblHost = new Label { Text = "IP do PC controlado:", Left = 20, Top = 104, AutoSize = true };
        _txtHost = new TextBox { Left = 150, Top = 100, Width = 160 };

        var lblPort = new Label { Text = "Porta:", Left = 325, Top = 104, AutoSize = true };
        _numPort = new NumericUpDown
        {
            Left = 370, Top = 100, Width = 70,
            Minimum = 1024, Maximum = 65535, Value = 24801,
        };

        _lblLocalIps = new Label
        {
            Left = 20, Top = 132, Width = 420, Height = 18,
            ForeColor = Color.DimGray,
        };

        _btnStartStop = new Button { Text = "Iniciar", Left = 20, Top = 162, Width = 200, Height = 36 };
        _btnStartStop.Click += (_, _) => ToggleStartStop();

        _btnLayout = new Button { Text = "Configurar telas...", Left = 240, Top = 162, Width = 200, Height = 36 };
        _btnLayout.Click += (_, _) => OpenLayout();

        _lblStatus = new Label
        {
            Left = 20, Top = 216, Width = 420, Height = 80,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8),
            Text = "Parado.",
        };

        Controls.AddRange(new Control[]
        {
            lblMode, _rbController, _rbControlled,
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
                MessageBox.Show(this, "Informe o IP do PC controlado.", "MouseLink",
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
            _endpoint.Start();
        }

        _running = true;
        _btnStartStop.Text = "Parar";
        _rbController.Enabled = _rbControlled.Enabled = false;
        _numPort.Enabled = false;
        UpdateUiForMode();
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
                "MouseLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var form = new LayoutForm(_config);
        if (form.ShowDialog(this) == DialogResult.OK)
            _engine?.ReloadLayout();
    }
}

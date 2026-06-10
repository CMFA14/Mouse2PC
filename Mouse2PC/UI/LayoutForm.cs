using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Mouse2PC.Core;

namespace Mouse2PC.UI;

// Painel de configuração do layout, no estilo do painel de telas do Windows
// (Configurações > Sistema > Tela): retângulos arredondados numerados num
// canvas escuro, seleção com a cor de destaque e botão "Identificar".
// A posição relativa das telas define por qual borda o mouse atravessa.
public class LayoutForm : Form
{
    // Paleta inspirada nas Configurações do Windows 11 (tema escuro)
    private static readonly Color BackDark = Color.FromArgb(32, 32, 32);
    private static readonly Color CanvasBack = Color.FromArgb(43, 43, 43);
    private static readonly Color ScreenFill = Color.FromArgb(63, 63, 63);
    private static readonly Color ScreenFillHover = Color.FromArgb(72, 72, 72);
    private static readonly Color ScreenBorder = Color.FromArgb(96, 96, 96);
    private static readonly Color Accent = Color.FromArgb(76, 160, 224);
    private static readonly Color AccentDark = Color.FromArgb(38, 90, 130);
    private static readonly Color TextDim = Color.FromArgb(170, 170, 170);

    private readonly AppConfig _config;
    private readonly ControllerEngine? _engine;
    private readonly List<ScreenNode> _screens;

    private ScreenNode? _selected;
    private ScreenNode? _hovered;
    private ScreenNode? _dragging;
    private Point _dragOffsetVirtual; // cursor - canto da tela, em coords virtuais
    private const int SnapThreshold = 60; // em pixels virtuais

    private readonly Panel _canvas;
    private readonly Label _lblSelected;

    public LayoutForm(AppConfig config, ControllerEngine? engine)
    {
        _config = config;
        _engine = engine;
        _screens = VirtualLayout.Build(config, config.RemoteMonitorsCache).Screens;
        _selected = _screens.FirstOrDefault();

        Text = "Mouse2PC – Organizar telas";
        ClientSize = new Size(860, 600);
        MinimumSize = new Size(560, 440);
        BackColor = BackDark;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);

        var lblTitle = new Label
        {
            Text = "Organizar as telas",
            Font = new Font("Segoe UI Semibold", 15f),
            AutoSize = true,
            Location = new Point(24, 18),
        };

        var lblHint = new Label
        {
            Text = "Arraste as telas para reorganizá-las. As bordas encostadas são por onde o mouse atravessa.",
            ForeColor = TextDim,
            AutoSize = true,
            Location = new Point(25, 52),
        };

        _canvas = new DoubleBufferedPanel
        {
            BackColor = CanvasBack,
            Location = new Point(24, 84),
        };

        _lblSelected = new Label
        {
            ForeColor = TextDim,
            AutoSize = true,
        };

        var btnIdentify = MakeButton("Identificar", accent: false);
        btnIdentify.Click += (_, _) => Identify();

        var btnApply = MakeButton("Aplicar", accent: true);
        btnApply.Click += (_, _) => SaveAndClose();

        var btnCancel = MakeButton("Cancelar", accent: false);
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
            { lblTitle, lblHint, _canvas, _lblSelected, btnIdentify, btnApply, btnCancel });

        void Reposition()
        {
            _canvas.Size = new Size(ClientSize.Width - 48, ClientSize.Height - 84 - 76);
            btnCancel.Location = new Point(ClientSize.Width - 24 - btnCancel.Width, ClientSize.Height - 56);
            btnApply.Location = new Point(btnCancel.Left - 12 - btnApply.Width, ClientSize.Height - 56);
            btnIdentify.Location = new Point(24, ClientSize.Height - 56);
            _lblSelected.Location = new Point(btnIdentify.Right + 16, ClientSize.Height - 48);
        }
        Reposition();
        Resize += (_, _) => { Reposition(); _canvas.Invalidate(); };

        _canvas.Paint += OnCanvasPaint;
        _canvas.MouseDown += OnCanvasMouseDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseUp += (_, _) => { _dragging = null; _canvas.Invalidate(); };
        _canvas.MouseLeave += (_, _) => { _hovered = null; _canvas.Invalidate(); };

        UpdateSelectedLabel();
    }

    private static Button MakeButton(string text, bool accent)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(120, 34),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = accent ? Color.FromArgb(0, 110, 200) : Color.FromArgb(55, 55, 55),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
        b.FlatAppearance.BorderSize = accent ? 0 : 1;
        return b;
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    }

    private int NumberOf(ScreenNode s) => _screens.IndexOf(s) + 1;

    private string OwnerOf(ScreenNode s) => s.IsLocal ? "Este PC"
        : string.IsNullOrEmpty(_config.RemoteName) ? "Remoto" : _config.RemoteName;

    // ------------------------------------------------------------------
    // Mapeamento espaço virtual <-> canvas

    private (float scale, PointF offset) GetTransform()
    {
        var bounds = _screens.Aggregate(Rectangle.Empty,
            (acc, s) => acc.IsEmpty ? s.Virtual : Rectangle.Union(acc, s.Virtual));
        bounds.Inflate(bounds.Width / 6 + 300, bounds.Height / 6 + 300);

        float scale = Math.Min((float)_canvas.Width / bounds.Width,
                               (float)_canvas.Height / bounds.Height);
        var offset = new PointF(
            (_canvas.Width - bounds.Width * scale) / 2 - bounds.X * scale,
            (_canvas.Height - bounds.Height * scale) / 2 - bounds.Y * scale);
        return (scale, offset);
    }

    private static RectangleF ToCanvas(Rectangle v, float scale, PointF off) =>
        new(v.X * scale + off.X, v.Y * scale + off.Y, v.Width * scale, v.Height * scale);

    private static Point ToVirtual(Point canvasPt, float scale, PointF off) =>
        new((int)((canvasPt.X - off.X) / scale), (int)((canvasPt.Y - off.Y) / scale));

    // ------------------------------------------------------------------
    // Interação

    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        var (scale, off) = GetTransform();
        foreach (var s in Enumerable.Reverse(_screens))
        {
            if (ToCanvas(s.Virtual, scale, off).Contains(e.Location))
            {
                _selected = s;
                _dragging = s;
                var v = ToVirtual(e.Location, scale, off);
                _dragOffsetVirtual = new Point(v.X - s.Virtual.X, v.Y - s.Virtual.Y);
                UpdateSelectedLabel();
                _canvas.Invalidate();
                return;
            }
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        var (scale, off) = GetTransform();

        if (_dragging == null)
        {
            var prev = _hovered;
            _hovered = _screens.LastOrDefault(s => ToCanvas(s.Virtual, scale, off).Contains(e.Location));
            _canvas.Cursor = _hovered != null ? Cursors.SizeAll : Cursors.Default;
            if (prev != _hovered) _canvas.Invalidate();
            return;
        }

        var v = ToVirtual(e.Location, scale, off);
        var newPos = new Point(v.X - _dragOffsetVirtual.X, v.Y - _dragOffsetVirtual.Y);
        newPos = Snap(newPos, _dragging);

        if (newPos != _dragging.Virtual.Location)
        {
            _dragging.Virtual = new Rectangle(newPos, _dragging.Virtual.Size);
            _canvas.Invalidate();
        }
    }

    // Encosta a tela arrastada nas vizinhas quando fica perto.
    private Point Snap(Point pos, ScreenNode dragged)
    {
        var r = new Rectangle(pos, dragged.Virtual.Size);
        int bestDx = int.MaxValue, bestDy = int.MaxValue;

        foreach (var o in _screens.Where(s => s != dragged).Select(s => s.Virtual))
        {
            // Bordas verticais (atravessar para esquerda/direita).
            TrySnap(o.Right - r.Left, ref bestDx);
            TrySnap(o.Left - r.Right, ref bestDx);
            TrySnap(o.Left - r.Left, ref bestDx);
            // Bordas horizontais (atravessar para cima/baixo).
            TrySnap(o.Bottom - r.Top, ref bestDy);
            TrySnap(o.Top - r.Bottom, ref bestDy);
            TrySnap(o.Top - r.Top, ref bestDy);
        }

        return new Point(
            pos.X + (bestDx == int.MaxValue ? 0 : bestDx),
            pos.Y + (bestDy == int.MaxValue ? 0 : bestDy));
    }

    private static void TrySnap(int delta, ref int best)
    {
        if (Math.Abs(delta) <= SnapThreshold && Math.Abs(delta) < Math.Abs(best))
            best = delta;
    }

    // ------------------------------------------------------------------
    // Desenho

    private void OnCanvasPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var (scale, off) = GetTransform();

        foreach (var s in _screens)
        {
            var r = ToCanvas(s.Virtual, scale, off);
            r.Inflate(-2, -2); // respiro entre telas encostadas, como no Windows
            bool selected = s == _selected;
            bool hovered = s == _hovered || s == _dragging;

            using var path = RoundedRect(r, 8);
            using var fill = new SolidBrush(
                selected ? AccentDark : hovered ? ScreenFillHover : ScreenFill);
            using var pen = new Pen(selected ? Accent : ScreenBorder, selected ? 2.5f : 1.5f);

            g.FillPath(fill, path);
            g.DrawPath(pen, path);

            // Número grande no centro
            float numSize = Math.Clamp(r.Height * 0.34f, 14f, 64f);
            using var numFont = new Font("Segoe UI", numSize);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(NumberOf(s).ToString(), numFont, Brushes.White, r, sf);

            // Dono da tela, discreto, na parte de baixo
            using var tagFont = new Font("Segoe UI", Math.Clamp(r.Height * 0.085f, 8f, 12f));
            using var tagBrush = new SolidBrush(selected ? Color.White : TextDim);
            var tagRect = new RectangleF(r.X + 4, r.Bottom - r.Height * 0.24f,
                                         r.Width - 8, r.Height * 0.18f);
            using var sfTag = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(OwnerOf(s), tagFont, tagBrush, tagRect, sfTag);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void UpdateSelectedLabel()
    {
        _lblSelected.Text = _selected == null ? "" :
            $"Tela {NumberOf(_selected)} — {OwnerOf(_selected)}, " +
            $"{_selected.Physical.Width}×{_selected.Physical.Height}";
    }

    // ------------------------------------------------------------------

    // Pisca o número em cada tela real: locais aqui mesmo; remotas via rede.
    private void Identify()
    {
        var locals = new List<(Rectangle, int)>();
        var remoteNumbers = new int[_screens.Count(s => !s.IsLocal)];

        foreach (var s in _screens)
        {
            if (s.IsLocal)
            {
                locals.Add((s.Physical, NumberOf(s)));
            }
            else
            {
                int idx = int.Parse(s.Id[1..]); // "R3" -> 3
                if (idx < remoteNumbers.Length) remoteNumbers[idx] = NumberOf(s);
            }
        }

        IdentifyOverlay.ShowNumbers(locals);
        _engine?.SendIdentify(remoteNumbers);
    }

    private void SaveAndClose()
    {
        foreach (var s in _screens)
            _config.Layout[s.Id] = new AppConfig.VirtualPos { X = s.Virtual.X, Y = s.Virtual.Y };
        _config.Save();
        DialogResult = DialogResult.OK;
        Close();
    }
}

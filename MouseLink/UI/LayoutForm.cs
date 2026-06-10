using MouseLink.Core;

namespace MouseLink.UI;

// Painel de configuração do layout: mostra todas as telas (deste PC e do
// remoto) como retângulos arrastáveis. A posição relativa define por qual
// borda o mouse atravessa de uma tela para outra.
public class LayoutForm : Form
{
    private readonly AppConfig _config;
    private readonly List<ScreenNode> _screens;

    private ScreenNode? _dragging;
    private Point _dragOffsetVirtual; // cursor - canto da tela, em coords virtuais
    private const int SnapThreshold = 40; // em pixels virtuais

    public LayoutForm(AppConfig config)
    {
        _config = config;
        _screens = VirtualLayout.Build(config, config.RemoteMonitorsCache).Screens;

        Text = "MouseLink – Layout das telas";
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(520, 400);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(32, 32, 36);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(45, 45, 52),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Text = "Arraste as telas para definir onde cada uma fica. " +
                   "As bordas encostadas são por onde o mouse atravessa.",
        };

        var btnSave = new Button
        {
            Text = "Salvar layout",
            Dock = DockStyle.Bottom,
            Height = 40,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(0, 110, 200),
        };
        btnSave.Click += (_, _) => SaveAndClose();

        Controls.Add(hint);
        Controls.Add(btnSave);

        MouseDown += OnCanvasMouseDown;
        MouseMove += OnCanvasMouseMove;
        MouseUp += (_, _) => { _dragging = null; Invalidate(); };
        Resize += (_, _) => Invalidate();
    }

    // ------------------------------------------------------------------
    // Mapeamento espaço virtual <-> canvas

    private (float scale, PointF offset) GetTransform()
    {
        var bounds = _screens.Aggregate(Rectangle.Empty,
            (acc, s) => acc.IsEmpty ? s.Virtual : Rectangle.Union(acc, s.Virtual));
        bounds.Inflate(bounds.Width / 6 + 200, bounds.Height / 6 + 200);

        var canvas = GetCanvasArea();
        float scale = Math.Min((float)canvas.Width / bounds.Width,
                               (float)canvas.Height / bounds.Height);
        var offset = new PointF(
            canvas.X + (canvas.Width - bounds.Width * scale) / 2 - bounds.X * scale,
            canvas.Y + (canvas.Height - bounds.Height * scale) / 2 - bounds.Y * scale);
        return (scale, offset);
    }

    private Rectangle GetCanvasArea() => new(10, 46, ClientSize.Width - 20, ClientSize.Height - 96);

    private RectangleF ToCanvas(Rectangle v, float scale, PointF off) =>
        new(v.X * scale + off.X, v.Y * scale + off.Y, v.Width * scale, v.Height * scale);

    private Point ToVirtual(Point canvasPt, float scale, PointF off) =>
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
                _dragging = s;
                var v = ToVirtual(e.Location, scale, off);
                _dragOffsetVirtual = new Point(v.X - s.Virtual.X, v.Y - s.Virtual.Y);
                return;
            }
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging == null) return;

        var (scale, off) = GetTransform();
        var v = ToVirtual(e.Location, scale, off);
        var newPos = new Point(v.X - _dragOffsetVirtual.X, v.Y - _dragOffsetVirtual.Y);

        newPos = Snap(newPos, _dragging);
        _dragging.Virtual = new Rectangle(newPos, _dragging.Virtual.Size);
        Invalidate();
    }

    // Encosta a tela arrastada nas vizinhas quando fica perto (em virtual px,
    // ajustado pela escala para a sensação ser uniforme na tela).
    private Point Snap(Point pos, ScreenNode dragged)
    {
        var (scale, _) = GetTransform();
        int t = (int)(SnapThreshold / Math.Max(scale, 0.001f) * 0.25f) + SnapThreshold;

        var r = new Rectangle(pos, dragged.Virtual.Size);
        int bestDx = int.MaxValue, bestDy = int.MaxValue;

        foreach (var o in _screens.Where(s => s != dragged).Select(s => s.Virtual))
        {
            // Bordas verticais (atravessar para esquerda/direita).
            TrySnap(o.Right - r.Left, t, ref bestDx);
            TrySnap(o.Left - r.Right, t, ref bestDx);
            TrySnap(o.Left - r.Left, t, ref bestDx);
            // Bordas horizontais (atravessar para cima/baixo).
            TrySnap(o.Bottom - r.Top, t, ref bestDy);
            TrySnap(o.Top - r.Bottom, t, ref bestDy);
            TrySnap(o.Top - r.Top, t, ref bestDy);
        }

        return new Point(
            pos.X + (bestDx == int.MaxValue ? 0 : bestDx),
            pos.Y + (bestDy == int.MaxValue ? 0 : bestDy));
    }

    private static void TrySnap(int delta, int threshold, ref int best)
    {
        if (Math.Abs(delta) <= threshold && Math.Abs(delta) < Math.Abs(best))
            best = delta;
    }

    // ------------------------------------------------------------------
    // Desenho

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var (scale, off) = GetTransform();

        foreach (var s in _screens)
        {
            var r = ToCanvas(s.Virtual, scale, off);
            var fill = s.IsLocal ? Color.FromArgb(36, 90, 150) : Color.FromArgb(60, 140, 80);
            var border = s == _dragging ? Color.White
                : s.IsLocal ? Color.FromArgb(110, 170, 230) : Color.FromArgb(130, 210, 150);

            using var brush = new SolidBrush(fill);
            using var pen = new Pen(border, s == _dragging ? 3 : 2);
            g.FillRectangle(brush, r);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);

            string text = $"{s.Label}\n{s.Physical.Width}×{s.Physical.Height}";
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, Font, Brushes.White, r, sf);
        }
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

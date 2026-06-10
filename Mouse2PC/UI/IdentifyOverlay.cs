namespace Mouse2PC.UI;

// Mostra o número da tela no canto inferior esquerdo de cada monitor por
// alguns segundos, igual ao botão "Identificar" das configurações do Windows.
public static class IdentifyOverlay
{
    public static void ShowNumbers(IEnumerable<(Rectangle bounds, int number)> screens)
    {
        foreach (var (bounds, number) in screens)
            new OverlayForm(bounds, number).Show();
    }

    private sealed class OverlayForm : Form
    {
        private readonly int _number;

        public OverlayForm(Rectangle screenBounds, int number)
        {
            _number = number;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(32, 32, 32);
            Size = new Size(180, 140);
            Location = new Point(screenBounds.Left + 48,
                                 screenBounds.Bottom - Height - 48);

            var timer = new System.Windows.Forms.Timer { Interval = 2500 };
            timer.Tick += (_, _) => { timer.Dispose(); Close(); };
            timer.Start();
        }

        // Não rouba o foco da janela atual.
        protected override bool ShowWithoutActivation => true;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Segoe UI", 64f, FontStyle.Bold);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            e.Graphics.DrawString(_number.ToString(), font, Brushes.White,
                ClientRectangle, sf);
        }
    }
}

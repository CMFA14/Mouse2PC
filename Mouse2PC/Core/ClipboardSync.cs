using System.Runtime.InteropServices;

namespace Mouse2PC.Core;

// Observa a área de transferência local (WM_CLIPBOARDUPDATE) e avisa quando
// o usuário copia texto; também aplica texto vindo do outro PC sem reecoar.
// Deve ser criado e usado na thread de UI (clipboard exige STA).
public class ClipboardSync : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxChars = 5_000_000; // ~10 MB em UTF-16; acima disso ignora

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly ListenerWindow _window;
    private bool _suppressNext; // a próxima mudança fomos nós que fizemos

    // Usuário copiou texto neste PC (enviar ao outro lado).
    public event Action<string>? LocalCopy;

    public ClipboardSync()
    {
        _window = new ListenerWindow(OnClipboardUpdate);
        AddClipboardFormatListener(_window.Handle);
    }

    // Aplica texto copiado no outro PC à área de transferência local.
    public void SetFromRemote(string text)
    {
        try
        {
            _suppressNext = true;
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true);
        }
        catch
        {
            // Clipboard ocupado por outro app; o usuário pode copiar de novo.
            _suppressNext = false;
        }
    }

    private void OnClipboardUpdate()
    {
        if (_suppressNext) { _suppressNext = false; return; }

        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (text.Length == 0 || text.Length > MaxChars) return;
            LocalCopy?.Invoke(text);
        }
        catch { /* clipboard ocupado; ignora esta mudança */ }
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_window.Handle);
        _window.DestroyHandle();
    }

    // Janela invisível só para receber WM_CLIPBOARDUPDATE.
    private sealed class ListenerWindow : NativeWindow
    {
        private readonly Action _onUpdate;

        public ListenerWindow(Action onUpdate)
        {
            _onUpdate = onUpdate;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE) _onUpdate();
            base.WndProc(ref m);
        }
    }
}

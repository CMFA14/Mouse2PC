using System.Net.Sockets;
using Mouse2PC.Native;
using Mouse2PC.Net;

namespace Mouse2PC.Core;

// Roda no PC que tem o mouse/teclado físico. Instala hooks de baixo nível;
// quando o cursor cruza a borda para uma tela remota, passa a bloquear o
// input local e a encaminhá-lo pela rede.
//
// Deve ser criado e iniciado na thread de UI (os hooks LL exigem uma thread
// com message loop). Eventos de rede são postados de volta nessa thread.
public class ControllerEngine : IDisposable
{
    private readonly AppConfig _config;
    private SynchronizationContext? _ui;

    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    // Mantém referências para o GC não coletar os delegates dos hooks.
    private NativeMethods.HookProc? _mouseProc;
    private NativeMethods.HookProc? _keyboardProc;

    private PeerConnection? _conn;
    private CancellationTokenSource? _connectCts;

    private VirtualLayout? _layout;
    private bool _remoteActive;          // cursor está numa tela remota
    private Point _virtualPos;           // posição do cursor no espaço virtual
    private Point _park;                 // onde o cursor físico fica "estacionado"
    private ScreenNode? _lastLocalScreen;
    private readonly HashSet<int> _heldKeys = new();

    public event Action<string>? StatusChanged;
    public event Action? RemoteInfoReceived;
    // Texto copiado no PC remoto (aplicar ao clipboard local).
    public event Action<string>? ClipboardReceived;

    public bool IsConnected => _conn != null && _layout != null;

    public ControllerEngine(AppConfig config) => _config = config;

    public void Start()
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Start deve ser chamado na thread de UI.");

        var module = NativeMethods.GetModuleHandle(null);
        _mouseProc = MouseHookProc;
        _keyboardProc = KeyboardHookProc;
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);

        _connectCts = new CancellationTokenSource();
        _ = ConnectLoop(_connectCts.Token);

        // Monitores locais mudaram (conectado/desconectado/resolução):
        // reconstrói o layout com a configuração atual.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (_layout == null) return;
        if (_remoteActive) ReturnToLocal(force: true); // telas mudaram sob o cursor
        ReloadLayout();
        StatusChanged?.Invoke($"Telas locais alteradas ({Screen.AllScreens.Length} monitor(es)) — layout atualizado.");
        RemoteInfoReceived?.Invoke();
    });

    // Pede ao PC controlado que pisque o número de cada tela dele
    // (posição i do array = índice do monitor remoto, valor = número).
    public void SendIdentify(int[] numbersByRemoteIndex) =>
        _conn?.Send(new Msg { Type = Msg.Ident, Numbers = numbersByRemoteIndex });

    // Texto copiado neste PC (replicar no clipboard remoto).
    public void SendClipboard(string text) =>
        _conn?.Send(new Msg { Type = Msg.Clip, Text = text });

    // Reconstroi o layout (chamado após salvar no painel de configuração).
    public void ReloadLayout()
    {
        if (_config.RemoteMonitorsCache.Count > 0)
            _layout = VirtualLayout.Build(_config, _config.RemoteMonitorsCache);
    }

    private async Task ConnectLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Post(() => StatusChanged?.Invoke($"Conectando a {_config.RemoteHost}:{_config.Port}..."));
                var client = new TcpClient();
                await client.ConnectAsync(_config.RemoteHost, _config.Port, ct);

                var conn = new PeerConnection(client);
                var done = new TaskCompletionSource();
                conn.MessageReceived += msg => Post(() => OnMessage(msg));
                conn.Disconnected += () =>
                {
                    Post(OnDisconnected);
                    done.TrySetResult();
                };
                Post(() =>
                {
                    _conn = conn;
                    StatusChanged?.Invoke("Conectado. Aguardando informações das telas...");
                });

                await done.Task; // segura o loop até a conexão cair
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                Post(() => StatusChanged?.Invoke("Falha ao conectar. Nova tentativa em 3s..."));
            }

            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    private void OnMessage(Msg msg)
    {
        if (msg.Type == Msg.Hello && msg.Monitors != null)
        {
            // Um hello no meio da sessão significa que as telas remotas
            // mudaram; volta o cursor para cá antes de trocar o layout.
            if (_remoteActive) ReturnToLocal(force: true);
            _config.RemoteMonitorsCache = msg.Monitors;
            _config.RemoteName = msg.MachineName ?? "Remoto";
            _config.Save();
            _layout = VirtualLayout.Build(_config, msg.Monitors);
            StatusChanged?.Invoke($"Conectado a {_config.RemoteName} " +
                $"({msg.Monitors.Count} tela(s) remota(s)). Mova o mouse até a borda para atravessar.");
            RemoteInfoReceived?.Invoke();
        }
        else if (msg.Type == Msg.Clip && msg.Text != null)
        {
            ClipboardReceived?.Invoke(msg.Text);
        }
    }

    private void OnDisconnected()
    {
        // Segurança: se a conexão cair com o cursor "do outro lado",
        // devolve o controle local imediatamente.
        if (_remoteActive) ReturnToLocal(force: true);
        _conn?.Dispose();
        _conn = null;
        _layout = null;
        StatusChanged?.Invoke("Desconectado.");
    }

    private void Post(Action a) => _ui!.Post(_ => a(), null);

    // ------------------------------------------------------------------
    // Hook de mouse (thread de UI)

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var info = System.Runtime.InteropServices.Marshal
            .PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

        // Ignora eventos que nós mesmos injetamos (SetCursorPos do "park").
        if ((info.flags & NativeMethods.LLMHF_INJECTED) != 0)
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        int msg = (int)wParam;

        if (!_remoteActive)
        {
            if (msg == NativeMethods.WM_MOUSEMOVE && _layout != null && _conn != null)
                CheckEdgeCrossing(new Point(info.pt.X, info.pt.Y));
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // Modo remoto: consome tudo localmente e encaminha.
        switch (msg)
        {
            case NativeMethods.WM_MOUSEMOVE:
                HandleRemoteMove(new Point(info.pt.X, info.pt.Y));
                break;

            case NativeMethods.WM_LBUTTONDOWN: SendButton(0, true); break;
            case NativeMethods.WM_LBUTTONUP: SendButton(0, false); break;
            case NativeMethods.WM_RBUTTONDOWN: SendButton(1, true); break;
            case NativeMethods.WM_RBUTTONUP: SendButton(1, false); break;
            case NativeMethods.WM_MBUTTONDOWN: SendButton(2, true); break;
            case NativeMethods.WM_MBUTTONUP: SendButton(2, false); break;

            case NativeMethods.WM_XBUTTONDOWN:
            case NativeMethods.WM_XBUTTONUP:
                int xb = (int)(info.mouseData >> 16) == 1 ? 3 : 4;
                SendButton(xb, msg == NativeMethods.WM_XBUTTONDOWN);
                break;

            case NativeMethods.WM_MOUSEWHEEL:
                _conn?.Send(new Msg { Type = Msg.Wheel, WheelV = (short)(info.mouseData >> 16) });
                break;
            case NativeMethods.WM_MOUSEHWHEEL:
                _conn?.Send(new Msg { Type = Msg.Wheel, WheelH = (short)(info.mouseData >> 16) });
                break;
        }

        return (IntPtr)1; // bloqueia o evento local
    }

    private void SendButton(int button, bool down) =>
        _conn?.Send(new Msg { Type = Msg.Btn, Button = button, Down = down });

    private void CheckEdgeCrossing(Point physical)
    {
        var screen = _layout!.LocalScreenAtPhysical(physical);
        if (screen == null) return;

        var v = screen.PhysicalToVirtual(physical);
        var b = screen.Virtual;

        // Em qual borda da tela o cursor encostou?
        int dx = 0, dy = 0;
        if (physical.X <= screen.Physical.Left) dx = -3;
        else if (physical.X >= screen.Physical.Right - 1) dx = 3;
        if (physical.Y <= screen.Physical.Top) dy = -3;
        else if (physical.Y >= screen.Physical.Bottom - 1) dy = 3;
        if (dx == 0 && dy == 0) return;

        var probe = new Point(
            Math.Clamp(v.X + dx, b.Left - 3, b.Right + 2),
            Math.Clamp(v.Y + dy, b.Top - 3, b.Bottom + 2));

        var target = _layout.ScreenAt(probe);
        if (target == null || target.IsLocal) return;

        EnterRemote(probe, target, screen);
    }

    private void EnterRemote(Point virtualPos, ScreenNode remoteScreen, ScreenNode fromLocal)
    {
        _remoteActive = true;
        _virtualPos = virtualPos;
        _lastLocalScreen = fromLocal;
        _heldKeys.Clear();

        // Estaciona o cursor físico no centro da tela local de origem para
        // medir deltas de movimento sem que ele esbarre nas bordas.
        _park = new Point(
            fromLocal.Physical.X + fromLocal.Physical.Width / 2,
            fromLocal.Physical.Y + fromLocal.Physical.Height / 2);
        NativeMethods.SetCursorPos(_park.X, _park.Y);

        SendRemoteMove(remoteScreen);
        StatusChanged?.Invoke($"Controlando: {_config.RemoteName} (Ctrl+Alt+Home para voltar)");
    }

    private void HandleRemoteMove(Point physical)
    {
        int dx = physical.X - _park.X;
        int dy = physical.Y - _park.Y;
        if (dx == 0 && dy == 0) return;

        NativeMethods.SetCursorPos(_park.X, _park.Y);

        var (pos, screen) = _layout!.Move(_virtualPos, dx, dy);
        _virtualPos = pos;

        if (screen == null) return;

        if (screen.IsLocal)
        {
            ReturnToLocal(force: false);
            return;
        }

        SendRemoteMove(screen);
    }

    private void SendRemoteMove(ScreenNode remoteScreen)
    {
        var p = remoteScreen.VirtualToPhysical(_virtualPos);
        _conn?.Send(new Msg { Type = Msg.Move, X = p.X, Y = p.Y });
    }

    private void ReturnToLocal(bool force)
    {
        _remoteActive = false;
        _heldKeys.Clear();
        _conn?.Send(new Msg { Type = Msg.Leave });

        Point physical;
        if (!force && _layout?.ScreenAt(_virtualPos) is { IsLocal: true } local)
        {
            physical = local.VirtualToPhysical(_virtualPos);
        }
        else
        {
            // Fallback (queda de conexão / hotkey): volta para onde saiu.
            var s = _lastLocalScreen ?? _layout?.Screens.FirstOrDefault(x => x.IsLocal);
            physical = s != null
                ? new Point(s.Physical.X + s.Physical.Width / 2, s.Physical.Y + s.Physical.Height / 2)
                : _park;
        }

        NativeMethods.SetCursorPos(physical.X, physical.Y);
        StatusChanged?.Invoke("Controle local.");
    }

    // ------------------------------------------------------------------
    // Hook de teclado (thread de UI)

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_remoteActive)
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var info = System.Runtime.InteropServices.Marshal
            .PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        if ((info.flags & NativeMethods.LLKHF_INJECTED) != 0)
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        bool down = (info.flags & NativeMethods.LLKHF_UP) == 0;
        int vk = (int)info.vkCode;

        if (down) _heldKeys.Add(vk); else _heldKeys.Remove(vk);

        // Hotkey de emergência Ctrl+Alt+Home: devolve o controle local.
        if (down && vk == (int)Keys.Home && IsHeld(Keys.ControlKey, Keys.LControlKey, Keys.RControlKey)
                 && IsHeld(Keys.Menu, Keys.LMenu, Keys.RMenu))
        {
            ReturnToLocal(force: true);
            return (IntPtr)1;
        }

        _conn?.Send(new Msg
        {
            Type = Msg.Key,
            Vk = vk,
            Scan = (int)info.scanCode,
            Extended = (info.flags & NativeMethods.LLKHF_EXTENDED) != 0,
            Down = down,
        });

        return (IntPtr)1;
    }

    private bool IsHeld(params Keys[] anyOf) => anyOf.Any(k => _heldKeys.Contains((int)k));

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        if (_remoteActive) ReturnToLocal(force: true);
        _connectCts?.Cancel();
        _conn?.Dispose();
        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
    }
}

using System.Net;
using System.Net.Sockets;
using Mouse2PC.Native;
using Mouse2PC.Net;

namespace Mouse2PC.Core;

// Roda no PC controlado: escuta na porta TCP, anuncia seus monitores e
// injeta os eventos de mouse/teclado recebidos via SendInput.
public class ControlledEndpoint : IDisposable
{
    private readonly int _port;
    private TcpListener? _listener;
    private PeerConnection? _conn;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<int> _injectedHeldKeys = new();

    public event Action<string>? StatusChanged;
    // Disparado quando o controlador pede "Identificar": posição i do array
    // é o índice do monitor (mesma ordem do hello), valor é o número a piscar.
    public event Action<int[]>? IdentifyRequested;

    public ControlledEndpoint(int port) => _port = port;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _ = AcceptLoop();
        StatusChanged?.Invoke($"Aguardando conexão na porta {_port}...");
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(_cts.Token);

                _conn?.Dispose(); // só um controlador por vez
                var conn = new PeerConnection(client);
                _conn = conn;
                conn.MessageReceived += OnMessage;
                conn.Disconnected += () =>
                {
                    ReleaseHeldKeys();
                    StatusChanged?.Invoke($"Controlador desconectou. Aguardando conexão na porta {_port}...");
                };

                conn.Send(BuildHello());
                StatusChanged?.Invoke(
                    $"Conectado: {((IPEndPoint?)client.Client.RemoteEndPoint)?.Address}");
            }
            catch (OperationCanceledException) { break; }
            catch { /* listener fechado ou erro transitório */ }
        }
    }

    private static Msg BuildHello() => new()
    {
        Type = Msg.Hello,
        MachineName = Environment.MachineName,
        Monitors = Screen.AllScreens
            .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .Select((s, i) => new MonitorDto
            {
                Index = i,
                X = s.Bounds.X,
                Y = s.Bounds.Y,
                W = s.Bounds.Width,
                H = s.Bounds.Height,
                Name = s.DeviceName,
            }).ToList(),
    };

    private void OnMessage(Msg msg)
    {
        switch (msg.Type)
        {
            case Msg.Move: InjectMove(msg.X, msg.Y); break;
            case Msg.Btn: InjectButton(msg.Button, msg.Down); break;
            case Msg.Wheel: InjectWheel(msg.WheelV, msg.WheelH); break;
            case Msg.Key: InjectKey(msg.Vk, msg.Scan, msg.Extended, msg.Down); break;
            case Msg.Leave: ReleaseHeldKeys(); break;
            case Msg.Ident when msg.Numbers != null:
                IdentifyRequested?.Invoke(msg.Numbers);
                break;
        }
    }

    // ------------------------------------------------------------------
    // Injeção

    private static void InjectMove(int x, int y)
    {
        // Coordenadas absolutas normalizadas (0..65535) sobre o desktop
        // virtual inteiro, para suportar múltiplos monitores.
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 1 || vh <= 1) return;

        Send(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.INPUTUNION
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = (x - vx) * 65535 / (vw - 1),
                    dy = (y - vy) * 65535 / (vh - 1),
                    dwFlags = NativeMethods.MOUSEEVENTF_MOVE
                            | NativeMethods.MOUSEEVENTF_ABSOLUTE
                            | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                }
            }
        });
    }

    private static void InjectButton(int button, bool down)
    {
        uint flags;
        uint data = 0;
        switch (button)
        {
            case 0: flags = down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP; break;
            case 1: flags = down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP; break;
            case 2: flags = down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP; break;
            case 3: flags = down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP; data = NativeMethods.XBUTTON1; break;
            case 4: flags = down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP; data = NativeMethods.XBUTTON2; break;
            default: return;
        }

        Send(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.INPUTUNION
            {
                mi = new NativeMethods.MOUSEINPUT { mouseData = data, dwFlags = flags }
            }
        });
    }

    private static void InjectWheel(int v, int h)
    {
        if (v != 0)
            Send(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.INPUTUNION
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)v),
                        dwFlags = NativeMethods.MOUSEEVENTF_WHEEL
                    }
                }
            });
        if (h != 0)
            Send(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.INPUTUNION
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)h),
                        dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL
                    }
                }
            });
    }

    private void InjectKey(int vk, int scan, bool extended, bool down)
    {
        if (down) _injectedHeldKeys.Add(vk); else _injectedHeldKeys.Remove(vk);

        uint flags = 0;
        if (extended) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= NativeMethods.KEYEVENTF_KEYUP;

        Send(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)scan,
                    dwFlags = flags,
                }
            }
        });
    }

    // Solta qualquer tecla que ficou pressionada quando o cursor saiu ou a
    // conexão caiu (evita Ctrl/Alt "presos" no PC controlado).
    private void ReleaseHeldKeys()
    {
        foreach (var vk in _injectedHeldKeys.ToList())
            InjectKey(vk, 0, false, down: false);
        _injectedHeldKeys.Clear();
    }

    private static void Send(NativeMethods.INPUT input) =>
        NativeMethods.SendInput(1, new[] { input },
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());

    public void Dispose()
    {
        _cts.Cancel();
        ReleaseHeldKeys();
        _conn?.Dispose();
        try { _listener?.Stop(); } catch { }
    }
}

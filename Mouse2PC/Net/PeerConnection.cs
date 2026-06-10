using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Mouse2PC.Net;

// Envolve um TcpClient conectado: escrita enfileirada (para nunca bloquear o
// hook de mouse) e leitura linha a linha em background.
public class PeerConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<string> _sendQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    public event Action<Msg>? MessageReceived;
    public event Action? Disconnected;

    private int _disconnectedFired;

    public PeerConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true; // latência > throughput para eventos de input
        _stream = client.GetStream();

        _ = Task.Run(SendLoop);
        _ = Task.Run(ReceiveLoop);
    }

    public void Send(Msg msg) => _sendQueue.Writer.TryWrite(msg.Serialize());

    private async Task SendLoop()
    {
        try
        {
            await foreach (var line in _sendQueue.Reader.ReadAllAsync(_cts.Token))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _stream.WriteAsync(bytes, _cts.Token);
            }
        }
        catch { FireDisconnected(); }
    }

    private async Task ReceiveLoop()
    {
        try
        {
            using var reader = new StreamReader(_stream, Encoding.UTF8, false, 8192, leaveOpen: true);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line == null) break;
                var msg = Msg.Deserialize(line);
                if (msg != null) MessageReceived?.Invoke(msg);
            }
        }
        catch { /* queda de conexão */ }
        FireDisconnected();
    }

    private void FireDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectedFired, 1) == 0)
            Disconnected?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _client.Close(); } catch { }
    }
}

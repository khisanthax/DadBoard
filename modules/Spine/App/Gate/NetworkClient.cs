using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DadBoard.Gate;

sealed class NetworkClient : IDisposable
{
    private readonly int _port;
    private readonly UdpClient _client;
    private readonly IPEndPoint _broadcastEndpoint;
    private readonly CancellationTokenSource _cts = new();

    public event Action<GateMessage>? MessageReceived;

    public NetworkClient(int port)
    {
        _port = port;
        _client = new UdpClient(AddressFamily.InterNetwork);
        _client.EnableBroadcast = true;
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _port);
    }

    public void Start()
    {
        _ = Task.Run(ReceiveLoop);
    }

    public void Send(GateMessage message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json);
            _client.Send(data, data.Length, _broadcastEndpoint);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to send message: {ex.Message}");
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _client.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var msg = JsonSerializer.Deserialize<GateMessage>(json);
                if (msg != null)
                {
                    MessageReceived?.Invoke(msg);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Receive error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        _cts.Dispose();
    }
}

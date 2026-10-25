using System.Net.Sockets;
using System.Text.Json;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Client;

/// <summary>
/// Соединение с сервером. Любая сетевая ошибка превращается в <see cref="Response"/>
/// с кодом SERVER_UNAVAILABLE / NETWORK_ERROR — клиент не падает.
/// </summary>
public sealed class ServerConnection : IDisposable
{
    private static readonly JsonSerializerOptions ReadOptions = new(Json.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public ServerConnection(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public bool IsConnected => _client?.Connected == true;

    public async Task<Response> SendAsync(string operation, object? payload = null, CancellationToken ct = default)
    {
        try
        {
            await EnsureConnectedAsync(ct);

            var request = new Request
            {
                Operation = operation,
                Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, Json.Options)
            };

            await MessageFramer.WriteMessageAsync(_stream!, JsonSerializer.Serialize(request, Json.Options), ct);
            string json = await MessageFramer.ReadMessageAsync(_stream!, ct);
            return JsonSerializer.Deserialize<Response>(json, ReadOptions)
                   ?? Response.Fail(ErrorCodes.NetworkError, "Пустой ответ сервера");
        }
        catch (SocketException ex)
        {
            Close();
            return Response.Fail(ErrorCodes.ServerUnavailable, $"Сервер {_host}:{_port} недоступен ({ex.SocketErrorCode})");
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or JsonException)
        {
            Close();
            return Response.Fail(ErrorCodes.NetworkError, $"Ошибка сети: {ex.Message}");
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected && _stream is not null) return;

        Close();
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, ct);
        _stream = _client.GetStream();
    }

    private void Close()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose() => Close();
}

using System.Net.Sockets;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Client;

/// <summary>Исключение: сервер недоступен (не удалось подключиться).</summary>
public class ServerUnavailableException : Exception
{
    public ServerUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Сетевой клиент: подключается к серверу, отправляет JSON-запрос и получает ответ.
/// На каждое обращение открывается новое соединение (простой вариант протокола).
/// </summary>
public class NetworkClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;

    public NetworkClient(string host = "127.0.0.1", int port = ProtocolDefaults.DefaultPort)
    {
        _host = host;
        _port = port;
    }

    /// <summary>
    /// Отправка запроса серверу и получение ответа (ЛР №2: асинхронная версия).
    /// Бросает ServerUnavailableException, если сервер недоступен.
    /// </summary>
    public async Task<Response> SendAsync(Request request, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        try
        {
            // Таймаут подключения, чтобы не зависать, если сервер не запущен
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(_host, _port, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ServerUnavailableException($"Сервер {_host}:{_port} не отвечает (таймаут подключения)");
            }
            catch (SocketException ex)
            {
                throw new ServerUnavailableException($"Сервер {_host}:{_port} недоступен: {ex.Message}", ex);
            }

            using NetworkStream stream = tcp.GetStream();
            string requestJson = System.Text.Json.JsonSerializer.Serialize(request, Json.Options);
            await MessageFramer.WriteMessageAsync(stream, requestJson, ct);

            string responseJson = await MessageFramer.ReadMessageAsync(stream, ct);
            Response? response = System.Text.Json.JsonSerializer.Deserialize<Response>(responseJson, Json.Options);
            return response ?? Response.Fail(ErrorCodes.NetworkError, "Пустой ответ сервера");
        }
        catch (ServerUnavailableException)
        {
            throw;
        }
        catch (SocketException ex)
        {
            throw new ServerUnavailableException($"Сервер {_host}:{_port} недоступен: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ServerUnavailableException($"Ошибка сетевого взаимодействия с {_host}:{_port}: {ex.Message}", ex);
        }
    }

    /// <summary>Синхронная отправка (обёртка над <see cref="SendAsync"/>).</summary>
    public Response Send(Request request) => SendAsync(request).GetAwaiter().GetResult();

    public void Dispose() { }
}

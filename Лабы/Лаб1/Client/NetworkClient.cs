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
    /// Отправка запроса серверу и получение ответа.
    /// Бросает ServerUnavailableException, если сервер недоступен.
    /// </summary>
    public Response Send(Request request)
    {
        try
        {
            using var tcp = new TcpClient();
            // Таймаут подключения, чтобы не зависать, если сервер не запущен
            try
            {
                var connectTask = tcp.ConnectAsync(_host, _port);
                if (!connectTask.Wait(5000))
                {
                    throw new ServerUnavailableException($"Сервер {_host}:{_port} не отвечает (таймаут подключения)");
                }
            }
            catch (AggregateException ex) when (ex.InnerException is SocketException se)
            {
                throw new ServerUnavailableException($"Сервер {_host}:{_port} недоступен: {se.Message}", se);
            }

            using NetworkStream stream = tcp.GetStream();
            string requestJson = System.Text.Json.JsonSerializer.Serialize(request, Json.Options);
            MessageFramer.WriteMessage(stream, requestJson);

            string responseJson = MessageFramer.ReadMessage(stream);
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

    public void Dispose() { }
}

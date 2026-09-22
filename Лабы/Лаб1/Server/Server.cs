using System.Net;
using System.Net.Sockets;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server;

/// <summary>
/// TCP-сервер: принимает подключения, читает JSON-запрос, передает обработчику и
/// возвращает ответ. Каждый клиент обрабатывается в отдельном потоке.
/// </summary>
public class Server
{
    public const int DefaultPort = ProtocolDefaults.DefaultPort;

    private readonly TcpListener _listener;
    private readonly RequestHandler _handler;
    private bool _running;

    public Server(int port = DefaultPort)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _handler = new RequestHandler(new BusinessLogic());
    }

    /// <summary>Запуск сервера и ожидание подключений.</summary>
    public void Start()
    {
        _listener.Start();
        _running = true;
        Console.WriteLine($"[Сервер] Запущен и ожидает подключений на порту {((IPEndPoint)_listener.LocalEndpoint).Port}...");

        try
        {
            while (_running)
            {
                TcpClient client = _listener.AcceptTcpClient();
                var t = new Thread(() => HandleClient(client)) { IsBackground = true };
                t.Start();
                Console.WriteLine($"[Сервер] Новое подключение: {client.Client.RemoteEndPoint}");
            }
        }
        catch (SocketException) when (!_running)
        {
            // Нормальная остановка сервера
        }
    }

    /// <summary>Обработка одного клиентского подключения (может быть несколько запросов).</summary>
    private void HandleClient(TcpClient client)
    {
        using TcpClient c = client;
        try
        {
            NetworkStream stream = c.GetStream();
            while (true)
            {
                string json = MessageFramer.ReadMessage(stream);
                Response response = _handler.Handle(json);
                string responseJson = System.Text.Json.JsonSerializer.Serialize(response, Json.Options);
                MessageFramer.WriteMessage(stream, responseJson);
            }
        }
        catch (EndOfStreamException)
        {
            // Клиент закрыл соединение — это нормально
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[Сервер] Ошибка сетевого взаимодействия: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Сервер] Ошибка обработки клиента: {ex.Message}");
        }
    }

    public void Stop()
    {
        _running = false;
        _listener.Stop();
    }
}

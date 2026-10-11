using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server;

/// <summary>
/// Запись журнала обработки запроса (ЛР №2, Требование 3; дополнено в ЛР №3).
/// Хранит временную метку, номер клиента, фазу (START/END, а также события
/// критической секции LOCK/UNLOCK/READ/WRITE/SUCCESS/FAIL/TIME), операцию,
/// идентификатор Task, номер потока и подробное описание события.
/// </summary>
public sealed record ProcessingLogEntry(
    DateTime Timestamp,
    int ClientId,
    string Phase,
    string Operation,
    int? TaskId,
    int ThreadId,
    double? ElapsedMs,
    string? Detail = null);

/// <summary>
/// Многопоточный TCP-сервер.
/// Принимает подключения в цикле и НЕ блокируется на обработке одного клиента:
/// каждый клиент обслуживается в отдельной Task (асинхронный HandleClientAsync),
/// поэтому сервер продолжает принимать следующие подключения (Требования 1, 2, 7).
/// Все события пишутся в консоль с временными метками и попадают в <see cref="Journal"/>
/// (unit-тесты используют журнал как доказательство параллельной обработки и
/// взаимного исключения критических секций — ЛР №3).
/// Механизм синхронизации общего ресурса задаётся параметром <c>sync</c>
/// (lock — по умолчанию, SemaphoreSlim или None).
/// </summary>
public class Server
{
    public const int DefaultPort = ProtocolDefaults.DefaultPort;

    private readonly TcpListener _listener;
    private readonly RequestHandler _handler;
    private volatile bool _running;
    private int _clientCounter;

    /// <summary>Бизнес-логика с общим ресурсом (для тестов и экспериментов).</summary>
    public BusinessLogic Logic { get; }

    /// <summary>Журнал обработки: START/END и события критической секции по каждому клиенту.</summary>
    public ConcurrentQueue<ProcessingLogEntry> Journal { get; } = new();

    /// <summary>Фактический порт (полезен при порте 0 — эфемерный порт в тестах).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Механизм синхронизации общего ресурса (ЛР №3): lock, SemaphoreSlim или None.</summary>
    public SyncMechanism Sync => Logic.Sync;

    public Server(int port = DefaultPort, BusinessLogic? logic = null, SyncMechanism? sync = null)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        Logic = logic ?? new BusinessLogic(sync: sync ?? SyncMechanism.Lock);
        _handler = new RequestHandler(Logic);
    }

    /// <summary>
    /// Запуск сервера: приём подключений в бесконечном цикле.
    /// Каждое подключение сразу передаётся в фоновую Task — цикл немедленно
    /// возвращается к приёму следующего клиента (Требование 2).
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _listener.Start();
        _running = true;
        Console.WriteLine($"[Сервер] Запущен и ожидает подключений на порту {Port}...");

        try
        {
            while (_running)
            {
                // Асинхронный приём: сервер не блокируется ни на одном клиенте
                TcpClient client = await _listener.AcceptTcpClientAsync(ct);

                int clientId = Interlocked.Increment(ref _clientCounter);
                Console.WriteLine($"[Сервер] Новое подключение: {client.Client.RemoteEndPoint} → Client {clientId}");

                // Обработка клиента — в отдельной Task (Требование 2)
                _ = Task.Run(() => HandleClientAsync(client, clientId), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Нормальная остановка сервера (Stop() закрывает listener)
        }
        catch (OperationCanceledException)
        {
            // Отмена по токену
        }
    }

    /// <summary>
    /// Асинхронная обработка одного клиентского подключения (может быть несколько запросов).
    /// Ошибка одного клиента не роняет сервер (Требование 9).
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, int clientId)
    {
        using TcpClient c = client;
        Log(clientId, "CONNECT", "-");

        try
        {
            NetworkStream stream = c.GetStream();
            while (_running)
            {
                string json = await MessageFramer.ReadMessageAsync(stream);
                string operation = RequestHandler.TryGetOperationName(json);

                // Требование 3: фиксируем начало обработки
                var stopwatch = Stopwatch.StartNew();
                Log(clientId, "START", operation);

                // ЛР №3: операции с общим ресурсом пишут события синхронизации
                // (LOCK/UNLOCK/READ/WRITE/SUCCESS/FAIL/TIME) в тот же журнал (Требование 8).
                var context = new RequestContext
                {
                    ClientId = clientId,
                    Journal = (phase, detail) => Log(clientId, phase, operation, detail: detail)
                };

                Response response = await _handler.HandleAsync(json, context);

                stopwatch.Stop();
                Log(clientId, "END", operation, stopwatch.Elapsed.TotalMilliseconds);

                string responseJson = System.Text.Json.JsonSerializer.Serialize(response, Json.Options);
                await MessageFramer.WriteMessageAsync(stream, responseJson);
            }
        }
        catch (EndOfStreamException)
        {
            // Клиент закрыл соединение — это нормально (Требование 9)
            Log(clientId, "DISCONNECT", "-");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[Сервер] Client {clientId}: ошибка сетевого взаимодействия: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Ошибка одного клиента не приводит к аварийному завершению сервера (Требование 9)
            Console.WriteLine($"[Сервер] Client {clientId}: ошибка обработки: {ex.Message}");
        }
    }

    /// <summary>
    /// Вывод в консоль и запись в журнал (Требования 3, 8). Формат:
    /// [10:15:01.120] Client 1 → START  CreateOrder Task=4 Thread=6 (10.005 с)
    /// [10:15:01.130] Client 1 → LOCK   ReserveBouquet Task=4 Thread=6  Букет #3 — вход в критическую секцию (lock)
    /// </summary>
    private void Log(int clientId, string phase, string operation, double? elapsedMs = null, string? detail = null)
    {
        var entry = new ProcessingLogEntry(
            DateTime.Now, clientId, phase, operation,
            Task.CurrentId, Environment.CurrentManagedThreadId, elapsedMs, detail);

        Journal.Enqueue(entry);

        string elapsed = elapsedMs is double ms ? $" ({ms / 1000:0.000} с)" : "";
        string tail = string.IsNullOrWhiteSpace(detail) ? "" : $"  {detail}";
        Console.WriteLine(
            $"[{entry.Timestamp:HH:mm:ss.fff}] Client {clientId} → {phase,-10} {operation,-22} " +
            $"Task={entry.TaskId?.ToString() ?? "-"} Thread={entry.ThreadId}{elapsed}{tail}");
    }

    public void Stop()
    {
        _running = false;
        _listener.Stop();
    }
}

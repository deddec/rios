using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlowerShop.Server.Consensus;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server;

/// <summary>
/// TCP-сервер (протокол: 4 байта длины + JSON). Каждый клиент обслуживается отдельной задачей;
/// одновременные запросы нескольких клиентов сериализуются внутри кластера.
/// Ошибки одного клиента (обрыв соединения, некорректное сообщение) не влияют на остальных.
/// </summary>
public sealed class ConsensusTcpServer
{
    private static readonly JsonSerializerOptions ReadOptions = new(Json.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RequestRouter _router;
    private readonly ConsensusLog _log;
    private TcpListener? _listener;

    public ConsensusTcpServer(RequestRouter router, ConsensusLog log)
    {
        _router = router;
        _log = log;
    }

    /// <summary>Порт, на котором сервер принимает соединения (после Start).</summary>
    public int Port => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? 0;

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_listener is null) throw new InvalidOperationException("Сервер не запущен (вызовите Start)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { /* штатная остановка */ }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    string json = await MessageFramer.ReadMessageAsync(stream, ct).ConfigureAwait(false);

                    Response response;
                    try
                    {
                        var request = JsonSerializer.Deserialize<Request>(json, ReadOptions);
                        response = _router.Handle(request);
                    }
                    catch (JsonException ex)
                    {
                        _log.Write($"Client {remote}: некорректный JSON ({ex.Message})");
                        response = Response.Fail(ErrorCodes.ValidationError, "Некорректный JSON запроса");
                    }

                    string payload = JsonSerializer.Serialize(response, Json.Options);
                    await MessageFramer.WriteMessageAsync(stream, payload, ct).ConfigureAwait(false);
                }
            }
            catch (EndOfStreamException) { /* клиент закрыл соединение */ }
            catch (OperationCanceledException) { }
            catch (IOException) { /* обрыв соединения */ }
            catch (InvalidDataException ex)
            {
                _log.Write($"Client {remote}: нарушение протокола ({ex.Message})");
            }
            catch (Exception ex)
            {
                _log.Write($"Client {remote}: ошибка обработки ({ex.Message})");
            }
        }
    }
}

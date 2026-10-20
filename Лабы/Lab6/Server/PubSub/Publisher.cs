using FlowerShop.Shared.PubSub;
using FlowerShop.Shared.Protocol;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6: Publisher (издатель) — компонент, который публикует события.
/// Передаёт событие только Broker'у и НЕ обращается напрямую к Subscriber'ам
/// (слабая связанность компонентов). Поддерживает несколько Broker: при отказе
/// активного Broker публикация выполняется через следующий доступный.
/// </summary>
public sealed class Publisher
{
    private readonly IReadOnlyList<EventBroker> _brokers;

    public Publisher(IEnumerable<EventBroker> brokers, string name = "Publisher")
    {
        _brokers = brokers?.ToList() ?? throw new ArgumentNullException(nameof(brokers));
        if (_brokers.Count == 0)
        {
            throw new ArgumentException("Publisher должен иметь хотя бы один Broker", nameof(brokers));
        }
        Name = string.IsNullOrWhiteSpace(name) ? "Publisher" : name;
    }

    /// <summary>Имя Publisher (компонент журнала).</summary>
    public string Name { get; }

    /// <summary>Обработчик журнальных записей (назначается PubSubManager).</summary>
    public Action<PubSubLogEntry>? OnLog { get; set; }

    /// <summary>Broker'ы, известные Publisher (в порядке переключения).</summary>
    public IReadOnlyList<EventBroker> Brokers => _brokers;

    /// <summary>
    /// Получить событие и передать его Broker'у (Требование 3 ч.1).
    /// Возвращает информацию о результате публикации; ошибки публикации
    /// регистрируются в журнале и в результате — Publisher не завершается аварийно.
    /// </summary>
    public async Task<PublishResult> PublishAsync(ShopEvent shopEvent, CancellationToken ct = default)
    {
        Log("PUBLISH", shopEvent, detail: $"событие передаётся Broker (известно Broker'ов: {_brokers.Count})");

        foreach (EventBroker broker in _brokers)
        {
            if (!broker.IsAvailable)
            {
                // Отказ одного Broker не останавливает публикацию (ч.5)
                Log("BROKER SKIP", shopEvent, detail: $"Broker '{broker.Name}' недоступен — пробую следующий");
                continue;
            }

            try
            {
                PublishResult result = await broker.PublishAsync(shopEvent, ct).ConfigureAwait(false);
                Log("PUBLISH OK", shopEvent,
                    detail: $"Broker '{broker.Name}': доставлено {result.DeliveredCount}/{result.Deliveries.Count}");
                return result;
            }
            catch (BrokerUnavailableException ex)
            {
                // Broker отказал во время публикации — переключение на следующий
                Log("BROKER FAIL", shopEvent, detail: ex.Message);
            }
            catch (Exception ex)
            {
                // Ошибка публикации зарегистрирована, понятный результат вместо исключения
                Log("PUBLISH FAILED", shopEvent, detail: $"Broker '{broker.Name}': {ex.Message}");
                return new PublishResult
                {
                    BrokerName = broker.Name,
                    EventId = shopEvent.EventId,
                    EventType = shopEvent.EventType,
                    Success = false,
                    ErrorCode = ErrorCodes.PublishError,
                    Error = $"Ошибка публикации через Broker '{broker.Name}': {ex.Message}"
                };
            }
        }

        Log("PUBLISH FAILED", shopEvent, detail: "все Broker недоступны — публикация невозможна");
        return new PublishResult
        {
            EventId = shopEvent.EventId,
            EventType = shopEvent.EventType,
            Success = false,
            ErrorCode = ErrorCodes.BrokerUnavailable,
            Error = "Все Broker недоступны: событие не опубликовано (нажмите 'Восстановить Broker')"
        };
    }

    private void Log(string phase, ShopEvent shopEvent, string? detail) =>
        OnLog?.Invoke(new PubSubLogEntry(
            DateTime.Now, Name, phase, shopEvent.ClientId, shopEvent.EventType, null, detail));
}
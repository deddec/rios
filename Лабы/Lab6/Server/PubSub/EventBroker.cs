using System.Collections.Concurrent;
using System.Diagnostics;
using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №6: Broker (брокер) — компонент, который принимает опубликованные события,
/// хранит информацию о подписках, определяет подходящих подписчиков, передаёт им
/// события, фиксирует ошибки доставки и продолжает работу при отказе отдельного
/// Subscriber'а. Промышленный брокер (Kafka/RabbitMQ) НЕ используется — реализация
/// средствами C#.
/// </summary>
public sealed class EventBroker
{
    /// <summary>Число попыток доставки одному Subscriber'у (Attempt 1 → 2 → 3).</summary>
    public const int DefaultMaxAttempts = 3;

    private readonly ConcurrentDictionary<string, List<ISubscriber>> _subscriptions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ISubscriber> _subscribers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<PendingDelivery> _queue = new();

    public EventBroker(string name)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Имя Broker не должно быть пустым", nameof(name))
            : name;
    }

    /// <summary>Имя Broker (Broker-1, Broker-2, …).</summary>
    public string Name { get; }

    /// <summary>Доступность Broker: false — смоделирован отказ брокера (Требование 1 ч.5).</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>Максимум попыток доставки одному Subscriber'у.</summary>
    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    /// <summary>Пауза между повторными попытками доставки.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Обработчик журнальных записей (назначается PubSubManager).</summary>
    public Action<PubSubLogEntry>? OnLog { get; set; }

    /// <summary>Подписчики, зарегистрированные в Broker.</summary>
    public IReadOnlyCollection<ISubscriber> Subscribers => _subscribers.Values.ToArray();

    /// <summary>Очередь событий, ожидающих повторной доставки (временное хранение).</summary>
    public IReadOnlyList<PendingDelivery> Queued => _queue.ToArray();

    /// <summary>Количество событий в очереди.</summary>
    public int QueueCount => _queue.Count;

    /// <summary>Число зарегистрированных подписок (тип события → подписчики).</summary>
    public int SubscriptionCount => _subscriptions.Count;

    /// <summary>
    /// Регистрация подписки: Broker запоминает, что Subscriber заинтересован
    /// в данном типе события (Требование 5 ч.1).
    /// </summary>
    public void Subscribe(ISubscriber subscriber, string eventType)
    {
        List<ISubscriber> list = _subscriptions.GetOrAdd(eventType, _ => new List<ISubscriber>());
        lock (list)
        {
            if (!list.Contains(subscriber)) list.Add(subscriber);
        }
        _subscribers[subscriber.Name] = subscriber;

        Log("SUBSCRIBE", eventType: eventType, subscriber: subscriber.Name,
            detail: $"Broker '{Name}' хранит подписку");
    }

    /// <summary>Отмена подписки Subscriber'а на тип события.</summary>
    public void Unsubscribe(string subscriberName, string eventType)
    {
        if (_subscriptions.TryGetValue(eventType, out List<ISubscriber>? list))
        {
            lock (list) list.RemoveAll(s => string.Equals(s.Name, subscriberName, StringComparison.OrdinalIgnoreCase));
        }
        Log("UNSUBSCRIBE", eventType: eventType, subscriber: subscriberName, detail: $"Broker '{Name}'");
    }

    /// <summary>Подписчики, заинтересованные в типе события.</summary>
    public IReadOnlyList<ISubscriber> TargetsFor(string eventType)
    {
        if (_subscriptions.TryGetValue(eventType, out List<ISubscriber>? list))
        {
            lock (list) return list.ToArray();
        }
        return Array.Empty<ISubscriber>();
    }

    /// <summary>
    /// Принять событие и доставить его всем подходящим Subscriber'ам.
    /// Доставка АСИНХРОННАЯ и НЕЗАВИСИМАЯ: каждому Subscriber'у — своя Task,
    /// обработка выполняется параллельно (Требование 1 ч.3).
    /// Бросает <see cref="BrokerUnavailableException"/>, если Broker недоступен.
    /// </summary>
    public async Task<PublishResult> PublishAsync(ShopEvent shopEvent, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            throw new BrokerUnavailableException($"Broker '{Name}' недоступен (UNAVAILABLE)");
        }

        Log("EVENT RECEIVED", shopEvent: shopEvent, detail: $"принято Broker '{Name}'");

        IReadOnlyList<ISubscriber> targets = TargetsFor(shopEvent.EventType);
        if (targets.Count == 0)
        {
            // Событие не теряется и не считается ошибкой: подписчиков нет (контрольный вопрос 12)
            Log("NO SUBSCRIBERS", shopEvent: shopEvent,
                detail: "на данный тип события никто не подписан — событие никому не доставлено");
            return new PublishResult
            {
                BrokerName = Name,
                EventId = shopEvent.EventId,
                EventType = shopEvent.EventType,
                Success = true
            };
        }

        DeliveryResult[] results = await Task.WhenAll(
            targets.Select(sub => DeliverAsync(sub, shopEvent, ct))).ConfigureAwait(false);

        return new PublishResult
        {
            BrokerName = Name,
            EventId = shopEvent.EventId,
            EventType = shopEvent.EventType,
            Success = true,
            Deliveries = results
        };
    }

    /// <summary>
    /// Повторная доставка событий из очереди (Требование 3 ч.5: Attempt … → SUCCESS).
    /// Недоставленные события остаются в очереди до успешной доставки.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryResult>> RetryQueuedAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            throw new BrokerUnavailableException($"Broker '{Name}' недоступен (UNAVAILABLE)");
        }

        var pending = new List<PendingDelivery>();
        while (_queue.TryDequeue(out PendingDelivery? item)) pending.Add(item);

        var results = new List<DeliveryResult>();
        foreach (PendingDelivery p in pending)
        {
            Log("RETRY", shopEvent: p.Event, subscriber: p.Subscriber.Name,
                detail: $"повторная доставка из очереди Broker '{Name}' (ранее попыток: {p.Attempts})");

            DeliveryResult result = await DeliverCoreAsync(p.Subscriber, p.Event, enqueueOnFailure: false, ct)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                // Событие по-прежнему не потеряно — возвращается в очередь
                _queue.Enqueue(p with { Attempts = p.Attempts + result.Attempts, LastError = result.Error ?? result.Status });
            }
            results.Add(result);
        }
        return results;
    }

    /// <summary>Доставка одному Subscriber'у с повторными попытками (и постановкой в очередь при отказе).</summary>
    private async Task<DeliveryResult> DeliverAsync(ISubscriber subscriber, ShopEvent shopEvent, CancellationToken ct) =>
        await DeliverCoreAsync(subscriber, shopEvent, enqueueOnFailure: true, ct).ConfigureAwait(false);

    private async Task<DeliveryResult> DeliverCoreAsync(ISubscriber subscriber, ShopEvent shopEvent,
        bool enqueueOnFailure, CancellationToken ct)
    {
        Log("DELIVER", shopEvent: shopEvent, subscriber: subscriber.Name, detail: $"через Broker '{Name}'");

        var stopwatch = Stopwatch.StartNew();
        string status = "FAILED";
        string? error = null;
        int attemptsUsed = 0;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            attemptsUsed = attempt;
            try
            {
                await subscriber.HandleAsync(shopEvent, ct).ConfigureAwait(false);
                status = "SUCCESS";
                error = null;
                break;
            }
            catch (SubscriberUnavailableException ex)
            {
                status = "UNAVAILABLE";
                error = ex.Message;
                Log("ATTEMPT FAIL", shopEvent: shopEvent, subscriber: subscriber.Name,
                    detail: $"Attempt {attempt}/{MaxAttempts} > FAIL: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Ошибка обработки события не должна ронять Broker (Требование 5 ч.4)
                status = "FAILED";
                error = ex.Message;
                Log("ATTEMPT FAIL", shopEvent: shopEvent, subscriber: subscriber.Name,
                    detail: $"Attempt {attempt}/{MaxAttempts} > FAIL: {ex.Message}");
            }

            if (attempt < MaxAttempts && RetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }

        stopwatch.Stop();

        if (status == "SUCCESS")
        {
            Log("SUCCESS", shopEvent: shopEvent, subscriber: subscriber.Name,
                detail: $"Attempt {attemptsUsed} > SUCCESS, обработка {stopwatch.ElapsedMilliseconds} мс");
        }
        else
        {
            Log("FAIL", shopEvent: shopEvent, subscriber: subscriber.Name,
                detail: $"{attemptsUsed}/{MaxAttempts} попыток > {status}: {error}");

            if (enqueueOnFailure)
            {
                // Временное хранение в Broker: событие не теряется при отказе Subscriber
                _queue.Enqueue(new PendingDelivery(shopEvent, subscriber, attemptsUsed, error ?? status));
                Log("QUEUED", shopEvent: shopEvent, subscriber: subscriber.Name,
                    detail: $"событие сохранено в очереди Broker '{Name}' до восстановления подписчика");
            }
        }

        return new DeliveryResult(subscriber.Name, status, attemptsUsed, stopwatch.ElapsedMilliseconds, error);
    }

    private void Log(string phase, ShopEvent? shopEvent = null, string? eventType = null,
        string? subscriber = null, string? detail = null) =>
        OnLog?.Invoke(new PubSubLogEntry(
            DateTime.Now, Name, phase,
            shopEvent?.ClientId, shopEvent?.EventType ?? eventType, subscriber, detail));
}

/// <summary>
/// ЛР №6: Broker недоступен — Publisher перехватывает исключение и публикует
/// событие через следующий доступный Broker (работа с несколькими Broker, ч.5).
/// </summary>
public sealed class BrokerUnavailableException : Exception
{
    public BrokerUnavailableException(string message) : base(message) { }
}
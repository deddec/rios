using System.Text.Json;
using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №7: очередь мёртвых писем (Dead Letter Queue).
/// События, которые не могут быть доставлены ни одному подписчику после
/// исчерпания всех попыток, перемещаются сюда для ручного анализа и повторной обработки.
/// </summary>
public sealed class DeadLetterQueue : IDisposable
{
    private readonly string _dlqPath;
    private readonly object _gate = new();

    public DeadLetterQueue(string dlqPath)
    {
        _dlqPath = dlqPath;
        Directory.CreateDirectory(_dlqPath);
    }

    /// <summary>Добавить событие в DLQ.</summary>
    public void Add(ShopEvent shopEvent, string reason, IReadOnlyList<DeliveryResult> deliveries)
    {
        lock (_gate)
        {
            var entry = new
            {
                Event = shopEvent,
                Reason = reason,
                Deliveries = deliveries,
                AddedAt = DateTime.Now
            };
            string fileName = $"{shopEvent.EventId}_{shopEvent.EventType}.json";
            string filePath = Path.Combine(_dlqPath, fileName);
            string json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
    }

    /// <summary>Получить все события в DLQ.</summary>
    public IReadOnlyList<DeadLetterEntry> GetAll()
    {
        lock (_gate)
        {
            var entries = new List<DeadLetterEntry>();
            foreach (string file in Directory.GetFiles(_dlqPath, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<DeadLetterEntryRaw>(json);
                    if (data is not null)
                    {
                        entries.Add(new DeadLetterEntry(
                            data.Event,
                            data.Reason,
                            data.Deliveries.Select(d => new DeliveryResult(
                                d.Subscriber, d.Status, d.Attempts, d.ElapsedMs, d.Error)).ToList(),
                            data.AddedAt));
                    }
                }
                catch
                {
                    // Пропускаем повреждённые файлы.
                }
            }
            return entries;
        }
    }

    /// <summary>Удалить событие из DLQ (например, после повторной обработки).</summary>
    public void Remove(string eventId)
    {
        lock (_gate)
        {
            foreach (string file in Directory.GetFiles(_dlqPath, $"{eventId}_*.json"))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Очистить всю DLQ.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (string file in Directory.GetFiles(_dlqPath, "*.json"))
            {
                File.Delete(file);
            }
        }
    }

    public int Count => Directory.GetFiles(_dlqPath, "*.json").Length;

    public void Dispose() { }
}

/// <summary>Запись DLQ с десериализованными доставками.</summary>
public sealed record DeadLetterEntry(ShopEvent Event, string Reason, IReadOnlyList<DeliveryResult> Deliveries, DateTime AddedAt);

internal sealed class DeadLetterEntryRaw
{
    public ShopEvent Event { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
    public List<DeliveryResultRaw> Deliveries { get; set; } = new();
    public DateTime AddedAt { get; set; }
}

internal sealed class DeliveryResultRaw
{
    public string Subscriber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public long ElapsedMs { get; set; }
    public string? Error { get; set; }
}
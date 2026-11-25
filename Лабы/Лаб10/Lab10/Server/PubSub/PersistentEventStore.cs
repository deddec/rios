using System.Text.Json;
using FlowerShop.Shared.PubSub;

namespace FlowerShop.Server.PubSub;

/// <summary>
/// ЛР №7: постоянное хранилище событий Pub/Sub на диске (JSON-файлы).
/// Гарантирует, что события не теряются при перезапуске Broker'а.
/// События сохраняются перед доставкой подписчикам (write-ahead log).
/// </summary>
public sealed class PersistentEventStore : IDisposable
{
    private readonly string _storePath;
    private readonly object _gate = new();
    private long _nextSequence = 1;

    /// <summary>Путь к папке хранилища событий.</summary>
    public string StorePath => _storePath;

    public PersistentEventStore(string storePath)
    {
        _storePath = storePath;
        Directory.CreateDirectory(_storePath);

        // Восстановление последовательности из существующих файлов при запуске.
        RecoverSequence();
    }

    /// <summary>Сохранить событие в постоянное хранилище (WAL). Возвращает порядковый номер.</summary>
    public long Save(ShopEvent shopEvent)
    {
        lock (_gate)
        {
            long seq = _nextSequence++;
            shopEvent = shopEvent with { SequenceNumber = seq };

            string fileName = $"{seq:D10}_{shopEvent.EventId}.json";
            string filePath = Path.Combine(_storePath, fileName);
            string json = JsonSerializer.Serialize(shopEvent, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);

            return seq;
        }
    }

    /// <summary>Прочитать событие по порядковому номеру.</summary>
    public ShopEvent? Load(long sequenceNumber)
    {
        lock (_gate)
        {
            string pattern = $"{sequenceNumber:D10}_*.json";
            foreach (string file in Directory.GetFiles(_storePath, pattern))
            {
                string json = File.ReadAllText(file);
                return JsonSerializer.Deserialize<ShopEvent>(json);
            }
            return null;
        }
    }

    /// <summary>Прочитать все события из хранилища (для восстановления после перезапуска).</summary>
    public IReadOnlyList<ShopEvent> LoadAll()
    {
        lock (_gate)
        {
            var events = new List<ShopEvent>();
            foreach (string file in Directory.GetFiles(_storePath, "*.json").OrderBy(f => f))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var @event = JsonSerializer.Deserialize<ShopEvent>(json);
                    if (@event is not null) events.Add(@event);
                }
                catch
                {
                    // Пропускаем повреждённые файлы.
                }
            }
            return events;
        }
    }

    /// <summary>Удалить событие из хранилища после успешной доставки всем подписчикам.</summary>
    public void Delete(long sequenceNumber)
    {
        lock (_gate)
        {
            string pattern = $"{sequenceNumber:D10}_*.json";
            foreach (string file in Directory.GetFiles(_storePath, pattern))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Получить следующий порядковый номер без сохранения.</summary>
    public long PeekNextSequence()
    {
        lock (_gate) return _nextSequence;
    }

    private void RecoverSequence()
    {
        long maxSeq = 0;
        foreach (string file in Directory.GetFiles(_storePath, "*.json"))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Length >= 10 && long.TryParse(fileName[..10], out long seq))
            {
                if (seq > maxSeq) maxSeq = seq;
            }
        }
        _nextSequence = maxSeq + 1;
    }

    public void Dispose()
    {
        // Ничего special не требуется для файловых операций.
    }
}
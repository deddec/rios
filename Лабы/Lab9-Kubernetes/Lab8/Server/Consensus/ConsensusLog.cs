namespace FlowerShop.Server.Consensus;

/// <summary>
/// Журнал работы узлов (Требование 1): каждая запись — «[ЧЧ:мм:сс.ммм] сообщение».
/// Потокобезопасен; опционально дублируется в файл и в событие <see cref="LineWritten"/>.
/// </summary>
public sealed class ConsensusLog
{
    private readonly List<string> _lines = new();
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private readonly string? _filePath;

    /// <summary>Вызывается после добавления каждой записи (например, для вывода в консоль).</summary>
    public event Action<string>? LineWritten;

    public ConsensusLog(string? filePath = null, Func<DateTime>? clock = null)
    {
        _filePath = filePath;
        _clock = clock ?? (() => DateTime.Now);

        if (!string.IsNullOrEmpty(_filePath))
        {
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(_filePath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch (IOException) { /* журнал в памяти продолжает работать */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Добавить запись; возвращает полную строку с меткой времени.</summary>
    public string Write(string message)
    {
        string line = $"[{_clock():HH:mm:ss.fff}] {message}";
        lock (_gate)
        {
            _lines.Add(line);
            if (!string.IsNullOrEmpty(_filePath))
            {
                try { File.AppendAllText(_filePath, line + Environment.NewLine); }
                catch (IOException) { /* ошибка записи в файл не должна ронять кластер */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        LineWritten?.Invoke(line);
        return line;
    }

    /// <summary>Копия всех записей.</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    /// <summary>Последние <paramref name="count"/> записей.</summary>
    public IReadOnlyList<string> Tail(int count)
    {
        lock (_gate)
        {
            if (count <= 0) return Array.Empty<string>();
            return _lines.Skip(Math.Max(0, _lines.Count - count)).ToList();
        }
    }

    public int Count
    {
        get { lock (_gate) return _lines.Count; }
    }

    /// <summary>Есть ли запись, содержащая фрагмент.</summary>
    public bool Contains(string fragment)
    {
        lock (_gate) return _lines.Any(l => l.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>Индекс первой записи с фрагментом начиная с <paramref name="from"/> (или -1).</summary>
    public int IndexOf(string fragment, int from = 0)
    {
        lock (_gate)
        {
            for (int i = Math.Max(0, from); i < _lines.Count; i++)
                if (_lines[i].Contains(fragment, StringComparison.Ordinal)) return i;
            return -1;
        }
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }
}

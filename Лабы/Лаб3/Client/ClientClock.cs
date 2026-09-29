namespace FlowerShop.Client;

/// <summary>
/// Модель часов клиента (ЛР №3, часть 2, Требование 1).
/// Часы каждого узла распределённой системы могут «уходить»: локальное время клиента
/// получается как <c>DateTime.UtcNow + ClockOffset</c>, где <see cref="ClockOffset"/> —
/// искусственное смещение (например, +7 с или −5 с). Системное время компьютера
/// при этом НЕ изменяется — рассинхронизация моделируется внутри программы.
///
/// После синхронизации с сервером клиент сохраняет измеренную поправку
/// <see cref="Correction"/>: CorrectedTime = LocalTime + Correction.
/// </summary>
public sealed class ClientClock
{
    /// <summary>Искусственное смещение часов клиента (модель рассинхронизации).</summary>
    public TimeSpan ClockOffset { get; set; }

    /// <summary>Поправка, вычисленная при синхронизации времени с сервером.</summary>
    public TimeSpan Correction { get; private set; }

    /// <summary>true — поправка рассчитана и сохранена (время синхронизировано).</summary>
    public bool IsSynchronized { get; private set; }

    /// <summary>Локальное время клиента (с учётом моделируемого смещения часов).</summary>
    public DateTime LocalNow => DateTime.UtcNow + ClockOffset;

    /// <summary>Скорректированное время клиента: CorrectedTime = LocalTime + Correction.</summary>
    public DateTime CorrectedNow => LocalNow + Correction;

    /// <summary>Сохранение поправки, вычисленной <see cref="TimeSynchronizer"/> (Требование 4).</summary>
    public void ApplyCorrection(TimeSpan correction)
    {
        Correction = correction;
        IsSynchronized = true;
    }

    /// <summary>Сброс поправки (например, при изменении моделируемого смещения часов).</summary>
    public void ResetCorrection()
    {
        Correction = TimeSpan.Zero;
        IsSynchronized = false;
    }
}

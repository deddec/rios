namespace FlowerShop.Server;

/// <summary>
/// ЛР №9: неконфиденциальные параметры конфигурации.
/// Значение передаётся в контейнер через ConfigMap → переменную окружения APP_MODE.
/// </summary>
public static class AppConfig
{
    /// <summary>Имя переменной окружения.</summary>
    public const string AppModeVariable = "APP_MODE";

    /// <summary>Значение, используемое, если переменная не задана или пуста.</summary>
    public const string DefaultMode = "default";

    /// <summary>
    /// Возвращает режим работы приложения.
    /// <paramref name="readVariable"/> позволяет подменить чтение переменной окружения в тестах.
    /// </summary>
    public static string GetAppMode(Func<string, string?>? readVariable = null)
    {
        Func<string, string?> read = readVariable ?? (name => Environment.GetEnvironmentVariable(name));
        string? value = read(AppModeVariable);
        return string.IsNullOrWhiteSpace(value) ? DefaultMode : value.Trim();
    }
}

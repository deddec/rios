using System.Text;

namespace FlowerShop.Shared.Protocol;

/// <summary>
/// Кадрирование сообщений в TCP-потоке:
/// [4 байта длины (little-endian)][UTF-8 JSON].
/// Гарантирует корректную передачу произвольного JSON поверх потока.
/// </summary>
public static class MessageFramer
{
    private const int MaxMessageSize = 8 * 1024 * 1024; // 8 МБ

    public static void WriteMessage(Stream stream, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = BitConverter.GetBytes(body.Length);
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    /// <summary>Асинхронная запись сообщения (ЛР №2: async/await).</summary>
    public static async Task WriteMessageAsync(Stream stream, string message, CancellationToken ct = default)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = BitConverter.GetBytes(body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static string ReadMessage(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        ReadExactly(stream, header);
        return ReadBody(stream, header);
    }

    /// <summary>Асинхронное чтение сообщения (ЛР №2: async/await).</summary>
    public static async Task<string> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        await ReadExactlyAsync(stream, header, ct);
        return await ReadBodyAsync(stream, header, ct);
    }

    private static string ReadBody(Stream stream, ReadOnlySpan<byte> header)
    {
        int length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageSize)
        {
            throw new IOException($"Некорректная длина сообщения: {length}");
        }

        var body = new byte[length];
        ReadExactly(stream, body);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<string> ReadBodyAsync(Stream stream, ReadOnlyMemory<byte> header, CancellationToken ct)
    {
        int length = BitConverter.ToInt32(header.Span);
        if (length <= 0 || length > MaxMessageSize)
        {
            throw new IOException($"Некорректная длина сообщения: {length}");
        }

        var body = new byte[length];
        await ReadExactlyAsync(stream, body, ct);
        return Encoding.UTF8.GetString(body);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read <= 0)
            {
                throw new EndOfStreamException("Соединение закрыто другой стороной");
            }

            offset += read;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], ct);
            if (read <= 0)
            {
                throw new EndOfStreamException("Соединение закрыто другой стороной");
            }

            offset += read;
        }
    }
}

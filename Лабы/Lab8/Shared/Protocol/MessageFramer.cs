using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace FlowerShop.Shared.Protocol;

/// <summary>Кадрирование сообщений по протоколу: 4 байта длина + JSON-тело.</summary>
public static class MessageFramer
{
    public static async Task WriteMessageAsync(Stream stream, string json, CancellationToken ct = default)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = BitConverter.GetBytes(body.Length);
        await stream.WriteAsync(header, 0, 4, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
    }

    public static async Task<string> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await stream.ReadAsync(header.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Соединение закрыто при чтении заголовка");
            read += n;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > 10_000_000)
            throw new InvalidDataException($"Некорректная длина сообщения: {length}");

        byte[] body = new byte[length];
        read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Соединение закрыто при чтении тела");
            read += n;
        }

        return Encoding.UTF8.GetString(body);
    }
}
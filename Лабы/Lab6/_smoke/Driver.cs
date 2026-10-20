using System.Net.Sockets;
using System.Text;
using System.Text.Json;

string host = "127.0.0.1"; int port = 5099;
async Task<string> Send(object req)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(host, port);
    using NetworkStream s = tcp.GetStream();
    string json = JsonSerializer.Serialize(req);
    byte[] body = Encoding.UTF8.GetBytes(json);
    await s.WriteAsync(BitConverter.GetBytes(body.Length));
    await s.WriteAsync(body);
    byte[] hdr = new byte[4];
    int r = 0; while (r < 4) r += await s.ReadAsync(hdr.AsMemory(r));
    int len = BitConverter.ToInt32(hdr);
    byte[] buf = new byte[len];
    int got = 0; while (got < len) got += await s.ReadAsync(buf.AsMemory(got));
    return Encoding.UTF8.GetString(buf);
}
async Task Op(string name, object? payload = null)
{
    var resp = JsonSerializer.Deserialize<JsonElement>(await Send(new { operation = name, payload }));
    Console.WriteLine($"[{name}] success={resp.GetProperty("success")} :: {resp.GetProperty("message").GetString()}");
}
await Op("GetInstanceState");
await Op("SetBouquetAvailability", new { bouquetId = 2, count = 5 });
await Op("CheckConsistency");
await Op("SetInstanceState", new { instance = "primary", available = false });
await Op("Failover");
await Op("GetBouquetStatus", new { bouquetId = 2 });
await Op("Recover");
await Op("CheckConsistency");

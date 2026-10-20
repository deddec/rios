using FlowerShop.Client;
using FlowerShop.Shared.Protocol;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// Тест 4 (Требование 9): сервер недоступен.
/// Проверяет поведение клиента при попытке отправки запроса на незанятый порт.
/// </summary>
public class NetworkClientTests
{
    [Fact]
    public void Send_ToUnavailableServer_ThrowsServerUnavailableException()
    {
        // Arrange: порт наверняка не занят
        int freePort = GetFreePort();
        using var client = new NetworkClient("127.0.0.1", freePort);
        var request = RequestBuilder.Build("CreateOrder", new { customerId = 1 });

        // Act
        Exception? caught = Record.Exception(() => client.Send(request));

        // Assert
        Assert.IsType<ServerUnavailableException>(caught);
    }

    [Fact]
    public void Send_ToUnavailableServer_ErrorMentionsHostAndPort()
    {
        int freePort = GetFreePort();
        using var client = new NetworkClient("127.0.0.1", freePort);
        var request = RequestBuilder.Build("GetProducts", null);

        try
        {
            client.Send(request);
            Assert.Fail("Ожидалось ServerUnavailableException");
        }
        catch (ServerUnavailableException ex)
        {
            Assert.Contains("127.0.0.1", ex.Message);
            Assert.Contains(freePort.ToString(), ex.Message);
        }
    }

    /// <summary>Получение свободного TCP-порта.</summary>
    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

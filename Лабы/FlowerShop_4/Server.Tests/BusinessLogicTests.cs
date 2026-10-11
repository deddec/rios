using System.Text.Json;
using FlowerShop.Server;
using FlowerShop.Shared.Models;
using FlowerShop.Shared.Protocol;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// Unit-тесты бизнес-логики сервера (Требование 9).
/// Тест 1 — успешный сценарий; тест 2 — неизвестная операция;
/// тест 3 — некорректные данные; тест 4 — недоступный сервер (см. NetworkClientTests).
/// </summary>
public class BusinessLogicTests
{
    private static Request CreateOrderRequest(object payload) => new()
    {
        Operation = "CreateOrder",
        Payload = JsonSerializer.SerializeToElement(payload)
    };

    private static readonly object ValidCreateOrderPayload = new
    {
        customerId = 1,
        bouquetId = 1,
        deliveryAddress = "ул. Ленина, 10",
        deliveryDate = "2026-09-30"
    };

    /// <summary>Прогон запроса через RequestHandler, как на реальном сервере.</summary>
    private static Response Handle(BusinessLogic logic, Request request) =>
        new RequestHandler(logic).Handle(JsonSerializer.Serialize(request, Json.Options));

    // ----- Тест 1. Успешный сценарий ------------------------------------

    [Fact]
    public void Handle_CreateOrder_WithValidData_ReturnsSuccess()
    {
        var logic = new BusinessLogic();
        var request = CreateOrderRequest(ValidCreateOrderPayload);

        Response response = Handle(logic, request);

        Assert.True(response.Success);
        Assert.Null(response.ErrorCode);
        Assert.NotNull(response.Data);
        Assert.Single(logic.DbgOrders);
        Assert.Contains(logic.DbgOrders, o => o.Status == OrderStatus.Created);
    }

    /// <summary>Полный жизненный цикл: все 4 операции и все 4 события.</summary>
    [Fact]
    public void Handle_FullWorkflow_CreatesOrderPreparesBouquetAssignsFloristArrangesDelivery()
    {
        var logic = new BusinessLogic();

        Response created = Handle(logic, CreateOrderRequest(ValidCreateOrderPayload));
        Assert.True(created.Success);
        int orderId = logic.DbgOrders[0].Id;

        Response customized = Handle(logic, new Request
        {
            Operation = "CustomizeBouquet",
            Payload = JsonSerializer.SerializeToElement(new
            {
                orderId,
                name = "Свадебный шарм",
                flowers = new object[]
                {
                    new { flowerName = "Роза", count = 9, pricePerUnit = 150m },
                    new { flowerName = "Пион", count = 5, pricePerUnit = 200m }
                }
            })
        });
        Assert.True(customized.Success);

        Response assigned = Handle(logic, new Request
        {
            Operation = "AssignFlorist",
            Payload = JsonSerializer.SerializeToElement(new { orderId, floristId = 1 })
        });
        Assert.True(assigned.Success);

        Response delivered = Handle(logic, new Request
        {
            Operation = "ArrangeDelivery",
            Payload = JsonSerializer.SerializeToElement(new { orderId })
        });
        Assert.True(delivered.Success);

        var order = logic.DbgOrders[0];
        Assert.Equal(OrderStatus.DeliveryArranged, order.Status);
        Assert.NotNull(order.FloristId);
        Assert.Equal(
            new[] { "OrderCreated", "BouquetPrepared", "FloristAssigned", "DeliveryArranged" },
            order.Events);
    }

    // ----- Тест 2. Неизвестная операция ----------------------------------

    [Theory]
    [InlineData("CreateUser")]
    [InlineData("DeleteWorld")]
    [InlineData("createorder")]
    public void Handle_UnknownOperation_ReturnsUnknownOperationError(string operation)
    {
        var logic = new BusinessLogic();
        var request = new Request { Operation = operation, Payload = null };

        Response response = Handle(logic, request);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.UnknownOperation, response.ErrorCode);
        Assert.Contains(operation, response.Message);
    }

    // ----- Тест 3. Некорректные данные ------------------------------------

    [Fact]
    public void Handle_CreateOrder_WithEmptyAddress_ReturnsValidationError()
    {
        var logic = new BusinessLogic();
        var request = CreateOrderRequest(new
        {
            customerId = 1,
            bouquetId = 1,
            deliveryAddress = "",
            deliveryDate = "2026-09-30"
        });

        Response response = Handle(logic, request);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ValidationError, response.ErrorCode);
        Assert.Empty(logic.DbgOrders);
    }


    [Fact]
    public void Handle_CreateOrder_WithInvalidDate_ReturnsValidationError()
    {
        var logic = new BusinessLogic();
        var request = CreateOrderRequest(new
        {
            customerId = 1,
            bouquetId = 1,
            deliveryAddress = "ул. Ленина, 10",
            deliveryDate = "soon"
        });

        Response response = Handle(logic, request);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ValidationError, response.ErrorCode);
        Assert.Empty(logic.DbgOrders);
    }

    [Fact]
    public void Handle_CreateOrder_WithNullPayload_ReturnsValidationError()
    {
        var logic = new BusinessLogic();
        var request = new Request { Operation = "CreateOrder", Payload = null };

        Response response = Handle(logic, request);

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ValidationError, response.ErrorCode);
    }

    // ----- Отсутствующий объект ------------------------------------------

    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public void Handle_CustomizeBouquet_WithNonExistentOrder_ReturnsNotFound(int orderId)
    {
        var logic = new BusinessLogic();

        Response response = Handle(logic, new Request
        {
            Operation = "CustomizeBouquet",
            Payload = JsonSerializer.SerializeToElement(new { orderId })
        });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.NotFound, response.ErrorCode);
    }

    [Fact]
    public void Handle_AssignFlorist_WithNonExistentFlorist_ReturnsNotFound()
    {
        var logic = new BusinessLogic();
        Handle(logic, CreateOrderRequest(ValidCreateOrderPayload));
        int orderId = logic.DbgOrders[0].Id;

        Response response = Handle(logic, new Request
        {
            Operation = "AssignFlorist",
            Payload = JsonSerializer.SerializeToElement(new { orderId, floristId = 42 })
        });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.NotFound, response.ErrorCode);
        Assert.Null(logic.DbgOrders[0].FloristId);
    }
}


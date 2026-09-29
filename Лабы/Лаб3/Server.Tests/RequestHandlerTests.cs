using System.Text.Json;
using FlowerShop.Server;
using FlowerShop.Shared.Protocol;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>
/// Unit-тесты RequestHandler: парсинг JSON, обработка некорректных запросов.
/// </summary>
public class RequestHandlerTests
{
    private readonly RequestHandler _handler = new(new BusinessLogic());

    [Fact]
    public void Handle_ValidJsonRequest_ReturnsSuccess()
    {
        string json = @"{ ""operation"": ""CreateOrder"", ""payload"": {
            ""customerId"": 1, ""bouquetId"": 2,
            ""deliveryAddress"": ""пр. Мира, 25"", ""deliveryDate"": ""2026-10-01"" } }";

        Response response = _handler.Handle(json);

        Assert.True(response.Success);
        Assert.Null(response.ErrorCode);
    }

    [Fact]
    public void Handle_InvalidJson_ReturnsValidationError()
    {
        Response response = _handler.Handle("{ это не json !!!");

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ValidationError, response.ErrorCode);
    }

    [Fact]
    public void Handle_EmptyOperation_ReturnsValidationError()
    {
        Response response = _handler.Handle(@"{ ""operation"": """" }");

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ValidationError, response.ErrorCode);
    }

    [Fact]
    public void Handle_MissingOperation_ReturnsValidationError()
    {
        Response response = _handler.Handle(@"{ ""payload"": {} }");

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ValidationError, response.ErrorCode);
    }

    [Fact]
    public void Handle_UnknownOperation_ReturnsUnknownOperationError()
    {
        Response response = _handler.Handle(@"{ ""operation"": ""FooBar"" }");

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.UnknownOperation, response.ErrorCode);
    }
}

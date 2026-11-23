using FlowerShop.Server;
using Xunit;

namespace FlowerShop.Tests;

/// <summary>ЛР №9: логика параметра конфигурации APP_MODE (ConfigMap → переменная окружения).</summary>
public class AppConfigTests
{
    [Fact]
    public void GetAppMode_VariableSet_ReturnsValue()
    {
        string mode = AppConfig.GetAppMode(name => name == "APP_MODE" ? "kubernetes" : null);

        Assert.Equal("kubernetes", mode);
    }

    [Fact]
    public void GetAppMode_VariableMissing_ReturnsDefault()
    {
        string mode = AppConfig.GetAppMode(_ => null);

        Assert.Equal(AppConfig.DefaultMode, mode);
        Assert.Equal("default", mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetAppMode_EmptyOrWhitespace_ReturnsDefault(string value)
    {
        Assert.Equal("default", AppConfig.GetAppMode(_ => value));
    }

    [Fact]
    public void GetAppMode_ValueWithSpaces_IsTrimmed()
    {
        Assert.Equal("kubernetes", AppConfig.GetAppMode(_ => "  kubernetes \n"));
    }

    [Fact]
    public void GetAppMode_ReadsVariableNamedAppMode()
    {
        string? requested = null;

        AppConfig.GetAppMode(name => { requested = name; return null; });

        Assert.Equal("APP_MODE", requested);
    }

    [Fact]
    public void GetAppMode_WithoutArgument_DoesNotThrow()
    {
        string mode = AppConfig.GetAppMode();

        Assert.False(string.IsNullOrWhiteSpace(mode));
    }
}

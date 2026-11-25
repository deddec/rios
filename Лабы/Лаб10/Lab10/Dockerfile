# Многоэтапная сборка для серверного приложения FlowerShop (ЛР № 8 — контейнеризация)
# Этап 1: сборка приложения с .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Копируем файлы проектов для восстановления зависимостей
COPY ["Server/FlowerShop.Server.csproj", "Server/"]
COPY ["Shared/FlowerShop.Shared.csproj", "Shared/"]
RUN dotnet restore "Server/FlowerShop.Server.csproj"

# Копируем всё остальное и собираем
COPY . .
WORKDIR "/src/Server"
RUN dotnet publish "FlowerShop.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Этап 2: рантайм-образ для запуска
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Порт, на котором слушает сервер (по умолчанию 5000)
ENV ASPNETCORE_URLS=http://+:5000
ENV FlowerShop__Port=5000

# Копируем опубликованные файлы из этапа сборки
COPY --from=build /app/publish .

# Точка входа — серверное приложение
ENTRYPOINT ["dotnet", "FlowerShop.Server.dll"]
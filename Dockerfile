# Используем официальный .NET SDK для сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Копируем проект и восстанавливаем зависимости
COPY . ./
RUN dotnet restore
RUN dotnet publish -c Release -o out

# Используем минимальный .NET рантайм
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .

# Указываем порт (важно для WebSockets)
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "WebSocketServer.dll"]

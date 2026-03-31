FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/MyTelegram.AuthServer/MyTelegram.AuthServer.csproj", "src/MyTelegram.AuthServer/"]
COPY ["src/MyTelegram.Core/MyTelegram.Core.csproj", "src/MyTelegram.Core/"]
COPY ["src/MyTelegram.Schema/MyTelegram.Schema.csproj", "src/MyTelegram.Schema/"]
COPY ["src/MyTelegram.Domain.Shared/MyTelegram.Domain.Shared.csproj", "src/MyTelegram.Domain.Shared/"]
COPY ["src/MyTelegram.Domain/MyTelegram.Domain.csproj", "src/MyTelegram.Domain/"]
COPY ["src/MyTelegram.Services/MyTelegram.Services.csproj", "src/MyTelegram.Services/"]
COPY ["src/MyTelegram.EventFlow/MyTelegram.EventFlow.csproj", "src/MyTelegram.EventFlow/"]
COPY ["src/MyTelegram.EventFlow.MongoDB/MyTelegram.EventFlow.MongoDB.csproj", "src/MyTelegram.EventFlow.MongoDB/"]
COPY ["src/MyTelegram.EventBus/MyTelegram.EventBus.csproj", "src/MyTelegram.EventBus/"]
COPY ["src/MyTelegram.EventBus.RabbitMQ/MyTelegram.EventBus.RabbitMQ.csproj", "src/MyTelegram.EventBus.RabbitMQ/"]
COPY ["src/MyTelegram.Caching.Redis/MyTelegram.Caching.Redis.csproj", "src/MyTelegram.Caching.Redis/"]
COPY ["src/MyTelegram.Abstractions/MyTelegram.Abstractions.csproj", "src/MyTelegram.Abstractions/"]
RUN dotnet restore "src/MyTelegram.AuthServer/MyTelegram.AuthServer.csproj"
COPY . .
WORKDIR "/src/src/MyTelegram.AuthServer"
RUN dotnet build "MyTelegram.AuthServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MyTelegram.AuthServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MyTelegram.AuthServer.dll"]

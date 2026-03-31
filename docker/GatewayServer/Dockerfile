FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY source/src /src/src
RUN dotnet restore "src/src/MyTelegram.GatewayServer/MyTelegram.GatewayServer.csproj"
WORKDIR "/src/src/MyTelegram.GatewayServer"
RUN dotnet build "MyTelegram.GatewayServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MyTelegram.GatewayServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MyTelegram.GatewayServer.dll"]

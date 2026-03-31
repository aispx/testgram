FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY source /src
RUN dotnet restore "src/MyTelegram.Messenger.CommandServer/MyTelegram.Messenger.CommandServer.csproj"
WORKDIR "/src/src/MyTelegram.Messenger.CommandServer"
RUN dotnet build "MyTelegram.Messenger.CommandServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MyTelegram.Messenger.CommandServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MyTelegram.Messenger.CommandServer.dll"]

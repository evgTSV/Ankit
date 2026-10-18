FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Ankit.Server/Ankit.Server.fsproj", "Ankit.Server/"]
RUN dotnet restore "Ankit.Server/Ankit.Server.fsproj"
COPY . .
WORKDIR "/src/Ankit.Server"
RUN dotnet build "./Ankit.Server.fsproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Ankit.Server.fsproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Ankit.Server.dll"]

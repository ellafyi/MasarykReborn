FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Masaryk.Core/Masaryk.Core.fsproj", "Masaryk.Core/"]
COPY ["Masaryk.Postgres/Masaryk.Postgres.fsproj", "Masaryk.Postgres/"]
COPY ["Masaryk.Configuration/Masaryk.Configuration.fsproj", "Masaryk.Configuration/"]
RUN dotnet restore "Masaryk.Core/Masaryk.Core.fsproj"
COPY . .
WORKDIR "/src/Masaryk.Core"
RUN dotnet publish "./Masaryk.Core.fsproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Masaryk.Core.dll"]

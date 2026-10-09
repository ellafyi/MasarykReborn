FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Masaryk.Postgres.Migrator/Masaryk.Postgres.Migrator.fsproj", "Masaryk.Postgres.Migrator/"]
COPY ["Masaryk.Postgres/Masaryk.Postgres.fsproj", "Masaryk.Postgres/"]
RUN dotnet restore "Masaryk.Postgres.Migrator/Masaryk.Postgres.Migrator.fsproj"
COPY . .
RUN dotnet publish "Masaryk.Postgres.Migrator/Masaryk.Postgres.Migrator.fsproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
USER $APP_UID
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Masaryk.Postgres.Migrator.dll"]

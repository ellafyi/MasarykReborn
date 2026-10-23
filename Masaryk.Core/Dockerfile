FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Masaryk.Core/Masaryk.Core.fsproj", "Masaryk.Core/"]
COPY ["Masaryk.Postgres/Masaryk.Postgres.fsproj", "Masaryk.Postgres/"]
COPY ["Masaryk.Configuration/Masaryk.Configuration.fsproj", "Masaryk.Configuration/"]
RUN dotnet restore "Masaryk.Core/Masaryk.Core.fsproj"
COPY . .
RUN dotnet publish "Masaryk.Core/Masaryk.Core.fsproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
USER $APP_UID
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Masaryk.Core.dll"]

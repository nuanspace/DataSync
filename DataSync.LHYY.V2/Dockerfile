FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["DataSync.LHYY.V2/DataSync.LHYY.V2.csproj", "DataSync.LHYY.V2/"]
COPY ["DataSync.LHYY.V2/NuGet.Config", "DataSync.LHYY.V2/"]
COPY ["DataSync.Common/DataSync.Common.csproj", "DataSync.Common/"]
RUN dotnet restore "DataSync.LHYY.V2/DataSync.LHYY.V2.csproj"
COPY . .
WORKDIR "/src/DataSync.LHYY.V2"
RUN dotnet build "DataSync.LHYY.V2.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DataSync.LHYY.V2.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl tesseract-ocr tesseract-ocr-chi-sim poppler-utils \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-17 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=publish /app/publish .
RUN rm -f appsettings.json appsettings.Development.json appsettings.Production.json
ENTRYPOINT ["dotnet", "DataSync.LHYY.V2.dll"]

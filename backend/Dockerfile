# Backend (.NET 8 + SignalR)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY backend/NodiClawdbot.Backend/NodiClawdbot.Backend.csproj backend/NodiClawdbot.Backend/
RUN dotnet restore backend/NodiClawdbot.Backend/NodiClawdbot.Backend.csproj

COPY backend/NodiClawdbot.Backend/ backend/NodiClawdbot.Backend/
RUN dotnet publish backend/NodiClawdbot.Backend/NodiClawdbot.Backend.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# For PDF text extraction (pdftotext)
RUN apt-get update \
  && apt-get install -y --no-install-recommends poppler-utils \
  && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out ./
EXPOSE 5300
ENV ASPNETCORE_URLS=http://0.0.0.0:5300
ENTRYPOINT ["dotnet", "NodiClawdbot.Backend.dll"]

# Build context is the repo root; the project lives in src/.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/PhoneOrchestrator.csproj ./
RUN dotnet restore
COPY src/ ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
# The aspnet image has no wget and no curl, so a healthcheck that shells out
# to either fails every time and Swarm kills the container in a restart loop.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PhoneOrchestrator.dll"]
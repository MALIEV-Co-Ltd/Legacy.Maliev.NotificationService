# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV GITHUB_ACTIONS=false
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && git clone --filter=blob:none --no-checkout https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults.git /dependencies/Legacy.Maliev.ServiceDefaults \
    && git -C /dependencies/Legacy.Maliev.ServiceDefaults checkout 8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3 \
    && git clone --filter=blob:none --no-checkout https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts.git /dependencies/Legacy.Maliev.CompatibilityContracts \
    && git -C /dependencies/Legacy.Maliev.CompatibilityContracts checkout 78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7
COPY nuget.config .
COPY Legacy.Maliev.NotificationService.Api/Legacy.Maliev.NotificationService.Api.csproj Legacy.Maliev.NotificationService.Api/
COPY Legacy.Maliev.NotificationService.Application/Legacy.Maliev.NotificationService.Application.csproj Legacy.Maliev.NotificationService.Application/
COPY Legacy.Maliev.NotificationService.Domain/Legacy.Maliev.NotificationService.Domain.csproj Legacy.Maliev.NotificationService.Domain/
COPY Legacy.Maliev.NotificationService.Data/Legacy.Maliev.NotificationService.Data.csproj Legacy.Maliev.NotificationService.Data/
RUN dotnet restore Legacy.Maliev.NotificationService.Api/Legacy.Maliev.NotificationService.Api.csproj \
    -p:MalievWorkspaceRoot=/dependencies
COPY . .
RUN dotnet publish Legacy.Maliev.NotificationService.Api/Legacy.Maliev.NotificationService.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false \
    -p:MalievWorkspaceRoot=/dependencies

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Legacy.Maliev.NotificationService.Api.dll"]

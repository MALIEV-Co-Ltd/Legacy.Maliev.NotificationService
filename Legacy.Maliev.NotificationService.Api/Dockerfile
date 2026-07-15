# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV GITHUB_ACTIONS=false
COPY nuget.config .
COPY .dependencies/ .dependencies/
COPY Legacy.Maliev.NotificationService.Api/Legacy.Maliev.NotificationService.Api.csproj Legacy.Maliev.NotificationService.Api/
COPY Legacy.Maliev.NotificationService.Application/Legacy.Maliev.NotificationService.Application.csproj Legacy.Maliev.NotificationService.Application/
COPY Legacy.Maliev.NotificationService.Domain/Legacy.Maliev.NotificationService.Domain.csproj Legacy.Maliev.NotificationService.Domain/
COPY Legacy.Maliev.NotificationService.Data/Legacy.Maliev.NotificationService.Data.csproj Legacy.Maliev.NotificationService.Data/
RUN dotnet restore Legacy.Maliev.NotificationService.Api/Legacy.Maliev.NotificationService.Api.csproj \
    -p:MalievWorkspaceRoot=/src/.dependencies
COPY . .
RUN dotnet publish Legacy.Maliev.NotificationService.Api/Legacy.Maliev.NotificationService.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false \
    -p:MalievWorkspaceRoot=/src/.dependencies

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Legacy.Maliev.NotificationService.Api.dll"]

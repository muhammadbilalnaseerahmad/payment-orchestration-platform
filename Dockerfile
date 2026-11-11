FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/PaymentOrchestration.Api/PaymentOrchestration.Api.csproj src/PaymentOrchestration.Api/
RUN dotnet restore src/PaymentOrchestration.Api/PaymentOrchestration.Api.csproj
COPY src/PaymentOrchestration.Api/ src/PaymentOrchestration.Api/
RUN dotnet publish src/PaymentOrchestration.Api/PaymentOrchestration.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentOrchestration.Api.dll"]

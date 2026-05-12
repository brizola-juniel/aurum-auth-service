FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY src/AuthService/AuthService.csproj src/AuthService/
COPY tests/AuthService.Tests/AuthService.Tests.csproj tests/AuthService.Tests/
RUN dotnet restore src/AuthService/AuthService.csproj

COPY src src
RUN dotnet publish src/AuthService/AuthService.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
USER app
COPY --from=build /app/publish ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "AuthService.dll"]

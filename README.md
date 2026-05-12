# Aurum Auth Service

Microsserviço C# responsável por cadastro, login, emissão de JWT e refresh
token para o Aurum Reservas Brasil.

Este código funciona em dois formatos:

- dentro do monorepo `reservation-system`, em `auth-service/`;
- como repositório standalone exportado, `aurum-auth-service`.

## Responsabilidade

- Persistir usuários em banco relacional próprio.
- Armazenar senhas com PBKDF2 e salt por senha.
- Emitir access token JWT assinado com HS256.
- Emitir refresh token opaco, armazenado apenas como hash SHA-256.
- Expor endpoints REST consumidos pelo frontend/BFF.
- Não consumir o serviço Python de reservas.

## Stack

- .NET 10 LTS.
- ASP.NET Core Minimal APIs.
- Entity Framework Core 10.
- EF Core migrations versionadas.
- PostgreSQL via Npgsql.
- xUnit + WebApplicationFactory + SQLite in-memory nos testes.
- GitHub Actions Docker-only usando `mcr.microsoft.com/dotnet/sdk:10.0-alpine`.
- Dependabot para NuGet, GitHub Actions e Docker.

## Variáveis

```env
ConnectionStrings__DefaultConnection=Host=localhost;Port=5433;Database=authdb;Username=auth;Password=auth
Jwt__Secret=change-this-shared-secret-with-at-least-32-bytes
Jwt__Issuer=aurum-auth-service
Jwt__Audience=aurum-reservation-system
Jwt__AccessTokenMinutes=60
Jwt__RefreshTokenDays=7
Cors__AllowedOrigins__0=http://localhost:3000
```

`Jwt__Secret` deve ser exatamente o mesmo valor usado em `reservation-service`
como `JWT_SECRET`.

## Endpoints

- `GET /health`
- `POST /api/auth/register`
- `POST /api/auth/login`
- `POST /api/auth/refresh`
- `GET /api/auth/me`

## Rodar no monorepo

Na raiz do `reservation-system`:

```bash
./scripts/dev-up.sh
```

O monorepo tambem fornece os comandos oficiais de migrations e validação
integrada:

```bash
./scripts/auth-migration-add.sh AddAuditColumns
./scripts/auth-migration-apply.sh
./scripts/migrations-status.sh
./scripts/test-all.sh
```

Esses comandos usam Docker/Compose e SDK .NET em container. Não é necessário
instalar .NET SDK no host.

## Rodar standalone

Na raiz do repositório `aurum-auth-service`:

```bash
docker network create aurum-auth-local || true

docker run -d \
  --name aurum-auth-postgres \
  --network aurum-auth-local \
  -e POSTGRES_DB=authdb \
  -e POSTGRES_USER=auth \
  -e POSTGRES_PASSWORD=auth \
  -p 5433:5432 \
  postgres:18-alpine

docker build --pull --tag aurum-auth-service:local .

docker run --rm \
  --name aurum-auth-service \
  --network aurum-auth-local \
  -p 5000:8080 \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e ConnectionStrings__DefaultConnection="Host=aurum-auth-postgres;Port=5432;Database=authdb;Username=auth;Password=auth" \
  -e Jwt__Secret=change-this-shared-secret-with-at-least-32-bytes \
  -e Jwt__Issuer=aurum-auth-service \
  -e Jwt__Audience=aurum-reservation-system \
  -e Jwt__AccessTokenMinutes=60 \
  -e Jwt__RefreshTokenDays=7 \
  -e Cors__AllowedOrigins__0=http://localhost:3000 \
  aurum-auth-service:local
```

Healthcheck local:

```bash
curl http://localhost:5000/health
```

## Migrations

- Migration inicial: `src/AuthService/Migrations/20260512033016_InitialAuthSchema.cs`.
- Histórico em runtime: tabela PostgreSQL `__EFMigrationsHistory`.
- O startup executa `Database.MigrateAsync()` como proteção idempotente.
- Nos testes SQLite em container, o schema efêmero usa `EnsureCreatedAsync()`.

No monorepo, prefira os scripts oficiais listados acima. No repositório
standalone, novas migrations devem ser criadas somente em container com SDK
.NET 10, nunca com `dotnet` do host.

## Testes

Execução standalone Docker-only:

```bash
docker run --rm \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  -e NUGET_XMLDOC_MODE=skip \
  -v "$PWD:/workspace" \
  -w /workspace \
  mcr.microsoft.com/dotnet/sdk:10.0-alpine \
  sh -lc "dotnet restore tests/AuthService.Tests/AuthService.Tests.csproj && \
          dotnet build src/AuthService/AuthService.csproj --configuration Release --no-restore -warnaserror && \
          dotnet test tests/AuthService.Tests/AuthService.Tests.csproj --configuration Release --no-restore --logger 'trx;LogFileName=auth-service-tests.trx' --collect:'XPlat Code Coverage' --results-directory TestResults && \
          dotnet publish src/AuthService/AuthService.csproj --configuration Release --no-restore --output /tmp/aurum-auth-publish"
```

Execução a partir da raiz do monorepo:

```bash
docker run --rm \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  -e NUGET_XMLDOC_MODE=skip \
  -v "$PWD/auth-service:/workspace" \
  -w /workspace \
  mcr.microsoft.com/dotnet/sdk:10.0-alpine \
  sh -lc "dotnet restore tests/AuthService.Tests/AuthService.Tests.csproj && \
          dotnet build src/AuthService/AuthService.csproj --configuration Release --no-restore -warnaserror && \
          dotnet test tests/AuthService.Tests/AuthService.Tests.csproj --configuration Release --no-restore --logger 'trx;LogFileName=auth-service-tests.trx' --collect:'XPlat Code Coverage' --results-directory TestResults && \
          dotnet publish src/AuthService/AuthService.csproj --configuration Release --no-restore --output /tmp/aurum-auth-publish"
```

Detalhes adicionais ficam em `TESTING.md`.

## Segurança

Diretrizes de divulgação, rotação de segredo e baseline de segurança ficam em
`SECURITY.md`. Não publique tokens, connection strings ou payloads sensíveis em
issues, logs ou evidências de teste.

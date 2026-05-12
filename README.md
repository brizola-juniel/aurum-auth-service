# Auth Service

Microsserviço C# responsável por cadastro, login, emissão de JWT e refresh token.

## Responsabilidade

- Persistir usuários em banco relacional próprio.
- Armazenar senhas com PBKDF2 + salt.
- Emitir access token JWT assinado com HS256.
- Emitir refresh token opaco, armazenado apenas como hash SHA-256.
- Expor endpoints REST consumidos pelo frontend.
- Não consumir o serviço Python.

## Stack

- .NET 10 LTS.
- ASP.NET Core Minimal APIs.
- Entity Framework Core 10.
- EF Core migrations versionadas.
- PostgreSQL via Npgsql.
- xUnit + WebApplicationFactory + SQLite in-memory nos testes.

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

`Jwt__Secret` deve ser exatamente o mesmo valor usado em `reservation-service` como `JWT_SECRET`.

## Endpoints

- `GET /health`
- `POST /api/auth/register`
- `POST /api/auth/login`
- `POST /api/auth/refresh`
- `GET /api/auth/me`

## Rodar via Docker

Na raiz:

```bash
./scripts/dev-up.sh
```

Este projeto é Docker-first. Não é necessário instalar .NET SDK no host.

## Migrations

- Migration inicial: `src/AuthService/Migrations/20260512033016_InitialAuthSchema.cs`.
- Histórico em runtime: tabela PostgreSQL `__EFMigrationsHistory`.
- No PostgreSQL, o schema é aplicado por `../scripts/auth-migration-apply.sh`.
- O startup executa `Database.MigrateAsync()` como proteção idempotente.
- Nos testes SQLite em container, o schema efêmero continua com `EnsureCreatedAsync()`.

Comandos oficiais pela raiz do monorepo:

```bash
./scripts/auth-migration-add.sh AddAuditColumns
./scripts/auth-migration-apply.sh
./scripts/migrations-status.sh
```

Esses comandos usam Docker/Compose e o SDK .NET 10 em container. O `dotnet-ef` fica
pinado em `.config/dotnet-tools.json`.

## Testes

Na raiz, a execução padronizada é:

```bash
./scripts/test-all.sh
```

Somente o serviço C#:

```bash
docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0-alpine \
  sh -lc "dotnet restore ReservationSystem.sln && dotnet test ReservationSystem.sln --no-restore"
```

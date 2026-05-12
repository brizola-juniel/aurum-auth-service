# Testing

This repository is Docker-first. The host only needs Docker for the official
validation path.

## Standalone Repository

Run from the root of `aurum-auth-service`:

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

Build the runtime image:

```bash
docker build --pull --tag aurum-auth-service:local .
```

## Monorepo Workspace

Run the auth-only Docker gate from `/home/juniel/Documentos/reservation-system`
or from the monorepo root:

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

The broader monorepo gates remain:

```bash
./scripts/test-all.sh
./scripts/audit-sbom.sh
```

## CI Gate

GitHub Actions runs `.github/workflows/ci.yml` on pushes to `main`, pull
requests, and manual dispatches. The required status check is named `auth-quality`.

The workflow validates:

- restore;
- Release build with warnings as errors;
- xUnit tests with TRX and coverage output;
- publish smoke test;
- Docker image build.

## Expected Test Shape

- API tests use `WebApplicationFactory`.
- Persistence tests run against ephemeral SQLite in memory.
- Production PostgreSQL behavior is covered by versioned EF Core migrations and
  the monorepo Docker smoke path.
- Authentication assertions should cover JWT claims, invalid credentials,
  duplicate registration, refresh-token behavior, and password hashing.

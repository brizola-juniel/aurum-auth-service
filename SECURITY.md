# Security Policy

## Supported Scope

This policy covers the `aurum-auth-service` repository and the same code when it
is maintained inside the reservation-system monorepo under `auth-service/`.

Supported branch:

| Branch | Status |
| --- | --- |
| `main` | Supported |

## Security Controls

- Passwords are hashed with PBKDF2 and per-password salt.
- Access tokens are JWT HS256 tokens with issuer, audience, subject and email
  claims.
- Refresh tokens are opaque values stored only as SHA-256 hashes.
- `Jwt__Secret` must be supplied by the orchestrator and must never be committed.
- `Jwt__Secret` must match the reservation service `JWT_SECRET`.
- Use a production secret with at least 32 bytes and rotate it outside Git.
- CORS must allow only trusted frontend/BFF origins.
- Production databases must use dedicated credentials with least privilege.
- Logs must not include raw passwords, access tokens, refresh tokens, or
  connection strings.

## Reporting

Do not open public issues with secrets, tokens, exploit payloads, private logs,
or personally identifiable information.

Report security issues privately to the repository owner with:

- affected endpoint, commit, or release;
- reproduction steps using local test data only;
- observed impact;
- suggested remediation, if known.

Expected handling:

- acknowledge the report before public disclosure;
- reproduce in a Docker-only environment;
- fix on a private branch when disclosure risk is material;
- release with a clear changelog and rotation guidance when secrets or tokens
  could have been affected.

## Dependency and Supply Chain Baseline

- GitHub Actions runs without host .NET. The CI uses
  `mcr.microsoft.com/dotnet/sdk:10.0-alpine`.
- Dependabot monitors NuGet, GitHub Actions, and Docker manifests.
- Dependency PRs must pass the Docker-only `quality` gate before merge.
- Docker images should be rebuilt after dependency or base-image updates.

## Secret Rotation

Rotate `Jwt__Secret` and invalidate refresh tokens when:

- the secret is exposed or suspected to be exposed;
- a privileged environment variable store is compromised;
- signing behavior changes;
- a production incident involves authentication or session integrity.

After rotation, restart all services that validate tokens so issuer, audience,
and signing secret remain consistent across the system.

## Validation

Run the Docker-only quality gate before every release:

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

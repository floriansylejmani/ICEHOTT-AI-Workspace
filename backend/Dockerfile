# ICEHOTT ASP.NET image. Build context: repository root.
#
#   docker build -f backend/Dockerfile --build-arg GIT_SHA=<sha> -t icehott-api:<sha> .
#   docker build -f backend/Dockerfile --build-arg GIT_SHA=<sha> --target migrator -t icehott-migrator:<sha> .
#
# One runtime image serves both the API and the worker; the role is chosen at run time
# with Service__Role=Api|Worker. The image never contains configuration secrets.

ARG DOTNET_VERSION=9.0

# ---- restore: cached until a project file or the SDK pin changes ----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS restore
WORKDIR /src
COPY global.json ./
COPY .config/dotnet-tools.json ./.config/dotnet-tools.json
COPY backend/src/ICEHOTT.API/ICEHOTT.API.csproj backend/src/ICEHOTT.API/
COPY backend/src/ICEHOTT.Application/ICEHOTT.Application.csproj backend/src/ICEHOTT.Application/
COPY backend/src/ICEHOTT.Domain/ICEHOTT.Domain.csproj backend/src/ICEHOTT.Domain/
COPY backend/src/ICEHOTT.Infrastructure/ICEHOTT.Infrastructure.csproj backend/src/ICEHOTT.Infrastructure/
COPY backend/src/ICEHOTT.Persistence/ICEHOTT.Persistence.csproj backend/src/ICEHOTT.Persistence/
RUN dotnet restore backend/src/ICEHOTT.API/ICEHOTT.API.csproj

# ---- publish ----
FROM restore AS publish
ARG GIT_SHA=unknown
COPY backend/src/ backend/src/
RUN dotnet publish backend/src/ICEHOTT.API/ICEHOTT.API.csproj \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false -p:SourceRevisionId=${GIT_SHA}

# ---- migration bundle: a self-contained, version-pinned EF migrations executable ----
FROM restore AS migrations-build
COPY backend/src/ backend/src/
# The design-time DbContext factory needs only a connection string, so the bundle runs
# without any application settings and receives its connection at run time.
RUN dotnet tool restore \
 && dotnet ef migrations bundle \
      --self-contained -r linux-x64 --force \
      --project backend/src/ICEHOTT.Persistence/ICEHOTT.Persistence.csproj \
      --startup-project backend/src/ICEHOTT.API/ICEHOTT.API.csproj \
      --output /out/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS migrator
ARG GIT_SHA=unknown
ARG SOURCE_URL=https://github.com/floriansylejmani/ICEHOTT-AI-Workspace
LABEL org.opencontainers.image.revision=${GIT_SHA} \
      org.opencontainers.image.source=${SOURCE_URL}
WORKDIR /migrate
COPY --from=migrations-build /out/efbundle ./efbundle
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
USER $APP_UID
# Usage: docker run --rm <image> --connection "<runtime-supplied connection string>"
ENTRYPOINT ["./efbundle"]

# ---- runtime (default target) ----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG GIT_SHA=unknown
ARG SOURCE_URL=https://github.com/floriansylejmani/ICEHOTT-AI-Workspace
LABEL org.opencontainers.image.revision=${GIT_SHA} \
      org.opencontainers.image.source=${SOURCE_URL}
WORKDIR /app
COPY --from=publish /app/publish .
# Local-only artifact directory; production uses object storage.
RUN mkdir -p /app/data/artifacts && chown -R $APP_UID:$APP_UID /app/data
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    Release__GitSha=${GIT_SHA}
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ICEHOTT.API.dll"]

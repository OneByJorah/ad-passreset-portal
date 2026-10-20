# ── Stage 1: build the SPA (Vite → src/PassReset.Web/wwwroot) ────────────────
FROM node:24-alpine AS client
WORKDIR /src
COPY src/PassReset.Web/ClientApp/package.json src/PassReset.Web/ClientApp/package-lock.json ./ClientApp/
RUN cd ClientApp && npm ci --no-audit --no-fund
COPY src/PassReset.Web/ClientApp/ ./ClientApp/
RUN cd ClientApp && npm run build

# ── Stage 2: publish the .NET app (Linux TFM → LDAP provider wiring) ─────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY src/PassReset.sln ./
COPY src/PassReset.Common/ ./PassReset.Common/
COPY src/PassReset.PasswordProvider.Ldap/ ./PassReset.PasswordProvider.Ldap/
COPY src/PassReset.PasswordProvider/ ./PassReset.PasswordProvider/
COPY src/PassReset.Web/*.csproj ./PassReset.Web/
COPY src/PassReset.Web/ ./PassReset.Web/
COPY --from=client /src/wwwroot/ ./PassReset.Web/wwwroot/
RUN dotnet publish PassReset.Web -c Release -o /app/publish

# ── Stage 3: runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=server /app/publish ./
# Writable dirs for data-protection keys + branding assets (named volumes in
# compose inherit this ownership on first use).
RUN mkdir -p /app/keys /usr/share/PassReset/brand && chown -R app:app /app/keys /usr/share/PassReset
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /api/health HTTP/1.0\r\n\r\n" >&3 && grep -q " 200" <&3' || exit 1
ENTRYPOINT ["dotnet", "PassReset.Web.dll"]

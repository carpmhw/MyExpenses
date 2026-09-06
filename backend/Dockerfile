FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MyExpenses.Api/MyExpenses.Api.csproj .
RUN dotnet restore
COPY MyExpenses.Api/ .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VCS_REF=unknown
LABEL org.opencontainers.image.revision="${VCS_REF}" \
      org.opencontainers.image.source="https://github.com/carpmhw/MyExpenses"
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DataProtection__ApplicationName=MyExpenses
ENV DataProtection__KeyDirectory=/app/keys
VOLUME /app/data
VOLUME /app/keys
ENTRYPOINT ["dotnet", "MyExpenses.Api.dll"]
HEALTHCHECK --interval=10s --timeout=5s --retries=12 --start-period=15s CMD ["sh", "-c", "test \"$(curl --fail --silent --show-error --max-time 4 -H 'X-Forwarded-Proto: https' -o /dev/null -w '%{http_code}' http://127.0.0.1:5000/health/ready)\" = \"200\""]

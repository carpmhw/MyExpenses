FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MyExpenses.Api/MyExpenses.Api.csproj .
RUN dotnet restore
COPY MyExpenses.Api/ .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
VOLUME /app/data
ENTRYPOINT ["dotnet", "MyExpenses.Api.dll"]

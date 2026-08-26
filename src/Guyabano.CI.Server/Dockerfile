FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /source

COPY ["src/Guyabano.CI.Contracts/Guyabano.CI.Contracts.csproj", "src/Guyabano.CI.Contracts/"]
COPY ["src/Guyabano.CI.Server/Guyabano.CI.Server.csproj", "src/Guyabano.CI.Server/"]

RUN dotnet restore "src/Guyabano.CI.Server/Guyabano.CI.Server.csproj"

COPY . .

RUN dotnet publish "src/Guyabano.CI.Server/Guyabano.CI.Server.csproj" \
    -c Release \
    -o /publish \
    /p:UseAppHost=false

# Visual Studio fast mode runs this build stage directly. Keep its runtime
# identity aligned with the workflow worker so both can write the shared
# generated workspace.
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS final

WORKDIR /app

RUN dotnet tool install JetBrains.ReSharper.GlobalTools --tool-path /tools

ENV PATH="/tools:${PATH}"
ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /publish .

EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "Guyabano.CI.Server.dll"]

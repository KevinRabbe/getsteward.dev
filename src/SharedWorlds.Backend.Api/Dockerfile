FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/SharedWorlds.Backend.Api/SharedWorlds.Backend.Api.csproj
RUN dotnet publish src/SharedWorlds.Backend.Api/SharedWorlds.Backend.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# 8080 is the provider-neutral local/container default. Platforms that inject PORT override it;
# Program.cs validates and binds that value explicitly.
ENV PORT=8080
EXPOSE 8080

USER app
ENTRYPOINT ["dotnet", "SharedWorlds.Backend.Api.dll"]

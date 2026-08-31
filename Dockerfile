FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project file first and restore. This layer stays cached until a
# dependency changes, so editing a .cs file does not re-download every package.
COPY src/RaffleIndexer/RaffleIndexer.csproj src/RaffleIndexer/
RUN dotnet restore src/RaffleIndexer/RaffleIndexer.csproj

COPY src/ src/
RUN dotnet publish src/RaffleIndexer/RaffleIndexer.csproj -c Release -o /app --no-restore

# Runtime-only image: no SDK, no compilers, roughly half the size.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "RaffleIndexer.dll"]

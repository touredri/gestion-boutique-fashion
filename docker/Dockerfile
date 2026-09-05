# Build depuis la racine du dépôt :  docker build -f docker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Les csproj d'abord : tant qu'aucune dépendance ne change, la couche de restauration est
# réutilisée et les rebuilds ne repayent pas le téléchargement des paquets.
COPY src/BoutiqueFashion.Domain/BoutiqueFashion.Domain.csproj      src/BoutiqueFashion.Domain/
COPY src/BoutiqueFashion.Contracts/BoutiqueFashion.Contracts.csproj src/BoutiqueFashion.Contracts/
COPY src/BoutiqueFashion.Server/BoutiqueFashion.Server.csproj       src/BoutiqueFashion.Server/
COPY Directory.Build.props .
RUN dotnet restore src/BoutiqueFashion.Server/BoutiqueFashion.Server.csproj

COPY src/BoutiqueFashion.Domain/    src/BoutiqueFashion.Domain/
COPY src/BoutiqueFashion.Contracts/ src/BoutiqueFashion.Contracts/
COPY src/BoutiqueFashion.Server/    src/BoutiqueFashion.Server/
RUN dotnet publish src/BoutiqueFashion.Server/BoutiqueFashion.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Utilisateur non privilégié : le serveur n'a aucune raison d'être root dans son conteneur.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BoutiqueFashion.Server.dll"]

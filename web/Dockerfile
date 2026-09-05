# Construction de la PWA. Le résultat est un dossier de fichiers statiques, servi par Caddy.
FROM node:24-alpine AS build
WORKDIR /web

COPY package.json package-lock.json ./
COPY apps/pilote/package.json apps/pilote/
COPY apps/vitrine/package.json apps/vitrine/
RUN npm ci --no-audit --no-fund

COPY apps/pilote/ apps/pilote/
COPY apps/vitrine/ apps/vitrine/
RUN npm run build --workspace @bana/pilote && npm run build --workspace @bana/vitrine

# Étape finale sans serveur : Caddy montera ce dossier. Une image nginx de plus n'apporterait
# qu'un second processus à surveiller.
FROM scratch AS export
COPY --from=build /web/apps/pilote/out /

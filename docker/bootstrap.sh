#!/bin/sh
# Prépare docker/.env sur le serveur. Idempotent : les valeurs déjà présentes ne sont jamais
# réécrites, les manquantes sont engendrées.
#
# C'est ce qui permet au premier déploiement de ne rien demander à personne. Un fichier .env à
# remplir à la main est un fichier qu'on remplit une fois, mal, un soir de mise en service — et
# « changez-moi » finit en mot de passe de production.
#
#   DOMAIN=api.exemple.ci sh docker/bootstrap.sh
set -eu

cd "$(dirname "$0")"
ENV_FILE=".env"
touch "$ENV_FILE"
chmod 600 "$ENV_FILE"

# Écrit une clé si elle est absente ou vide. Une valeur existante est laissée intacte :
# régénérer POSTGRES_PASSWORD sur une base déjà créée la rendrait inaccessible.
set_default() {
  key="$1"; value="$2"
  current=$(grep "^${key}=" "$ENV_FILE" 2>/dev/null | head -1 | cut -d= -f2- || true)
  if [ -n "${current:-}" ]; then
    return
  fi
  grep -v "^${key}=" "$ENV_FILE" > "$ENV_FILE.tmp" 2>/dev/null || true
  mv "$ENV_FILE.tmp" "$ENV_FILE"
  printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  echo "  + $key engendré"
}

secret() { openssl rand -base64 30 | tr -d '/+=' | cut -c1-32; }

if [ -z "${DOMAIN:-}" ] && ! grep -q "^DOMAIN=." "$ENV_FILE" 2>/dev/null; then
  echo "DOMAIN est requis au premier déploiement (nom de domaine pointant vers ce serveur)." >&2
  exit 1
fi

[ -n "${DOMAIN:-}" ] && set_default DOMAIN "$DOMAIN"

set_default POSTGRES_DB boutique
set_default POSTGRES_USER boutique
set_default POSTGRES_PASSWORD "$(secret)"

# Premier compte de pilotage. Le mot de passe est engendré puis affiché une seule fois : il doit
# être changé depuis l'application à la première connexion.
set_default BOOTSTRAP_USERNAME proprietaire
set_default BOOTSTRAP_PASSWORD "$(secret)"
set_default BOOTSTRAP_DISPLAY_NAME "Proprietaire"

# Clé de publication des mises à jour (lot 5). Sans elle, les routes de publication répondent
# 404 — ce qui est le bon défaut : pas de clé, pas de porte.
set_default ADMIN_API_KEY "$(secret)"

set_default VAPID_SUBJECT "mailto:contact@${DOMAIN:-exemple.ci}"
set_default VAPID_PUBLIC_KEY ""
set_default VAPID_PRIVATE_KEY ""
set_default OPENWA_BASE_URL ""
set_default OPENWA_API_KEY ""

echo "docker/.env prêt."

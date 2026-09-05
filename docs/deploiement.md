# Déploiement sur le VPS

Tout ce qui tourne sur le serveur — API, base, site vitrine, application de pilotage, proxy TLS,
sauvegardes — est déployé par [.github/workflows/deploy.yml](../.github/workflows/deploy.yml).

## Ce qui va où

| Composant | Où il tourne | Comment il arrive |
|---|---|---|
| API `BoutiqueFashion.Server` | conteneur `server` | image construite par la CI, tirée depuis GHCR |
| PostgreSQL 17 | conteneur `postgres` | image officielle, volume nommé |
| Site vitrine (`/`) | fichiers statiques servis par Caddy | image `web`, recopiée dans un volume |
| Pilotage (`/pilote/`) | fichiers statiques servis par Caddy | idem |
| Routage de l'application | conteneur `caddy`, sur `127.0.0.1:8081` | image officielle |
| TLS et porte d'entrée | **nginx de l'hôte** | bloc ajouté par le déploiement, certificat par certbot |
| Sauvegarde quotidienne | conteneur `backup` | `pg_dump` gzippé, 14 jours de rétention |
| Paquets de mise à jour des terminaux | volume `updates` | déposés par [build-windows.yml](../.github/workflows/build-windows.yml) sur tag |

**Les images sont construites par GitHub, pas par le VPS.** Compiler deux applications Next et un
serveur .NET demande plus de mémoire qu'un petit VPS n'en a, pour un résultat identique à celui
que le runner produit gratuitement. Le serveur ne fait que tirer et démarrer.

**nginx reste la porte d'entrée.** Il occupe déjà 80 et 443 sur cette machine, et sert peut-être
d'autres sites : on ne le remplace pas. Caddy écoute sur `127.0.0.1:8081` et nginx lui transmet
le domaine. Tout le routage de l'application — chemin `/pilote`, repli SPA, en-têtes de cache,
service worker non mis en cache — reste dans le Caddyfile, où il est déjà écrit et vérifié. Le
réécrire dans nginx aurait été une seconde chance de se tromper, sur la partie la plus fragile.

## Prérequis

1. **Docker et docker compose v2** sur le VPS. Le déploiement s'arrête avec un message clair
   sinon, plutôt que d'échouer trente lignes plus loin.
2. **Un enregistrement DNS A** pointant le domaine vers l'adresse du VPS, et les ports **80 et
   443** ouverts — certbot a besoin des deux.
3. **Trois secrets GitHub** : `SSH_PRIVATE_KEY`, `SSH_HOST`, `SSH_USER`. La clé publique
   correspondante doit être dans `~/.ssh/authorized_keys` de l'utilisateur sur le VPS, et cet
   utilisateur doit pouvoir lancer `docker` sans `sudo` (groupe `docker`).
4. **`sudo` sans mot de passe**, si vous voulez que le bloc nginx soit posé automatiquement.
   Sans lui le déploiement continue et affiche les quatre commandes à lancer une fois à la main :
   c'est une gêne de première installation, pas un échec.

## Premier déploiement

Actions → *Déployer sur le VPS* → **Run workflow**, en renseignant le champ **domaine**
(`api.exemple.ci`). Il n'est demandé qu'une fois : il est ensuite conservé dans `docker/.env` sur
le serveur.

Le workflow crée `~/bana/docker/.env` avec des mots de passe engendrés, démarre la pile, engendre
les clés de notification web, puis vérifie que l'API répond. À la fin du journal :

```
Domaine    : api.exemple.ci
Pilotage   : https://api.exemple.ci/pilote/
Identifiant: proprietaire
```

### Le certificat

Le déploiement installe le bloc nginx en HTTP. Une fois qu'il répond, une seule commande sur le
VPS pose le certificat et la redirection :

```sh
sudo certbot --nginx -d api.exemple.ci
```

certbot réécrit le bloc pour y ajouter le TLS. Les déploiements suivants le détectent et ne
l'écrasent plus — sans quoi chaque mise en ligne retirerait le certificat.

### Les identifiants

Le mot de passe initial et la clé de publication sont dans `~/bana/docker/.env` sur le VPS :

```sh
grep -E 'BOOTSTRAP_PASSWORD|ADMIN_API_KEY' ~/bana/docker/.env
```

**Changez le mot de passe depuis l'application à la première connexion.** Celui engendré ici a
transité par un journal de CI et par un fichier ; il fait un bon mot de passe d'installation, pas
un bon mot de passe de service.

## Déploiements suivants

Automatiques à chaque poussée sur `main`, hors documentation et hors code du terminal Windows —
celui-ci ne va pas sur le serveur, il se met à jour par Velopack (voir
[lot5-mises-a-jour-a-distance.md](lot5-mises-a-jour-a-distance.md)).

Le `.env` existant n'est jamais réécrit : le script d'amorçage ne remplit que ce qui manque.
Régénérer `POSTGRES_PASSWORD` sur une base déjà créée la rendrait inaccessible.

Les conteneurs démarrent sur l'image **du SHA déployé**, pas sur `latest` : deux déploiements
rapprochés ne peuvent pas se retrouver à faire tourner deux versions différentes selon le moment
où le `pull` a eu lieu.

## Publication des mises à jour des terminaux

Une fois le serveur en ligne, trois secrets de plus permettent à la CI Windows d'y déposer les
paquets :

| Secret | Valeur |
|---|---|
| `UPDATE_SERVER_URL` | `https://votre-domaine` |
| `UPDATE_ADMIN_KEY` | `ADMIN_API_KEY` relevé dans `~/bana/docker/.env` |
| `UPDATE_FIRST_SHOP_ID` | identifiant de la boutique pilote, visible dans l'application |

Sans le troisième, une version se dépose mais n'est distribuée à aucune boutique — et la CI le
signale en avertissement, plutôt que de tout envoyer partout par défaut.

## En cas de problème

```sh
cd ~/bana/docker
docker compose -f compose.prod.yml --env-file .env.effectif ps
docker compose -f compose.prod.yml --env-file .env.effectif logs --tail 100 server
docker compose -f compose.prod.yml --env-file .env.effectif logs --tail 50 caddy
```

**Le certificat n'est pas émis** — `sudo certbot --nginx -d votre-domaine`, et lisez ce qu'il
dit. Presque toujours : le DNS ne pointe pas encore, ou le port 80 est fermé.

**nginx renvoie 502** — la pile ne répond pas sur la boucle locale. Vérifiez avec
`curl -v http://127.0.0.1:8081/health` depuis le VPS, puis les journaux du conteneur `caddy`.

**Une sauvegarde à restaurer** — elles sont dans `~/bana/docker/backups`, une par jour :

```sh
gunzip -c backups/boutique-AAAAMMJJ-HHMMSS.sql.gz \
  | docker compose -f compose.prod.yml --env-file .env.effectif exec -T postgres psql -U boutique -d boutique
```

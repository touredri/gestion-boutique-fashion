"use client";

import { BottomNav } from "@/components/BottomNav";
import { Card, Screen } from "@/components/ui";

/**
 * Commandes du site vitrine. L'onglet existe déjà pour que la place soit tenue : déplacer une
 * destination dans une barre qu'on utilise au pouce est plus déroutant qu'un écran qui annonce
 * ce qui arrive.
 */
export default function CommandesPage() {
  return (
    <>
      <Screen eyebrow="Commandes" title="Depuis le site vitrine">
        <Card>
          <p className="text-sm text-ink">Aucune commande pour l’instant.</p>
          <p className="mt-2 text-sm text-muted">
            Les demandes passées depuis le site vitrine arriveront ici. Vous pourrez les router vers
            une boutique, les faire encaisser en caisse, puis les marquer livrées.
          </p>
          <p className="mt-3 text-xs text-faint">
            Le site vitrine et la file de commandes arrivent avec le lot suivant.
          </p>
        </Card>
      </Screen>
      <BottomNav />
    </>
  );
}

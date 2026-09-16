# Auto-Repair Kit

Plugin Dalamud pour FINAL FANTASY XIV qui répare automatiquement votre équipement dès qu'une pièce
passe sous un seuil de durabilité configurable, avec une jauge visuelle façon RepairMe.

## Fonctionnalités

- **Seuil configurable** (%) : la réparation ne se déclenche que si au moins une pièce équipée passe
  sous ce seuil. Rien n'est réparé (ni consommé) tant que tout est au-dessus.
- **Deux méthodes de réparation**, au choix dans la config :
  - **Matière sombre** : réparation sur soi, détecte automatiquement le meilleur palier de matière
    sombre possédé dans l'inventaire.
  - **PNJ réparateur** : déplacement automatique (via [vnavmesh](https://github.com/awgil/ffxiv_navmesh))
    jusqu'à un PNJ que vous avez enregistré, puis réparation gratuite.
- **Jauge visuelle** façon RepairMe : barre globale + détail dépliable par pièce d'équipement (icône,
  % de durabilité, % de lien matéria), avec code couleur vert/orange/rouge.
- Ne se déclenche jamais en combat, en craft/récolte, en cinématique ou en instance.

## Installation (version expérimentale)

1. En jeu, ouvrez les réglages Dalamud : `/xlsettings` (ou `xlsettings` dans la console).
2. Onglet **Expérimental**, section **Dépôts personnalisés**.
3. Ajoutez l'URL suivante puis validez :
   ```
   https://raw.githubusercontent.com/MrSuricate2/AutoRepairKit/master/pluginmaster.json
   ```
4. Ouvrez le gestionnaire de plugins : `/xlplugins` (ou `xlplugins`).
5. Cherchez **Auto-Repair Kit** (visible dans "Tous les plugins" / dépôts personnalisés) et installez-le.

Une nouvelle version expérimentale est publiée automatiquement à chaque mise à jour du code ; Dalamud
vous proposera la mise à jour dans le gestionnaire de plugins.

### Prérequis pour le mode "PNJ réparateur"

Ce mode nécessite le plugin [vnavmesh](https://github.com/awgil/ffxiv_navmesh) pour le déplacement
automatique (à installer séparément, non fourni ici). Sans lui, seul le mode "Matière sombre" fonctionnera.

## Utilisation

Ouvrez la fenêtre de configuration avec `/pautorepair`, ou via le bouton dans le gestionnaire de plugins.

- **Onglet Réparation automatique** : activer/désactiver, choisir la méthode, régler le seuil, bouton
  "Réparer maintenant" pour forcer une réparation immédiate.
- **Onglet Jauge** : afficher/masquer la jauge, la verrouiller en position, régler les seuils de
  couleur orange/rouge.
- **Onglet PNJ réparateurs** : ciblez en jeu un PNJ capable de réparer, puis cliquez sur
  "Enregistrer la cible actuelle" pour l'associer à la zone courante. Un PNJ enregistré par zone est
  utilisé automatiquement quand le mode "PNJ" est actif.

## Limitations connues

- Le clic automatique dans le menu d'un PNJ (SelectString/SelectIconString) se base sur une détection
  par mot-clé ("repair", "réparer", etc.) ; si un PNJ présente un menu inhabituel, le plugin abandonne
  proprement après quelques secondes et vous prévient dans le chat plutôt que de rester bloqué.
- Les noms d'IPC vnavmesh utilisés peuvent évoluer avec les mises à jour de ce plugin tiers ; voir
  `SamplePlugin/Repair/VNavmeshIpc.cs` en cas de souci de déplacement.

## Compilation depuis les sources

### Prérequis

- XIVLauncher, FINAL FANTASY XIV et Dalamud installés, avec le jeu lancé au moins une fois avec Dalamud.
- XIVLauncher installé dans ses répertoires par défaut (sinon, définir la variable d'environnement
  `DALAMUD_HOME` vers le dossier de dev de Dalamud).
- Le SDK .NET 10 installé (généralement géré automatiquement par l'IDE).

### Étapes

1. Ouvrez `SamplePlugin.slnx` dans votre éditeur C# (Visual Studio ou JetBrains Rider).
2. Compilez la solution (`Debug` ou `Release`).
3. Le plugin compilé se trouve dans `SamplePlugin/bin/x64/Debug/SamplePlugin.dll` (ou `Release`).

Pour tester une build locale sans passer par le dépôt personnalisé : `/xlsettings` > **Expérimental** >
**Dev Plugin Locations**, ajoutez le chemin complet vers `SamplePlugin.dll`, puis activez-le depuis
`/xlplugins` > **Dev Tools** > **Installed Dev Plugins**.

## CI/CD

Le workflow `.github/workflows/release.yml` rebuild le plugin et republie la release `experimental`
(avec `latest.zip` et `pluginmaster.json`) à chaque push sur `master`.

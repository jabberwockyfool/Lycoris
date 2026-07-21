# Lycoris

Éditeur de données **Yo-kai Watch 3** façon [Albatross](https://github.com/Tiniifan/Albatross), en un
seul exécutable WPF (.NET Framework 4.7.2), **sans aucune dépendance externe** : les codecs Level-5
(`.cfg.bin` / T2B et `.xi` / IMGC) sont réimplémentés.

Lycoris ouvre un dossier de mod **YWML** (arborescence `include/data/res/…`) et permet de lire et
d'éditer les yo-kai — stats, moves, évolutions, Blaster T, drops, portraits — puis de réécrire les
`.cfg.bin` **à l'octet près**.

---

## Fonctionnalités

- **Lecture / écriture `.cfg.bin` (T2B)** — round-trip *byte-identique* prouvé ; une sauvegarde ne
  touche que les octets réellement modifiés.
- **Panneau d'édition + sélecteur** de yo-kai (recherche, ◀ ▶) façon Albatross.
- **Stats** Min/Max (HP / Force / Esprit / Défense / Vitesse), résistance & faiblesse, medal, « Show ».
- **Moves nommés** : Attaque, Technique, Inspirit, Guard, Soultimate, Ability — résolus via
  `skill_config` + `chara_ability` et leurs fichiers texte.
- **Rank / Tribu / Attributs** en listes déroulantes (dictionnaires YW3).
- **chara_scale** — les 7 champs d'échelle éditables.
- **Ajout de yo-kai** + édition directe du **nom** et de la **description**.
- **Portraits** : décodage des icônes `.xi` (IMGC : RGBA4/ETC1A4, Huffman/LZ10, swizzle 3DS) et
  **remplacement par un PNG** (conversion PNG → `.xi` automatique).
- **Évolutions** : cible + niveau éditables, et une case **« Évoluable »** pour rendre n'importe quel
  yo-kai évolutif.
- **Blaster T** (mode Hackslash) : ability + soultimate + attaques A / Y / X.
- **Drops** : argent, expérience, 2 objets + taux.
- **Échelle de puissance 1-10** : règle d'un clic un set de stats cohérent, calé sur la distribution
  réelle des stats du jeu.

---

## Utilisation

1. Lance `bin\Debug\Lycoris.exe`.
2. **Ouvrir un dossier…** → sélectionne la racine de ton mod YWML.
3. Choisis un yo-kai dans le sélecteur, édite ses onglets, puis **Sauver le mod**.

### Dossier de référence

Un mod YWML ne contient que les fichiers qu'il modifie. Lycoris utilise automatiquement un dossier
**`cfg`** (une extraction complète du jeu, placé à côté du projet) pour **résoudre en lecture seule**
tout ce qui manque au mod : noms de moves, icônes, technics/abilities Blaster T, items des drops, etc.

- Les fichiers **éditables** (`chara_param`, `chara_base`, `chara_text`, `chara_desc_text`,
  `chara_scale`, `hackslash_chara_param`, `battle_chara_param`) sont lus depuis **ton mod** et n'y sont
  réécrits **que s'ils s'y trouvent** — jamais dans les fichiers de référence.
- Les fichiers **de résolution seule** (`skill_config`, `skill_text`, `chara_ability`, `item_config`,
  `face_icon/…`, …) peuvent venir de la référence ; ils ne sont jamais modifiés.

---

## Fichiers gérés (`/data/res/character` sauf mention)

| Fichier | Rôle | Écrit ? |
|---|---|---|
| `chara_param` | stats, moves, résistance, évolutions (`CHARA_EVOLVE_INFO`) | ✅ |
| `chara_base` | rank, tribu, nom/desc/icône (hashes) | ✅ |
| `chara_text` / `chara_desc_text` | noms / descriptions | ✅ |
| `chara_scale` | échelle du modèle | ✅ |
| `hackslash_chara_param` | moveset Blaster T par yo-kai | ✅ |
| `battle_chara_param` | argent, exp, drops par yo-kai | ✅ |
| `skill_config` + `skill_text` | noms des moves | lecture |
| `chara_ability` + `chara_ability_text` | noms des abilities | lecture |
| `hackslash_technic` / `hackslash_chara_ability` (+ text) | noms Blaster T | lecture |
| `item_config` + `item_text` | noms des objets (drops) | lecture |
| `face_icon/*.xi` (`/data/menu/face_icon`) | portraits | ✅ (remplacement PNG) |

---

## Architecture

- **`Formats/`** — codecs autonomes, sans dépendance :
  - `T2bReader` / `T2bWriter` / `T2bModel` — format `.cfg.bin` (T2B), CRC32 standard + JAM.
  - `Imgc` — format `.xi` (IMGC) : décodeur + encodeur, Huffman/LZ10, RGBA4/RGBA8/ETC1A4, swizzle 3DS.
- **`Yokai/`** — couche métier YW3 :
  - `YokaiSchema` — tous les index de champs centralisés.
  - `YokaiDatabase` — chargement, résolution des noms, sauvegarde.
  - `YokaiModels` — modèle éditable (INotifyPropertyChanged).
  - `YokaiEnums` / `IconNaming` / `StatCurve` — dictionnaires, nommage d'icônes, échelle de puissance.
- **`MainWindow.xaml(.cs)`**, **`AddYokaiDialog.cs`** — l'interface WPF.

### Compilation

Projet MSBuild classique (.NET Framework 4.7.2). Ouvre `Lycoris.csproj` dans Visual Studio, ou :

```
msbuild Lycoris.csproj /t:Build /p:Configuration=Debug
```

---

## Crédits

Formats et mécanismes de résolution reverse-engineerés d'après :
- [**CfgBinEditor**](https://github.com/onepiecefreak3/CfgBinEditor) (onepiecefreak) — format T2B.
- [**Albatross**](https://github.com/Tiniifan/Albatross) (Tiniifan) — logique de jeu YW3, pipeline IMGC,
  dictionnaires, chaînes de résolution des noms.

À usage de modding personnel / communautaire.

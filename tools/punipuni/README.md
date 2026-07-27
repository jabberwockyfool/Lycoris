# ykport — portage d'animations Puni-Puni → Yo-kai Watch 3

Automatise les étapes manuelles pénibles d'un port d'animation :

1. **Combine** : colle tous les clips d'animation (chacun exporté en `.mtn2`
   depuis Blender via studio_eleven) bout à bout sur **une seule timeline**, et
   écrit **un `.mtn2` combiné par groupe** (`p10`/`p20`/`p21`/`p84`).
2. **Split (`.mtninf`)** : génère un fichier MINF par clip, avec l'**ID de slot
   YW3**, la plage `[frame_start..frame_end]` dans la timeline combinée, et la
   vitesse.
3. **Package (`_pXX.xc`)** *(optionnel, si un `donor` est fourni)* : produit
   directement un `_pXX.xc` jouable, à partir d'un `.xc` vanilla du jeu servant
   de gabarit (voir plus bas).

Tu continues d'exporter chaque clip en `.mtn2` dans Blender (comme d'habitude) ;
l'outil remplace le collage manuel « toutes les anims dans une seule »,
l'écriture manuelle des `.mtninf`, et l'assemblage du `.xc`.

## Prérequis
- **Python 3.11+** (hors Blender, ça marche).
- **studio_eleven** installé (github.com/Tiniifan/studio_eleven). L'outil
  importe ses codecs `.mtn2` **sans copier ni modifier** ses fichiers, et sans
  charger Blender/`bpy`. Auto-détecté dans les add-ons Blender ; sinon
  `--se <chemin>` ou la variable d'env `STUDIO_ELEVEN`.

## Depuis Blender (actions dans un .blend) — recommandé
Si tes animations sont des **actions Blender** (une par anim, nommées
`{model}_p20_21000s_sti`, etc.), utilise le panneau Blender :

1. studio_eleven doit être **installé ET activé** dans Blender.
2. Blender → onglet **Scripting** → Open → `ykport_blender.py` → **Run Script**
   (une fois par session). Un onglet **« YW3 Port »** apparaît dans la sidebar de
   la vue 3D (touche **N**).
3. Sélectionne ton armature, renseigne le **dossier de sortie** et un **donor
   `.xc`** par groupe (p20 = `y152000_p20.xc` de Jibanyan…), puis **Export**.

Il regroupe les actions par `pXX` (lu dans le nom), en déduit le slot
(`21000s_sti` → battle_start, `27500d_wii` → victory1…), les colle en une
timeline, et sort les `_pXX.xc` prêts. Model ID auto-déduit des noms d'actions.

**p10 / p84 sans anims dédiées** : coche *« Fill p10/p84 from p20 »* (défaut on).
Les slots overworld/blasters sont remplis avec les anims p20 (idle→idle,
attack→attaque blaster, death→mort & ascension, tired→long idle,
soultimate→soultimate, le reste→idle ; voir `slots.REUSE_FROM_P20`). Deux cas :
- **avec un donor du groupe** (p.ex. `y152000_p10.xc`, `y152020_p84.xc`) →
  chaque slot du donor est mappé sur une anim p20 en lisant son **nom** dans le
  donor (立ち→idle, こうげき→attack, ダメージ→damage, 死→death, ひっさつ→soultimate,
  勝利→victory…). Fiable quelle que soit la version. **Recommandé pour p84.**
- **sans donor** (repli) → l'outil part du **donor p20** et relabelise les hex id
  (mtninf + RES). Les slots propres au groupe absents du p20 ne sont pas créés.

## GUI standalone (si tu as déjà des .mtn2)
Double-clique **`ykport_gui.bat`** (ou `py ykport_gui.py`). Renseigne Model ID /
Clips dir / Output dir, choisis un `.xc` donor par groupe, mappe chaque clip à un
slot (menu déroulant), et clique **Build ▶**. Boutons Load/Save config pour
réutiliser un mapping. studio_eleven est auto-détecté (sinon renseigne le champ).

## CLI
```bash
python ykport.py slots            # affiche la table de référence des ID de slot
python ykport.py build mon.json   # combine + .mtninf + .xc
```

## Config (JSON)
Voir `config_example.json`. Champs :
- `model_id` — préfixe des noms de split (ex. `x192000` → `x192000_p20_...`).
- `clips_dir` — dossier des `.mtn2` exportés.
- `output_dir` — sortie (crée `output/<groupe>/*.mtninf` + `output/<model>_<groupe>.mtn2`).
- `gap` — nb de frames entre 2 clips (défaut 1, pour ne pas les faire se chevaucher).
- `groups.pXX` — liste ordonnée de clips : `file`, `slot`, `name`, `speed`.
  - `slot` accepte une **clé de rôle** (`attack`, `idle`, … cf. `ykport.py slots`)
    **ou** des octets bruts `"58 DF 5B 84"`.

## Packaging `.xc` (gabarit « donor »)
Plutôt que de régénérer la table RES/les `.cmn` à partir de zéro (risqué),
l'outil part d'un **`.xc` vanilla du jeu** (le `donor`) et le patche :
- remplace le `000.mtn2` par ton anim combinée (renommée comme celle du donor,
  ex. `out_00`, pour que `anim_crc32` reste valide) ;
- pour chaque `.mtninf` du donor, repointe la plage de frames vers TA timeline
  en **matchant l'ID de slot** ; les slots que tu ne fournis pas retombent sur
  le clip `fallback` (défaut `idle`) — donc rien ne pointe hors-timeline ;
- garde `RES.bin` et les `.cmn` du donor ; repack via XPCK.

Le donor idéal = le même contexte pour un yo-kai existant (ex. `y152000_p20.xc`
de Jibanyan) : ses slots sont exactement la liste standard YW3.

## Ce qui est fait / validé (sur de vrais fichiers)
- Lecture/écriture `.mtn2` (XMTN) via studio_eleven.
- Combine bout-à-bout + plages de frames (round-trip OK).
- `.mtninf` (MINF, 96 o) : **ID de slot brut** (le jeu lit la valeur stockée),
  `anim_crc32 = crc32("out_00")`, `frame_start/end`, `speed`.
- Packaging `_pXX.xc` par gabarit donor : validé — le `.xc` produit se ré-ouvre,
  toutes les plages `.mtninf` restent dans la timeline, `anim_crc32` correct.
  Confirmé sur Jibanyan (`y152000_p10/p20.xc`) : `split_id == crc32(nom JP
  canonique)`, anim nommée `out_00`, vitesse vanilla = `0.5`.

## Notes / à confirmer en jeu
- **Slots non mappés** → jouent l'anim `fallback` (idle). Mappe plus de slots au
  fur et à mesure que tu as les clips.
- **`.cmn`** : conservés tels quels depuis le donor (paramètres par-slot).
- **Mapping victory** : les noms Puni-Puni `27500d_wii`/`27501d_wil` sont
  préfixés `p20` ; en pratique tu peux les mapper sur les slots victory de `p21`
  (le gabarit donor décide où ils vont).

## Note licence
studio_eleven n'a pas de fichier LICENSE. Cet outil **n'inclut ni ne redistribue**
son code : il importe la copie que **tu** as installée. À n'utiliser que pour tes
propres ports.

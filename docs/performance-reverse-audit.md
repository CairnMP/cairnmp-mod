# Audit natif de performance : streaming et visibilité

## Résultat et périmètre

Audit statique du binaire Cairn installé, réalisé le 15 septembre 2026 pour
préparer des optimisations intégrables à CairnMP, en solo comme en multijoueur,
sans réduire la qualité visuelle ni modifier le gameplay.

Quatre pistes méritent une mesure : recherche des zones de culling, recherche
des cellules d'occlusion, recherche de la zone de streaming et logs pendant
certaines attentes après téléportation. **Ce sont des travaux répétés observés
dans le code, pas des goulots d'étranglement mesurés.** Aucun gain de FPS ni
responsabilité du jeu ou du mod dans les ralentissements n'est établi.

Cet audit ne modifie aucun comportement du jeu ou du mod. Il ne constitue pas
une reconstruction complète du code source du jeu. Les diagnostics existants
sont décrits dans [performance-diagnostics.md](performance-diagnostics.md).

## Provenance et méthode

| Élément | Version ou vérification |
| --- | --- |
| Binaire | `GameAssembly.dll`, PE x64, 95 804 928 octets |
| Base préférée | `0x180000000` ; adresse affichée = base + RVA |
| Unity indiqué par le générateur | `6000.0.59` |
| Dump existant | Cpp2IL `2022.1.0-pre-release.21` |
| Reconstruction des corps | Cpp2IL `development` commit `b5ad444` (12 septembre 2026) |
| Correspondance dump/binaire | SHA-512 du binaire identique à `GameAssemblyHash` dans `Config.cfg` |
| Analyse | C# approximatif Cpp2IL/ILSpy 11, ISIL Cpp2IL, métadonnées et instructions LLVM |
| Contrôle des extractions | 124 fichiers assembleur : RVA, offset et longueur vérifiés contre les sections PE |

SHA-512 du binaire audité :

```text
20DF73488AFE48E39D7FD68F6ED72A60F23DE6D2F75AF49A7E12B8F23202CF32F64520C7BCCA061CE9AEFE540967F60DDCF000F767A45E1A0E94BBD2EC79D7CF
```

Les DLL produites par la version Cpp2IL incluse dans CairnLoader fournissent les
noms, signatures et offsets des champs, mais leurs corps C# vides ne décrivent
**pas** le comportement natif. Une seconde passe avec la branche de développement
de Cpp2IL a reconstruit de l'IL pour 78 261 méthodes sur 78 262, puis ILSpy 11 a
produit du C# approximatif pour les types ciblés. Cette sortie retrouve de
nombreux appels et embranchements, mais conserve des accès mémoire non résolus,
des types temporaires incorrects et parfois des conditions impossibles. Les
constats ci-dessous ont donc été vérifiés contre l'ISIL et les instructions du
binaire correspondant.

Les extractions, index, pseudocode, outils locaux et empreintes sont conservés
dans `docs/local/reverse-performance/20DF73488AFE48E39/`. Les DLL récupérées,
le C# ciblé et l'ISIL sont dans
`docs/local/cpp2il-recovered/20DF73488AFE48E39/`. Ces deux emplacements sont
ignorés par Git. `manifest.json` contient aussi les empreintes des métadonnées,
du dump et des octets natifs extraits. Le rapport reste lisible sans publier ces
extractions.

Précautions d'interprétation :

- Les noms d'appels reposent sur une adresse exacte. Un nom d'export voisin
  affiché par un désassembleur ne prouve pas l'identité d'une fonction.
- Une adresse partagée par plusieurs méthodes IL2CPP reste ambiguë ; les
  annotations ne choisissent pas arbitrairement l'un de ses alias.
- Les initialisations protégées par un indicateur statique ne sont pas comptées
  comme des allocations répétées à chaque frame.
- Les appels indirects, abonnés aux événements et appels intégrés par le
  compilateur ne sont pas tous résolus. L'inventaire d'appels directs n'est pas
  un graphe exhaustif du jeu.
- Les priorités ci-dessous ordonnent les prochaines mesures, pas les gains.

## P1 — Recherche répétée des zones de culling

**Confiance : élevée sur le parcours ; impact réel inconnu.**

`ContextualCullingManager.LateUpdate` (`0x2BE7F90`) lit une fois la position de
la caméra, parcourt les zones enregistrées et calcule leur état intérieur /
extérieur. Il teste d'abord les bounds, puis les volumes concernés, avec arrêt
au premier volume contenant la caméra.

Il parcourt ensuite les objets de culling et appelle
`ContextualCulling.UpdateState` (`0x2BE99B0`) avec la liste complète des zones.
Chaque objet recherche linéairement le premier `areaID` correspondant.
L'appel est visible à `0x2BE83FA`, la comparaison d'identifiants à `0x2BE9A97`.

Pour A zones et C objets, la partie association objet/zone peut atteindre
O(C × A) comparaisons par LateUpdate éligible, en plus du calcul des volumes.
Aucun garde lié à l'immobilité de la caméra n'a été trouvé dans cette méthode.
Les effectifs réels et le temps consommé restent à mesurer.

**Optimisation candidate :** indexer la première zone enregistrée par identifiant,
ou mémoriser l'association de chaque objet. Préserver absolument :

- le premier résultat en cas d'identifiants dupliqués ;
- l'absence de modification d'état si aucune zone ne correspond ;
- les ajouts, retraits, remplacements, destructions et changements d'identifiant ;
- l'ordre de mise à jour des états des zones avant celui des objets.

Une invalidation fiable lors des mutations est nécessaire. Reparcourir toutes
les associations chaque frame pour vérifier un cache annulerait son intérêt.
Mesurer aussi le coût des hooks et des accès IL2CPP depuis CairnMP.

## P2 — Recherche de cellule d'occlusion avant le garde de changement

**Confiance : élevée sur le parcours ; fréquence et taille des données inconnues.**

`GraphicsElementOcclusionCulling.Enable` (`0x2B6FCE0`) ne contient que des
écritures de configuration. La logique de visibilité se trouve notamment dans
`SceneOptimizer/OcclusionCulling.LateUpdate_Runtime` (`0x2BF32A0`).

Quand le module dispose de données et est actif, cette méthode recherche la
cellule de la caméra via `OcclusionCullingData.FindCellIndex` (`0x2C05D30`,
appel à `0x2BF3468`). La recherche transforme la position en coordonnées de
cellule puis parcourt les entrées, compare leurs trois coordonnées et retourne
l'identifiant associé à la première correspondance, ou -1. La boucle est
visible à `0x2C05DE6`–`0x2C05E25`.

Le garde comparant l'identifiant à la cellule précédente intervient **après** la
recherche (`0x2BF3476`). Il évite les changements de renderers lorsque la cellule
reste identique, mais pas le parcours des données. Le coût potentiel est
linéaire dans le nombre d'entrées à chaque appel actif.

**Optimisation candidate :** mémoriser le résultat pour des coordonnées de
cellule identiques et une même version des données, ou indexer les coordonnées.
Conserver la conversion numérique native, les limites de cellules, le premier
doublon, le résultat -1 et l'identifiant stocké dans l'entrée : ce dernier ne
doit pas être remplacé par sa position dans le tableau.

Invalider lors du remplacement des données, d'un changement d'origine ou de
taille de cellule et lors des transitions d'activation. L'activation/désactivation
du module comporte sa propre remise à zéro, à préserver. Mesurer d'abord le
nombre d'entrées et d'appels actifs, puis les temps de recherche en caméra fixe
et en déplacement. Ne pas désactiver l'occlusion pour contourner ce coût.

## P3 — Recherche de zone de streaming répétée à position identique

**Confiance : élevée ; intérêt dépendant du nombre de zones et des déplacements.**

`StreamingManager.Update` (`0x2D466A0`) comporte des gardes de présence du joueur,
de données disponibles et de mode. Dans le chemin gameplay éligible,
`UpdateStreamingGameplay` (`0x2D461F0`) traite d'abord les états de transition,
de préchargement et de zone verrouillée.

Sans zone verrouillée et lorsque ce chemin atteint la sélection spatiale, il
appelle `GetBestZoneIndexUsingZoneLimiterData` (`0x2D43580`) à `0x2D4662E`.
Cette méthode parcourt toute la liste des positions des zones avec des distances
au carré, sans racine carrée. La distance minimale commence à `float.MaxValue`.
Une égalité conserve le premier résultat. Aucun cache de position n'est visible
sur ce chemin ; un joueur immobile peut donc répéter le même parcours.

**Optimisation candidate :** réutiliser uniquement le résultat de cette recherche
pour une position bit à bit identique et une même version de la liste de zones.
Préserver les calculs flottants et les égalités ; éviter une tolérance spatiale
qui pourrait déplacer une frontière. Invalider pour toute mutation des données.

**Ne pas sauter `UpdateStreamingGameplay` ou `UpdateCurrentZone`.** Cette dernière
(`0x2D457F0`) compare déjà les zones, mais son chemin « même zone » continue de
traiter le préchargement et la zone tampon. Les callbacks de préchargement sont
indirects et leur coût n'a pas été résolu par cet audit.

### Cas caméra à distinguer

`UpdateStreamingCamera` (`0x2D45F80`) appelle `UpdateStreamingCameraEx`
(`0x2D45DA0`) dans le chemin `IsEagleEyeViewPath`, après plusieurs conditions.
Ce n'est pas une deuxième recherche systématique pendant tout le gameplay.

`UpdateStreamingCameraEx` évite le travail pendant certaines occupations ou
transitions du gestionnaire de scènes, appelle `GetBestZoneAt` (`0x2D433E0`),
puis évite une demande de chargement si la zone caméra n'a pas changé.
`GetBestZoneAt` recherche d'abord un collider contenant la position, puis peut
revenir à la recherche de zone la plus proche. Les bounds des colliders sont
lus pendant ce parcours. Tout cache futur doit prendre en compte leur mobilité.

## P4 — Formatage de logs dans une attente après téléportation

**Confiance : élevée sur la branche ; occurrence et allocations réelles non mesurées.**

Un chemin de `OnPreTeleportedTo` (`0x2D44550`) active temporairement
`disablePreloadingProcessRuntime` et mémorise un numéro de frame.
Dans `UpdateStreamingGameplay`, lorsque le préchargement joueur n'est pas
désactivé par configuration, que cet indicateur temporaire est actif et que
`CairnSceneManager.IsBusy()` retourne vrai, une branche :

1. actualise le numéro de frame ;
2. boxe une valeur via `il2cpp_value_box` (`0x2D46540`) ;
3. appelle `String.Format` (`0x2D46552`) ;
4. transmet le message à `Debug.Log` (`0x2D465C7`).

Aucun garde de répétition n'est visible dans cette branche. Elle peut donc
effectuer ce travail à chaque Update éligible pendant cette attente. Ce constat
ne s'applique ni à tout le gameplay ni à tous les chargements.

`Debug.Log` (`0x3415B30`) transmet à `Debug.LogMessage` (`0x3414610`). Le filtre
`TryFilter` apparaît tard dans ce dernier chemin ; filtrer seulement la sortie
finale ne supprime pas le boxing et le formatage déjà exécutés par l'appelant.
Les sorties effectives du logger dépendent de son état d'initialisation et de
sa configuration ; aucun volume d'écriture n'a été mesuré.

**Optimisation candidate :** garder les mises à jour de frame et d'état, mais
regrouper ou limiter les messages informatifs répétés avant leur formatage.
Conserver les erreurs et les événements de début/fin utiles au diagnostic.
Valider sur une téléportation reproduisant cette branche, avec compte des appels,
durée de l'attente et allocations. La valeur de `IsBusy` ne doit pas être
réinterprétée : son code retourne vrai lorsque `uniqueToken` est nul.

## Optimisations déjà présentes et pistes secondaires

| Chemin | Constat natif | Conséquence |
| --- | --- | --- |
| `ContextualCulling.UpdateRenderers`, `0x2BE9960` | Compare l'état désiré à l'état mémorisé avant `SetEnabled` (`0x2BE9995`) | Pas de désactivation/réactivation générale à chaque frame inchangée |
| `ContextualCulling.SetEnabled`, `0x2BE9510` | Utilise les tableaux de renderers, decals et lights déjà stockés ; avertissement de références nulles protégé | Pas de recherche générale de composants à chaque UpdateState |
| `SphericalCulling.Update`, `0x3041AD0` | Distance au carré ; `SetActive` seulement au premier passage ou changement d'état | Ne pas proposer de supprimer une racine carrée ou des SetActive répétitifs inexistants |
| `SceneLodRenderer/ExplicitCulling/CullingPlane.Update`, `0x2B91800` | Résout le plan via `Find` seulement quand son indicateur de résolution est faux, puis stocke ses coefficients | Le registre LOD n'est pas une recherche linéaire inconditionnelle par frame |
| `SceneOptimizer/OcclusionCulling.LateUpdate_Runtime` | Garde de changement de cellule avant les changements de visibilité | Préserver ce garde en optimisant la recherche en amont |

Pour les sphères, la lecture de la position de la caméra et des transforms reste
répétée par instance. Mutualiser la caméra pourrait aider si les instances sont
nombreuses, mais doit préserver l'ordre d'exécution et les changements de caméra
dans une même frame. C'est une piste secondaire, sans mesure d'effectif.

Le registre `SceneLodCullingPlaneSystem.Find` (`0x2BA82E0`) recherche bien le
premier identifiant correspondant. Son appel à `0x2B91999` a été confirmé dans
le désassemblage du consommateur ci-dessus. Une résolution échouée peut être
retentée ; cela ne justifie pas de changer le cas nominal déjà mémorisé.

## Chargements : ce que le code permet de conclure

`CairnSceneManager.LoadZoneSceneData` (`0x2B2CCD0`) prépare un processus et lance
une coroutine. Le corps de la coroutine (`0x2B3B210`) contient notamment des
parcours de scènes et des demandes de chargement/déchargement. Le corps de
`LoadSceneAsync` (`0x2B3A6D0`) appelle `Addressables.LoadSceneAsync` à
`0x2B3A9A7`.

`UnloadUnusedAssetsIfNeeded` (`0x2B30560`) est conditionné par un indicateur.
La coroutine correspondante (`0x2B5AFE0`) remet cet indicateur à zéro, appelle
`Resources.UnloadUnusedAssets` à `0x2B5B0C4` et cède l'opération asynchrone.
Il n'y a pas de preuve ici d'un nettoyage inconditionnel à chaque frame.

Ces appels asynchrones ne démontrent pas l'absence de coût sur le thread
principal : démarrage, intégration des scènes, callbacks et libération des
ressources restent à mesurer. Aucun chargement synchrone fautif, blocage du
thread principal ou pic de GC n'est établi par cet audit.

## Validation avant toute optimisation intégrée

Les [diagnostics existants](performance-diagnostics.md) donnent une référence
des intervalles et du coût global du mod. Ils n'attribuent pas automatiquement
le temps aux méthodes natives de ce rapport. Il faudra une mesure ciblée ou un
profilage natif, avec coût d'instrumentation contrôlé.

| Scénario | Mesures et invariants à vérifier |
| --- | --- |
| Caméra fixe puis mobile, scène dense | Nombre de zones, objets et cellules ; appels et durée P1/P2 ; états visibles identiques |
| Franchissement de volumes et cellules | Première correspondance, frontières, absence de correspondance, changement de caméra |
| Joueur immobile puis déplacement/téléportation | Appels et durée P3 ; mêmes zones, événements, préchargements et ordre des transitions |
| Téléportation pendant occupation du gestionnaire | Présence effective de P4 ; boxing/allocations, logs, durée d'attente, fin correcte du préchargement |
| Chargement/déchargement de scène | Invalidation des caches, doublons, objets détruits, données remplacées |
| Solo, hôte et client multijoueur | Même trajet et paramètres ; répétitions comparables ; pas de divergence de visibilité ou de streaming |

Comparer jeu de référence, CairnMP sans optimisation, puis chaque candidat
isolément. Conserver les mêmes sauvegarde, trajet, réglages, limite FPS et état
de chauffe. Observer les distributions de frametime et les longues frames,
ainsi que CPU/GPU et allocations quand les outils les exposent. Un résultat
global seul ne prouve pas la causalité d'une méthode.

**Décision :** commencer par mesurer P1 et P2, puis P3 ; reproduire séparément
la fenêtre P4. N'intégrer un remplacement que si le bénéfice dépasse le coût
du hook et que l'équivalence de comportement est vérifiée. Les adresses et
offsets de ce rapport doivent être revalidés après toute mise à jour du jeu.

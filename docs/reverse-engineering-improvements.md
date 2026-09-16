# Améliorations tirées de la rétro-ingénierie

## Résultat

L'audit du 16 septembre 2026 a comparé les accès natifs de CairnMP avec les
wrappers IL2CPP installés et les corps reconstruits par Cpp2IL. Les changements
retenus remplacent les hypothèses de disposition mémoire par les API natives
dont le rôle et le cycle de vie ont pu être confirmés.

Après ces changements, la couche `Internal/Game` ne contient plus d'appel manuel
à `GetIl2CppField`, `il2cpp_field_get_offset` ou `il2cpp_runtime_invoke`. Les
wrappers générés continuent naturellement d'utiliser IL2CPP en interne ; la
différence est que CairnMP ne devine plus les offsets, la disposition des value
types ou l'ordre des overloads.

## Provenance

| Élément | Référence |
| --- | --- |
| Binaire | `GameAssembly.dll` x64 installé |
| SHA-512 | `20DF73488AFE48E39D7FD68F6ED72A60F23DE6D2F75AF49A7E12B8F23202CF32F64520C7BCCA061CE9AEFE540967F60DDCF000F767A45E1A0E94BBD2EC79D7CF` |
| Reconstruction | Cpp2IL `development` commit `b5ad444` |
| Vérification des API | wrappers de `CairnLoader/Il2CppAssemblies` et compilation du mod |
| Extractions locales | `docs/local/cpp2il-recovered/20DF73488AFE48E39/` (ignoré par Git) |

Le C# produit par Cpp2IL reste approximatif. Une modification n'a été intégrée
que lorsque le corps reconstruit, les métadonnées et le wrapper réellement
référencé par le projet concordaient.

## Changements appliqués

| Domaine | Avant | Preuve native | Après |
| --- | --- | --- | --- |
| Joueur local | Scan global puis lecture de `<MCGameObject>k__BackingField` par offset | `PawnManager` est un `MoSingleton` et expose `MCGameObject` | `PawnManager.Instance?.MCGameObject` |
| Cycle jour/nuit | Écriture directe de `isFrozen`, `lastDayTime01OnFreeze` et `dayTime01` | `NightDayCycle.Freeze` et `SetFreezeDayTime01` actualisent aussi les setups visuels | Cycle natif de gel/dégel, avec conservation des gels appartenant au jeu |
| Sommeil | Scan de tous les `MonoBehaviour` et lecture de candidats de champ | `PlayerStateFeedbacks` est un singleton avec `IsAsleep` | Lecture directe de la propriété |
| Météo | L'override réseau pouvait rester actif après déconnexion | Le nettoyage natif efface l'état forcé, redéfinit les définitions, relance la météo et libère le vent | Même séquence, limitée à l'origine `NetPlay` |
| État du pawn | `CaptureFrame` construisait tous les tableaux d'os pour lire un enum | `GetPlayerPawnState` contient la lecture ciblée utilisée par `CaptureFrame` | Appel direct sans allocation de frame |
| Pitons | Résolution d'overload par ordre des métadonnées, argument `ClimbingSetting` nul, puis réparation tardive | `Lifeline.AddPiton(..., ClimbingV2PawnController)` initialise le réglage dès la création | Overload typé avec le contrôleur local |
| Liste des pitons | Décodage manuel des en-têtes de `List<T>` et des objets | `PlacedPitons`, `PlacedPitonData.Piton`, `GetLastPiton` et `DetachPiton` sont exposés | Parcours et retrait typés |
| Ghosts | Offsets de `NetFrame`, `NetplayRemotePlayer` et `NetplayRemoteClimbot` | Les wrappers exposent `currentFrame`, `initialized`, l'id et `ownerId` avec setters | Patch Harmony conservé, champs mis à jour par les wrappers |
| Os des ghosts | Lecture de `LiveGhostAnchors` et du tableau par arithmétique de pointeurs | `anchors.relatives` est disponible sur les deux types de ghost | Tableau IL2CPP typé |
| Lampe | Réflexion sur `CurrentMode` et `SetMode`, recherche de composant distant | `AavaLightStick.Mode`, `CurrentMode`, `SetMode` et `NetplayRemotePlayer.LightStick` sont exposés | Capture et application typées |
| Bivouac | Réflexion pour `SelectHand`, `CurrentModel`, `Init`, `FakeInit` et `Deactivate` | Les wrappers générés rendent ces appels accessibles avec leurs vrais types | Appels directs et tableau de modèles typé |
| Menu principal | Écriture d'un nullable statique avec une disposition supposée | `MainMenu.ForceStepTransition` expose un `Nullable<Step>` | Setter généré |
| Managers | Scans globaux pour input, bivouac, météo et état global | Ces managers héritent de `MoSingleton<T>` | Accès aux singletons |

## Scans conservés

Les recherches restantes correspondent à des objets sans accès global fiable :

- `NetplayPawnCapture` du Climbot et `RobotPawnController` sont recherchés par
  type, avec cache et temporisation. Ils ne sont pas des singletons.
- Les écrans de mode photo, inventaire et paramètres sont créés et détruits par
  l'UI. Leurs recherches sont bornées et les résultats sont mis en cache.
- Les sprites et polices de l'habillage sont découverts une fois parmi les
  assets déjà chargés.
- Le prefab du joueur distant vient d'abord de `NetplayManager`; l'Addressable
  natif reste un secours pendant le chargement précoce. Le scan global par nom
  et la lecture de backing field ont été supprimés.

## Travaux qui demandent une mesure ou un test en jeu

Les optimisations P1 à P4 de
[l'audit de performance](performance-reverse-audit.md) restent des candidats.
La RE prouve leurs parcours, pas leur coût réel ; elles ne doivent pas être
patchées sans profil comparatif et tests de transitions de scène.

Le `SharedRopeGamemode` natif sert de référence pour l'ordre des opérations de
corde. Le réutiliser entièrement lierait cependant CairnMP au transport et aux
dictionnaires du netplay natif, alors que le mod possède son protocole et son
cycle de connexion. Les primitives confirmées (`LogicalRope`, `Lifeline`,
`Harness`) restent donc appelées séparément.

Les contrôles à deux comptes à effectuer sont : placement/récupération et
sauvegarde de pitons distants, déconnexion pendant météo forcée, synchronisation
jour/nuit avec et sans mode photo, lampe dans les trois modes, entrée/sortie de
bivouac, puis animation joueur et Climbot après streaming de zone. Les tests
gérés vérifient que les chemins fragiles ne réintroduisent pas d'accès mémoire
manuel, mais ils ne remplacent pas la validation du comportement natif.

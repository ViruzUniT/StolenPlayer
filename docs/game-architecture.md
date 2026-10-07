# Thief Simulator 2 Game Architecture Findings

These findings are from the installed Thief Simulator 2 build and its managed assemblies. Method bodies were inspected as IL with the Mono.Cecil copy shipped in BepInEx. They describe the installed version only; runtime ordering and scene prefab suitability still need in-game verification.

## Runtime and development environment

- Game: Unity `2020.3.33f1`, Windows x64, Mono CLR `4.0.30319`; BepInEx reports `Supports SRE: False`.
- Loader: BepInEx `5.4.23.4`; Harmony is available from `BepInEx/core/0Harmony.dll`.
- The game ships `Assembly-CSharp.dll`, Steamworks.NET `20.0.0` / SDK `1.52`, and native `steam_api64.dll`.
- The plugin project targets `netstandard2.0`. A build against the installed game assemblies succeeded during investigation.

## Player control and representation

`PlayerScript` is the main first-person gameplay controller. It references `PlayerMotorBehavior`, `PlayerCameraBehavior`, first-person hand animations, interaction prompts, `Inventory`, `SaveGame`, `GameManager`, and `Police_Manager`. It also owns many gameplay flags, including current item, carry state, vehicle state, and interaction state.

`PlayerMotorBehavior` exposes movement states such as `isMoving`, `isRunning`, `isCrouching`, and `isGrounded`, plus input and a `CharacterController`. `PlayerCameraBehavior` owns camera look and head-bobbing state. This coupling means a remote representation should not be another unrestricted `PlayerScript`; first verify a full-body NPC model and disable local input, camera, hands, UI, and save behavior on its representation.

Relevant methods include `PlayerScript.AddItem(Int32)`, `RemoveItem(Int32)`, `InteractDelayed(Single)`, `PickUpMoney(Int32)`, `HidePlayer(HidingSpot)`, and `UnhidePlayer()`.

## Saves, inventory, and player progression

`SaveGame.SaveGameNewSystem(Boolean, Single)` writes through `SPrefs`. Observed keys include `Playtime`, `Story`, `StoryBackup`, `Saved_Pos`, `GameTime`, `Week`, `Day`, `Money`, `Debt`, and player condition values. `SaveGame.LoadGame()` reads these values and delegates to inventory, abilities, contracts, survival, rating, and cloak systems.

`GameManager.SaveToProfile(Int32)` stages a selected profile through `InitializePrefs.SaveExtra`, invokes `PlayerScript.Quicksave`, and restores the saved data. `GameManager.LoadProfile(Int32)` selects a `saveData*.thief` file, copies it to the active `saveData.thief`, and loads a checkpoint. The base game therefore combines many world and player values in a profile-oriented save flow.

`PlayerScript.AddItem` and `RemoveItem` mutate `ItemID.item_amount` and player carry weight. `ItemID.Save_Item_Amount()` writes inventory, storage, and locker counts under keys derived from the item ID (`Item_`, `Storage_Item_`, and `Locker_Item_`). `Pickupable` includes `prefName` and `generatedHash` candidates, but neither has been proven to be a unique, cross-session network identity.

`UpgradeIt.BuyUpgrade()` debits `PlayerScript.moneyAm`, increments `UpgradeItem.currentUpgrade`, and writes preference keys based on `UpgradeItem.upgradePref`. This shows progression and money share the base profile flow; it does not establish whether co-op money should be shared or player-specific. That policy remains open pending gameplay review.

## World interactions and progression

- `Door` holds `isOpen`, `opening`, `openedByPlayer`, lock, and container/window flags. `Start()` captures initial transform values in `SaveRot`, `SavePos`, and `SaveRotVector`; `Update()` animates from the door's local state. `Use()` has local gates (`useTim`, `canUse`, `npcUsing`), toggles `isOpen`/`openedByPlayer`, starts the animation, changes its attached `PlayerScript` movement/camera state, and emits noise/audio. It does not itself validate key or lockpick requirements. `UseFully()` bypasses those gates and is called by vehicle-door code; it is not a safe generic network entry point. Other callers include vehicle entry/engine paths. `PlayerScript.Update()` raycasts a `Door` tag, checks its `Door.canUse`/`brokeHinge` state, shows the prompt, handles relocking, and can set `assignDoor.load_slow` for the slow-open path. The ordinary door raycast branch does not directly call `Door.Use()`; the actual normal-open input callback is still unresolved (possibly a serialized UI/event path). This path must be identified before patching door actions. Door save persistence and a unique stable door identifier remain unproven.
- `PlayerScript.Update()` raycasts for `Pickupable`, displays item/money/value information, and on the local `interact` input calls `Pickupable.PickUp()` (or `PickUpHeavy()` for heavy objects). `PickUp()` starts `PickingItem()` rather than completing synchronously. Depending on pickup type and path, its coroutine and `ItemID.Item_Picked()` can mutate `item_amount`, carry weight, inventory persistence, XP, money, mission/story progression, and destroy the world pickup. The inspected flow resolves inventory through the game-local player/item references; it has no target-peer parameter. Pickup replication is blocked until there is a safe per-player inventory/save boundary; a world-only pickup event now would risk loss, duplication, or awarding the wrong player.
- Static door/pickup identity should use a build/scene identity plus canonical hierarchy path and component discriminator, with collision detection. `Pickupable.prefName` and `generatedHash` have not been proven stable or unique across machines; Unity runtime instance IDs and traversal order are unsuitable.
- `Mission_Item.Mission_Complete()` updates a mission count held by `Mission_Inventory` and invokes completion behavior when the requirement is met. `Mission_Inventory.mission_prefs` supplies a persistence key.
- `AI` owns resident behavior and alert state. `Police_Manager` owns spawned police/cars and chase state. These systems need host authority and incremental adapters.
- Runtime F9 inspection found 142 scene objects carrying NPC/traffic scripts in one loaded scene. Resident candidates are generally under `NPCs/<location>/NPC`, with full-body `SkinnedMeshRenderer` meshes below `NPC/Anim`; observed body meshes use roughly 28–63 bones. Candidates also carry `AI`, `RichAI`, `Seeker`, colliders, and rigidbodies, and some also carry `Pickpocket_Object` or `Mission_Item`. Police candidates carry `PolicemanAI` and navigation/physics components. `SidewalkAI` also appeared on traffic cars, so that script alone does not identify a pedestrian.
- The inspected scene exposed no `Animator` components on candidate roots or descendants. Some candidates have Unity's legacy `Animation` component, but the initial scan did not record clips or establish which mesh variant is active. Several outfit/model variants are disabled. The inactive `NPC Test` objects include `NPC_PositionTest` and visible skinned meshes, but have not been verified as a safe runtime template.
- Serialized asset inspection of `Thief Simulator 2_Data/level1` found an inactive scene object at `NPCs/101/NPC` with a legacy `Animation` component. It has the default `Bored` state and clips including `Walk`, `Running`, `WorkOn`, `Sleep`, `Sit`, `Scared`, `StandUp`, and `KnockedOut`. Its visual hierarchy contains a shared Mixamo skeleton and skinned body/outfit parts (`Body`, `Tops`, `Bottoms`, `Shoes`, `Hair`, `Eyes`, and `Eyelashes`). The root also has colliders, a rigidbody, and scene service children such as sensors, voice, footsteps, and whistle. The `Bored`, `Walk`, and `Running` clips have no serialized animation events or generic root-transform flag; their position curves target the `mixamorig:Hips` bone, so network movement should be applied to a parent transform. Runtime reports said looping `Bored` repeatedly swings the arms. Remote avatars now search the live legacy `Animation` state names case-insensitively and prefer a neutral/standing state, then `StandUp`, then `Bored`. The chosen idle state uses `ClampForever` so `StandUp` reaches an upright pose and holds its final frame instead of sitting or looping the arm-swinging idle. The selected and available state names are logged once per spawned visual.
- The inactive `Main Character Final` scene object in `level1` is a weaker locomotion source: one instance's legacy clips are `Sit` and `Driving`; the other uses an Animator Controller whose only clip is `Character_Skill_Image`. The NPC scene object is therefore selected as the first visual prototype basis. It is a serialized scene object rather than a proven portable prefab, so availability in other scenes remains to be verified.
- **Remote-player model decision:** do not clone a live NPC hierarchy as-is. A visual-only model still needs one candidate's active renderer, legacy clips, and required bones verified. Disable all AI, navigation, physics, mission, pickup, and interaction behavior before using a candidate as a remote representation. F9 now reports bounded legacy Animation clip and active skinned-mesh details to support this verification.
- A local-only F10 preview prototype now clones only the exact `NPCs/101/NPC` source under an inactive staging parent, strips nonvisual components while the clone is inactive, and uses the same live-state idle selection as remote avatars. It exists to validate model appearance and clone cleanup in the running game; it is not connected to network player transforms. Availability in other scenes and rendered appearance still need manual runtime verification.
- `DayNightTime`, `PassTime`, and `GameManager` own related world-time and scene flow. `GameManager.LoadNewSceneAsync(String)` is a scene transition boundary.

## Steam integration

`SteamManager` is part of `Assembly-CSharp`. Its `Awake()` runs Steamworks checks, calls `SteamAPI.Init()`, and invokes `SteamAPI.RestartAppIfNecessary()` with App ID `1332720`. Its lifecycle also owns the Steam warning hook. This is retained as reverse-engineering context; the current direct-IP plugin does not initialize or depend on Steamworks.

The shipped Steamworks.NET assembly exposes `SteamNetworkingSockets` P2P connection/send/receive methods, lobby create/join/leave/invite methods, friend overlay invite methods, and `SteamUser.GetSteamID()`. API presence does not prove the installed launch path is connected to the official Steam client: an emulator configuration is present beside the native library. The current project uses direct IP instead.

## Verified limitations

- Assembly metadata and IL reveal candidate owners and writes, but do not establish all Unity callback ordering or cross-system side effects.
- No static scene object ID has been proven durable and unique. Door identity must be resolved before door replication.
- Full-body NPC prefab suitability, local-component disabling, and save overlay feasibility need runtime inspection.
- Money ownership and the boundary between shared world progression and per-player progression remain design questions; no irreversible persistence behavior should be implemented before resolving them.

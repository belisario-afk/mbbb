using System;
using System.Collections.Generic;
using System.Reflection;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Simple Prefab Editor", "belisario-afk", "8.0.0")]
    [Description("Spawn, select and live-edit prefab entities with mystery boxes, templates, SignArtist images, rigid group editing, and a Black Ops style Mystery Box with ImageLibrary GUI reel, cascading gun drops, proximity-buy UI, auto-registration of medieval large wood boxes, spawn point saving, 10-minute respawn timer, and 2-minute active duration.")]
    public class SimplePrefabEditor : RustPlugin
    {
        private const string PermissionUse = "simpleprefabeditor.use";
        private const string CuiPanelName = "SimplePrefabEditor.Panel";
        private const string DataFileName = "SimplePrefabEditor";

        private const ulong DefaultWoodDoorSkinId = 3613584704;

        // Mystery Box root is now the medieval large wood box (always spawns registered)
        private const string MysteryBoxRootPrefab =
            "assets/prefabs/deployable/large wood storage/skins/medieval_large_wood_box/medieval.box.wooden.large.prefab";

        // Proximity radius for showing the BUY UI
        private const float MysteryBoxProximityRadius = 3f;

        [PluginReference] private Plugin SignArtist;
        [PluginReference] private Plugin ImageLibrary;

        private MethodInfo _miUpdateHasPower;

        #region Data classes

        private class EditState
        {
            public BaseEntity Entity;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale = Vector3.one;

            public float Sensitivity = 0.25f;
            public bool GuiOpen;
            public bool KeyboardMode = true;

            public HashSet<ulong> MysteryBoxEntities = new HashSet<ulong>();
            public bool BoxActive;

            public bool GroupEditEnabled;
            public BaseEntity GroupOrigin;

            // Legacy per-player display
            public bool DisplayActive;
            public BaseEntity DisplayEntity;
            public Vector3 DisplayOriginPos;
            public Quaternion DisplayOriginRot;
            public float DisplayCycleInterval = 5f;
            public float DisplayStartTime;
            public Timer DisplayCycleTimer;
        }

        private class FullEntityDef
        {
            public string Prefab;
            public ulong SkinId;
            public Vector3 LocalPosition;
            public Vector3 LocalEulerAngles;
            public Vector3 LocalScale;

            public bool IsPaintable;
            public string ImageUrl;
            public uint TextureIndex;
        }

        private class FullBoxTemplate
        {
            public string Name;
            public List<FullEntityDef> Entities = new List<FullEntityDef>();
        }

        private class MysteryBoxDef
        {
            public uint RootEntityId;
            public Vector3 RootPosition;
            public Vector3 DisplayOffset = new Vector3(0f, 1.2f, 0f);
        }

        /// <summary>
        /// Saved spawn point for Mystery Box - persists across server restarts
        /// </summary>
        private class MysteryBoxSpawnPoint
        {
            public Vector3 Position;
            public Vector3 RotationEuler;   // Store rotation as Euler angles (Vector3) for JSON serialization
            public bool IsActive;           // Whether a box is currently spawned here
            public float LastSpawnTime;     // Server time when last spawned
        }

        private class BoxSaveData
        {
            public Dictionary<ulong, Dictionary<string, List<ulong>>> PlayerBoxes =
                new Dictionary<ulong, Dictionary<string, List<ulong>>>();

            public Dictionary<string, FullBoxTemplate> FullBoxTemplates =
                new Dictionary<string, FullBoxTemplate>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<uint, MysteryBoxDef> MysteryBoxes =
                new Dictionary<uint, MysteryBoxDef>();

            // Saved spawn points for Mystery Boxes - these persist and respawn every 10 minutes
            public List<MysteryBoxSpawnPoint> SpawnPoints = new List<MysteryBoxSpawnPoint>();
        }

        private class SavedEntity
        {
            public string Prefab;
            public ulong SkinId;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        // Runtime mystery box state
        private class MysteryBoxRuntime
        {
            public bool Busy;                // spinning or waiting for pickup
            public string CurrentShortname;  // last chosen shortname
            public ulong ResultDropNetId;    // net ID of spawned drop for this roll

            public HashSet<ulong> ActivePlayers = new HashSet<ulong>();

            // Deactivation timer state
            public float SpawnTime;          // When this box was spawned
            public Timer DeactivationTimer;  // Timer to despawn after 2 minutes
            public int SpawnPointIndex;      // Which spawn point this box belongs to (-1 if manual)
        }

        #endregion

        private BoxSaveData _boxData;
        private readonly Dictionary<ulong, EditState> editing = new Dictionary<ulong, EditState>();
        private SavedEntity _lastSaved;
        private readonly Dictionary<ulong, string> _signUrls = new Dictionary<ulong, string>();

        private readonly (string shortname, float weight)[] DefaultGunPool =
        {
            ("rifle.ak",        1f),
            ("smg.thompson",    1f),
            ("smg.2",           1f), // custom SMG
            ("pistol.python",   1f),
            ("pistol.semiauto", 1f),
            ("shotgun.pump",    1f),
            ("lmg.m249",        0.2f),
            ("pistol.water",    0.1f) // extra rare
        };

        private List<(string shortname, float weight)> _gunPool = new List<(string, float)>();
        private readonly Dictionary<uint, MysteryBoxRuntime> _mysteryRuntime =
            new Dictionary<uint, MysteryBoxRuntime>();

        // Gun shortname -> ImageLibrary key
        private Dictionary<string, string> _gunImageKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Config
        private PluginConfig _config;

        // Proximity loop timer
        private Timer _mboxProximityTimer;

        // Mystery Box spawn/deactivation timers
        private const float MysteryBoxRespawnInterval = 600f;    // 10 minutes in seconds
        private const float MysteryBoxActiveTime = 120f;         // 2 minutes active before deactivation
        private Timer _mysteryBoxRespawnTimer;
        private readonly Dictionary<int, BaseEntity> _spawnedBoxes = new Dictionary<int, BaseEntity>(); // SpawnPointIndex -> Entity

        // Track which box UI is visible for each player (only 1 box UI at a time per player)
        private readonly Dictionary<ulong, uint> _playerVisibleBox = new Dictionary<ulong, uint>();

        // Track countdown UI for players
        private readonly Dictionary<ulong, uint> _playerCountdownBox = new Dictionary<ulong, uint>();

        private class PluginConfig
        {
            public Dictionary<string, string> GunImages = new Dictionary<string, string>
            {
                // Default placeholders – replace with your own image URLs
                ["rifle.ak"]        = "https://via.placeholder.com/256x256.png?text=AK",
                ["smg.thompson"]    = "https://via.placeholder.com/256x256.png?text=Thompson",
                ["smg.2"]           = "https://via.placeholder.com/256x256.png?text=Custom+SMG",
                ["pistol.python"]   = "https://via.placeholder.com/256x256.png?text=Python",
                ["pistol.semiauto"] = "https://via.placeholder.com/256x256.png?text=Semi+Pistol",
                ["shotgun.pump"]    = "https://via.placeholder.com/256x256.png?text=Pump",
                ["lmg.m249"]        = "https://via.placeholder.com/256x256.png?text=M249",
                ["pistol.water"]    = "https://via.placeholder.com/256x256.png?text=Waterpipe"
            };
        }

        private PluginConfig GetDefaultConfig() => new PluginConfig();

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config null");
            }
            catch
            {
                PrintWarning("Config invalid, loading default.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        #region Init / Data

        private void Init()
        {
            permission.RegisterPermission(PermissionUse, this);

            cmd.AddChatCommand("spawn_edit", this, CmdSpawnEdit);
            cmd.AddChatCommand("select_edit", this, CmdSelectEdit);
            cmd.AddChatCommand("save_edit", this, CmdSaveEdit);
            cmd.AddChatCommand("respawn_last", this, CmdRespawnLast);

            cmd.AddChatCommand("edit_gui", this, CmdEditGui);
            cmd.AddChatCommand("edit_close", this, CmdEditClose);
            cmd.AddChatCommand("edit_sensitivity", this, CmdEditSensitivity);

            cmd.AddChatCommand("edit_box_new", this, CmdEditBoxNew);
            cmd.AddChatCommand("edit_box_add", this, CmdEditBoxAdd);
            cmd.AddChatCommand("edit_box_list", this, CmdEditBoxList);
            cmd.AddChatCommand("edit_box_on", this, CmdEditBoxOn);
            cmd.AddChatCommand("edit_box_off", this, CmdEditBoxOff);

            cmd.AddChatCommand("edit_box_save", this, CmdEditBoxSave);
            cmd.AddChatCommand("edit_box_load", this, CmdEditBoxLoad);
            cmd.AddChatCommand("edit_box_saves", this, CmdEditBoxSaves);
            cmd.AddChatCommand("edit_box_delete", this, CmdEditBoxDelete);

            cmd.AddChatCommand("edit_box_save_full", this, CmdEditBoxSaveFull);
            cmd.AddChatCommand("edit_box_spawn", this, CmdEditBoxSpawn);

            cmd.AddChatCommand("edit_group_on", this, CmdEditGroupOn);
            cmd.AddChatCommand("edit_group_off", this, CmdEditGroupOff);

            cmd.AddChatCommand("edit_display_start", this, CmdEditDisplayStart);
            cmd.AddChatCommand("edit_display_stop", this, CmdEditDisplayStop);

            cmd.AddChatCommand("mbox_register", this, CmdMBoxRegister);
            cmd.AddChatCommand("mbox_unregister", this, CmdMBoxUnregister);
            cmd.AddChatCommand("mbox_use", this, CmdMBoxUse);
            cmd.AddChatCommand("mbox_test", this, CmdMBoxTest);
            cmd.AddChatCommand("mbox_clearall", this, CmdMBoxClearAll);

            // Spawn point management commands
            cmd.AddChatCommand("mbox_addspawn", this, CmdMBoxAddSpawn);
            cmd.AddChatCommand("mbox_removespawn", this, CmdMBoxRemoveSpawn);
            cmd.AddChatCommand("mbox_listspawns", this, CmdMBoxListSpawns);
            cmd.AddChatCommand("mbox_forcespawn", this, CmdMBoxForceSpawn);
        }

        private void OnServerInitialized()
        {
            LoadData();
            CacheReflection();
            BuildGunPool();
            InitGunImages();
            StartMysteryBoxProximityLoop();
            AutoRegisterMedievalBoxes();
            StartMysteryBoxRespawnLoop();
            SpawnAllMysteryBoxes(); // Spawn boxes at all saved spawn points on server start
        }

        /// <summary>
        /// Automatically finds and registers any existing medieval large wood box entities as Mystery Boxes.
        /// This ensures the specified asset always spawns registered.
        /// </summary>
        private void AutoRegisterMedievalBoxes()
        {
            int registered = 0;
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var baseEntity = entity as BaseEntity;
                if (baseEntity == null || baseEntity.IsDestroyed || baseEntity.net == null)
                    continue;

                if (!string.Equals(baseEntity.PrefabName, MysteryBoxRootPrefab, StringComparison.OrdinalIgnoreCase))
                    continue;

                uint id = (uint)baseEntity.net.ID.Value;

                // Skip if already registered
                if (_boxData.MysteryBoxes.ContainsKey(id))
                    continue;

                var def = new MysteryBoxDef
                {
                    RootEntityId = id,
                    RootPosition = baseEntity.transform.position,
                    DisplayOffset = new Vector3(0f, 1.2f, 0f)
                };

                _boxData.MysteryBoxes[id] = def;
                _mysteryRuntime[id] = new MysteryBoxRuntime();
                registered++;
            }

            if (registered > 0)
            {
                SaveData();
                Puts($"[SimplePrefabEditor] Auto-registered {registered} medieval large wood box(es) as Mystery Box(es).");
            }
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CloseGui(player);
                if (editing.TryGetValue(player.userID, out var state))
                    StopDisplayForState(state);

                DestroyMysteryGuiForPlayer(player);
                DestroyBuyUi(player);
                DestroyCountdownUi(player);
            }

            _mboxProximityTimer?.Destroy();
            _mboxProximityTimer = null;

            _mysteryBoxRespawnTimer?.Destroy();
            _mysteryBoxRespawnTimer = null;

            // Destroy all deactivation timers
            foreach (var rt in _mysteryRuntime.Values)
            {
                rt.DeactivationTimer?.Destroy();
                rt.DeactivationTimer = null;
            }

            SaveData();
            editing.Clear();
            _mysteryRuntime.Clear();
            _playerVisibleBox.Clear();
            _playerCountdownBox.Clear();
            _spawnedBoxes.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            CloseGui(player);
            DestroyMysteryGuiForPlayer(player);
            DestroyBuyUi(player);
            DestroyCountdownUi(player);

            if (editing.TryGetValue(player.userID, out var state))
            {
                StopDisplayForState(state);
                editing.Remove(player.userID);
            }

            _playerVisibleBox.Remove(player.userID);
            _playerCountdownBox.Remove(player.userID);
        }

        private void LoadData()
        {
            try
            {
                _boxData = Interface.Oxide.DataFileSystem.ReadObject<BoxSaveData>(DataFileName);
                if (_boxData?.PlayerBoxes == null)
                    _boxData = new BoxSaveData();
                if (_boxData.FullBoxTemplates == null)
                    _boxData.FullBoxTemplates = new Dictionary<string, FullBoxTemplate>(StringComparer.OrdinalIgnoreCase);
                if (_boxData.MysteryBoxes == null)
                    _boxData.MysteryBoxes = new Dictionary<uint, MysteryBoxDef>();
                if (_boxData.SpawnPoints == null)
                    _boxData.SpawnPoints = new List<MysteryBoxSpawnPoint>();
            }
            catch
            {
                _boxData = new BoxSaveData
                {
                    FullBoxTemplates = new Dictionary<string, FullBoxTemplate>(StringComparer.OrdinalIgnoreCase),
                    MysteryBoxes = new Dictionary<uint, MysteryBoxDef>(),
                    SpawnPoints = new List<MysteryBoxSpawnPoint>()
                };
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _boxData);
        }

        #endregion

        #region Gun Pool & Images

        private void BuildGunPool()
        {
            _gunPool.Clear();

            foreach (var entry in DefaultGunPool)
            {
                var def = ItemManager.FindItemDefinition(entry.shortname);
                if (def == null)
                {
                    PrintWarning($"[SimplePrefabEditor] Gun shortname not found: {entry.shortname}");
                    continue;
                }
                _gunPool.Add((entry.shortname, entry.weight));
            }

            if (_gunPool.Count == 0)
                PrintWarning("[SimplePrefabEditor] Gun pool is empty; mystery box will not spawn anything.");
            else
                Puts($"[SimplePrefabEditor] Gun pool built with {_gunPool.Count} entries (pistol.water weighted low).");
        }

        private string GetRandomGunShortname()
        {
            if (_gunPool == null || _gunPool.Count == 0)
                return null;

            float totalWeight = 0f;
            foreach (var g in _gunPool)
                totalWeight += g.weight;

            if (totalWeight <= 0f)
                return null;

            float roll = UnityEngine.Random.Range(0f, totalWeight);
            float cumulative = 0f;

            foreach (var g in _gunPool)
            {
                cumulative += g.weight;
                if (roll <= cumulative)
                    return g.shortname;
            }

            return _gunPool[_gunPool.Count - 1].shortname;
        }

        private void InitGunImages()
        {
            _gunImageKeys.Clear();

            if (ImageLibrary == null)
            {
                PrintWarning("[SimplePrefabEditor] ImageLibrary not found. Mystery Box GUI will show text only.");
                return;
            }

            foreach (var kv in _config.GunImages)
            {
                string shortname = kv.Key;
                string url = kv.Value;
                if (string.IsNullOrEmpty(url))
                    continue;

                string key = $"spe_{shortname}";

                try
                {
                    ImageLibrary.Call("AddImage", url, key, (ulong)0);
                    _gunImageKeys[shortname] = key;
                }
                catch (Exception ex)
                {
                    PrintWarning($"[SimplePrefabEditor] Failed to add image for {shortname} via ImageLibrary: {ex}");
                }
            }

            Puts($"[SimplePrefabEditor] Registered {_gunImageKeys.Count} gun images via ImageLibrary.");
        }

        private string GetGunImageKey(string shortname)
        {
            if (string.IsNullOrEmpty(shortname))
                return null;

            if (_gunImageKeys.TryGetValue(shortname, out var key))
                return key;

            return null;
        }

        #endregion

        #region SignArtist hook

        private void OnImagePost(BasePlayer player, string url, bool raw, BaseEntity entity, uint textureIndex)
        {
            if (entity == null || entity.net == null || string.IsNullOrEmpty(url))
                return;
            if (textureIndex != 0) return;

            _signUrls[entity.net.ID.Value] = url;
        }

        #endregion

        #region Spawn with Skin

        private BaseEntity SpawnSkinnedEntity(string prefab, Vector3 position, Quaternion rotation, ulong skinId = 0)
        {
            BaseEntity ent;
            try
            {
                ent = GameManager.server.CreateEntity(prefab, position, rotation, true);
            }
            catch (Exception ex)
            {
                PrintWarning($"SpawnSkinnedEntity error for '{prefab}': {ex}");
                return null;
            }

            if (ent == null)
                return null;

            if (skinId != 0)
                ent.skinID = skinId;

            ent.Spawn();

            ent.transform.position   = position;
            ent.transform.rotation   = rotation;
            ent.transform.localScale = Vector3.one;
            ent.SendNetworkUpdate();

            return ent;
        }

        #endregion

        #region Core Commands

        private void CmdSpawnEdit(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (args == null || args.Length < 1)
            {
                PrintToChat(player,
                    "<color=#ffcc00>Usage:</color> /spawn_edit <prefabpath> [skinId]\n" +
                    $"Example:\n/spawn_edit assets/prefabs/deployable/door.hinged.wood/door.hinged.wood.prefab {DefaultWoodDoorSkinId}");
                return;
            }

            var prefabPath = args[0];
            ulong skinId = 0;
            if (args.Length >= 2)
                ulong.TryParse(args[1], out skinId);

            var pos = player.eyes.position + player.eyes.BodyForward() * 2f;
            var rot = Quaternion.identity;

            var ent = SpawnSkinnedEntity(prefabPath, pos, rot, skinId);
            if (ent == null)
            {
                PrintToChat(player, $"<color=#ff0000>Could not create entity:</color> {prefabPath}");
                return;
            }

            var state = GetOrCreateState(player);
            state.Entity   = ent;
            state.Position = ent.transform.position;
            state.Rotation = ent.transform.rotation;
            state.Scale    = ent.transform.localScale;

            PrintToChat(player,
                $"<color=#00ff00>Spawned & selected:</color> {prefabPath} {(skinId != 0 ? $"(skin {skinId})" : "")}");
        }

        private void CmdSelectEdit(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var ent = GetLookAtEntity(player);
            if (ent == null || ent.transform == null)
            {
                PrintToChat(player, "<color=#ffcc00>No valid entity where you’re looking.</color>");
                return;
            }

            var state = GetOrCreateState(player);
            state.Entity   = ent;
            state.Position = ent.transform.position;
            state.Rotation = ent.transform.rotation;
            state.Scale    = ent.transform.localScale;

            PrintToChat(player,
                $"<color=#00ff00>Selected:</color> {ent.ShortPrefabName} ({ent.PrefabName})");
        }

        private void CmdSaveEdit(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state) ||
                state.Entity == null || state.Entity.IsDestroyed || state.Entity.transform == null)
            {
                PrintToChat(player, "<color=#ffcc00>No active entity selected.</color>");
                return;
            }

            var e = state.Entity;
            var t = e.transform;
            var pos = t.position;
            var rotEuler = t.rotation.eulerAngles;
            var scale = t.localScale;
            ulong skinId = e.skinID;

            _lastSaved = new SavedEntity
            {
                Prefab = e.PrefabName,
                SkinId = skinId,
                Position = pos,
                Rotation = Quaternion.Euler(rotEuler),
                Scale = scale
            };

            PrintToChat(player,
                "<color=#00ffff>Saved current entity.</color>\n" +
                "Use <color=#ffffff>/respawn_last</color> to spawn a copy later.");
        }

        private void CmdRespawnLast(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (_lastSaved == null || string.IsNullOrEmpty(_lastSaved.Prefab))
            {
                PrintToChat(player, "<color=#ffcc00>No last saved entity.</color>");
                return;
            }

            var e = SpawnSkinnedEntity(_lastSaved.Prefab, _lastSaved.Position, _lastSaved.Rotation, _lastSaved.SkinId);
            if (e == null)
            {
                PrintToChat(player, "<color=#ff0000>Failed to respawn last entity.</color>");
                return;
            }

            e.transform.localScale = _lastSaved.Scale;
            e.networkEntityScale = true;
            e.SendNetworkUpdate();

            var state = GetOrCreateState(player);
            state.Entity   = e;
            state.Position = e.transform.position;
            state.Rotation = e.transform.rotation;
            state.Scale    = e.transform.localScale;

            PrintToChat(player,
                $"<color=#00ff00>Respawned:</color> {e.ShortPrefabName} (skin {_lastSaved.SkinId})");
        }

        #endregion

        #region GUI / Sensitivity

        private void CmdEditGui(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var state = GetOrCreateState(player);
            if (state.Entity == null || state.Entity.IsDestroyed || state.Entity.transform == null)
            {
                PrintToChat(player, "<color=#ffcc00>No active entity. Use /spawn_edit or /select_edit first.</color>");
                return;
            }

            OpenGui(player, state);
        }

        private void CmdEditClose(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            CloseGui(player);
        }

        private void CmdEditSensitivity(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var state = GetOrCreateState(player);

            if (args == null || args.Length < 1)
            {
                PrintToChat(player,
                    $"<color=#ffcc00>Usage:</color> /edit_sensitivity <value> (current {state.Sensitivity:F3})");
                return;
            }

            if (!float.TryParse(args[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0f)
            {
                PrintToChat(player, "<color=#ff0000>Invalid sensitivity. Must be > 0.</color>");
                return;
            }

            state.Sensitivity = value;
            PrintToChat(player, $"<color=#00ff00>Sensitivity set to:</color> {state.Sensitivity:F3}");

            if (state.GuiOpen)
                OpenGui(player, state);
        }

        #endregion

        #region Mystery Box (ID-based editor only)

        private void CmdEditBoxNew(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var state = GetOrCreateState(player);
            state.MysteryBoxEntities.Clear();
            state.BoxActive = false;

            PrintToChat(player, "<color=#00ff00>New box started.</color> Use /edit_box_add for each piece.");
        }

        private void CmdEditBoxAdd(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state) ||
                state.Entity == null || state.Entity.IsDestroyed || state.Entity.net == null)
            {
                PrintToChat(player, "<color=#ffcc00>No active entity. Use /select_edit first.</color>");
                return;
            }

            var ent = state.Entity;
            state.MysteryBoxEntities.Add(ent.net.ID.Value);
            PrintToChat(player, $"<color=#00ff00>Added:</color> {ent.ShortPrefabName} ({ent.net.ID.Value})");
        }

        private void CmdEditBoxList(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state) ||
                state.MysteryBoxEntities.Count == 0)
            {
                PrintToChat(player, "<color=#ffcc00>Box is empty.</color>");
                return;
            }

            PrintToChat(player, "<color=#00ffff>=== Box Entities ===</color>");
            foreach (var id in state.MysteryBoxEntities)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                if (ent == null)
                    PrintToChat(player, $"{id} - <color=#ff0000>missing/destroyed</color>");
                else
                    PrintToChat(player, $"{id} - {ent.ShortPrefabName}");
            }
        }

        private void CmdEditBoxOn(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state) ||
                state.MysteryBoxEntities.Count == 0)
            {
                PrintToChat(player, "<color=#ffcc00>No entities in box.</color>");
                return;
            }

            state.BoxActive = true;
            int count = 0;
            foreach (var id in state.MysteryBoxEntities)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                if (ent == null || ent.IsDestroyed) continue;
                if (ForceLightOn(ent)) count++;
            }

            PrintToChat(player, $"<color=#00ff00>Lights forced on for {count} entities.</color>");
        }

        private void CmdEditBoxOff(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state))
            {
                PrintToChat(player, "<color=#ffcc00>No current edit state.</color>");
                return;
            }

            state.BoxActive = false;
            PrintToChat(player, "<color=#ffcc00>Mystery box deactivated.</color>");
        }

        private void CmdEditBoxSave(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state) ||
                state.MysteryBoxEntities.Count == 0)
            {
                PrintToChat(player, "<color=#ffcc00>Box is empty.</color>");
                return;
            }

            if (args == null || args.Length < 1 || string.IsNullOrEmpty(args[0]))
            {
                PrintToChat(player, "<color=#ffcc00>Usage:</color> /edit_box_save <name>");
                return;
            }

            var name = args[0].Trim();
            if (string.IsNullOrEmpty(name))
            {
                PrintToChat(player, "<color=#ffcc00>Name cannot be empty.</color>");
                return;
            }

            if (!_boxData.PlayerBoxes.TryGetValue(player.userID, out var map))
            {
                map = new Dictionary<string, List<ulong>>(StringComparer.OrdinalIgnoreCase);
                _boxData.PlayerBoxes[player.userID] = map;
            }

            map[name] = new List<ulong>(state.MysteryBoxEntities);
            SaveData();

            PrintToChat(player,
                $"<color=#00ff00>Saved ID-based box:</color> {name} ({state.MysteryBoxEntities.Count} IDs)");
        }

        private void CmdEditBoxLoad(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (args == null || args.Length < 1 || string.IsNullOrEmpty(args[0]))
            {
                PrintToChat(player, "<color=#ffcc00>Usage:</color> /edit_box_load <name>");
                return;
            }

            var name = args[0].Trim();

            if (!_boxData.PlayerBoxes.TryGetValue(player.userID, out var map) ||
                !map.TryGetValue(name, out var ids) ||
                ids == null || ids.Count == 0)
            {
                PrintToChat(player, $"<color=#ffcc00>No saved box with name:</color> {name}");
                return;
            }

            var state = GetOrCreateState(player);
            state.MysteryBoxEntities.Clear();

            int valid = 0;
            BaseEntity first = null;

            foreach (var id in ids)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                if (ent == null || ent.IsDestroyed || ent.transform == null)
                    continue;

                state.MysteryBoxEntities.Add(id);
                valid++;
                if (first == null)
                    first = ent;
            }

            if (valid == 0)
            {
                PrintToChat(player,
                    $"<color=#ffcc00>Saved box '{name}' has no valid entities alive now.</color>");
                return;
            }

            state.Entity   = first;
            state.Position = first.transform.position;
            state.Rotation = first.transform.rotation;
            state.Scale    = first.transform.localScale;

            PrintToChat(player,
                $"<color=#00ff00>Loaded:</color> {name} ({valid} valid entities)");
        }

        private void CmdEditBoxSaves(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!_boxData.PlayerBoxes.TryGetValue(player.userID, out var map) || map.Count == 0)
            {
                PrintToChat(player, "<color=#ffcc00>No ID-based box saves.</color>");
                return;
            }

            PrintToChat(player, "<color=#00ffff>=== Your ID-based boxes ===</color>");
            foreach (var kv in map)
            {
                int count = kv.Value?.Count ?? 0;
                PrintToChat(player, $"{kv.Key} ({count} IDs)");
            }
        }

        private void CmdEditBoxDelete(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (args == null || args.Length < 1 || string.IsNullOrEmpty(args[0]))
            {
                PrintToChat(player, "<color=#ffcc00>Usage:</color> /edit_box_delete <name>");
                return;
            }

            var name = args[0].Trim();

            if (!_boxData.PlayerBoxes.TryGetValue(player.userID, out var map) || !map.Remove(name))
            {
                PrintToChat(player, $"<color=#ffcc00>No saved box with name:</color> {name}");
                return;
            }

            SaveData();
            PrintToChat(player, $"<color=#ff0000>Deleted box:</color> {name}");
        }

        #endregion

        #region Full Templates

        private void CmdEditBoxSaveFull(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state) ||
                state.MysteryBoxEntities.Count == 0)
            {
                PrintToChat(player, "<color=#ffcc00>Box is empty. Use /edit_box_add first.</color>");
                return;
            }

            if (args == null || args.Length < 1 || string.IsNullOrEmpty(args[0]))
            {
                PrintToChat(player, "<color=#ffcc00>Usage:</color> /edit_box_save_full <templateName>");
                return;
            }

            string templateName = args[0].Trim();
            if (string.IsNullOrEmpty(templateName))
            {
                PrintToChat(player, "<color=#ffcc00>Template name cannot be empty.</color>");
                return;
            }

            BaseEntity origin = null;
            if (state.Entity != null && state.Entity.net != null &&
                state.MysteryBoxEntities.Contains(state.Entity.net.ID.Value))
                origin = state.Entity;
            else
            {
                foreach (var id in state.MysteryBoxEntities)
                {
                    var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                    if (ent != null && !ent.IsDestroyed && ent.transform != null)
                    {
                        origin = ent;
                        break;
                    }
                }
            }

            if (origin == null || origin.transform == null)
            {
                PrintToChat(player, "<color=#ffcc00>Could not find valid origin in box.</color>");
                return;
            }

            Vector3 originPos = origin.transform.position;
            Quaternion originRot = origin.transform.rotation;

            var template = new FullBoxTemplate { Name = templateName };
            int saved = 0;

            foreach (var id in state.MysteryBoxEntities)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                if (ent == null || ent.IsDestroyed || ent.transform == null)
                    continue;
                if (string.IsNullOrEmpty(ent.PrefabName))
                    continue;

                Vector3 worldPos = ent.transform.position;
                Quaternion worldRot = ent.transform.rotation;
                Vector3 worldScale = ent.transform.localScale;
                ulong skinId = ent.skinID;

                Vector3 localPos = Quaternion.Inverse(originRot) * (worldPos - originPos);
                Quaternion localRot = Quaternion.Inverse(originRot) * worldRot;

                var def = new FullEntityDef
                {
                    Prefab = ent.PrefabName,
                    SkinId = skinId,
                    LocalPosition = localPos,
                    LocalEulerAngles = localRot.eulerAngles,
                    LocalScale = worldScale,
                    IsPaintable = false,
                    ImageUrl = null,
                    TextureIndex = 0
                };

                TryCapturePaintableUrl(ent, ref def);

                template.Entities.Add(def);
                saved++;
            }

            if (saved == 0)
            {
                PrintToChat(player, "<color=#ffcc00>No valid entities for template.</color>");
                return;
            }

            _boxData.FullBoxTemplates[templateName] = template;
            SaveData();

            PrintToChat(player,
                $"<color=#00ff00>Saved template:</color> {templateName} ({saved} entities, origin {origin.ShortPrefabName})");
        }

        private void CmdEditBoxSpawn(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (args == null || args.Length < 2 ||
                string.IsNullOrEmpty(args[0]) || string.IsNullOrEmpty(args[1]))
            {
                PrintToChat(player, "<color=#ffcc00>Usage:</color> /edit_box_spawn <templateName> <instanceName>");
                return;
            }

            string templateName = args[0].Trim();
            string instanceName = args[1].Trim();

            if (!_boxData.FullBoxTemplates.TryGetValue(templateName, out var template) ||
                template == null || template.Entities == null || template.Entities.Count == 0)
            {
                PrintToChat(player, $"<color=#ffcc00>No template with name:</color> {templateName}");
                return;
            }

            var rayPos = player.eyes.position;
            var rayDir = player.eyes.BodyForward();
            Vector3 spawnPos = rayPos + rayDir * 3f;
            RaycastHit hit;
            if (Physics.Raycast(rayPos, rayDir, out hit, 50f))
                spawnPos = hit.point;

            Quaternion rootRot = Quaternion.identity;

            var newEntities = new List<BaseEntity>();

            foreach (var def in template.Entities)
            {
                Vector3 worldPos = spawnPos + rootRot * def.LocalPosition;
                Quaternion localRot = Quaternion.Euler(def.LocalEulerAngles);
                Quaternion worldRot = rootRot * localRot;

                var e = SpawnSkinnedEntity(def.Prefab, worldPos, worldRot, def.SkinId);
                if (e == null) continue;

                e.transform.localScale = def.LocalScale;
                e.networkEntityScale = true;
                e.SendNetworkUpdate();

                newEntities.Add(e);

                if (def.IsPaintable && !string.IsNullOrEmpty(def.ImageUrl))
                    TryApplyPaintableUrl(player, e, def);
            }

            if (newEntities.Count == 0)
            {
                PrintToChat(player, "<color=#ff0000>Failed to spawn any entities from template.</color>");
                return;
            }

            var state = GetOrCreateState(player);
            state.MysteryBoxEntities.Clear();
            foreach (var e in newEntities)
                if (e?.net != null)
                    state.MysteryBoxEntities.Add(e.net.ID.Value);

            var root = newEntities[0];
            state.Entity   = root;
            state.Position = root.transform.position;
            state.Rotation = root.transform.rotation;
            state.Scale    = root.transform.localScale;

            PrintToChat(player,
                $"<color=#00ff00>Spawned instance:</color> {instanceName} from {templateName} " +
                $"({newEntities.Count} entities)");
        }

        #endregion

        #region Group Edit Mode

        private void CmdEditGroupOn(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state) ||
                state.Entity == null || state.Entity.IsDestroyed || state.Entity.transform == null)
            {
                PrintToChat(player, "<color=#ffcc00>No active entity. Use /select_edit first.</color>");
                return;
            }

            if (state.MysteryBoxEntities == null || state.MysteryBoxEntities.Count == 0)
            {
                PrintToChat(player, "<color=#ffcc00>Box is empty. Use /edit_box_add first.</color>");
                return;
            }

            state.GroupEditEnabled = true;
            state.GroupOrigin = state.Entity;

            PrintToChat(player,
                "<color=#00ff00>Group edit ON.</color> " +
                "Move/rotate while holding LMB will move the whole box as a rigid shape.");
        }

        private void CmdEditGroupOff(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (!editing.TryGetValue(player.userID, out var state))
                return;

            state.GroupEditEnabled = false;
            state.GroupOrigin = null;

            PrintToChat(player, "<color=#ffcc00>Group edit OFF.</color> Edits are single-entity again.");
        }

        #endregion

        #region Legacy Display (per-player floating model)

        private void CmdEditDisplayStart(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var state = GetOrCreateState(player);

            if (state.Entity == null || state.Entity.IsDestroyed || state.Entity.transform == null)
            {
                PrintToChat(player, "<color=#ffcc00>No active entity to anchor display.</color> Use /select_edit first.");
                return;
            }

            float interval = 5f;
            if (args != null && args.Length >= 1)
            {
                float.TryParse(args[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out interval);
                if (interval <= 0f)
                    interval = 5f;
            }

            state.DisplayOriginPos = state.Entity.transform.position + new Vector3(0f, 1.0f, 0f);
            state.DisplayOriginRot = Quaternion.identity;
            state.DisplayCycleInterval = interval;

            StopDisplayForState(state, keepActiveFlag: false);

            state.DisplayActive = true;

            SpawnNextDisplayGunForStateLegacy(state);

            state.DisplayCycleTimer = timer.Every(state.DisplayCycleInterval, () =>
            {
                SpawnNextDisplayGunForStateLegacy(state);
            });

            PrintToChat(player,
                $"<color=#00ff00>Display started.</color> Random model every {interval:0.##}s.");
        }

        private void CmdEditDisplayStop(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var state = GetOrCreateState(player);
            StopDisplayForState(state);

            PrintToChat(player, "<color=#ffcc00>Display stopped.</color>");
        }

        private void StopDisplayForState(EditState state, bool keepActiveFlag = false)
        {
            if (state == null)
                return;

            state.DisplayCycleTimer?.Destroy();
            state.DisplayCycleTimer = null;

            if (state.DisplayEntity != null && !state.DisplayEntity.IsDestroyed)
            {
                state.DisplayEntity.Kill();
                state.DisplayEntity = null;
            }

            if (!keepActiveFlag)
                state.DisplayActive = false;
        }

        private void SpawnNextDisplayGunForStateLegacy(EditState state)
        {
            if (state == null || !state.DisplayActive)
                return;

            if (state.DisplayEntity != null && !state.DisplayEntity.IsDestroyed)
            {
                state.DisplayEntity.Kill();
                state.DisplayEntity = null;
            }

            string prefab = "assets/prefabs/misc/item drop/item_drop.prefab";
            var pos = state.DisplayOriginPos;
            var rot = state.DisplayOriginRot;

            BaseEntity ent;
            try
            {
                ent = GameManager.server.CreateEntity(prefab, pos, rot, true);
            }
            catch
            {
                return;
            }

            if (ent == null)
                return;

            ent.Spawn();
            ent.transform.position = pos;
            ent.transform.rotation = rot;
            ent.SendNetworkUpdate();

            state.DisplayEntity = ent;
            state.DisplayStartTime = Time.realtimeSinceStartup;
        }

        #endregion

        #region Mystery Box Registration & Trigger (Proximity UI)

        private void CmdMBoxRegister(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var ent = GetLookAtEntity(player, 5f);
            if (ent == null || ent.net == null)
            {
                PrintToChat(player, "<color=#ffcc00>Look at the medieval large wood box you want to register.</color>");
                return;
            }

            if (!string.Equals(ent.PrefabName, MysteryBoxRootPrefab, StringComparison.OrdinalIgnoreCase))
            {
                PrintToChat(player,
                    $"<color=#ffcc00>This is not the correct prefab for the Mystery Box root.</color>\nExpected: {MysteryBoxRootPrefab}\nGot: {ent.PrefabName}");
                return;
            }

            uint id = (uint)ent.net.ID.Value;

            var def = new MysteryBoxDef
            {
                RootEntityId = id,
                RootPosition = ent.transform.position,
                DisplayOffset = new Vector3(0f, 1.2f, 0f)
            };

            _boxData.MysteryBoxes[id] = def;
            SaveData();

            _mysteryRuntime[id] = new MysteryBoxRuntime();

            PrintToChat(player,
                $"<color=#00ff00>Registered Mystery Box (medieval box):</color> {ent.ShortPrefabName} ({id})");
            PrintToChat(player,
                "Players will see a BUY UI when close to this box and can click it to roll the Mystery Box.");
        }

        private void CmdMBoxUnregister(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var ent = GetLookAtEntity(player, 5f);
            if (ent == null || ent.net == null)
            {
                PrintToChat(player, "<color=#ffcc00>Look at the medieval box you want to unregister.</color>");
                return;
            }

            uint id = (uint)ent.net.ID.Value;

            if (!_boxData.MysteryBoxes.Remove(id))
            {
                PrintToChat(player, $"<color=#ffcc00>That entity ({id}) is not a registered Mystery Box.</color>");
                return;
            }

            if (_mysteryRuntime.TryGetValue(id, out var rt))
            {
                ClearMysteryBoxGuiForAll(rt);
                _mysteryRuntime.Remove(id);
            }

            // Clean any BUY UI for this box
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null) continue;
                if (_playerVisibleBox.TryGetValue(p.userID, out var current) && current == id)
                {
                    DestroyBuyUi(p);
                    _playerVisibleBox[p.userID] = 0;
                }
            }

            SaveData();

            PrintToChat(player,
                $"<color=#ff0000>Unregistered Mystery Box:</color> {ent.ShortPrefabName} ({id})");
        }

        // Optional: manual command to use the box while looking at it
        private void CmdMBoxUse(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var ent = GetLookAtEntity(player, 5f);
            if (ent == null || ent.net == null)
            {
                PrintToChat(player, "<color=#ffcc00>Look at a registered Mystery Box.</color>");
                return;
            }

            if (!string.Equals(ent.PrefabName, MysteryBoxRootPrefab, StringComparison.OrdinalIgnoreCase))
            {
                PrintToChat(player, "<color=#ffcc00>That is not a Mystery Box (medieval box).</color>");
                return;
            }

            uint id = (uint)ent.net.ID.Value;
            if (!_boxData.MysteryBoxes.ContainsKey(id))
            {
                PrintToChat(player, "<color=#ffcc00>This medieval box is not registered as a Mystery Box. It should auto-register on spawn, or an admin can run /mbox_register.</color>");
                return;
            }

            if (!_mysteryRuntime.TryGetValue(id, out var rt))
            {
                rt = new MysteryBoxRuntime();
                _mysteryRuntime[id] = rt;
            }

            if (rt.Busy)
            {
                PrintToChat(player, "<color=#ffcc00>The Mystery Box is busy, wait for the current roll to finish.</color>");
                return;
            }

            if (!ChargeMysteryBoxUse(player, id))
            {
                PrintToChat(player, "<color=#ff0000>You cannot afford the Mystery Box.</color>");
                return;
            }

            StartMysteryBoxSpin(id, ent, player);
        }

        // Debug/test: manually trigger a roll on the box you're looking at
        private void CmdMBoxTest(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var ent = GetLookAtEntity(player, 5f);
            if (ent == null || ent.net == null)
            {
                PrintToChat(player, "<color=#ffcc00>Look at a registered Mystery Box (medieval box).</color>");
                return;
            }

            uint id = (uint)ent.net.ID.Value;
            if (!_boxData.MysteryBoxes.ContainsKey(id))
            {
                PrintToChat(player, "<color=#ffcc00>That entity is not a registered Mystery Box. Medieval boxes auto-register on spawn, or use /mbox_register.</color>");
                return;
            }

            if (!_mysteryRuntime.TryGetValue(id, out var rt))
            {
                rt = new MysteryBoxRuntime();
                _mysteryRuntime[id] = rt;
            }

            if (rt.Busy)
            {
                PrintToChat(player, "<color=#ffcc00>Mystery Box is already rolling.</color>");
                return;
            }

            if (!ChargeMysteryBoxUse(player, id))
            {
                PrintToChat(player, "<color=#ff0000>You cannot afford the Mystery Box.</color>");
                return;
            }

            StartMysteryBoxSpin(id, ent, player);
        }

        /// <summary>
        /// Admin panic button: clear all Mystery Boxes and all related UI.
        /// </summary>
        [ChatCommand("mbox_clearall")]
        private void CmdMBoxClearAll(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            // 1) Clear all data & runtime state
            _boxData.MysteryBoxes.Clear();
            _mysteryRuntime.Clear();
            _playerVisibleBox.Clear();
            SaveData();

            // 2) Destroy ALL possible Mystery Box UI for ALL players
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null) continue;

                // Destroy any BUY UI and Reel UI with known or possible IDs
                for (uint fakeId = 1; fakeId <= 2000; fakeId++)
                {
                    string buyName = GetBuyUiName(fakeId, p.userID);
                    CuiHelper.DestroyUi(p, buyName);

                    string reelName = GetMysteryGuiName(fakeId, p.userID);
                    CuiHelper.DestroyUi(p, reelName);
                }
            }

            PrintToChat(player, "<color=#ff0000>All Mystery Boxes and their UIs have been cleared.</color>");
        }

        /// <summary>
        /// Placeholder for your currency plugin integration.
        /// Return true if payment succeeded, false to block the roll.
        /// </summary>
        private bool ChargeMysteryBoxUse(BasePlayer player, uint boxId)
        {
            // TODO: integrate your currency plugin here, e.g.:
            // object result = MyEconomyPlugin?.Call("ChargePlayerForMysteryBox", player, boxId);
            // return result is bool b && b;
            return true;
        }

        #endregion

        #region Mystery Box Spawn Point Management

        /// <summary>
        /// Add a spawn point at the player's current position
        /// </summary>
        private void CmdMBoxAddSpawn(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            var spawnPoint = new MysteryBoxSpawnPoint
            {
                Position = player.transform.position,
                RotationEuler = player.transform.rotation.eulerAngles,  // Store as Euler angles for JSON serialization
                IsActive = false,
                LastSpawnTime = 0f
            };

            _boxData.SpawnPoints.Add(spawnPoint);
            SaveData();

            int index = _boxData.SpawnPoints.Count - 1;
            PrintToChat(player, $"<color=#00ff00>Added Mystery Box spawn point #{index}</color> at your position.\n" +
                $"Position: {spawnPoint.Position}\n" +
                $"Boxes will spawn here every 10 minutes and stay active for 2 minutes.");
        }

        /// <summary>
        /// Remove a spawn point by index
        /// </summary>
        private void CmdMBoxRemoveSpawn(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (args == null || args.Length < 1)
            {
                PrintToChat(player, "<color=#ffcc00>Usage:</color> /mbox_removespawn <index>");
                return;
            }

            if (_boxData.SpawnPoints == null || _boxData.SpawnPoints.Count == 0)
            {
                PrintToChat(player, "<color=#ff0000>No spawn points to remove.</color>");
                return;
            }

            if (!int.TryParse(args[0], out int index) || index < 0 || index >= _boxData.SpawnPoints.Count)
            {
                PrintToChat(player, $"<color=#ff0000>Invalid spawn point index.</color> Valid range: 0 to {_boxData.SpawnPoints.Count - 1}");
                return;
            }

            // Kill the box if one is spawned at this point
            if (_spawnedBoxes.TryGetValue(index, out var entity))
            {
                if (entity != null && !entity.IsDestroyed)
                    entity.Kill();
                _spawnedBoxes.Remove(index);
            }

            _boxData.SpawnPoints.RemoveAt(index);
            SaveData();

            // Update indices in _spawnedBoxes dictionary
            var updatedBoxes = new Dictionary<int, BaseEntity>();
            foreach (var kv in _spawnedBoxes)
            {
                int newIndex = kv.Key > index ? kv.Key - 1 : kv.Key;
                updatedBoxes[newIndex] = kv.Value;
            }
            _spawnedBoxes.Clear();
            foreach (var kv in updatedBoxes)
                _spawnedBoxes[kv.Key] = kv.Value;

            PrintToChat(player, $"<color=#ff0000>Removed spawn point #{index}.</color>");
        }

        /// <summary>
        /// List all spawn points
        /// </summary>
        private void CmdMBoxListSpawns(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            if (_boxData.SpawnPoints == null || _boxData.SpawnPoints.Count == 0)
            {
                PrintToChat(player, "<color=#ffcc00>No Mystery Box spawn points saved.</color>\nUse /mbox_addspawn to add one.");
                return;
            }

            PrintToChat(player, "<color=#00ffff>=== Mystery Box Spawn Points ===</color>");
            for (int i = 0; i < _boxData.SpawnPoints.Count; i++)
            {
                var sp = _boxData.SpawnPoints[i];
                string status = sp.IsActive ? "<color=#00ff00>ACTIVE</color>" : "<color=#ff0000>INACTIVE</color>";
                PrintToChat(player, $"#{i}: {sp.Position} - {status}");
            }
        }

        /// <summary>
        /// Force spawn boxes at all spawn points immediately
        /// </summary>
        private void CmdMBoxForceSpawn(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
                return;

            SpawnAllMysteryBoxes();
            PrintToChat(player, $"<color=#00ff00>Force spawned Mystery Boxes at all {_boxData.SpawnPoints.Count} spawn points.</color>");
        }

        /// <summary>
        /// Start the 10-minute respawn loop
        /// </summary>
        private void StartMysteryBoxRespawnLoop()
        {
            _mysteryBoxRespawnTimer?.Destroy();
            _mysteryBoxRespawnTimer = timer.Every(MysteryBoxRespawnInterval, () =>
            {
                SpawnAllMysteryBoxes();
            });

            Puts($"[SimplePrefabEditor] Mystery Box respawn loop started (every {MysteryBoxRespawnInterval / 60f} minutes).");
        }

        /// <summary>
        /// Spawn Mystery Boxes at all saved spawn points
        /// </summary>
        private void SpawnAllMysteryBoxes()
        {
            if (_boxData.SpawnPoints == null || _boxData.SpawnPoints.Count == 0)
                return;

            int spawned = 0;
            for (int i = 0; i < _boxData.SpawnPoints.Count; i++)
            {
                var sp = _boxData.SpawnPoints[i];

                // Kill existing box at this spawn point if any
                if (_spawnedBoxes.TryGetValue(i, out var existingEntity))
                {
                    if (existingEntity != null && !existingEntity.IsDestroyed)
                    {
                        uint oldId = (uint)(existingEntity.net?.ID.Value ?? 0);
                        if (oldId != 0)
                        {
                            if (_mysteryRuntime.TryGetValue(oldId, out var oldRt))
                            {
                                oldRt.DeactivationTimer?.Destroy();
                                _mysteryRuntime.Remove(oldId);
                            }
                            _boxData.MysteryBoxes.Remove(oldId);
                        }
                        existingEntity.Kill();
                    }
                    _spawnedBoxes.Remove(i);
                }

                // Spawn new box
                var entity = SpawnMysteryBoxAtPoint(sp, i);
                if (entity != null)
                {
                    sp.IsActive = true;
                    sp.LastSpawnTime = Time.realtimeSinceStartup;
                    spawned++;
                }
            }

            if (spawned > 0)
            {
                SaveData();
                Puts($"[SimplePrefabEditor] Spawned {spawned} Mystery Box(es). They will deactivate in {MysteryBoxActiveTime / 60f} minutes.");

                // Broadcast to all players
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null) continue;
                    player.ChatMessage($"<color=#00ffff>⚡ Mystery Box has spawned! ⚡</color>\n<color=#ffcc00>Available for {MysteryBoxActiveTime / 60f} minutes!</color>");
                }
            }
        }

        /// <summary>
        /// Spawn a single Mystery Box at a spawn point
        /// </summary>
        private BaseEntity SpawnMysteryBoxAtPoint(MysteryBoxSpawnPoint spawnPoint, int spawnPointIndex)
        {
            BaseEntity entity;
            try
            {
                // Convert stored Euler angles back to Quaternion for spawning
                Quaternion rotation = Quaternion.Euler(spawnPoint.RotationEuler);
                entity = GameManager.server.CreateEntity(MysteryBoxRootPrefab, spawnPoint.Position, rotation, true);
            }
            catch (Exception ex)
            {
                PrintWarning($"[SimplePrefabEditor] Failed to spawn Mystery Box: {ex}");
                return null;
            }

            if (entity == null)
                return null;

            entity.Spawn();
            entity.SendNetworkUpdate();

            uint id = (uint)entity.net.ID.Value;

            // Register this box
            var def = new MysteryBoxDef
            {
                RootEntityId = id,
                RootPosition = spawnPoint.Position,
                DisplayOffset = new Vector3(0f, 1.2f, 0f)
            };

            _boxData.MysteryBoxes[id] = def;

            var runtime = new MysteryBoxRuntime
            {
                SpawnTime = Time.realtimeSinceStartup,
                SpawnPointIndex = spawnPointIndex
            };

            // Set up deactivation timer (2 minutes)
            runtime.DeactivationTimer = timer.Once(MysteryBoxActiveTime, () =>
            {
                DeactivateMysteryBox(id, spawnPointIndex);
            });

            _mysteryRuntime[id] = runtime;
            _spawnedBoxes[spawnPointIndex] = entity;

            return entity;
        }

        /// <summary>
        /// Deactivate (despawn) a Mystery Box after 2 minutes
        /// </summary>
        private void DeactivateMysteryBox(uint boxId, int spawnPointIndex)
        {
            // Clean up runtime
            if (_mysteryRuntime.TryGetValue(boxId, out var rt))
            {
                rt.DeactivationTimer?.Destroy();
                ClearMysteryBoxGuiForAll(rt);
                _mysteryRuntime.Remove(boxId);
            }

            // Clean up box definition
            _boxData.MysteryBoxes.Remove(boxId);

            // Find and kill the entity
            var entity = BaseNetworkable.serverEntities.Find(new NetworkableId(boxId)) as BaseEntity;
            if (entity != null && !entity.IsDestroyed)
            {
                entity.Kill();
            }

            // Update spawn point state
            if (spawnPointIndex >= 0 && spawnPointIndex < _boxData.SpawnPoints.Count)
            {
                _boxData.SpawnPoints[spawnPointIndex].IsActive = false;
            }

            _spawnedBoxes.Remove(spawnPointIndex);

            // Clear Buy UI and Countdown UI for all players near this box
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                if (_playerVisibleBox.TryGetValue(player.userID, out var currentId) && currentId == boxId)
                {
                    DestroyBuyUi(player);
                    DestroyCountdownUi(player);
                    _playerVisibleBox[player.userID] = 0;
                }
            }

            Puts($"[SimplePrefabEditor] Mystery Box {boxId} deactivated after {MysteryBoxActiveTime / 60f} minutes.");
        }

        /// <summary>
        /// Get the remaining time for a Mystery Box before deactivation
        /// </summary>
        private float GetRemainingTime(uint boxId)
        {
            if (!_mysteryRuntime.TryGetValue(boxId, out var rt))
                return 0f;

            float elapsed = Time.realtimeSinceStartup - rt.SpawnTime;
            float remaining = MysteryBoxActiveTime - elapsed;
            return Mathf.Max(0f, remaining);
        }

        #endregion

        #region Mystery Box Spin Logic (Reused)

        private void StartMysteryBoxSpin(uint boxId, BaseEntity root, BasePlayer user)
        {
            if (!_boxData.MysteryBoxes.TryGetValue(boxId, out var def))
                return;

            if (!_mysteryRuntime.TryGetValue(boxId, out var rt))
            {
                rt = new MysteryBoxRuntime();
                _mysteryRuntime[boxId] = rt;
            }

            rt.Busy = true;
            rt.ResultDropNetId = 0;
            rt.CurrentShortname = null;
            rt.ActivePlayers.Clear();

            // Only the user sees the GUI reel for now
            rt.ActivePlayers.Add(user.userID);
            ShowMysteryGui(user, boxId, "<color=#00ffff>Rolling...</color>", null);

            // Sequence of ticks for flicker -> slow
            float[] intervals = { 0.05f, 0.05f, 0.07f, 0.09f, 0.12f, 0.16f, 0.20f, 0.30f, 0.45f, 0.70f };

            float currentDelay = 0f;
            for (int i = 0; i < intervals.Length; i++)
            {
                float delay = currentDelay;
                bool isFinal = (i == intervals.Length - 1);
                timer.Once(delay, () =>
                {
                    if (!_boxData.MysteryBoxes.ContainsKey(boxId))
                        return;

                    RunMysteryBoxTick(boxId, isFinal, user);
                });
                currentDelay += intervals[i];
            }

            user.ChatMessage("<color=#00ffff>Mystery Box:</color> Rolling...");
        }

        private void RunMysteryBoxTick(uint boxId, bool isFinal, BasePlayer viewer)
        {
            if (!_boxData.MysteryBoxes.ContainsKey(boxId))
                return;
            if (!_mysteryRuntime.TryGetValue(boxId, out var rt))
                return;
            if (!rt.Busy)
                return;

            string shortname = GetRandomGunShortname();
            if (string.IsNullOrEmpty(shortname))
                return;

            rt.CurrentShortname = shortname;

            string imageKey = GetGunImageKey(shortname);
            string label = $"Mystery Box: {shortname}";

            foreach (var playerId in rt.ActivePlayers)
            {
                var p = BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
                if (p == null) continue;
                UpdateMysteryGui(p, boxId, label, imageKey);
            }

            var def = ItemManager.FindItemDefinition(shortname);
            if (def != null)
            {
                if (isFinal)
                {
                    SpawnMysteryBoxReward(boxId, def);
                }
                else
                {
                    // temporary drop each tick, quickly despawned
                    SpawnTemporaryGunDrop(boxId, def, 0.35f);
                }
            }
        }

        /// <summary>
        /// Drop a temporary gun item on top of the sphere that despawns after lifetime seconds.
        /// </summary>
        private void SpawnTemporaryGunDrop(uint boxId, ItemDefinition gunDef, float lifetime = 0.35f)
        {
            if (!_boxData.MysteryBoxes.TryGetValue(boxId, out var mbDef))
                return;

            Vector3 dropPos = mbDef.RootPosition + mbDef.DisplayOffset;

            var item = ItemManager.Create(gunDef, 1);
            if (item == null)
                return;

            var drop = item.Drop(dropPos, Vector3.zero) as DroppedItem;
            if (drop == null)
            {
                item.Remove();
                return;
            }

            timer.Once(lifetime, () =>
            {
                if (drop != null && !drop.IsDestroyed)
                    drop.Kill();

                if (item != null)
                    item.Remove();
            });
        }

        private void SpawnMysteryBoxReward(uint boxId, ItemDefinition gunDef)
        {
            if (!_boxData.MysteryBoxes.TryGetValue(boxId, out var mbDef))
                return;
            if (!_mysteryRuntime.TryGetValue(boxId, out var rt))
                return;

            // Final reward: drop right on top of the root
            Vector3 dropPos = mbDef.RootPosition + mbDef.DisplayOffset;

            var item = ItemManager.Create(gunDef, 1);
            if (item == null)
                return;

            var drop = item.Drop(dropPos, Vector3.zero) as DroppedItem;
            if (drop == null)
            {
                item.Remove();
                return;
            }

            rt.ResultDropNetId = drop.net?.ID.Value ?? 0UL;

            foreach (var playerId in rt.ActivePlayers)
            {
                var p = BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
                if (p == null) continue;
                p.ChatMessage($"<color=#00ffff>Mystery Box reward:</color> {gunDef.displayName.english} ({gunDef.shortname})");
                UpdateMysteryGui(p, boxId,
                    $"<color=#00ff00>{gunDef.displayName.english}</color>", GetGunImageKey(gunDef.shortname));
            }

            Puts($"[SimplePrefabEditor] Mystery Box {boxId} reward spawned: {gunDef.shortname} (drop NetID={rt.ResultDropNetId})");
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null || entity.net == null)
                return;

            // 1) Reward drop handling
            var drop = entity as DroppedItem;
            if (drop != null)
            {
                ulong dropId = drop.net.ID.Value;

                foreach (var kv in _mysteryRuntime)
                {
                    var boxId = kv.Key;
                    var rt = kv.Value;
                    if (rt.ResultDropNetId == dropId && rt.Busy)
                    {
                        rt.ResultDropNetId = 0;
                        rt.Busy = false;
                        Puts($"[SimplePrefabEditor] Mystery Box {boxId} reward collected/removed.");

                        ClearMysteryBoxGuiForAll(rt);
                        break;
                    }
                }

                return;
            }

            // 2) Root sphere handling: auto-unregister if root is destroyed
            var ent = entity as BaseEntity;
            if (ent == null)
                return;

            if (!string.Equals(ent.PrefabName, MysteryBoxRootPrefab, StringComparison.OrdinalIgnoreCase))
                return;

            uint id = (uint)ent.net.ID.Value;

            if (_boxData.MysteryBoxes.Remove(id))
            {
                Puts($"[SimplePrefabEditor] Mystery Box root entity destroyed, auto-unregistering box {id}.");
                SaveData();
            }

            if (_mysteryRuntime.TryGetValue(id, out var runtime))
            {
                ClearMysteryBoxGuiForAll(runtime);
                _mysteryRuntime.Remove(id);
            }

            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null) continue;
                if (_playerVisibleBox.TryGetValue(p.userID, out var currentId) && currentId == id)
                {
                    DestroyBuyUi(p);
                    _playerVisibleBox[p.userID] = 0;
                }
            }
        }

        /// <summary>
        /// Automatically registers newly spawned medieval large wood box entities as Mystery Boxes.
        /// </summary>
        private void OnEntitySpawned(BaseNetworkable entity)
        {
            // Ensure plugin is fully initialized
            if (_boxData == null || _mysteryRuntime == null)
                return;

            if (entity == null || entity.net == null)
                return;

            var baseEntity = entity as BaseEntity;
            if (baseEntity == null || baseEntity.IsDestroyed)
                return;

            if (!string.Equals(baseEntity.PrefabName, MysteryBoxRootPrefab, StringComparison.OrdinalIgnoreCase))
                return;

            uint id = (uint)baseEntity.net.ID.Value;

            // Skip if already registered
            if (_boxData.MysteryBoxes.ContainsKey(id))
                return;

            var def = new MysteryBoxDef
            {
                RootEntityId = id,
                RootPosition = baseEntity.transform.position,
                DisplayOffset = new Vector3(0f, 1.2f, 0f)
            };

            _boxData.MysteryBoxes[id] = def;
            _mysteryRuntime[id] = new MysteryBoxRuntime();

            // Use a short timer to batch saves when multiple boxes spawn simultaneously
            timer.Once(0.5f, () => SaveData());

            Puts($"[SimplePrefabEditor] Auto-registered newly spawned medieval box as Mystery Box: {baseEntity.ShortPrefabName} ({id})");
        }

        #endregion

        #region Mystery Box GUI (CUI + ImageLibrary)

        private string GetMysteryGuiName(uint boxId, ulong playerId)
        {
            return $"SPE.MysteryBox.{boxId}.{playerId}";
        }

        private void ShowMysteryGui(BasePlayer player, uint boxId, string label, string imageKey)
        {
            DestroyMysteryGui(player);

            var container = new CuiElementContainer();
            string panelName = GetMysteryGuiName(boxId, player.userID);

            var panel = new CuiPanel
            {
                Image = { Color = "0 0 0 0.7" },
                RectTransform =
                {
                    AnchorMin = "0.4 0.75",
                    AnchorMax = "0.6 0.95"
                },
                CursorEnabled = false
            };
            container.Add(panel, "Overlay", panelName);

            if (!string.IsNullOrEmpty(imageKey) && ImageLibrary != null)
            {
                var raw = new CuiRawImageComponent
                {
                    Png = ImageLibrary.Call("GetImage", imageKey) as string,
                    Color = "1 1 1 1"
                };

                var imageElement = new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panelName,
                    Components =
                    {
                        raw,
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.05 0.25",
                            AnchorMax = "0.95 0.95"
                        }
                    }
                };
                container.Add(imageElement);
            }

            var labelElement = new CuiLabel
            {
                Text =
                {
                    Text = label ?? "Mystery Box",
                    FontSize = 18,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 0 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.05 0.05",
                    AnchorMax = "0.95 0.25"
                }
            };
            container.Add(labelElement, panelName);

            CuiHelper.AddUi(player, container);
        }

        private void UpdateMysteryGui(BasePlayer player, uint boxId, string label, string imageKey)
        {
            ShowMysteryGui(player, boxId, label, imageKey);
        }

        private void DestroyMysteryGui(BasePlayer player)
        {
            if (player == null) return;

            foreach (var kv in _mysteryRuntime)
            {
                var panelName = GetMysteryGuiName(kv.Key, player.userID);
                CuiHelper.DestroyUi(player, panelName);
            }
        }

        private void DestroyMysteryGuiForPlayer(BasePlayer player)
        {
            DestroyMysteryGui(player);
        }

        private void ClearMysteryBoxGuiForAll(MysteryBoxRuntime rt)
        {
            if (rt == null) return;
            foreach (var playerId in rt.ActivePlayers)
            {
                var p = BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
                if (p == null) continue;
                DestroyMysteryGui(p);
            }
            rt.ActivePlayers.Clear();
        }

        #endregion

        #region Mystery Box Proximity BUY UI

        private string GetBuyUiName(uint boxId, ulong playerId)
        {
            return $"SPE.MysteryBox.Buy.{boxId}.{playerId}";
        }

        private void StartMysteryBoxProximityLoop()
        {
            _mboxProximityTimer?.Destroy();
            _mboxProximityTimer = timer.Every(0.25f, () =>
            {
                if (_boxData == null || _boxData.MysteryBoxes == null || _boxData.MysteryBoxes.Count == 0)
                    return;

                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null || !player.IsConnected) continue;

                    uint currentUiBoxId = 0;
                    _playerVisibleBox.TryGetValue(player.userID, out currentUiBoxId);

                    uint closestBoxId = 0;
                    float closestDistSqr = float.MaxValue;

                    foreach (var kv in _boxData.MysteryBoxes)
                    {
                        var boxId = kv.Key;
                        var def = kv.Value;
                        Vector3 pos = def.RootPosition;
                        float distSqr = (player.transform.position - pos).sqrMagnitude;
                        if (distSqr <= MysteryBoxProximityRadius * MysteryBoxProximityRadius &&
                            distSqr < closestDistSqr)
                        {
                            closestDistSqr = distSqr;
                            closestBoxId = boxId;
                        }
                    }

                    // If no box in range but UI visible -> hide
                    if (closestBoxId == 0)
                    {
                        if (currentUiBoxId != 0)
                        {
                            DestroyBuyUi(player);
                            DestroyCountdownUi(player);
                            _playerVisibleBox[player.userID] = 0;
                        }
                        continue;
                    }

                    // Update countdown UI if box has a timer
                    float remaining = GetRemainingTime(closestBoxId);
                    if (remaining > 0)
                    {
                        ShowCountdownUi(player, closestBoxId, remaining);
                    }
                    else
                    {
                        DestroyCountdownUi(player);
                    }

                    // If closest box is same as current UI, do nothing for buy UI
                    if (currentUiBoxId == closestBoxId)
                        continue;

                    // Otherwise, switch UI to new closest box
                    DestroyBuyUi(player);
                    ShowBuyUi(player, closestBoxId);
                    _playerVisibleBox[player.userID] = closestBoxId;
                }
            });
        }

        private void ShowBuyUi(BasePlayer player, uint boxId)
        {
            if (player == null) return;
            if (!_boxData.MysteryBoxes.ContainsKey(boxId))
                return;

            string panelName = GetBuyUiName(boxId, player.userID);

            var container = new CuiElementContainer();
            var panel = new CuiPanel
            {
                Image = { Color = "0 0 0 0.7" },
                RectTransform =
                {
                    AnchorMin = "0.4 0.02",
                    AnchorMax = "0.6 0.12"
                },
                CursorEnabled = false
            };
            container.Add(panel, "Overlay", panelName);

            // Label
            var label = new CuiLabel
            {
                Text =
                {
                    Text = "Mystery Box - Click BUY to roll",
                    FontSize = 15,
                    Align = TextAnchor.MiddleLeft,
                    Color = "1 1 0 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.05 0.15",
                    AnchorMax = "0.75 0.85"
                }
            };
            container.Add(label, panelName);

            // BUY button -> console command spe.mbox.buy <boxId>
            var button = new CuiButton
            {
                Button =
                {
                    Color = "0.15 0.6 0.15 1",
                    Command = $"spe.mbox.buy {boxId}"
                },
                RectTransform =
                {
                    AnchorMin = "0.78 0.15",
                    AnchorMax = "0.95 0.85"
                },
                Text =
                {
                    Text = "BUY",
                    FontSize = 16,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                }
            };
            container.Add(button, panelName);

            CuiHelper.AddUi(player, container);
        }

        private void DestroyBuyUi(BasePlayer player)
        {
            if (player == null) return;

            uint currentBoxId = 0;
            _playerVisibleBox.TryGetValue(player.userID, out currentBoxId);

            // If we know which box UI was visible, destroy that one
            if (currentBoxId != 0)
            {
                string panelName = GetBuyUiName(currentBoxId, player.userID);
                CuiHelper.DestroyUi(player, panelName);
                return;
            }

            // Otherwise, brute-force a cleanup
            for (uint fakeId = 1; fakeId <= 2000; fakeId++)
            {
                string name = GetBuyUiName(fakeId, player.userID);
                CuiHelper.DestroyUi(player, name);
            }
        }

        private string GetCountdownUiName(uint boxId, ulong playerId)
        {
            return $"SPE.MysteryBox.Countdown.{boxId}.{playerId}";
        }

        /// <summary>
        /// Show or update the countdown timer UI for a Mystery Box
        /// </summary>
        private void ShowCountdownUi(BasePlayer player, uint boxId, float remainingSeconds)
        {
            if (player == null) return;
            if (!_boxData.MysteryBoxes.ContainsKey(boxId))
                return;

            DestroyCountdownUi(player);

            string panelName = GetCountdownUiName(boxId, player.userID);

            int minutes = (int)(remainingSeconds / 60f);
            int seconds = (int)(remainingSeconds % 60f);
            string timeText = $"{minutes:00}:{seconds:00}";

            var container = new CuiElementContainer();
            var panel = new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.8" },
                RectTransform =
                {
                    AnchorMin = "0.4 0.13",
                    AnchorMax = "0.6 0.20"
                },
                CursorEnabled = false
            };
            container.Add(panel, "Overlay", panelName);

            // Timer label
            var label = new CuiLabel
            {
                Text =
                {
                    Text = $"<color=#ff6600>⏱ Mystery Box Timer: {timeText}</color>",
                    FontSize = 16,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform =
                {
                    AnchorMin = "0.05 0.1",
                    AnchorMax = "0.95 0.9"
                }
            };
            container.Add(label, panelName);

            CuiHelper.AddUi(player, container);
            _playerCountdownBox[player.userID] = boxId;
        }

        private void DestroyCountdownUi(BasePlayer player)
        {
            if (player == null) return;

            uint currentBoxId = 0;
            _playerCountdownBox.TryGetValue(player.userID, out currentBoxId);

            if (currentBoxId != 0)
            {
                string panelName = GetCountdownUiName(currentBoxId, player.userID);
                CuiHelper.DestroyUi(player, panelName);
            }

            // Also clean up any known mystery box countdown UIs
            if (_boxData != null && _boxData.MysteryBoxes != null)
            {
                foreach (var boxId in _boxData.MysteryBoxes.Keys)
                {
                    string name = GetCountdownUiName(boxId, player.userID);
                    CuiHelper.DestroyUi(player, name);
                }
            }

            _playerCountdownBox.Remove(player.userID);
        }

        [ConsoleCommand("spe.mbox.buy")]
        private void ConsoleBuyMysteryBox(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (arg.Args == null || arg.Args.Length < 1) return;
            if (!uint.TryParse(arg.Args[0], out var boxId)) return;

            if (!_boxData.MysteryBoxes.TryGetValue(boxId, out var def))
            {
                player.ChatMessage("<color=#ffcc00>This Mystery Box no longer exists.</color>");
                return;
            }

            // Check distance
            float distSqr = (player.transform.position - def.RootPosition).sqrMagnitude;
            if (distSqr > MysteryBoxProximityRadius * MysteryBoxProximityRadius)
            {
                player.ChatMessage("<color=#ffcc00>You are too far from the Mystery Box.</color>");
                return;
            }

            if (!_mysteryRuntime.TryGetValue(boxId, out var rt))
            {
                rt = new MysteryBoxRuntime();
                _mysteryRuntime[boxId] = rt;
            }

            if (rt.Busy)
            {
                player.ChatMessage("<color=#ffcc00>The Mystery Box is busy, wait for the current roll to finish.</color>");
                return;
            }

            if (!ChargeMysteryBoxUse(player, boxId))
            {
                player.ChatMessage("<color=#ff0000>You cannot afford the Mystery Box.</color>");
                return;
            }

            // Find the root entity
            var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(def.RootEntityId)) as BaseEntity;
            if (ent == null || ent.IsDestroyed)
            {
                player.ChatMessage("<color=#ff0000>The Mystery Box root entity is missing.</color>");
                return;
            }

            // Close Buy UI for this player now
            DestroyBuyUi(player);
            _playerVisibleBox[player.userID] = 0;

            StartMysteryBoxSpin(boxId, ent, player);
        }

        #endregion

        #region Paintable Helpers

        private void TryCapturePaintableUrl(BaseEntity ent, ref FullEntityDef def)
        {
            if (ent == null || ent.net == null) return;
            if (!(ent is Signage) && !(ent is PhotoFrame) && !(ent is CarvablePumpkin))
                return;

            if (!_signUrls.TryGetValue(ent.net.ID.Value, out var url) || string.IsNullOrEmpty(url))
                return;

            def.IsPaintable = true;
            def.ImageUrl = url;
            def.TextureIndex = 0;
        }

        private void TryApplyPaintableUrl(BasePlayer player, BaseEntity ent, FullEntityDef def)
        {
            if (SignArtist == null || string.IsNullOrEmpty(def.ImageUrl))
                return;

            try
            {
                if (ent is Signage signage)
                    SignArtist.Call("API_SkinSign", player, signage, def.ImageUrl, false, def.TextureIndex);
                else if (ent is PhotoFrame frame)
                    SignArtist.Call("API_SkinPhotoFrame", player, frame, def.ImageUrl, false);
                else if (ent is CarvablePumpkin pumpkin)
                    SignArtist.Call("API_SkinPumpkin", player, pumpkin, def.ImageUrl, false);
            }
            catch (Exception ex)
            {
                PrintWarning($"[SimplePrefabEditor] Failed to apply URL '{def.ImageUrl}' on {ent.ShortPrefabName}: {ex}");
            }
        }

        #endregion

        #region OnPlayerInput (Editing / Group)

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || !player.IsConnected || input == null)
                return;

            if (!editing.TryGetValue(player.userID, out var state))
                return;

            if (!state.KeyboardMode)
                return;

            var entity = state.Entity;
            if (entity == null || entity.IsDestroyed || entity.transform == null)
                return;

            if (!input.IsDown(BUTTON.FIRE_PRIMARY))
                return;

            var moveDelta = Vector3.zero;
            var rotDelta = Vector3.zero;
            var scaleDelta = Vector3.zero;

            float dt = Time.deltaTime;
            float sens = Mathf.Max(0.0001f, state.Sensitivity);

            float moveSpeed = sens * 4f * dt;
            if (input.IsDown(BUTTON.FORWARD))  moveDelta += player.eyes.BodyForward();
            if (input.IsDown(BUTTON.BACKWARD)) moveDelta -= player.eyes.BodyForward();
            if (input.IsDown(BUTTON.LEFT))     moveDelta -= player.eyes.BodyRight();
            if (input.IsDown(BUTTON.RIGHT))    moveDelta += player.eyes.BodyRight();
            if (input.IsDown(BUTTON.JUMP))     moveDelta += Vector3.up;
            if (input.IsDown(BUTTON.DUCK))     moveDelta += Vector3.down;
            moveDelta *= moveSpeed;

            float baseRot = sens * 20f;
            float yawSpeed   = baseRot * dt;
            float pitchSpeed = baseRot * dt;
            float rollSpeed  = baseRot * dt;

            if (input.IsDown(BUTTON.RELOAD))         rotDelta.y += yawSpeed;
            if (input.IsDown(BUTTON.FIRE_SECONDARY)) rotDelta.y -= yawSpeed;

            if (input.IsDown(BUTTON.SPRINT))
            {
                if (input.IsDown(BUTTON.FORWARD))  rotDelta.x += pitchSpeed;
                if (input.IsDown(BUTTON.BACKWARD)) rotDelta.x -= pitchSpeed;
                if (input.IsDown(BUTTON.LEFT))     rotDelta.z += rollSpeed;
                if (input.IsDown(BUTTON.RIGHT))    rotDelta.z -= rollSpeed;
            }

            float baseScale = sens * 0.5f * dt;
            if (input.IsDown(BUTTON.SPRINT))
            {
                scaleDelta += new Vector3(baseScale, baseScale, baseScale);
                if (input.IsDown(BUTTON.FIRE_SECONDARY))
                    scaleDelta -= new Vector3(2f * baseScale, 2f * baseScale, 2f * baseScale);
            }

            if (moveDelta == Vector3.zero && rotDelta == Vector3.zero && scaleDelta == Vector3.zero)
                return;

            ApplyEdits(state, moveDelta, rotDelta, scaleDelta);
        }

        private void ApplyEdits(EditState state, Vector3 moveWorld, Vector3 rotEulerDegrees, Vector3 scaleDeltaAxes)
        {
            if (state == null) return;
            var entity = state.Entity;
            if (entity == null || entity.IsDestroyed || entity.transform == null)
                return;

            bool group =
                state.GroupEditEnabled &&
                state.MysteryBoxEntities != null &&
                state.MysteryBoxEntities.Count > 0 &&
                state.GroupOrigin != null &&
                !state.GroupOrigin.IsDestroyed &&
                state.GroupOrigin.transform != null;

            if (!group)
            {
                ApplyEditsSingle(state, moveWorld, rotEulerDegrees, scaleDeltaAxes);
                return;
            }

            var origin = state.GroupOrigin;
            var ot = origin.transform;

            Vector3 originPos = ot.position + moveWorld;
            Quaternion originRot = ot.rotation;
            if (rotEulerDegrees != Vector3.zero)
            {
                var euler = originRot.eulerAngles + rotEulerDegrees;
                originRot = Quaternion.Euler(euler);
            }

            float uniformScaleFactor = 1f;
            if (scaleDeltaAxes != Vector3.zero)
            {
                float delta = scaleDeltaAxes.x;
                uniformScaleFactor = 1f + delta;
                uniformScaleFactor = Mathf.Clamp(uniformScaleFactor, 0.01f, 100f);
            }

            ot.position = originPos;
            ot.rotation = originRot;
            ot.localScale *= uniformScaleFactor;
            origin.networkEntityScale = true;
            origin.SendNetworkUpdate();

            if (state.Entity == origin)
            {
                state.Position = originPos;
                state.Rotation = originRot;
                state.Scale    = ot.localScale;
            }

            Quaternion deltaRot = (rotEulerDegrees != Vector3.zero)
                ? Quaternion.Euler(rotEulerDegrees)
                : Quaternion.identity;

            foreach (var id in state.MysteryBoxEntities)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                if (ent == null || ent.IsDestroyed || ent.transform == null)
                    continue;
                if (ent == origin)
                    continue;

                var t = ent.transform;

                Vector3 worldPos = t.position + moveWorld;

                if (rotEulerDegrees != Vector3.zero)
                {
                    Vector3 fromOrigin = t.position - originPos;
                    fromOrigin = deltaRot * fromOrigin;
                    worldPos = originPos + fromOrigin;
                }

                t.position = worldPos;

                ent.SendNetworkUpdate();
            }
        }

        private void ApplyEditsSingle(EditState state, Vector3 moveWorld, Vector3 rotEulerDegrees, Vector3 scaleDeltaAxes)
        {
            var entity = state.Entity;
            if (entity == null || entity.IsDestroyed || entity.transform == null)
                return;

            var t = entity.transform;

            state.Position += moveWorld;
            t.position = state.Position;

            if (rotEulerDegrees != Vector3.zero)
            {
                var currentEuler = state.Rotation.eulerAngles + rotEulerDegrees;
                state.Rotation = Quaternion.Euler(currentEuler);
            }
            t.rotation = state.Rotation;

            bool scaleChanged = false;
            if (scaleDeltaAxes != Vector3.zero)
            {
                var newScale = state.Scale + scaleDeltaAxes;

                const float minScale = 0.01f;
                const float maxScale = 100f;

                newScale.x = Mathf.Clamp(newScale.x, minScale, maxScale);
                newScale.y = Mathf.Clamp(newScale.y, minScale, maxScale);
                newScale.z = Mathf.Clamp(newScale.z, minScale, maxScale);

                if (newScale != state.Scale)
                {
                    state.Scale = newScale;
                    scaleChanged = true;
                }
            }

            if (scaleChanged)
                entity.networkEntityScale = true;

            t.localScale = state.Scale;
            entity.SendNetworkUpdate();
        }

        #endregion

        #region GUI & Console Buttons

        private void OpenGui(BasePlayer player, EditState state)
        {
            CloseGui(player);

            state.GuiOpen = true;
            state.KeyboardMode = false;

            var container = new CuiElementContainer();

            var panel = new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.9" },
                RectTransform = { AnchorMin = "0.7 0.2", AnchorMax = "0.98 0.8" },
                CursorEnabled = true
            };
            container.Add(panel, "Overlay", CuiPanelName);

            string itemName = "None";
            string itemId = "";
            if (state.Entity != null && !state.Entity.IsDestroyed)
            {
                itemName = state.Entity.ShortPrefabName ?? "Unknown";
                if (state.Entity.net != null)
                    itemId = state.Entity.net.ID.Value.ToString();
            }

            AddLabel(container, CuiPanelName, "Simple Prefab Editor", 18,
                "0.05 0.90", "0.60 0.98", "1 1 1 1");

            AddLabel(container, CuiPanelName,
                $"Selected: {itemName}{(string.IsNullOrEmpty(itemId) ? "" : $" ({itemId})")}",
                12, "0.05 0.84", "0.95 0.90", "0.9 0.9 0.9 1");

            AddButton(container, CuiPanelName, "Prev", "0.62 0.90", "0.70 0.98",
                "0.2 0.2 0.2 1", "0.3 0.3 0.3 1", "simpleprefabeditor.select.prev");
            AddButton(container, CuiPanelName, "Next", "0.72 0.90", "0.80 0.98",
                "0.2 0.2 0.2 1", "0.3 0.3 0.3 1", "simpleprefabeditor.select.next");
            AddButton(container, CuiPanelName, "Duplicate", "0.82 0.90", "0.98 0.98",
                "0.3 0.3 0.1 1", "0.5 0.5 0.2 1", "simpleprefabeditor.duplicate");

            AddLabel(container, CuiPanelName,
                $"Sensitivity: {state.Sensitivity:F3}",
                14, "0.05 0.78", "0.90 0.84", "0.8 0.8 0.8 1");
            AddButton(container, CuiPanelName, "X", "0.94 0.78", "0.98 0.84",
                "0.6 0.1 0.1 1", "0.8 0.2 0.2 1", "simpleprefabeditor.ui.close");

            // Move
            AddLabel(container, CuiPanelName, "Move", 14, "0.05 0.71", "0.30 0.76", "1 1 1 1");
            AddButton(container, CuiPanelName, "↑Y", "0.08 0.61", "0.16 0.69",
                "0.2 0.4 0.2 1", "0.3 0.6 0.3 1", "simpleprefabeditor.move.y.up");
            AddButton(container, CuiPanelName, "↓Y", "0.08 0.53", "0.16 0.61",
                "0.2 0.4 0.2 1", "0.3 0.6 0.3 1", "simpleprefabeditor.move.y.down");
            AddButton(container, CuiPanelName, "←X", "0.02 0.57", "0.10 0.65",
                "0.2 0.4 0.2 1", "0.3 0.6 0.3 1", "simpleprefabeditor.move.x.neg");
            AddButton(container, CuiPanelName, "→X", "0.14 0.57", "0.22 0.65",
                "0.2 0.4 0.2 1", "0.3 0.6 0.3 1", "simpleprefabeditor.move.x.pos");
            AddButton(container, CuiPanelName, "+Z", "0.26 0.61", "0.34 0.69",
                "0.2 0.4 0.2 1", "0.3 0.6 0.3 1", "simpleprefabeditor.move.z.pos");
            AddButton(container, CuiPanelName, "-Z", "0.26 0.53", "0.34 0.61",
                "0.2 0.4 0.2 1", "0.3 0.6 0.3 1", "simpleprefabeditor.move.z.neg");

            // Rotate
            AddLabel(container, CuiPanelName, "Rotate", 14, "0.05 0.44", "0.30 0.49", "1 1 1 1");
            AddButton(container, CuiPanelName, "Yaw+", "0.02 0.35", "0.10 0.43",
                "0.2 0.2 0.4 1", "0.3 0.3 0.6 1", "simpleprefabeditor.rot.y.pos");
            AddButton(container, CuiPanelName, "Yaw-", "0.10 0.35", "0.18 0.43",
                "0.2 0.2 0.4 1", "0.3 0.3 0.6 1", "simpleprefabeditor.rot.y.neg");
            AddButton(container, CuiPanelName, "Pitch+", "0.22 0.39", "0.30 0.47",
                "0.2 0.2 0.4 1", "0.3 0.3 0.6 1", "simpleprefabeditor.rot.x.pos");
            AddButton(container, CuiPanelName, "Pitch-", "0.22 0.31", "0.30 0.39",
                "0.2 0.2 0.4 1", "0.3 0.3 0.6 1", "simpleprefabeditor.rot.x.neg");
            AddButton(container, CuiPanelName, "Roll+", "0.34 0.39", "0.42 0.47",
                "0.2 0.2 0.4 1", "0.3 0.3 0.6 1", "simpleprefabeditor.rot.z.pos");
            AddButton(container, CuiPanelName, "Roll-", "0.34 0.31", "0.42 0.39",
                "0.2 0.2 0.4 1", "0.3 0.3 0.6 1", "simpleprefabeditor.rot.z.neg");

            // Scale
            AddLabel(container, CuiPanelName, "Scale", 14, "0.05 0.24", "0.30 0.29", "1 1 1 1");
            AddButton(container, CuiPanelName, "Scale X+", "0.02 0.17", "0.14 0.23",
                "0.4 0.2 0.2 1", "0.6 0.3 0.3 1", "simpleprefabeditor.scale.x.pos");
            AddButton(container, CuiPanelName, "Scale X-", "0.14 0.17", "0.26 0.23",
                "0.4 0.2 0.2 1", "0.6 0.3 0.3 1", "simpleprefabeditor.scale.x.neg");
            AddButton(container, CuiPanelName, "Scale Y+", "0.02 0.10", "0.14 0.16",
                "0.4 0.2 0.2 1", "0.6 0.3 0.3 1", "simpleprefabeditor.scale.y.pos");
            AddButton(container, CuiPanelName, "Scale Y-", "0.14 0.10", "0.26 0.16",
                "0.4 0.2 0.2 1", "0.6 0.3 0.3 1", "simpleprefabeditor.scale.y.neg");
            AddButton(container, CuiPanelName, "Scale Z+", "0.02 0.03", "0.14 0.09",
                "0.4 0.2 0.2 1", "0.6 0.3 0.3 1", "simpleprefabeditor.scale.z.pos");
            AddButton(container, CuiPanelName, "Scale Z-", "0.14 0.03", "0.26 0.09",
                "0.4 0.2 0.2 1", "0.6 0.3 0.3 1", "simpleprefabeditor.scale.z.neg");
            AddButton(container, CuiPanelName, "Uniform +", "0.30 0.10", "0.42 0.16",
                "0.4 0.3 0.1 1", "0.6 0.45 0.15 1", "simpleprefabeditor.scale.u.pos");
            AddButton(container, CuiPanelName, "Uniform -", "0.30 0.03", "0.42 0.09",
                "0.4 0.3 0.1 1", "0.6 0.45 0.15 1", "simpleprefabeditor.scale.u.neg");

            CuiHelper.AddUi(player, container);
        }

        private void CloseGui(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, CuiPanelName);

            if (editing.TryGetValue(player.userID, out var state))
            {
                state.GuiOpen = false;
                state.KeyboardMode = true;
            }
        }

        [ConsoleCommand("simpleprefabeditor.ui.close")]
        private void ConsoleCloseUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CloseGui(player);
        }

        [ConsoleCommand("simpleprefabeditor.select.prev")]
        private void ConsoleSelectPrev(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!editing.TryGetValue(player.userID, out var state)) return;
            if (state.MysteryBoxEntities.Count == 0) return;

            var list = new List<ulong>(state.MysteryBoxEntities);
            if (list.Count == 0) return;

            ulong currentId = state.Entity?.net?.ID.Value ?? 0;
            int idx = list.FindIndex(id => id == currentId);
            if (idx == -1) idx = 0;

            idx = (idx - 1 + list.Count) % list.Count;
            SelectById(player, state, list[idx]);
        }

        [ConsoleCommand("simpleprefabeditor.select.next")]
        private void ConsoleSelectNext(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!editing.TryGetValue(player.userID, out var state)) return;
            if (state.MysteryBoxEntities.Count == 0) return;

            var list = new List<ulong>(state.MysteryBoxEntities);
            if (list.Count == 0) return;

            ulong currentId = state.Entity?.net?.ID.Value ?? 0;
            int idx = list.FindIndex(id => id == currentId);
            if (idx == -1) idx = -1;

            idx = (idx + 1) % list.Count;
            SelectById(player, state, list[idx]);
        }

        [ConsoleCommand("simpleprefabeditor.duplicate")]
        private void ConsoleDuplicate(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (!editing.TryGetValue(player.userID, out var state) ||
                state.Entity == null || state.Entity.IsDestroyed || state.Entity.transform == null)
            {
                player.ChatMessage("<color=#ffcc00>No active entity to duplicate.</color>");
                return;
            }

            var src = state.Entity;
            if (string.IsNullOrEmpty(src.PrefabName))
            {
                player.ChatMessage("<color=#ff0000>Cannot duplicate: no valid PrefabName.</color>");
                return;
            }

            var pos   = src.transform.position;
            var rot   = src.transform.rotation;
            var scale = src.transform.localScale;

            BaseEntity clone;
            try
            {
                clone = GameManager.server.CreateEntity(src.PrefabName, pos, rot, true);
            }
            catch
            {
                player.ChatMessage("<color=#ff0000>Failed to duplicate entity.</color>");
                return;
            }

            if (clone == null)
            {
                player.ChatMessage("<color=#ff0000>Failed to duplicate entity (null).</color>");
                return;
            }

            clone.Spawn();
            clone.transform.position   = pos;
            clone.transform.rotation   = rot;
            clone.transform.localScale = scale;
            clone.networkEntityScale   = true;
            clone.SendNetworkUpdate();

            if (clone.net != null)
                state.MysteryBoxEntities.Add(clone.net.ID.Value);

            state.Entity   = clone;
            state.Position = clone.transform.position;
            state.Rotation = clone.transform.rotation;
            state.Scale    = clone.transform.localScale;

            player.ChatMessage(
                $"<color=#00ff00>Duplicated & selected:</color> {clone.ShortPrefabName} ({clone.net?.ID.Value})");
        }

        [ConsoleCommand("simpleprefabeditor.move.y.up")]
        private void ConsoleMoveYUp(ConsoleSystem.Arg arg) => OnGuiMove(arg, Vector3.up);
        [ConsoleCommand("simpleprefabeditor.move.y.down")]
        private void ConsoleMoveYDown(ConsoleSystem.Arg arg) => OnGuiMove(arg, Vector3.down);
        [ConsoleCommand("simpleprefabeditor.move.x.pos")]
        private void ConsoleMoveXPos(ConsoleSystem.Arg arg) => OnGuiMove(arg, Vector3.right);
        [ConsoleCommand("simpleprefabeditor.move.x.neg")]
        private void ConsoleMoveXNeg(ConsoleSystem.Arg arg) => OnGuiMove(arg, Vector3.left);
        [ConsoleCommand("simpleprefabeditor.move.z.pos")]
        private void ConsoleMoveZPos(ConsoleSystem.Arg arg) => OnGuiMove(arg, Vector3.forward);
        [ConsoleCommand("simpleprefabeditor.move.z.neg")]
        private void ConsoleMoveZNeg(ConsoleSystem.Arg arg) => OnGuiMove(arg, Vector3.back);

        [ConsoleCommand("simpleprefabeditor.rot.y.pos")]
        private void ConsoleRotYPos(ConsoleSystem.Arg arg) => OnGuiRotate(arg, new Vector3(0f, 1f, 0f));
        [ConsoleCommand("simpleprefabeditor.rot.y.neg")]
        private void ConsoleRotYNeg(ConsoleSystem.Arg arg) => OnGuiRotate(arg, new Vector3(0f, -1f, 0f));
        [ConsoleCommand("simpleprefabeditor.rot.x.pos")]
        private void ConsoleRotXPos(ConsoleSystem.Arg arg) => OnGuiRotate(arg, new Vector3(1f, 0f, 0f));
        [ConsoleCommand("simpleprefabeditor.rot.x.neg")]
        private void ConsoleRotXNeg(ConsoleSystem.Arg arg) => OnGuiRotate(arg, new Vector3(-1f, 0f, 0f));
        [ConsoleCommand("simpleprefabeditor.rot.z.pos")]
        private void ConsoleRotZPos(ConsoleSystem.Arg arg) => OnGuiRotate(arg, new Vector3(0f, 0f, 1f));
        [ConsoleCommand("simpleprefabeditor.rot.z.neg")]
        private void ConsoleRotZNeg(ConsoleSystem.Arg arg) => OnGuiRotate(arg, new Vector3(0f, 0f, -1f));

        [ConsoleCommand("simpleprefabeditor.scale.x.pos")]
        private void ConsoleScaleXPos(ConsoleSystem.Arg arg) => OnGuiScale(arg, new Vector3(1f, 0f, 0f));
        [ConsoleCommand("simpleprefabeditor.scale.x.neg")]
        private void ConsoleScaleXNeg(ConsoleSystem.Arg arg) => OnGuiScale(arg, new Vector3(-1f, 0f, 0f));
        [ConsoleCommand("simpleprefabeditor.scale.y.pos")]
        private void ConsoleScaleYPos(ConsoleSystem.Arg arg) => OnGuiScale(arg, new Vector3(0f, 1f, 0f));
        [ConsoleCommand("simpleprefabeditor.scale.y.neg")]
        private void ConsoleScaleYNeg(ConsoleSystem.Arg arg) => OnGuiScale(arg, new Vector3(0f, -1f, 0f));
        [ConsoleCommand("simpleprefabeditor.scale.z.pos")]
        private void ConsoleScaleZPos(ConsoleSystem.Arg arg) => OnGuiScale(arg, new Vector3(0f, 0f, 1f));
        [ConsoleCommand("simpleprefabeditor.scale.z.neg")]
        private void ConsoleScaleZNeg(ConsoleSystem.Arg arg) => OnGuiScale(arg, new Vector3(0f, 0f, -1f));
        [ConsoleCommand("simpleprefabeditor.scale.u.pos")]
        private void ConsoleScaleUniformPos(ConsoleSystem.Arg arg) => OnGuiScale(arg, new Vector3(1f, 1f, 1f));
        [ConsoleCommand("simpleprefabeditor.scale.u.neg")]
        private void ConsoleScaleUniformNeg(ConsoleSystem.Arg arg) => OnGuiScale(arg, new Vector3(-1f, -1f, -1f));

        private void OnGuiMove(ConsoleSystem.Arg arg, Vector3 direction)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!editing.TryGetValue(player.userID, out var state) ||
                state.Entity == null || state.Entity.IsDestroyed || state.Entity.transform == null)
                return;

            float step = state.Sensitivity;
            Vector3 move = direction.normalized * step;
            ApplyEdits(state, move, Vector3.zero, Vector3.zero);
        }

        private void OnGuiRotate(ConsoleSystem.Arg arg, Vector3 axis)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!editing.TryGetValue(player.userID, out var state) ||
                state.Entity == null || state.Entity.IsDestroyed || state.Entity.transform == null)
                return;

            float stepDegrees = Mathf.Clamp(state.Sensitivity * 20f, 1f, 90f);
            Vector3 rot = axis.normalized * stepDegrees;
            ApplyEdits(state, Vector3.zero, rot, Vector3.zero);
        }

        private void OnGuiScale(ConsoleSystem.Arg arg, Vector3 axis)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!editing.TryGetValue(player.userID, out var state) ||
                state.Entity == null || state.Entity.IsDestroyed || state.Entity.transform == null)
                return;

            float stepScale = Mathf.Clamp(state.Sensitivity * 0.5f, 0.01f, 5f);
            Vector3 delta = new Vector3(axis.x * stepScale, axis.y * stepScale, axis.z * stepScale);
            ApplyEdits(state, Vector3.zero, Vector3.zero, delta);
        }

        #endregion

        #region Low-Level Helpers

        private void CacheReflection()
        {
            _miUpdateHasPower = typeof(IOEntity).GetMethod(
                "UpdateHasPower",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(int), typeof(int) },
                null
            );
            if (_miUpdateHasPower == null)
                PrintWarning("[SimplePrefabEditor] Could not find IOEntity.UpdateHasPower; using Flags.On only.");
        }

        private EditState GetOrCreateState(BasePlayer player)
        {
            if (!editing.TryGetValue(player.userID, out var state))
            {
                state = new EditState();
                editing[player.userID] = state;
            }
            return state;
        }

        private bool HasPermission(BasePlayer player)
        {
            if (player == null) return false;
            if (!permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                PrintToChat(player, "<color=#ff0000>No permission.</color>");
                return false;
            }
            return true;
        }

        private BaseEntity GetLookAtEntity(BasePlayer player, float maxDistance = 30f)
        {
            if (player == null) return null;

            var src = player.eyes.position;
            var dir = player.eyes.BodyForward();

            RaycastHit hit;
            if (!Physics.Raycast(src, dir, out hit, maxDistance, ~0))
                return null;

            var ent = hit.GetEntity();
            if (ent == null && hit.collider != null)
                ent = hit.collider.GetComponentInParent<BaseEntity>();

            return ent;
        }

        private void SelectById(BasePlayer player, EditState state, ulong id)
        {
            var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
            if (ent == null || ent.IsDestroyed || ent.transform == null)
            {
                player.ChatMessage($"Entity {id} is missing/destroyed.");
                return;
            }

            state.Entity   = ent;
            state.Position = ent.transform.position;
            state.Rotation = ent.transform.rotation;
            state.Scale    = ent.transform.localScale;

            player.ChatMessage($"Selected {ent.ShortPrefabName} ({id})");
        }

        private bool ForceLightOn(BaseEntity entity)
        {
            if (entity == null || entity.IsDestroyed)
                return false;

            bool changed = false;

            var io = entity.GetComponent<IOEntity>();
            if (io != null)
            {
                try
                {
                    if (_miUpdateHasPower != null)
                        _miUpdateHasPower.Invoke(io, new object[] { 1, 0 });
                }
                catch { }

                if (!io.HasFlag(BaseEntity.Flags.On))
                {
                    io.SetFlag(BaseEntity.Flags.On, true);
                    io.SendNetworkUpdate();
                    changed = true;
                }
                return changed;
            }

            var oven = entity as BaseOven;
            if (oven != null)
            {
                if (!oven.HasFlag(BaseEntity.Flags.On))
                {
                    oven.SetFlag(BaseEntity.Flags.On, true);
                    oven.SendNetworkUpdate();
                    changed = true;
                }
                return changed;
            }

            if (!entity.HasFlag(BaseEntity.Flags.On))
            {
                entity.SetFlag(BaseEntity.Flags.On, true);
                entity.SendNetworkUpdate();
                changed = true;
            }
            return changed;
        }

        private void AddLabel(CuiElementContainer container, string parent, string text,
            int fontSize, string anchorMin, string anchorMax, string color)
        {
            var label = new CuiLabel
            {
                Text =
                {
                    Text = text,
                    FontSize = fontSize,
                    Align = TextAnchor.MiddleLeft,
                    Color = color
                },
                RectTransform =
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax
                }
            };
            container.Add(label, parent);
        }

        private void AddButton(CuiElementContainer container, string parent, string text,
            string anchorMin, string anchorMax, string color, string colorHover, string command)
        {
            var button = new CuiButton
            {
                Button =
                {
                    Color = color,
                    Command = command
                },
                RectTransform =
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax
                },
                Text =
                {
                    Text = text,
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                }
            };
            container.Add(button, parent);
        }

        #endregion
    }
}
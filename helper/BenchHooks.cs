using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;

namespace EmbervilleDiagnostic
{
    // Entry points called by the injected hooks. Benchmark automation stays opt-in.
    public static class BenchHooks
    {
        public static bool Has(string arg)
        {
            return new[]
            {
                "-diagnostic-high-load",
                "-fix-progress",
                "-fix-inventory",
                "-fix-wfc",
                "-fix-batch-tiles",
                "-fix-map-images",
                "-fix-prop-cache",
                "-fix-quick-fade",
                "-fix-light-map-data",
                "-fix-region-prefabs"
            }.Contains(arg) || Array.IndexOf(Environment.GetCommandLineArgs(), arg) >= 0;
        }

        public static bool InventoryEnabled()
        {
            return Has("-fix-inventory");
        }

        public static string Arg(string key, string fallback)
        {
            var a = Environment.GetCommandLineArgs();
            int n = Array.IndexOf(a, key);
            return n >= 0 && n + 1 < a.Length ? a[n + 1] : fallback;
        }

        public static readonly string Output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EmbervillePatch", "Logs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + System.Diagnostics.Process.GetCurrentProcess().Id);
        static readonly FieldInfo LoadingField = typeof(PlayerData).GetField("isLoading", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Dictionary<string, AsyncOperationHandle<ItemData>> Assets = new Dictionary<string, AsyncOperationHandle<ItemData>>();
        static AsyncOperation preloaded;
        static string preloadedScene;
        static bool barStarted, barEnded;
        static Driver driver;
        public static void OnBegin(string name)
        {
            if (name == "benchmark.FullFreshMap" || name == "benchmark.FullSavedMap")
                AssetFrameMonitor.Stop();
            if (name == "WorldMapTileGenerator.SaveImages")
                Optimizer.FlushTiles();
            if (name == "MainMenuManager.Awake" && Has("-bench-auto"))
                typeof(MainMenuManager).GetProperty("HasShowedIntro", BindingFlags.Public | BindingFlags.Static).SetValue(null, true, null);
            if (name == "GameManager.StartGameplay" && driver)
                driver.ReadyAt = Time.realtimeSinceStartup;
        }

        public static void OnEnd(string name, string status)
        {
            if (name == "MainMenuManager.Start" && status == "ok" && Has("-patch-build-caches"))
            {
                try
                {
                    LightMapData.BuildLocalCaches();
                    Application.Quit(0);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Application.Quit(10);
                }

                return;
            }

            if (name == "GameManager.StartGameplay" && status == "ok")
                LightMapData.GameplayReady();
            if (name == "MainMenuManager.Start" && status == "ok" && Has("-bench-auto") && !driver)
            {
                var go = new GameObject("LoadingBenchmarkDriver");
                UnityEngine.Object.DontDestroyOnLoad(go);
                driver = go.AddComponent<Driver>();
            }
        }

        public static void SplashPreload(SplashManager splash)
        {
            if (!Has("-fix-splash"))
                return;
            preloadedScene = (string)typeof(SplashManager).GetField("nextSceneName", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(splash);
            if (string.IsNullOrEmpty(preloadedScene))
                return;
            Probe.Mark("fix.splash.preload.begin:" + preloadedScene);
            preloaded = SceneManager.LoadSceneAsync(preloadedScene);
            preloaded.allowSceneActivation = false;
        }

        public static AsyncOperation SceneLoad(string scene)
        {
            if (preloaded != null && preloadedScene == scene)
            {
                var op = preloaded;
                preloaded = null;
                Probe.Mark("fix.splash.preload.reused");
                return op;
            }

            return SceneManager.LoadSceneAsync(scene);
        }

        public static float Progress(LoadingProgress progress, float value)
        {
            if (value >= 1 && !barStarted)
            {
                barStarted = true;
                Probe.Mark("gameplay.progress.wait.begin");
            }

            float result;
            if (Has("-fix-progress") && value >= 1)
            {
                typeof(LoadingProgress).GetField("currentProgress", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(progress, 1f);
                typeof(LoadingProgress).GetField("targetProgress", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(progress, 1f);
                typeof(LoadingProgress).GetMethod("UpdateBar", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(progress, new object[] { 1f });
                result = 1f;
            }
            else
                result = progress.UpdateProgress(value);
            if (result >= 1 && !barEnded)
            {
                barEnded = true;
                Probe.Mark("gameplay.progress.wait.end");
            }

            return result;
        }

        public static UniTask<List<InventoryItem>> PreviewInventory(PlayerData data)
        {
            if (!Has("-fix-preview"))
                return data.GetPlayerInventory();
            return LoadInventory(data, true);
        }

        public static async UniTask<List<InventoryItem>> LoadInventory(PlayerData data, bool gearOnly)
        {
            var label = gearOnly ? "experiment.PreviewGearInventory" : "PlayerData.GetPlayerInventory";
            long span = Probe.Begin(label);
            if ((bool)LoadingField.GetValue(data))
            {
                Probe.End(span, label, "busy");
                return null;
            }

            LoadingField.SetValue(data, true);
            try
            {
                var entries = data.playerInventory.Where(x => !gearOnly || x.Section == InventorySection.Gear).ToArray();
                Probe.Mark("inventory.request:" + (gearOnly ? "gear" : "all") + ":entries=" + entries.Length + ":keys=" + entries.Select(x => x.Key).Distinct().Count());
                var result = new List<InventoryItem>(entries.Length);
                if (InventoryEnabled())
                {
                    var keys = entries.Select(x => x.Key).Distinct().ToArray();
                    for (int offset = 0; offset < keys.Length; offset += 4)
                    {
                        int limit = Math.Min(keys.Length, offset + 4);
                        for (int j = offset; j < limit; j++)
                            if (!Assets.ContainsKey(keys[j]))
                                Assets.Add(keys[j], Addressables.LoadAssetAsync<ItemData>(keys[j]));
                        for (int j = offset; j < limit; j++)
                            await Assets[keys[j]].Task;
                    }

                    foreach (var saved in entries)
                    {
                        var h = Assets[saved.Key];
                        if (h.Status == AsyncOperationStatus.Succeeded)
                            Add(result, saved, h.Result);
                    }
                }
                else
                {
                    foreach (var saved in entries)
                    {
                        // Same sequential load policy as the original, with the preview filter only.
                        var h = Addressables.LoadAssetAsync<ItemData>(saved.Key);
                        await h.Task;
                        if (h.Status == AsyncOperationStatus.Succeeded)
                            Add(result, saved, h.Result);
                        // Retain successful handles while reconstructed items are live; release on process exit.
                        held.Add(h);
                    }
                }

                Probe.End(span, label, "ok");
                return result;
            }
            catch (Exception ex)
            {
                Probe.Mark("inventory.failure:" + ex.GetType().Name + ":" + ex.Message);
                Probe.End(span, label, "error");
                throw;
            }
            finally
            {
                LoadingField.SetValue(data, false);
            }
        }

        static readonly List<AsyncOperationHandle<ItemData>> held = new List<AsyncOperationHandle<ItemData>>();
        static void Add(List<InventoryItem> result, SavedItem saved, ItemData asset)
        {
            typeof(ItemRandomValues).GetMethod("SetData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(saved.Values, new object[] { asset });
            var item = new ItemObject(asset, saved.Amount)
            {
                randomValues = saved.Values
            };
            result.Add(new InventoryItem(item, saved.SlotIndex, saved.Section));
        }

        public static void ReleaseAssets()
        {
            foreach (var h in Assets.Values)
                if (h.IsValid())
                    Addressables.Release(h);
            Assets.Clear();
            foreach (var h in held)
                if (h.IsValid())
                    Addressables.Release(h);
            held.Clear();
        }

        [Serializable]
        public class Snapshot
        {
            public string scene;
            public string player;
            public string[] flags;
            public ItemSnapshot[] items;
            public bool inputEnabled;
            public string gameState;
            public bool playerPresent;
            public string inventoryJson;
            public int width;
            public int height;
            public string screenMode;
            public int vSync;
            public int frameTarget;
        }

        [Serializable]
        public class ItemSnapshot
        {
            public string key;
            public int amount;
            public int section;
            public int slot;
            public string values;
        }

        public static void SaveSnapshot()
        {
            var inv = Singleton<Inventory>.main;
            var items = (List<InventoryItem>)typeof(Inventory).GetField("allItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(inv);
            var gm = Singleton<GameManager>.main;
            var player = typeof(GameManager).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(gm) as PlayerMaster;
            var state = typeof(GameManager).GetProperty("gameState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(gm, null);
            var snap = new Snapshot
            {
                scene = SceneManager.GetActiveScene().name,
                player = GameData.playerData.playerName,
                flags = Environment.GetCommandLineArgs(),
                playerPresent = player,
                inputEnabled = MainInput.Actions.Player.enabled,
                gameState = state.ToString(),
                inventoryJson = JsonUtility.ToJson(GameData.playerData),
                width = Screen.width,
                height = Screen.height,
                screenMode = Screen.fullScreenMode.ToString(),
                vSync = QualitySettings.vSyncCount,
                frameTarget = Application.targetFrameRate,
                items = items.Select(x => new ItemSnapshot { key = x.itemObject.data.addressKey, amount = x.itemObject.amount, section = (int)x.section, slot = x.slotIndex, values = JsonUtility.ToJson(x.itemObject.randomValues) }).ToArray()
            };
            File.WriteAllText(Path.Combine(Output, "gameplay-snapshot.json"), Newtonsoft.Json.JsonConvert.SerializeObject(snap, Newtonsoft.Json.Formatting.Indented));
            Probe.Mark("validation.gameplay.snapshot:items=" + items.Count);
        }
    }

    public class Driver : MonoBehaviour
    {
        public float ReadyAt = -1f;
        float born, phaseAt;
        int stage;
        bool mapStarted, mapDone;
        MainMenuManager menu;
        string Mode
        {
            get
            {
                return BenchHooks.Arg("-bench-mode", "saved");
            }
        }

        static object Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target);
        }

        void Awake()
        {
            born = Time.realtimeSinceStartup;
            Application.quitting += BenchHooks.ReleaseAssets;
        }

        void Finish()
        {
            BenchHooks.SaveSnapshot();
            File.WriteAllText(Path.Combine(BenchHooks.Output, "world-snapshot.json"), JsonUtility.ToJson(GameData.worldData, true));
            Probe.Mark("benchmark.complete");
            Application.Quit();
            enabled = false;
        }

        void Update()
        {
            try
            {
                if (Time.realtimeSinceStartup - born > 900)
                {
                    Probe.Mark("benchmark.timeout");
                    Application.Quit();
                    enabled = false;
                    return;
                }

                if (ReadyAt >= 0)
                {
                    if (Mode == "fresh-map" || Mode == "reload-map")
                    {
                        if (!mapStarted && Time.realtimeSinceStartup - ReadyAt > float.Parse(BenchHooks.Arg("-bench-map-delay", "3")))
                        {
                            mapStarted = true;
                            GenerateMap().Forget();
                        }

                        if (mapDone && Time.realtimeSinceStartup - phaseAt > float.Parse(BenchHooks.Arg("-bench-hold", "8")))
                            Finish();
                    }
                    else if (Time.realtimeSinceStartup - ReadyAt > float.Parse(BenchHooks.Arg("-bench-hold", "8")))
                        Finish();
                    return;
                }

                if (stage == 0 && Time.realtimeSinceStartup - born > 3 && Application.isFocused)
                {
                    menu = UnityEngine.Object.FindAnyObjectByType<MainMenuManager>();
                    if (!menu)
                        return;
                    Probe.Mark("benchmark.selectPlay");
                    menu.SelectPlay();
                    stage = 1;
                    phaseAt = Time.realtimeSinceStartup;
                }
                else if (stage == 1 && Time.realtimeSinceStartup - phaseAt > 2 && !(bool)Field(menu, "isBusy"))
                {
                    Probe.Mark("benchmark.loadPlayer");
                    menu.LoadPlayer("player_slot1");
                    stage = 2;
                    phaseAt = Time.realtimeSinceStartup;
                }
                else if (stage == 2 && Time.realtimeSinceStartup - phaseAt > 2 && !(bool)Field(menu, "isBusy"))
                {
                    if (Mode == "saved" || Mode == "reload-map")
                    {
                        Probe.Mark("benchmark.loadWorld");
                        menu.LoadWorldSave(Mode == "reload-map" ? "world_slot3" : "world_slot2");
                        stage = 5;
                    }
                    else
                    {
                        Probe.Mark("benchmark.createWorld.open");
                        menu.CreateNewWorld();
                        stage = 3;
                        phaseAt = Time.realtimeSinceStartup;
                    }
                }
                else if (stage == 3 && Time.realtimeSinceStartup - phaseAt > 3)
                {
                    var panel = (WorldCreationPanel)Field(menu, "worldCreationPanel");
                    ((TMPro.TMP_InputField)Field(panel, "worldNameInputField")).text = "Diagnostic " + BenchHooks.Arg("-bench-run", "world");
                    ((TMPro.TMP_InputField)Field(panel, "worldSeedInputField")).text = BenchHooks.Arg("-bench-seed", "27102026");
                    Probe.Mark("benchmark.createWorld.confirm");
                    ((UnityEngine.UI.Button)Field(panel, "confirmButton")).onClick.Invoke();
                    stage = 4;
                }
            }
            catch (Exception ex)
            {
                Probe.Mark("benchmark.failure:" + ex);
                Debug.LogException(ex);
                Application.Quit();
                enabled = false;
            }
        }

        async UniTaskVoid GenerateMap()
        {
            try
            {
                var map = Singleton<WorldMap>.main;
                int seed = int.Parse(BenchHooks.Arg("-bench-seed", "27102026"));
                UnityEngine.Random.InitState(seed);
                // Identical seed control for every benchmark variant; retain the game's full algorithm.
                if (Mode != "reload-map")
                {
                    GameData.worldData.MapSeed = seed;
                    GameData.worldData.MapSeedVersion = 17;
                }
                else if (GameData.worldData.MapSeed != seed || GameData.worldData.MapSeedVersion != 17)
                    throw new Exception("Saved-map seed mismatch");
                typeof(WorldMap).GetField("forceNewSeed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(map, false);
                typeof(WorldMap).GetField("useRandomSeed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(map, false);
                Probe.Mark("benchmark.freshMap.begin:seed=" + seed);
                string mapSpan = Mode == "reload-map" ? "benchmark.FullSavedMap" : "benchmark.FullFreshMap";
                long span = Probe.Begin(mapSpan);
                map.OpenMap(true);
                await map.CreateAndRenderDungeon();
                Probe.End(span, mapSpan, "ok");
                await FastImages.Validate();
                var render = map.Render;
                var snapshot = new
                {
                    seed = GameData.worldData.MapSeed,
                    seedVersion = GameData.worldData.MapSeedVersion,
                    loaded = map.WorldMapState.IsMapLoaded,
                    regions = render.Regions.Select(r => new { r.ID, r.Name, chunks = r.Chunks.Count, inside = r.InsideNodes.Count, edges = r.Edges.Count, connections = r.Connections.Count }).ToArray(),
                    borders = render.BorderPrefabs.Count,
                    vertical = render.VerticalCells.Count,
                    horizontal = render.HorizontalCells.Count,
                    bounds = new[]
                    {
                        render.BottomLeft.x,
                        render.BottomLeft.y,
                        render.UpperRight.x,
                        render.UpperRight.y
                    },
                    generatedFiles = Directory.GetFiles(Probe.DataPath(), "*", SearchOption.AllDirectories).Select(p => new { name = p.Substring(Probe.DataPath().Length + 1), bytes = new FileInfo(p).Length }).ToArray()
                };
                File.WriteAllText(Path.Combine(BenchHooks.Output, "map-snapshot.json"), Newtonsoft.Json.JsonConvert.SerializeObject(snapshot, Newtonsoft.Json.Formatting.Indented));
                Probe.Mark("benchmark.freshMap.complete");
                await UniTask.NextFrame();
                map.CloseMap();
                await UniTask.NextFrame();
                BenchHooks.SaveSnapshot();
                File.Copy(Path.Combine(BenchHooks.Output, "gameplay-snapshot.json"), Path.Combine(BenchHooks.Output, "tutorial-snapshot.json"), true);
                if (BenchHooks.Has("-bench-region") && !Application.CanStreamedLevelBeLoaded("DungeonScene"))
                    Probe.Mark("benchmark.region.unsupported:scene-not-in-build");
                if (BenchHooks.Has("-bench-region") && Application.CanStreamedLevelBeLoaded("DungeonScene"))
                {
                    typeof(WorldMap).GetMethod("SetCurrentInfo", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(map, new object[] { new MapInfo.MainMap(0, 0) });
                    ReadyAt = -1;
                    Probe.Mark("benchmark.region.begin");
                    long region = Probe.Begin("benchmark.FirstRegionReady");
                    Singleton<SceneLoader>.main.CallScene("DungeonScene", null).Forget();
                    await UniTask.WaitUntil(() => ReadyAt >= 0);
                    Probe.End(region, "benchmark.FirstRegionReady", "ok");
                    Probe.Mark("benchmark.region.complete");
                }

                if (BenchHooks.Has("-verify-gameplay-resolve"))
                    await LightMapData.VerifyGameplay(map);
                if (BenchHooks.Has("-bench-prefabs"))
                    await RegionPrefabs.Benchmark(map);
                mapDone = true;
                phaseAt = Time.realtimeSinceStartup;
            }
            catch (Exception ex)
            {
                Probe.Mark("benchmark.map.failure:" + ex);
                Debug.LogException(ex);
                Application.Quit();
                enabled = false;
            }
        }
    }
}

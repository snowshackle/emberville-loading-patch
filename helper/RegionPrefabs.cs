using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Tilemaps;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EmbervilleDiagnostic
{
    // Load only the prefabs needed by the current region; retain them for this session.
    public static class RegionPrefabs
    {
        public static bool Enabled => BenchHooks.Has("-fix-region-prefabs");

        static readonly string[] BiomeFields =
        {
            "bigSpawnCollection",
            "mediumSpawnCollection",
            "smallSpawnCollection",
            "randomEvents"
        };
        static JObject Manifest;
        static Dictionary<string, JObject> Definitions;
        static readonly Dictionary<string, UnityEngine.Object> Assets = new Dictionary<string, UnityEngine.Object>();
        static readonly Dictionary<string, UnityEngine.Object> ExportObjects = new Dictionary<string, UnityEngine.Object>();
        static string Key(UnityEngine.Object obj) => obj.GetType().FullName + "|" + obj.name;
        static FieldInfo Field(string name) => typeof(BiomeSettings).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        static void ReadManifest()
        {
            if (Manifest != null)
                return;
            Manifest = JObject.Parse(File.ReadAllText(Path.Combine(LightMapData.Root, "region-manifest.json")));
            if (!(bool)Manifest["resolved"])
                throw new Exception("Region manifest is unresolved");
            Definitions = ((JArray)Manifest["assets"]).Cast<JObject>().ToDictionary(x => (string)x["key"]);
        }

        static bool LocalExport;
        static Dictionary<UnityEngine.Object, string> LocalAliases;
        public static void ExportLocal(IEnumerable<object> values)
        {
            LocalExport = true;
            try
            {
                LocalAliases = new Dictionary<UnityEngine.Object, string>();
                for (int i = 0; i < 314; i++)
                {
                    string alias = "__regiondata/root-" + i;
                    var asset = Resources.Load(alias);
                    if (asset)
                        LocalAliases[asset] = alias;
                }

                Probe.Mark("patch.local-caches.region-aliases-indexed");
                Export(values);
            }
            finally
            {
                LocalExport = false;
            }
        }

        public static void Export(IEnumerable<object> values)
        {
            var nodes = new JArray();
            var biomes = new JObject();
            ExportObjects.Clear();
            Func<UnityEngine.Object, JToken> add = obj =>
            {
                if (!obj)
                    return JValue.CreateNull();
                string key = Key(obj);
                if (!ExportObjects.ContainsKey(key))
                {
                    ExportObjects.Add(key, obj);
                    var asset = obj is Component component ? (UnityEngine.Object)component.gameObject : obj;
                    nodes.Add(new JObject { { "key", key }, { "name", asset.name }, { "assetType", asset.GetType().AssemblyQualifiedName }, { "lookupType", asset.GetType().FullName }, { "type", obj.GetType().AssemblyQualifiedName } });
                }

                return new JValue(key);
            };
            foreach (var value in values)
                if (value is DungeonChunk chunk)
                    add(chunk);
                else if (value is DungeonBarrierVariation barrier)
                    add(barrier);
            foreach (var biome in values.OfType<BiomeSettings>())
            {
                var fields = new JObject();
                foreach (string field in BiomeFields)
                {
                    object value = Field(field).GetValue(biome);
                    if (value is RandomEvent[] events)
                        fields[field] = new JArray(events.Select(e => add(e)));
                    else
                        fields[field] = add((UnityEngine.Object)value);
                }

                biomes[biome.name] = fields;
            }

            bool resolved = LocalExport || BenchHooks.Has("-resolve-region-catalog");
            if (resolved)
            {
                var aliases = LocalExport ? null : JObject.Parse(File.ReadAllText(Path.Combine(LightMapData.Root, "region-alias-candidates.json")));
                foreach (JObject node in nodes)
                {
                    var original = ExportObjects[(string)node["key"]];
                    var asset = original is Component c ? (UnityEngine.Object)c.gameObject : original;
                    string lookup = (string)node["lookupType"] + "|" + asset.name;
                    var candidates = (LocalExport ? null : (JArray)aliases[lookup]);
                    string match = null;
                    if (LocalExport)
                        LocalAliases.TryGetValue(asset, out match);
                    else
                        foreach (string alias in candidates)
                        {
                            if (Resources.Load(alias, asset.GetType()) == asset)
                            {
                                match = alias;
                                break;
                            }
                        }

                    if (match == null)
                        throw new Exception("No matching region alias: " + lookup);
                    node["alias"] = match;
                }
            }

            var document = new JObject
            {
                {
                    "version",
                    1
                },
                {
                    "resolved",
                    resolved
                },
                {
                    "assets",
                    nodes
                },
                {
                    "biomes",
                    biomes
                }
            };
            string name = resolved ? "region-manifest.json" : "region-manifest-unresolved.json";
            File.WriteAllText(Path.Combine(LightMapData.Root, name), document.ToString(Formatting.Indented));
            Probe.Mark("prefabs.export:assets=" + nodes.Count + ":biomes=" + biomes.Count + ":resolved=" + resolved);
        }

        public static List<string> Required(DungeonRender dungeon, int index)
        {
            ReadManifest();
            var region = dungeon.Regions[index];
            var keys = new HashSet<string>();
            Action<UnityEngine.Object> add = o =>
            {
                if (o)
                    keys.Add(Key(o));
            };
            foreach (var model in region.Chunks.Values)
                add(model.Type);
            foreach (var position in region.InsideNodes)
            {
                if (dungeon.BorderPrefabs.TryGetValue(position, out var border))
                    add(border);
                if (dungeon.VerticalCells.TryGetValue(position, out var vertical))
                    add(vertical);
                var right = position + Vector2Int.right;
                if (!region.InsideNodes.Contains(right) && dungeon.VerticalCells.TryGetValue(right, out vertical))
                    add(vertical);
                if (dungeon.HorizontalCells.TryGetValue(position, out var horizontal))
                    add(horizontal);
                var up = position + Vector2Int.up;
                if (!region.InsideNodes.Contains(up) && dungeon.HorizontalCells.TryGetValue(up, out horizontal))
                    add(horizontal);
            }

            foreach (var position in region.Edges)
                if (dungeon.BorderPrefabs.TryGetValue(position, out var border))
                    add(border);
            var biome = region.OverrideBiomeSettings ? region.OverrideBiomeSettings : dungeon.BiomeSettings;
            var fields = (JObject)Manifest["biomes"][biome.name];
            if (fields == null)
                throw new Exception("Unknown region biome: " + biome.name);
            foreach (var field in fields.Properties())
            {
                if (field.Value is JArray array)
                {
                    foreach (string key in array)
                        if (key != null)
                            keys.Add(key);
                }
                else if (field.Value.Type != JTokenType.Null)
                    keys.Add((string)field.Value);
            }

            foreach (string key in keys)
                if (!Definitions.ContainsKey(key))
                    throw new Exception("Unregistered region asset: " + key);
            return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        public static async UniTask Ensure(DungeonRender render, int region)
        {
            if (!LightMapData.IsLightBiome(render.BiomeSettings))
                return;
            var priority = Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.High;
            long total = Probe.Begin("prefabs.LoadRegion." + region);
            try
            {
                var keys = Required(render, region);
                var pending = new List<(string key, ResourceRequest request)>();
                long issue = Probe.Begin("prefabs.Requests." + region);
                foreach (string key in keys)
                    if (!Assets.ContainsKey(key))
                    {
                        var entry = Definitions[key];
                        pending.Add((key, Resources.LoadAsync((string)entry["alias"], Type.GetType((string)entry["assetType"], true))));
                    }

                Probe.End(issue, "prefabs.Requests." + region, "ok");
                long wait = Probe.Begin("prefabs.Wait." + region);
                while (pending.Any(p => !p.request.isDone))
                    await UniTask.NextFrame();
                Probe.End(wait, "prefabs.Wait." + region, "ok");
                long bind = Probe.Begin("prefabs.Bind." + region);
                foreach (var pendingAsset in pending)
                {
                    var value = pendingAsset.request.asset;
                    var type = Type.GetType((string)Definitions[pendingAsset.key]["type"], true);
                    if (value is GameObject go)
                        value = go.GetComponent(type);
                    if (!value || value.GetType() != type || Key(value) != pendingAsset.key)
                        throw new Exception("Wrong region asset: " + pendingAsset.key);
                    Assets.Add(pendingAsset.key, value);
                }

                foreach (var value in LightMapData.MetadataValues.OfType<UnityEngine.Object>())
                    if (value is DungeonChunk || value is DungeonBarrierVariation)
                        if (Assets.TryGetValue(Key(value), out var original))
                            LightMapData.BindOriginal(value, original);
                var selected = render.Regions[region];
                var biome = selected.OverrideBiomeSettings ? selected.OverrideBiomeSettings : render.BiomeSettings;
                foreach (var property in ((JObject)Manifest["biomes"][biome.name]).Properties())
                {
                    object value;
                    if (property.Value is JArray array)
                        value = array.Select(k => k.Type == JTokenType.Null ? null : (RandomEvent)Assets[(string)k]).ToArray();
                    else
                        value = property.Value.Type == JTokenType.Null ? null : Assets[(string)property.Value];
                    Field(property.Name).SetValue(biome, value);
                }

                LightMapData.RestoreAvailable(render);
                Probe.End(bind, "prefabs.Bind." + region, "ok");
                Probe.Mark("prefabs.ready:region=" + region + ":required=" + keys.Count + ":new=" + pending.Count + ":cached=" + Assets.Count);
                File.WriteAllText(Path.Combine(BenchHooks.Output, "region-requests-" + region + ".json"), JsonConvert.SerializeObject(new { region, biome = biome.name, required = keys, newRequests = pending.Select(p => p.key).ToArray(), cached = Assets.Count }, Formatting.Indented));
                Probe.End(total, "prefabs.LoadRegion." + region, "ok");
            }
            catch (Exception e)
            {
                Probe.End(total, "prefabs.LoadRegion." + region, "error");
                Probe.Mark("prefabs.failure:" + e);
                throw;
            }
            finally
            {
                Application.backgroundLoadingPriority = priority;
            }
        }

        static UnityEngine.Object Original(string key)
        {
            if (Assets.TryGetValue(key, out var cached))
                return cached;
            if (Enabled)
                throw new Exception("Required prefab was not loaded: " + key);
            var entry = Definitions[key];
            var asset = Resources.Load((string)entry["alias"], Type.GetType((string)entry["assetType"], true));
            var type = Type.GetType((string)entry["type"], true);
            return asset is GameObject go ? go.GetComponent(type) : asset;
        }

        static object Shape(UnityEngine.Object value)
        {
            if (value is Component component)
            {
                var all = component.GetComponentsInChildren<Component>(true);
                return new
                {
                    name = value.name,
                    type = value.GetType().FullName,
                    components = all.GroupBy(c => c ? c.GetType().FullName : "<missing>").OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
                    tilemaps = component.GetComponentsInChildren<Tilemap>(true).Select(t => new { t.name, bounds = t.cellBounds.ToString(), tiles = t.GetUsedTilesCount() }).ToArray()
                };
            }

            if (value is EnemySpawnGroupCollection collection)
                return new
                {
                    name = value.name,
                    groups = collection.Groups.Select(g => new { g.name, g.Name, enemies = g.Enemies.Select(e => new { e.name, e.ID, icon = e.Icon ? e.Icon.name : null, components = e.GetComponentsInChildren<Component>(true).Length }).ToArray() }).ToArray()
                };
            return new
            {
                name = value.name,
                type = value.GetType().FullName
            };
        }

        public static async UniTask Benchmark(WorldMap map)
        {
            ReadManifest();
            var ids = BenchHooks.Arg("-prefab-regions", "0").Split(',').Select(int.Parse).ToArray();
            var reports = new List<object>();
            var focusProbe = new GameObject("PrefabFocusProbe").AddComponent<PrefabFocusProbe>();
            foreach (int region in ids)
            {
                string before = LightMapData.SerializeRender(map.Render), infoBefore = LightMapData.InfoSnapshot(map.Render);
                var keys = Required(map.Render, region);
                focusProbe.Focused = focusProbe.Unfocused = 0;
                focusProbe.Sampling = true;
                bool focusBefore = Application.isFocused;
                string label = (Enabled ? "prefabs.Selective." : "prefabs.Control.") + region;
                long span = Probe.Begin(label);
                if (Enabled)
                    await Ensure(map.Render, region);
                else
                    await LightMapData.LoadAllForBenchmark(map.Render);
                Probe.End(span, label, "ok");
                focusProbe.Sampling = false;
                bool focusAfter = Application.isFocused;
                string after = LightMapData.SerializeRender(map.Render), infoAfter = LightMapData.InfoSnapshot(map.Render);
                if (before != after || infoBefore != infoAfter)
                    throw new Exception("Region load changed map data or UI summaries");
                var shapes = keys.ToDictionary(k => k, k => Shape(Original(k)));
                File.WriteAllText(Path.Combine(BenchHooks.Output, "region-fingerprint-" + region + ".json"), JsonConvert.SerializeObject(shapes, Formatting.Indented));
                var cloneChecks = new List<object>();
                if (BenchHooks.Has("-verify-region-clones"))
                    foreach (string key in keys)
                    {
                        if (!(Original(key)is Component component))
                            continue;
                        var clone = UnityEngine.Object.Instantiate(component, LightMapData.DormantRoot);
                        int count = clone.GetComponentsInChildren<Component>(true).Length;
                        int expected = component.GetComponentsInChildren<Component>(true).Length;
                        if (count != expected)
                            throw new Exception("Prefab clone component mismatch: " + key);
                        cloneChecks.Add(new { key, components = count, tilemaps = clone.GetComponentsInChildren<Tilemap>(true).Length });
                        UnityEngine.Object.Destroy(clone.gameObject);
                        await UniTask.NextFrame();
                    }

                reports.Add(new { region, required = keys.Count, sameWorldData = before == after, sameRegionInfo = infoBefore == infoAfter, focusBefore, focusAfter, focusedFrames = focusProbe.Focused, unfocusedFrames = focusProbe.Unfocused, cloneChecks });
                Probe.Mark("prefabs.validation.complete:" + region);
            }

            File.WriteAllText(Path.Combine(BenchHooks.Output, "region-validation.json"), JsonConvert.SerializeObject(reports, Formatting.Indented));
            UnityEngine.Object.Destroy(focusProbe.gameObject);
            if (BenchHooks.Has("-frames-after-prefabs"))
            {
                var monitor = new GameObject("PostPrefabFrameMonitor").AddComponent<AssetFrameMonitor>();
                monitor.FileStem = "post-prefab-frames";
                UnityEngine.Object.DontDestroyOnLoad(monitor.gameObject);
            }
        }
    }

    public sealed class PrefabFocusProbe : MonoBehaviour
    {
        public int Focused, Unfocused;
        public bool Sampling;
        void Update()
        {
            if (!Sampling)
                return;
            if (Application.isFocused)
                Focused++;
            else
                Unfocused++;
        }
    }
}

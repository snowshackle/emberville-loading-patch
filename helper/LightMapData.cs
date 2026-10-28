using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Tilemaps;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace EmbervilleDiagnostic {
public static class LightMapData {
 public static readonly string Root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"EmbervillePatch");
 public static readonly bool Enabled=BenchHooks.Has("-fix-light-map-data");
 static bool exported;
 public static void GameplayReady(){
  if(BenchHooks.Has("-bench-frames")){var go=new GameObject("MapDataFrameMonitor");UnityEngine.Object.DontDestroyOnLoad(go);go.AddComponent<AssetFrameMonitor>();}
  if((BenchHooks.Has("-export-map-catalog")||BenchHooks.Has("-export-region-catalog"))&&!exported){exported=true;try{Export();}catch(Exception e){Probe.Mark("catalog.export.error:"+e);Debug.LogException(e);}}
 }
 static FieldInfo SettingsField=typeof(WorldMap).GetField("settings",BindingFlags.Instance|BindingFlags.NonPublic);
 static UniTaskCompletionSource<bool> Ready;
 static DungeonSettings LightSettings;static AllBiomeSettings LightBiomes;
 static readonly Dictionary<Component,AssetMapRender[]> Props=new Dictionary<Component,AssetMapRender[]>();
 static readonly Dictionary<DungeonChunk,RegionMaterialInfo[]> MapMaterials=new Dictionary<DungeonChunk,RegionMaterialInfo[]>();
 static readonly Dictionary<BiomeSettings,Enemy[]> MapEnemies=new Dictionary<BiomeSettings,Enemy[]>();
 static object[] Loaded;static GameObject MetadataRoot;
 public static UniTask Ensure(){if(Ready==null){Ready=new UniTaskCompletionSource<bool>();LoadCatalog().Forget();}return Ready.Task;}
 static async UniTaskVoid LoadCatalog(){
  long span=Probe.Begin("catalog.Load");
  try{
   if(!File.Exists(Path.Combine(Root,"catalog.json")) || !File.Exists(Path.Combine(Root,"region-manifest.json"))) BuildLocalCaches();
   var doc=JObject.Parse(File.ReadAllText(Path.Combine(Root,"catalog.json")));if(!(bool)doc["resolved"])throw new Exception("Unresolved catalog");
   var nodes=(JArray)doc["nodes"];Loaded=new object[nodes.Count];
   MetadataRoot=new GameObject("LightweightMapMetadata");MetadataRoot.SetActive(false);UnityEngine.Object.DontDestroyOnLoad(MetadataRoot);
   var assetRequests=new List<(int id,ResourceRequest request)>();
   foreach(JObject n in nodes){int id=(int)n["id"];Type t=Type.GetType((string)n["type"],true);string kind=(string)n["kind"];
    if(kind=="asset"){assetRequests.Add((id,Resources.LoadAsync((string)n["alias"],t)));}
    else if(kind=="scriptable"){var so=ScriptableObject.CreateInstance(t);so.name=(string)n["name"];Loaded[id]=so;}
    else if(kind=="component"){
     var go=new GameObject((string)n["name"]);go.SetActive(false);go.transform.SetParent(MetadataRoot.transform,false);var component=go.AddComponent(t);Loaded[id]=component;
     var p=(JArray)n["localPosition"];go.transform.localPosition=new Vector3((float)p[0],(float)p[1],(float)p[2]);var q=(JArray)n["rotation"];go.transform.rotation=new Quaternion((float)q[0],(float)q[1],(float)q[2],(float)q[3]);var s=(JArray)n["localScale"];go.transform.localScale=new Vector3((float)s[0],(float)s[1],(float)s[2]);
    }else Loaded[id]=Activator.CreateInstance(t,true);
   }
   while(assetRequests.Any(r=>!r.request.isDone))await UniTask.NextFrame();
   foreach(var r in assetRequests){Loaded[r.id]=r.request.asset;if(!(Loaded[r.id] is UnityEngine.Object asset)||!asset)throw new Exception("Map asset missing at "+r.id);}
   foreach(JObject n in nodes){int id=(int)n["id"];if(n["fields"] is JObject fields)Fill(Loaded[id],fields);if(n["mapProps"] is JArray props)Props[(Component)Loaded[id]]=props.Select(p=>(AssetMapRender)Decode(p,typeof(AssetMapRender))).ToArray();if(Loaded[id] is ChunkPoint point){int count=(int)n["connectionCount"];point.DungeonConnections=count<0?null:Enumerable.Repeat<TeleporterDungeonRegion>(null,count).ToList();}}
   foreach(JObject n in nodes){int id=(int)n["id"];if(n["mapMaterials"] is JArray materials)MapMaterials[(DungeonChunk)Loaded[id]]=materials.Select(m=>new RegionMaterialInfo{Item=(ItemData)Decode(m["item"],typeof(ItemData)),Quantity=(int)m["quantity"]}).ToArray();if(n["mapEnemies"] is JArray enemies)MapEnemies[(BiomeSettings)Loaded[id]]=enemies.Select(e=>(Enemy)Decode(e,typeof(Enemy))).ToArray();}
   LightSettings=(DungeonSettings)Decode(doc["roots"]["settings"],typeof(DungeonSettings));LightBiomes=(AllBiomeSettings)Decode(doc["roots"]["allBiomes"],typeof(AllBiomeSettings));
   if(BenchHooks.Has("-verify-light-catalog")){
    var round=new CatalogWriter();var roots=new JObject{{"settings",round.Encode(LightSettings)},{"allBiomes",round.Encode(LightBiomes)}};
    var expected=(JArray)nodes.DeepClone();foreach(JObject n in expected)n.Remove("alias");
    // Compare the serialized representation: JValue(float) and parsed JValue(double)
    // can compare differently in memory despite identical round-trippable JSON.
    var actual=JArray.Parse(new JArray(round.Nodes).ToString(Formatting.None));
    bool same=JToken.DeepEquals(expected,actual)&&JToken.DeepEquals(doc["roots"],roots);
    File.WriteAllText(Path.Combine(BenchHooks.Output,"catalog-roundtrip.json"),new JObject{{"equal",same},{"expectedNodes",nodes.Count},{"actualNodes",round.Nodes.Count}}.ToString());
    if(!same){File.WriteAllText(Path.Combine(BenchHooks.Output,"catalog-roundtrip-actual.json"),new JArray(round.Nodes).ToString(Formatting.None));throw new Exception("Light catalog roundtrip differed");}
   }
   Probe.Mark("catalog.ready:nodes="+Loaded.Length+":chunks="+Props.Count+":biomes="+LightBiomes.AllSettings.Count);
   File.WriteAllText(Path.Combine(BenchHooks.Output,"catalog-loaded.json"),JsonConvert.SerializeObject(new{nodes=Loaded.Length,chunks=Props.Count,biomes=LightBiomes.AllSettings.Count,props=Props.Values.Sum(x=>x.Length),externalAssets=nodes.Count(n=>(string)n["kind"]=="asset")},Formatting.Indented));
   Probe.End(span,"catalog.Load","ok");Ready.TrySetResult(true);
  }catch(Exception e){Probe.End(span,"catalog.Load","error");Ready.TrySetException(e);Debug.LogException(e);}
 }
 static void Fill(object target,JObject fields){foreach(var p in fields.Properties()){int split=p.Name.LastIndexOf('|');var owner=target.GetType();while(owner!=null&&owner.FullName!=p.Name.Substring(0,split))owner=owner.BaseType;if(owner==null)throw new Exception("Missing field owner "+p.Name);var field=owner.GetField(p.Name.Substring(split+1),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(field==null)throw new Exception("Missing field "+p.Name);field.SetValue(target,Decode(p.Value,field.FieldType));}}
 static object Decode(JToken token,Type type){
  if(token==null||token.Type==JTokenType.Null)return null;
  if(token is JObject obj){if(obj["$ref"]!=null)return Loaded[(int)obj["$ref"]];if(obj["$value"]!=null){var value=Activator.CreateInstance(Type.GetType((string)obj["$value"],true));Fill(value,(JObject)obj["fields"]);return value;}throw new Exception("Unknown catalog token");}
  if(token is JArray array){Type item=type.IsArray?type.GetElementType():type.GetGenericArguments()[0];if(type.IsArray){Array a=Array.CreateInstance(item,array.Count);for(int i=0;i<array.Count;i++)a.SetValue(Decode(array[i],item),i);return a;}var list=(IList)Activator.CreateInstance(type);foreach(var e in array)list.Add(Decode(e,item));return list;}
  if(type.IsEnum)return Enum.ToObject(type,(long)token);return token.ToObject(type);
 }
 public static DungeonSettings GetSettings(WorldMap map){if(!Enabled)return Optimizer.WorldSettings(map);if(!LightSettings)throw new Exception("Light catalog not ready");SettingsField.SetValue(map,LightSettings);return LightSettings;}
 public static AssetMapRender[] MapProps(Component source){if(Enabled&&Props.TryGetValue(source,out var props))return props;return Optimizer.MapProps(source);}
 public static RegionInfo RegionInfo(RegionInfoBox box,RegionRenderModel region,DungeonRender dungeon){
  var biome=region.OverrideBiomeSettings?region.OverrideBiomeSettings:dungeon.BiomeSettings;
  if(!Enabled||!MapEnemies.TryGetValue(biome,out var enemies))return (RegionInfo)typeof(RegionInfoBox).GetMethod("__LightMapOriginalRegionInfo").Invoke(box,new object[]{region,dungeon});
  var materials=new Dictionary<ItemData,int>();foreach(var chunk in region.Chunks.Values)foreach(var material in MapMaterials[chunk.Type])if(material.Item){materials.TryAdd(material.Item,0);materials[material.Item]=material.Quantity;}
  return new RegionInfo(materials.Select(m=>new RegionMaterialInfo{Item=m.Key,Quantity=m.Value}).ToList(),enemies,region.Chunks.Values.Where(c=>c!=null&&c.Type.Features!=null).SelectMany(c=>c.Type.Features)){Name=biome.name.Contains("SC")?"region_ScavengerCamp":"region_VitromutosDungeon"};
 }
 public static async void FadeInStart(GameManager gm){try{if(Enabled)await Ensure();typeof(GameManager).GetMethod("__LightMapOriginalFadeInStart",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(gm,null);}catch(Exception e){Probe.Mark("catalog.fade.error:"+e);Debug.LogException(e);}}
 public static async UniTask CreateAndRender(WorldMap map){if(Enabled){await Ensure();SettingsField.SetValue(map,LightSettings);}await (UniTask)typeof(WorldMap).GetMethod("__LightMapOriginalCreateAndRender",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(map,null);}
 public static ResourceRequest RequestBiomes(string path){return Enabled?Resources.LoadAsync<TextAsset>("tables/levels"):Resources.LoadAsync<AllBiomeSettings>(path);}
 public static AllBiomeSettings ResolveBiomes(UnityEngine.Object original){return Enabled?LightBiomes:(AllBiomeSettings)original;}
 static UniTaskCompletionSource<bool> GameplayReadySource;
 static readonly Dictionary<UnityEngine.Object,UnityEngine.Object> Originals=new Dictionary<UnityEngine.Object,UnityEngine.Object>();
 static UniTask EnsureGameplay(){if(GameplayReadySource==null){GameplayReadySource=new UniTaskCompletionSource<bool>();LoadGameplay().Forget();}return GameplayReadySource.Task;}
 static async UniTaskVoid LoadGameplay(){
  long span=Probe.Begin("catalog.LoadGameplayPrefabs");var priority=Application.backgroundLoadingPriority;Application.backgroundLoadingPriority=UnityEngine.ThreadPriority.High;
  try{
   await Ensure();var request=Resources.LoadAsync<AllBiomeSettings>("AllBiomes");while(!request.isDone)await UniTask.NextFrame();var all=(AllBiomeSettings)request.asset;
   var sr=Resources.LoadAsync<DungeonSettings>("__diagnostic/world-settings");while(!sr.isDone)await UniTask.NextFrame();var settings=(DungeonSettings)sr.asset;
   var writer=new CatalogWriter();writer.Encode(settings);writer.Encode(all);
   var expected=(JArray)JObject.Parse(File.ReadAllText(Path.Combine(Root,"catalog.json")))["nodes"];
   if(writer.Values.Count!=Loaded.Length)throw new Exception("Gameplay catalog size mismatch");
   for(int i=0;i<Loaded.Length;i++){
    var source=writer.Values[i];var target=Loaded[i];
    if(source.GetType()!=target.GetType()||(source is UnityEngine.Object obj&&(string)expected[i]["name"]!=obj.name))throw new Exception("Gameplay catalog identity mismatch at "+i);
    if(target is UnityEngine.Object a&&source is UnityEngine.Object b)Originals[a]=b;
    for(var type=target.GetType();type!=null&&type!=typeof(MonoBehaviour)&&type!=typeof(ScriptableObject)&&type!=typeof(UnityEngine.Object)&&type!=typeof(object);type=type.BaseType)
     foreach(var f in type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))if(!f.IsStatic&&Skip(f))f.SetValue(target,f.GetValue(source));
   }
   Probe.End(span,"catalog.LoadGameplayPrefabs","ok");GameplayReadySource.TrySetResult(true);
  }catch(Exception e){Probe.End(span,"catalog.LoadGameplayPrefabs","error");GameplayReadySource.TrySetException(e);Debug.LogException(e);}
  finally{if(Application.backgroundLoadingPriority==UnityEngine.ThreadPriority.High)Application.backgroundLoadingPriority=priority;}
 }
 static object Restore(object value,HashSet<object> seen){
  if(value==null)return null;if(value is UnityEngine.Object u)return Originals.TryGetValue(u,out var original)?original:u;
  Type type=value.GetType();if(type.IsValueType||value is string||!seen.Add(value))return value;
  if(value is IDictionary dict){foreach(var key in dict.Keys.Cast<object>().ToArray())dict[key]=Restore(dict[key],seen);return value;}
  if(value is IList list){for(int i=0;i<list.Count;i++)list[i]=Restore(list[i],seen);return value;}
  if(type.Assembly!=typeof(DungeonRender).Assembly)return value;
  for(var t=type;t!=null&&t!=typeof(object);t=t.BaseType)foreach(var f in t.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))if(!f.IsStatic&&!f.IsInitOnly)f.SetValue(value,Restore(f.GetValue(value),seen));return value;
 }
 public static async UniTask<DungeonRender> RenderRegion(DungeonRegionRenderer renderer,DungeonRender render,int region,LoadingProgressReport progress){
  if(Enabled){if(RegionPrefabs.Enabled)await RegionPrefabs.Ensure(render,region);else {await EnsureGameplay();Restore(render,new HashSet<object>(new RefComparer()));}}
  return await (UniTask<DungeonRender>)typeof(DungeonRegionRenderer).GetMethod("__LightMapOriginalRenderRegion").Invoke(renderer,new object[]{render,region,progress});
 }
 internal static IEnumerable<object> MetadataValues=>Loaded;
 internal static Transform DormantRoot=>MetadataRoot.transform;
 internal static bool IsLightBiome(BiomeSettings biome)=>MapEnemies.ContainsKey(biome);
 internal static void BindOriginal(UnityEngine.Object proxy,UnityEngine.Object original){Originals[proxy]=original;if(proxy is DungeonChunk chunk&&original is DungeonChunk real){MapMaterials[real]=MapMaterials[chunk];Props[real]=Props[chunk];}}
 internal static void RestoreAvailable(DungeonRender render){Restore(render,new HashSet<object>(new RefComparer()));}
 internal static async UniTask LoadAllForBenchmark(DungeonRender render){await EnsureGameplay();RestoreAvailable(render);}
 internal static string SerializeRender(DungeonRender render)=>JsonConvert.SerializeObject(render,new DungeonBarrierVariationConverter(null),new BiomeSettingsConverter(null),new Vector2IntConverter(),new Vector2Converter(),new ChunkTypeDefConverter(null),new DungeonChunkConverter(null),new ChunkModelConverter(null));
 internal static string InfoSnapshot(DungeonRender render){var box=new RegionInfoBox();return JsonConvert.SerializeObject(render.Regions.Select(r=>{var info=box.GetRegionInfo(r,render);return new{r.ID,info.Name,materials=info.Materials.Select(m=>new{m.Item.addressKey,m.Quantity,icon=m.Item.itemIconUI?.name}),enemies=info.Enemies.Select(e=>e.Icon?.name),features=info.Features.Select(f=>new{icon=f.Icon?.name,position=f.transform.localPosition.ToString("R")})};}));}
 public static async UniTask VerifyGameplay(WorldMap map){
  string before=SerializeRender(map.Render),infoBefore=InfoSnapshot(map.Render);await EnsureGameplay();Restore(map.Render,new HashSet<object>(new RefComparer()));string after=SerializeRender(map.Render),infoAfter=InfoSnapshot(map.Render);
  var chunk=map.Render.Regions.SelectMany(r=>r.Chunks.Values).First().Type;
  var clone=UnityEngine.Object.Instantiate(chunk,MetadataRoot.transform);int components=clone.GetComponentsInChildren<Component>(true).Length;int tiles=clone.Tilemaps?.Count??0;
  UnityEngine.Object.Destroy(clone.gameObject);
  File.WriteAllText(Path.Combine(BenchHooks.Output,"gameplay-resolution.json"),JsonConvert.SerializeObject(new{sameWorldData=before==after,sameRegionInfo=infoBefore==infoAfter,regionsChecked=map.Render.Regions.Count,originalReferences=Originals.Count,clonedChunk=chunk.name,cloneComponents=components,cloneTilemaps=tiles},Formatting.Indented));
  if(infoBefore!=infoAfter){File.WriteAllText(Path.Combine(BenchHooks.Output,"map-info-light.json"),infoBefore);File.WriteAllText(Path.Combine(BenchHooks.Output,"map-info-original.json"),infoAfter);}
  if(before!=after||infoBefore!=infoAfter||components<=3||tiles==0)throw new Exception("Gameplay prefab resolution validation failed");
 }
 static void Export(){
  long span=Probe.Begin("catalog.Export");var map=UnityEngine.Object.FindAnyObjectByType<WorldMap>();if(!map)throw new Exception("WorldMap missing");
  var settings=(DungeonSettings)SettingsField.GetValue(map);var all=Resources.Load<AllBiomeSettings>("AllBiomes");
  var writer=new CatalogWriter();var roots=new JObject{{"settings",writer.Encode(settings)},{"allBiomes",writer.Encode(all)}};
  if(BenchHooks.Has("-export-region-catalog")){RegionPrefabs.Export(writer.Values);return;}
  JObject registry=!BenchHooks.Has("-export-unresolved")&&File.Exists(Path.Combine(Root,"alias-candidates.json"))?JObject.Parse(File.ReadAllText(Path.Combine(Root,"alias-candidates.json"))):null;
  if(registry!=null){foreach(var pair in writer.ExternalObjects){var n=writer.Nodes[pair.Key];string key=pair.Value.GetType().FullName+"|"+pair.Value.name;var candidates=(JArray)registry[key];if(candidates==null)throw new Exception("No alias candidates: "+key);string found=null;
    foreach(string path in candidates){var value=Resources.Load(path,pair.Value.GetType());if(value==pair.Value){found=path;break;}}
    if(found==null)throw new Exception("No alias matched original object: "+key);n["alias"]=found;
   }}
  var document=new JObject{{"version",1},{"roots",roots},{"nodes",new JArray(writer.Nodes)},{"resolved",registry!=null}};
  File.WriteAllText(Path.Combine(Root,registry==null?"catalog-unresolved.json":"catalog.json"),document.ToString(Formatting.None));
  var summary=new JObject{{"nodes",writer.Nodes.Count},{"externalAssets",writer.ExternalObjects.Count},{"types",JObject.FromObject(writer.Nodes.GroupBy(n=>(string)n["type"]).ToDictionary(g=>g.Key,g=>g.Count()))}};
  File.WriteAllText(Path.Combine(BenchHooks.Output,"catalog-export.json"),summary.ToString());Probe.End(span,"catalog.Export","ok");
 }

 public static void BuildLocalCaches(){
  Probe.Mark("patch.local-caches.begin");
  Directory.CreateDirectory(Root);
  var settings=Resources.Load<DungeonSettings>("__diagnostic/world-settings");
  Probe.Mark("patch.local-caches.settings-loaded");
  var all=Resources.Load<AllBiomeSettings>("AllBiomes");
  Probe.Mark("patch.local-caches.biomes-loaded");
  if(!settings || !all)throw new Exception("Cannot build map cache: original game resources missing");
  var writer=new CatalogWriter();var roots=new JObject{{"settings",writer.Encode(settings)},{"allBiomes",writer.Encode(all)}};
  Probe.Mark("patch.local-caches.encoded");
  var aliases=new string[]{"__mapdata/asset-10","__mapdata/asset-100","__mapdata/asset-101","__mapdata/asset-102","__mapdata/asset-103","__mapdata/asset-104","__mapdata/asset-11","__mapdata/asset-15","__mapdata/asset-19","__mapdata/asset-20","__mapdata/asset-23","__mapdata/asset-24","__mapdata/asset-25","__mapdata/asset-27","__mapdata/asset-32","__mapdata/asset-33","__mapdata/asset-34","__mapdata/asset-39","__mapdata/asset-42","__mapdata/asset-44","__mapdata/asset-49","__mapdata/asset-53","__mapdata/asset-60","__mapdata/asset-61","__mapdata/asset-64","__mapdata/asset-65","__mapdata/asset-67","__mapdata/asset-70","__mapdata/asset-75","__mapdata/asset-76","__mapdata/asset-79","__mapdata/asset-81","__mapdata/asset-82","__mapdata/asset-83","__mapdata/asset-84","__mapdata/asset-85","__mapdata/asset-86","__mapdata/asset-87","__mapdata/asset-88","__mapdata/asset-89","__mapdata/asset-90","__mapdata/asset-91","__mapdata/asset-92","__mapdata/asset-93","__mapdata/asset-94","__mapdata/asset-95","__mapdata/asset-96","__mapdata/asset-97","__mapdata/asset-98","__mapdata/asset-99"};
  var indexedAliases=new Dictionary<UnityEngine.Object,string>();
  foreach(string alias in aliases){var asset=Resources.Load(alias);if(asset)indexedAliases[asset]=alias;}
  Probe.Mark("patch.local-caches.map-aliases-indexed");
  foreach(var pair in writer.ExternalObjects){
   string found=null;
   indexedAliases.TryGetValue(pair.Value,out found);
   if(found==null)throw new Exception("Local cache alias resolution failed");
   writer.Nodes[pair.Key]["alias"]=found;
  }
  RegionPrefabs.ExportLocal(writer.Values);
  var doc=new JObject{{"version",1},{"roots",roots},{"nodes",new JArray(writer.Nodes)},{"resolved",true}};
  string dest=Path.Combine(Root,"catalog.json");File.WriteAllText(dest+".tmp",doc.ToString(Formatting.None));
  if(File.Exists(dest))File.Delete(dest);File.Move(dest+".tmp",dest);
  Probe.Mark("patch.local-caches.ready");
 }
 public static IEnumerable<FieldInfo> Fields(Type type){
  for(var t=type;t!=null&&t!=typeof(MonoBehaviour)&&t!=typeof(ScriptableObject)&&t!=typeof(UnityEngine.Object)&&t!=typeof(object);t=t.BaseType)
   foreach(var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly).OrderBy(f=>f.MetadataToken))
    if(!f.IsStatic&&!f.IsNotSerialized&&(type.IsValueType||f.IsPublic||f.IsDefined(typeof(SerializeField),true)||f.IsDefined(typeof(SerializeReference),true))&&!Skip(f)&&(!typeof(ItemData).IsAssignableFrom(type)||f.Name=="itemIconUI"||f.Name=="addressKey")&&(!typeof(Enemy).IsAssignableFrom(type)||f.Name=="icon"))yield return f;
 }
 static bool Skip(FieldInfo f){string n=f.Name;Type t=f.DeclaringType;
  if(t==typeof(DungeonSettings)&&n=="otherAreas")return true;
  if(t==typeof(BiomeSettings)&&new[]{"bigSpawnCollection","mediumSpawnCollection","smallSpawnCollection","randomEvents"}.Contains(n))return true;
  if(typeof(DungeonChunk).IsAssignableFrom(t)&&n=="tilemaps")return true;
  if(t==typeof(DungeonBarrierVariation)&&n=="tilemaps")return true;
  if(t==typeof(ChunkPointOption)&&new[]{"tilemaps","dungeonConnections"}.Contains(n))return true;
  if(t==typeof(ChunkPoint)&&new[]{"DungeonConnections","SpawnPoint"}.Contains(n))return true;
  if(t==typeof(ChunkPortal))return true;
  return false;
 }
 static bool Metadata(UnityEngine.Object obj){
  return obj is ScriptableObject&&!(obj is TileBase)&&obj.GetType().Assembly==typeof(DungeonSettings).Assembly
   ||obj is DungeonChunk||obj is DungeonBarrierVariation||obj is ChunkPointOption||obj is ChunkPortal||obj is AssetMapRender||obj is ChunkFeature||obj is Enemy;
 }
 sealed class RefComparer:IEqualityComparer<object>{public new bool Equals(object a,object b)=>ReferenceEquals(a,b);public int GetHashCode(object o)=>RuntimeHelpers.GetHashCode(o);}
 sealed class CatalogWriter {
  public readonly List<JObject> Nodes=new List<JObject>();public readonly Dictionary<int,UnityEngine.Object> ExternalObjects=new Dictionary<int,UnityEngine.Object>();
  public readonly List<object> Values=new List<object>();
  readonly Dictionary<object,int> ids=new Dictionary<object,int>(new RefComparer());readonly Dictionary<int,int> unityIds=new Dictionary<int,int>();
  public JToken Encode(object value){
   if(value==null)return JValue.CreateNull();Type type=value.GetType();
   if(value is UnityEngine.Object dead&&!dead)return JValue.CreateNull();
   if(type.IsEnum)return new JValue(Convert.ToInt64(value));
   if(type.IsPrimitive||value is string||value is decimal)return new JValue(value);
   if(value is IList list){var arr=new JArray();foreach(var element in list)arr.Add(Encode(element));return arr;}
   if(type.IsValueType){var fields=new JObject();foreach(var f in Fields(type))fields[f.DeclaringType.FullName+"|"+f.Name]=Encode(f.GetValue(value));return new JObject{{"$value",type.AssemblyQualifiedName},{"fields",fields}};}
   int id;if(value is UnityEngine.Object unity&&unityIds.TryGetValue(unity.GetInstanceID(),out id))return new JObject{{"$ref",id}};
   if(ids.TryGetValue(value,out id))return new JObject{{"$ref",id}};
   id=Nodes.Count;ids[value]=id;if(value is UnityEngine.Object u)unityIds[u.GetInstanceID()]=id;
   var n=new JObject{{"id",id},{"type",type.AssemblyQualifiedName}};Nodes.Add(n);Values.Add(value);
   if(value is UnityEngine.Object obj){n["name"]=obj.name;
    if(!Metadata(obj)){n["kind"]="asset";n["lookupType"]=type.FullName;ExternalObjects[id]=obj;return new JObject{{"$ref",id}};}
    n["kind"]=obj is ScriptableObject?"scriptable":"component";
    if(obj is Component comp){n["localPosition"]=new JArray(comp.transform.localPosition.x,comp.transform.localPosition.y,comp.transform.localPosition.z);n["rotation"]=new JArray(comp.transform.rotation.x,comp.transform.rotation.y,comp.transform.rotation.z,comp.transform.rotation.w);n["localScale"]=new JArray(comp.transform.localScale.x,comp.transform.localScale.y,comp.transform.localScale.z);}
   }else n["kind"]="object";
   var data=new JObject();n["fields"]=data;foreach(var f in Fields(type))data[f.DeclaringType.FullName+"|"+f.Name]=Encode(f.GetValue(value));
   if(value is ChunkPoint point){n["connectionCount"]=point.DungeonConnections?.Count??-1;n["pointXY"]=new JArray(point.Position.x,point.Position.y);}
   if(value is BiomeSettings biome){n["chunkSizeXY"]=new JArray(biome.ChunkSize.x,biome.ChunkSize.y);var enemies=MapEnemies.TryGetValue(biome,out var cachedEnemies)?cachedEnemies:biome.BigSpawnCollection.Groups.SelectMany(g=>g.Enemies).Concat(biome.MediumSpawnCollection.Groups.SelectMany(g=>g.Enemies)).Concat(biome.SmallSpawnCollection.Groups.SelectMany(g=>g.Enemies)).Distinct((IEqualityComparer<Enemy>)Activator.CreateInstance(typeof(RegionInfoBox).GetNestedType("Comparer",BindingFlags.NonPublic),true)).ToArray();n["mapEnemies"]=new JArray(enemies.Select(e=>Encode(e)));}
   if(value is DungeonChunk chunk){var props=new JArray();foreach(var prop in Props.TryGetValue(chunk,out var stored)?stored:chunk.GetComponentsInChildren<AssetMapRender>())props.Add(Encode(prop));n["mapProps"]=props;var materials=MapMaterials.TryGetValue(chunk,out var cachedMaterials)?cachedMaterials:chunk.GetComponentsInChildren<GatheringObject>().Select(g=>new RegionMaterialInfo{Item=g.ItemToDrop,Quantity=g.HitsToFinish}).ToArray();n["mapMaterials"]=new JArray(materials.Select(m=>new JObject{{"item",Encode(m.Item)},{"quantity",m.Quantity}}));}
   return new JObject{{"$ref",id}};
  }
 }
}
}

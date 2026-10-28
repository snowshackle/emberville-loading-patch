using System;
using System.Collections.Generic;
using System.Reflection;
using DungeonGeneration;
using UnityEngine;
using UnityEngine.Tilemaps;
using System.Linq;

namespace EmbervilleDiagnostic {
public static class Optimizer {
 public static readonly bool FastWfc=BenchHooks.Has("-fix-wfc");
 public static readonly bool BatchTiles=BenchHooks.Has("-fix-batch-tiles");
 public static readonly bool FastImages=BenchHooks.Has("-fix-map-images");
 public static readonly bool CacheProps=BenchHooks.Has("-fix-prop-cache");
 public static float FadeTime(float original){return BenchHooks.Has("-fix-quick-fade")?Math.Min(original,0.1f):original;}
 public static float FadeStep(float value,float minimum,float maximum){return Mathf.Clamp(value,minimum,BenchHooks.Has("-fix-quick-fade")?1f:maximum);}
 static readonly Dictionary<Component,AssetMapRender[]> PropArrays=new Dictionary<Component,AssetMapRender[]>();
 public static AssetMapRender[] MapProps(Component source){
  if(!CacheProps)return source.GetComponentsInChildren<AssetMapRender>();
  if(!PropArrays.TryGetValue(source,out var props)){props=source.GetComponentsInChildren<AssetMapRender>();PropArrays.Add(source,props);}return props;
 }
 static readonly Dictionary<Tilemap,Dictionary<Vector3Int,TileBase>> PendingTiles=new Dictionary<Tilemap,Dictionary<Vector3Int,TileBase>>();
 public static void SetTile(Tilemap map,Vector3Int pos,TileBase tile){
  if(!BatchTiles){map.SetTile(pos,tile);return;}
  if(!PendingTiles.TryGetValue(map,out var changes)){changes=new Dictionary<Vector3Int,TileBase>();PendingTiles.Add(map,changes);}changes[pos]=tile;
 }
 public static TileBase GetTile(Tilemap map,Vector3Int pos){if(BatchTiles&&PendingTiles.TryGetValue(map,out var changes)&&changes.TryGetValue(pos,out var tile))return tile;return map.GetTile(pos);}
 public static void FlushTiles(){
  if(!BatchTiles||PendingTiles.Count==0)return;
  long span=Probe.Begin("experiment.TilemapFlush");int count=0;
  foreach(var pair in PendingTiles){var edits=pair.Value.ToArray();pair.Key.SetTiles(edits.Select(e=>e.Key).ToArray(),edits.Select(e=>e.Value).ToArray());count+=edits.Length;}
  Probe.Mark("tiles.flushed:maps="+PendingTiles.Count+":tiles="+count);PendingTiles.Clear();Probe.End(span,"experiment.TilemapFlush","ok");
 }
 static readonly FieldInfo MapCells=typeof(WFCMap).GetField("_mapCellsArray",BindingFlags.Instance|BindingFlags.NonPublic);
 static readonly FieldInfo Settings=typeof(WorldMap).GetField("settings",BindingFlags.Instance|BindingFlags.NonPublic);
 static readonly FieldInfo Holder=typeof(WorldMap).GetField("worldHolderPrefab",BindingFlags.Instance|BindingFlags.NonPublic);
 class EntropyValue {public int Count;public double Value;}
 static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ICell,EntropyValue> Entropies=new System.Runtime.CompilerServices.ConditionalWeakTable<ICell,EntropyValue>();
 static int Count(ICell cell){if(cell is BaseCell b)return b.Options.Count;if(cell is HorizontalBarrierCell h)return h.Options.Count;if(cell is VerticalBarrierCell v)return v.Options.Count;return -1;}
 public static ICell SelectCell(WFCMap map){
  var cells=(ICell[])MapCells.GetValue(map);ICell best=null;double minimum=0;
  foreach(var cell in cells){
   if(cell.IsCollapsed)continue;
   int count=Count(cell);double value;
   // These option lists only lose entries during collapse; probability weights do not change.
   if(count<0)value=cell.Entropy();
   else {var cached=Entropies.GetValue(cell,k=>new EntropyValue{Count=-1});if(cached.Count!=count){cached.Count=count;cached.Value=Entropy(cell);}value=cached.Value;}
   // Match stable OrderBy(...).First(), including Double.CompareTo's NaN ordering.
   if(best==null||value.CompareTo(minimum)<0){best=cell;minimum=value;}
  }
  return best;
 }
 static double Entropy(ICell cell){
  if(cell is BaseCell b)return Entropy(b.Options);
  if(cell is HorizontalBarrierCell h)return Entropy(h.Options);
  if(cell is VerticalBarrierCell v)return Entropy(v.Options);
  return cell.Entropy();
 }
 static double Entropy<T>(List<T> options) where T:ICellState {
  // Preserve the original sum order and arithmetic; calculate the unchanged denominator once.
  double sum=options.Sum(x=>x.Probability),value=0;
  foreach(var option in options){double p=option.Probability/sum;value-=p*(Math.Log(p)/Math.Log(2.0));}return value;
 }
 public static DungeonSettings WorldSettings(WorldMap map){
  var value=(DungeonSettings)Settings.GetValue(map);
  if(!value){Probe.Mark("lazy.worldSettings.begin");value=Resources.Load<DungeonSettings>("__diagnostic/world-settings");if(!value)throw new InvalidOperationException("Missing deferred world settings");Settings.SetValue(map,value);Probe.Mark("lazy.worldSettings.end");}
  return value;
 }
 public static WorldMapTileGenerator WorldHolder(WorldMap map){
  var value=(WorldMapTileGenerator)Holder.GetValue(map);
  if(!value){Probe.Mark("lazy.worldHolder.begin");value=Resources.Load<WorldMapTileGenerator>("__diagnostic/world-holder");if(!value)throw new InvalidOperationException("Missing deferred world holder");Holder.SetValue(map,value);Probe.Mark("lazy.worldHolder.end");}
  return value;
 }
}
}

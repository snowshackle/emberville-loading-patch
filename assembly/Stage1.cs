using System;using System.IO;using System.Linq;using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

var root=Path.GetDirectoryName(Path.GetFullPath(args[1]));
var managed=Path.GetDirectoryName(Path.GetFullPath(args[0]));
var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(managed);
using var game=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver,InMemory=true});
using var helper=AssemblyDefinition.ReadAssembly(args[2]);
var mod=game.MainModule;var probe=helper.MainModule.GetType("EmbervilleDiagnostic.Probe");
MethodReference Import(string name)=>mod.ImportReference(probe.Methods.Single(m=>m.Name==name));
var begin=Import("Begin");var end=Import("End");var mark=Import("Mark");var dataPath=Import("DataPath");var progress=Import("Progress");
var fastBegin=Import("FastBegin");var fastEnd=Import("FastEnd");var observeLoading=Import("ObserveLoading");
IEnumerable<TypeDefinition> Walk(TypeDefinition t){yield return t;foreach(var c in t.NestedTypes)foreach(var n in Walk(c))yield return n;}
var types=mod.Types.SelectMany(Walk).ToArray();
var selected=new Dictionary<string,string[]> {
 ["SplashManager"]=new[]{"Awake","Start"},
 ["MainMenuManager"]=new[]{"Awake","Start","WaitLoad","ShowMainButtons","CreateNewChar","CreateNewWorld","LoadWorldSave"},
 ["SceneLoader"]=new[]{"CallScene","CallSceneNoProgress"},
 ["CharacterSelectionPanel"]=new[]{"Setup"},["CharacterSelectionButton"]=new[]{"Setup"},["PreviewHandler"]=new[]{"Setup"},
 ["PlayerData"]=new[]{"GetPlayerInventory"},["Inventory"]=new[]{"Start","Setup","Load"},
 ["GameManager"]=new[]{"Awake","SetupScene","Start","SetupRegionMap","SetupSubDungeonMap","FadeInStart","FadeInEnd","StartGameplay"},
 ["WorldMap"]=new[]{"CreateAndRenderDungeon","OpenMap"},
 ["DungeonSettings"]=new[]{"CreateDungeon"},["CreateRegionsVoronoiStep"]=new[]{"Generate"},["SelectFeatureChunksStep"]=new[]{"Generate"},
 ["RegionGenerationStep"]=new[]{"Generate","RunAlgorithm"},["LinearRegionGeneration"]=new[]{"Generate"},["ImprovedLinearPathGeneration"]=new[]{"Generate"},
 ["GenerateWFCMapStep"]=new[]{"Generate"},["DungeonGeneration.WFCMap"]=new[]{"Create","InitializeMap","CollapseWaveFunction"},
 ["DungeonModel"]=new[]{"GetRender","CalculateAllVariables"},
 ["DungeonRegionRenderer"]=new[]{"RenderDungeon","SetupChunks","SetupBarriers","SetupLayers","SetupNavMesh"},
 ["WorldMapTileGenerator"]=new[]{"RenderDungeon","SetupWithBorderMap","SaveImages"},
 ["ImageHelper"]=new[]{"SaveRenderTextureAsync"},["WorldMapContainer"]=new[]{"LoadImages","LoadMapImages","SetupFeatures"},
 ["SaveLoad"]=new[]{"LoadAsync","SaveAsync","Save"},["BackgroundTaskManager"]=new[]{"SchedulePerformanceSensitiveTask","WaitPerformanceSensitiveTask"}
};
var report=new List<string>();int redirected=0;
foreach(var t in types)foreach(var method in t.Methods.Where(m=>m.HasBody))foreach(var ins in method.Body.Instructions) {
 if(ins.Operand is MethodReference m && m.DeclaringType.FullName=="UnityEngine.Application" && m.Name=="get_persistentDataPath") {ins.OpCode=OpCodes.Call;ins.Operand=dataPath;redirected++;}
 if(ins.Operand is MethodReference ac && ac.DeclaringType.FullName=="UnityEngine.AsyncOperation" && ac.Name=="set_allowSceneActivation") {ins.OpCode=OpCodes.Call;ins.Operand=observeLoading;}
}
void InsertBefore(ILProcessor il,Instruction target,params Instruction[] instructions){foreach(var i in instructions)il.InsertBefore(target,i);}
foreach(var entry in selected) {
 var type=types.FirstOrDefault(t=>t.FullName==entry.Key);if(type==null){report.Add("MISSING TYPE "+entry.Key);continue;}
 foreach(var method in type.Methods.Where(m=>m.HasBody && entry.Value.Contains(m.Name)).ToArray()) {
  var label=type.FullName+"."+method.Name;
  var asyncAttr=method.CustomAttributes.FirstOrDefault(a=>a.AttributeType.Name=="AsyncStateMachineAttribute");
  if(asyncAttr!=null) {
   var sm=((TypeReference)asyncAttr.ConstructorArguments[0].Value).Resolve();
   var move=sm.Methods.Single(m=>m.Name=="MoveNext");var il=move.Body.GetILProcessor();
   var span=new FieldDefinition("__diagnosticSpan",FieldAttributes.Public,mod.TypeSystem.Int64);sm.Fields.Add(span);
   var old=move.Body.Instructions.First();
   InsertBefore(il,old,il.Create(OpCodes.Ldarg_0),il.Create(OpCodes.Ldfld,span),il.Create(OpCodes.Ldc_I4_0),il.Create(OpCodes.Conv_I8),il.Create(OpCodes.Ceq),il.Create(OpCodes.Brfalse,old),il.Create(OpCodes.Ldarg_0),il.Create(OpCodes.Ldstr,label),il.Create(OpCodes.Call,begin),il.Create(OpCodes.Stfld,span));
   int ends=0;
   foreach(var ins in move.Body.Instructions.ToArray())if(ins.Operand is MethodReference called && (called.Name=="SetResult"||called.Name=="SetException") && called.DeclaringType.Name.Contains("MethodBuilder")) {
    InsertBefore(il,ins,il.Create(OpCodes.Ldarg_0),il.Create(OpCodes.Ldfld,span),il.Create(OpCodes.Ldstr,label),il.Create(OpCodes.Ldstr,called.Name=="SetException"?"error":"ok"),il.Create(OpCodes.Call,end),il.Create(OpCodes.Ldarg_0),il.Create(OpCodes.Ldc_I4_0),il.Create(OpCodes.Conv_I8),il.Create(OpCodes.Stfld,span));ends++;
   }
   move.Body.MaxStackSize+=8;report.Add($"ASYNC {label} ends={ends}");
  } else {
   var il=method.Body.GetILProcessor();var first=method.Body.Instructions.First();
   var local=new VariableDefinition(mod.TypeSystem.Int64);method.Body.Variables.Add(local);method.Body.InitLocals=true;
   InsertBefore(il,first,il.Create(OpCodes.Ldstr,label),il.Create(OpCodes.Call,begin),il.Create(OpCodes.Stloc,local));
   foreach(var ret in method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray()) {
    // Turn the return into the first instrumentation instruction so existing branches still run the end marker.
    ret.OpCode=OpCodes.Ldloc;ret.Operand=local;var cursor=ret;
    foreach(var i in new[]{il.Create(OpCodes.Ldstr,label),il.Create(OpCodes.Ldstr,"ok"),il.Create(OpCodes.Call,end),il.Create(OpCodes.Ret)}) {il.InsertAfter(cursor,i);cursor=i;}
   }
   method.Body.MaxStackSize+=8;report.Add("SYNC "+label);
  }
 }
}
// Measure slow synchronous startup/serialization callbacks without logging every cheap invocation.
foreach(var t in types) foreach(var method in t.Methods.Where(m=>m.HasBody && new[]{"Awake","OnEnable","Start","OnAfterDeserialize",".cctor"}.Contains(m.Name)).ToArray()) {
 if(method.CustomAttributes.Any(a=>a.AttributeType.Name.Contains("StateMachineAttribute")))continue;
 if(selected.TryGetValue(t.FullName,out var names)&&names.Contains(method.Name))continue;
 var il=method.Body.GetILProcessor();var local=new VariableDefinition(mod.TypeSystem.Int64);method.Body.Variables.Add(local);method.Body.InitLocals=true;
 InsertBefore(il,method.Body.Instructions.First(),il.Create(OpCodes.Call,fastBegin),il.Create(OpCodes.Stloc,local));
 foreach(var ret in method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray()) {
  ret.OpCode=OpCodes.Ldloc;ret.Operand=local;var cursor=ret;
  foreach(var i in new[]{il.Create(OpCodes.Ldstr,t.FullName+"."+method.Name),il.Create(OpCodes.Call,fastEnd),il.Create(OpCodes.Ret)}){il.InsertAfter(cursor,i);cursor=i;}
 }
 method.Body.MaxStackSize+=5;
}
var lp=types.Single(t=>t.Name=="LoadingProgress");var late=lp.Methods.Single(m=>m.Name=="LateUpdate");var lip=late.Body.GetILProcessor();
InsertBefore(lip,late.Body.Instructions.First(),lip.Create(OpCodes.Ldarg_0),lip.Create(OpCodes.Ldfld,lp.Fields.Single(f=>f.Name=="currentProgress")),lip.Create(OpCodes.Ldarg_0),lip.Create(OpCodes.Ldfld,lp.Fields.Single(f=>f.Name=="targetProgress")),lip.Create(OpCodes.Call,progress));late.Body.MaxStackSize+=4;
// Mark engine scene-load requests/activation and costly image steps without changing their parameters or behavior.
var callMarkers=new Dictionary<string,string> {
 ["UnityEngine.SceneManagement.SceneManager::LoadSceneAsync"]="engine.SceneLoadRequest",
 ["UnityEngine.AsyncOperation::set_allowSceneActivation"]="engine.SceneActivationFlag",
 ["UnityEngine.ImageConversion::EncodeNativeArrayToPNG"]="image.PNGEncode",
 ["UnityEngine.Rendering.AsyncGPUReadback::RequestIntoNativeArrayAsync"]="image.GPUReadbackRequest",
 ["System.IO.FileStream::Flush"]="image.FileFlush"
 ,["UnityEngine.Resources::Load"]="resources.Load"
 ,["UnityEngine.Resources::LoadAsync"]="resources.LoadAsyncRequest"
};
foreach(var t in types)foreach(var method in t.Methods.Where(m=>m.HasBody)) {
 var il=method.Body.GetILProcessor();
 foreach(var ins in method.Body.Instructions.ToArray())if(ins.Operand is MethodReference called && callMarkers.TryGetValue(called.DeclaringType.FullName+"::"+called.Name,out var name)) {
  var label=name+":"+t.FullName+"."+method.Name;
  InsertBefore(il,ins,il.Create(OpCodes.Ldstr,label+".before"),il.Create(OpCodes.Call,mark));
  var ld=il.Create(OpCodes.Ldstr,label+".after");il.InsertAfter(ins,ld);il.InsertAfter(ld,il.Create(OpCodes.Call,mark));method.Body.MaxStackSize+=3;
 }
}

var hooks=helper.MainModule.GetType("EmbervilleDiagnostic.BenchHooks");
MethodReference Hook(string name)=>mod.ImportReference(hooks.Methods.Single(m=>m.Name==name));
var pd=types.Single(t=>t.FullName=="PlayerData");var getInv=pd.Methods.Single(m=>m.Name=="GetPlayerInventory");
var iil=getInv.Body.GetILProcessor();var invFirst=getInv.Body.Instructions.First();
InsertBefore(iil,invFirst,iil.Create(OpCodes.Call,Hook("InventoryEnabled")),iil.Create(OpCodes.Brfalse,invFirst),iil.Create(OpCodes.Ldarg_0),iil.Create(OpCodes.Ldc_I4_0),iil.Create(OpCodes.Call,Hook("LoadInventory")),iil.Create(OpCodes.Ret));
getInv.Body.MaxStackSize+=4;
var splash=types.Single(t=>t.FullName=="SplashManager").Methods.Single(m=>m.Name=="Start");
var sil=splash.Body.GetILProcessor();InsertBefore(sil,splash.Body.Instructions.First(),sil.Create(OpCodes.Ldarg_0),sil.Create(OpCodes.Call,Hook("SplashPreload")));splash.Body.MaxStackSize+=2;
foreach(var t in types)foreach(var method in t.Methods.Where(m=>m.HasBody))foreach(var ins in method.Body.Instructions) {
 if(ins.Operand is not MethodReference call) continue;
 if(t.FullName.StartsWith("PreviewHandler/")&&call.DeclaringType.FullName=="PlayerData"&&call.Name=="GetPlayerInventory") {ins.OpCode=OpCodes.Call;ins.Operand=Hook("PreviewInventory");}
 if(t.FullName.StartsWith("GameManager/<Start>")&&call.DeclaringType.FullName=="LoadingProgress"&&call.Name=="UpdateProgress") {ins.OpCode=OpCodes.Call;ins.Operand=Hook("Progress");}
 if(call.DeclaringType.FullName=="UnityEngine.SceneManagement.SceneManager"&&call.Name=="LoadSceneAsync"&&call.Parameters.Count==1&&call.Parameters[0].ParameterType.FullName=="System.String") {ins.OpCode=OpCodes.Call;ins.Operand=Hook("SceneLoad");}
}
report.Add("Opt-in flags: -fix-preview -fix-inventory -fix-progress -fix-splash; common benchmark harness -bench-auto");

// Widen short branches after inserting instrumentation.
var widened=new Dictionary<OpCode,OpCode>{{OpCodes.Br_S,OpCodes.Br},{OpCodes.Brfalse_S,OpCodes.Brfalse},{OpCodes.Brtrue_S,OpCodes.Brtrue},{OpCodes.Beq_S,OpCodes.Beq},{OpCodes.Bge_S,OpCodes.Bge},{OpCodes.Bge_Un_S,OpCodes.Bge_Un},{OpCodes.Bgt_S,OpCodes.Bgt},{OpCodes.Bgt_Un_S,OpCodes.Bgt_Un},{OpCodes.Ble_S,OpCodes.Ble},{OpCodes.Ble_Un_S,OpCodes.Ble_Un},{OpCodes.Blt_S,OpCodes.Blt},{OpCodes.Blt_Un_S,OpCodes.Blt_Un},{OpCodes.Bne_Un_S,OpCodes.Bne_Un},{OpCodes.Leave_S,OpCodes.Leave}};
foreach(var t in types)foreach(var method in t.Methods.Where(m=>m.HasBody))foreach(var i in method.Body.Instructions)if(widened.TryGetValue(i.OpCode,out var wider))i.OpCode=wider;
game.Write(args[1]);
report.Add("persistentDataPath redirects="+redirected);
File.WriteAllLines(Path.Combine(root,"instrumentation.txt"),report);Console.WriteLine(string.Join(Environment.NewLine,report));

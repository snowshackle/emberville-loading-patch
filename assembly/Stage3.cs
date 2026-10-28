using System;using System.IO;using System.Linq;using System.Collections.Generic;
using Mono.Cecil;using Mono.Cecil.Cil;
using var game=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{InMemory=true});
using var helper=AssemblyDefinition.ReadAssembly(args[2]);var mod=game.MainModule;var hook=helper.MainModule.GetType("EmbervilleDiagnostic.LightMapData");
MethodReference H(string n)=>mod.ImportReference(hook.Methods.Single(m=>m.Name==n));
void Wrap(string type,string method,string preservedName,string target){
 var owner=mod.GetType(type);var old=owner.Methods.Single(m=>m.Name==method);
 var kept=new MethodDefinition(preservedName,MethodAttributes.Public|MethodAttributes.HideBySig,old.ReturnType){Body=old.Body};owner.Methods.Add(kept);
 foreach(var p in old.Parameters)kept.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,p.ParameterType));
 foreach(var i in kept.Body.Instructions)if(i.Operand is ParameterDefinition p)i.Operand=kept.Parameters[p.Index];
 old.Body=new MethodBody(old);var il=old.Body.GetILProcessor();il.Append(il.Create(OpCodes.Ldarg_0));foreach(var p in old.Parameters)il.Append(il.Create(OpCodes.Ldarg,p));il.Append(il.Create(OpCodes.Call,H(target)));il.Append(il.Create(OpCodes.Ret));
}
Wrap("GameManager","FadeInStart","__LightMapOriginalFadeInStart","FadeInStart");
Wrap("WorldMap","CreateAndRenderDungeon","__LightMapOriginalCreateAndRender","CreateAndRender");
Wrap("DungeonRegionRenderer","RenderDungeon","__LightMapOriginalRenderRegion","RenderRegion");
Wrap("RegionInfoBox","GetRegionInfo","__LightMapOriginalRegionInfo","RegionInfo");
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> list){foreach(var t in list){yield return t;foreach(var n in Types(t.NestedTypes))yield return n;}}
int settings=0,props=0,requests=0,results=0;
foreach(var t in Types(mod.Types))foreach(var method in t.Methods.Where(m=>m.HasBody))foreach(var i in method.Body.Instructions.ToArray()){
 if(i.Operand is MethodReference mr&&mr.DeclaringType.FullName=="EmbervilleDiagnostic.Optimizer"){
  if(mr.Name=="WorldSettings"){i.Operand=H("GetSettings");settings++;}
  if(mr.Name=="MapProps"){i.Operand=H("MapProps");props++;}
 }
 if(t.FullName.Contains("WorldMap/<CreateAndRenderDungeon>")){
  if(i.Operand is GenericInstanceMethod g&&g.DeclaringType.FullName=="UnityEngine.Resources"&&g.Name=="LoadAsync"&&g.GenericArguments.Any(a=>a.FullName=="AllBiomeSettings")){i.Operand=H("RequestBiomes");requests++;}
  if(i.OpCode==OpCodes.Castclass&&i.Operand is TypeReference tr&&tr.FullName=="AllBiomeSettings"){i.OpCode=OpCodes.Call;i.Operand=H("ResolveBiomes");results++;}
 }
}
if(settings<1||props!=1||requests!=1||results!=1)throw new Exception($"Unexpected patch counts {settings},{props},{requests},{results}");
var mark=mod.ImportReference(helper.MainModule.GetType("EmbervilleDiagnostic.Probe").Methods.Single(m=>m.Name=="Mark"));
foreach(var pair in new[]{("ExtraLoopSystem","Shutdown"),("SavingManager","OnShutdown"),("GameData","SaveData"),("GameData","CallSaveListeners"),("GameData","SavePlayer"),("GameData","SaveWorld"),("GameData","SavePlayerState"),("OptionsPanel","Save")}){
 var method=mod.GetType(pair.Item1).Methods.Single(m=>m.Name==pair.Item2);var il=method.Body.GetILProcessor();var first=method.Body.Instructions[0];il.InsertBefore(first,il.Create(OpCodes.Ldstr,"shutdown.begin:"+pair.Item1+"."+pair.Item2));il.InsertBefore(first,il.Create(OpCodes.Call,mark));
 foreach(var ret in method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray()){ret.OpCode=OpCodes.Ldstr;ret.Operand="shutdown.end:"+pair.Item1+"."+pair.Item2;var call=il.Create(OpCodes.Call,mark);il.InsertAfter(ret,call);il.InsertAfter(call,il.Create(OpCodes.Ret));}
}
game.Write(args[1]);Console.WriteLine($"Light-map hooks: settings={settings}, props={props}, saved-map catalog={requests}/{results}");

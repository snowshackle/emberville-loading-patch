using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Stage 2: replace WFC, tile and image operations with the optimized helpers.
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(args[3]);
using var game = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
using var helper = AssemblyDefinition.ReadAssembly(args[2]);
var mod = game.MainModule;
var opt = helper.MainModule.GetType("EmbervilleDiagnostic.Optimizer");
MethodReference Hook(string name) => mod.ImportReference(opt.Methods.Single(m => m.Name == name));
var imageHelper = helper.MainModule.GetType("EmbervilleDiagnostic.FastImages");
MethodReference ImageHook(string name) => mod.ImportReference(imageHelper.Methods.Single(m => m.Name == name));
IEnumerable<TypeDefinition> Walk(TypeDefinition t)
{
    yield return t;
    foreach (var c in t.NestedTypes)
        foreach (var n in Walk(c))
            yield return n;
}

var types = mod.Types.SelectMany(Walk).ToArray();
var select = types.Single(t => t.FullName == "DungeonGeneration.WFCMap").Methods.Single(m => m.Name == "SelectCellHeuristic");
var il = select.Body.GetILProcessor();
var first = select.Body.Instructions[0];
foreach (var i in new[]
{
    il.Create(OpCodes.Ldsfld, mod.ImportReference(opt.Fields.Single(f => f.Name == "FastWfc"))),
    il.Create(OpCodes.Brfalse, first),
    il.Create(OpCodes.Ldarg_0),
    il.Create(OpCodes.Call, Hook("SelectCell")),
    il.Create(OpCodes.Ret)
}

)
    il.InsertBefore(first, i);
select.Body.MaxStackSize += 2;
int replaced = 0;
foreach (var t in types)
    foreach (var m in t.Methods.Where(m => m.HasBody))
        foreach (var ins in m.Body.Instructions.ToArray())
        {
            if (t.FullName.StartsWith("FaderManager") && ins.Operand is MethodReference clamp && clamp.DeclaringType.FullName == "UnityEngine.Mathf" && clamp.Name == "Clamp" && clamp.Parameters.Count == 3 && clamp.ReturnType.FullName == "System.Single")
            {
                ins.OpCode = OpCodes.Call;
                ins.Operand = Hook("FadeStep");
            }

            if (t.FullName == "GameManager" && m.Name.Contains("FadeInStart") && ins.OpCode == OpCodes.Ldc_R4 && (float)ins.Operand >= 0.4f)
            {
                m.Body.GetILProcessor().InsertAfter(ins, Instruction.Create(OpCodes.Call, Hook("FadeTime")));
            }

            if (t.FullName.StartsWith("WorldMapTileGenerator") && ins.Operand is GenericInstanceMethod generic && generic.Name == "GetComponentsInChildren" && generic.GenericArguments.SingleOrDefault()?.FullName == "AssetMapRender" && generic.Parameters.Count == 0)
            {
                ins.OpCode = OpCodes.Call;
                ins.Operand = Hook("MapProps");
            }

            if (t.FullName.StartsWith("WorldMapTileGenerator") && ins.Operand is MethodReference call && call.DeclaringType.FullName == "UnityEngine.Tilemaps.Tilemap")
            {
                if (call.Name == "SetTile" && call.Parameters.Count == 2)
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = Hook("SetTile");
                }

                if (call.Name == "GetTile" && call.Parameters.Count == 1 && call.ReturnType.FullName == "UnityEngine.Tilemaps.TileBase")
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = Hook("GetTile");
                }
            }

            if (ins.OpCode == OpCodes.Ldfld && ins.Operand is FieldReference field && field.DeclaringType.FullName == "WorldMap")
            {
                if (field.Name == "settings")
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = Hook("WorldSettings");
                    replaced++;
                }
                else if (field.Name == "worldHolderPrefab")
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = Hook("WorldHolder");
                    replaced++;
                }
            }
        }

void ConditionalEntry(string type, string method, string hook, int argsCount)
{
    var m = types.Single(t => t.FullName == type).Methods.Single(m => m.Name == method);
    var p = m.Body.GetILProcessor();
    var entry = m.Body.Instructions[0];
    p.InsertBefore(entry, p.Create(OpCodes.Ldsfld, mod.ImportReference(opt.Fields.Single(f => f.Name == "FastImages"))));
    p.InsertBefore(entry, p.Create(OpCodes.Brfalse, entry));
    for (int n = 0; n < argsCount; n++)
        p.InsertBefore(entry, p.Create(OpCodes.Ldarg, (short)n));
    p.InsertBefore(entry, p.Create(OpCodes.Call, ImageHook(hook)));
    p.InsertBefore(entry, p.Create(OpCodes.Ret));
    m.Body.MaxStackSize += 4;
}

ConditionalEntry("ImageHelper", "SaveRenderTextureAsync", "Save", 2);
ConditionalEntry("WorldMapContainer", "LoadMapImages", "Load", 3);
// Recompute branch offsets using long forms after insertions.
var opcodes = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Name);
foreach (var t in types)
    foreach (var m in t.Methods.Where(m => m.HasBody))
        foreach (var i in m.Body.Instructions)
            if (i.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                i.OpCode = opcodes[i.OpCode.Name.Substring(0, i.OpCode.Name.Length - 2)];
game.Write(args[1]);
Console.WriteLine($"WFC opt-in selector; {replaced} WorldMap lazy-reference accesses patched.");

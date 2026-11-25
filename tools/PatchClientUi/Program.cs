using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PatchClientUi;

internal static class Program
{
    private const string PatchMarkerType = "__GwtPatchUiMarker";

    private static int Main(string[] args)
    {
        bool verifyOnly = args.Contains("--verify", StringComparer.OrdinalIgnoreCase);
        string managedDir = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? @"D:\Games\Ground War Tanks\Ground War Tanks\Tank_Data\Managed";

        string dllPath = Path.Combine(managedDir, "Assembly-CSharp.dll");
        string bakPath = dllPath + ".bak";

        if (!File.Exists(dllPath))
        {
            Console.Error.WriteLine($"Assembly-CSharp.dll not found at: {dllPath}");
            return 1;
        }

        if (verifyOnly)
        {
            var vResolver = new DefaultAssemblyResolver();
            vResolver.AddSearchDirectory(managedDir);
            using var vAsm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = vResolver });
            var m = vAsm.MainModule;
            bool hasMarker = m.Types.Any(t => t.Name == PatchMarkerType);
            var enterShow = m.Types.First(t => t.Name == "ActivityWindowDataManager").Methods.First(x => x.Name == "EnterHallShow");
            var tsl = m.Types.First(t => t.Name == "TankSelectList");
            var cull = tsl.Methods.First(x => x.Name == "UpdateVisibleButtons");
            var dc = m.Types.First(t => t.Name == "DeathCamera");
            var upd = dc.Methods.First(x => x.Name == "UpdateDeath");
            Console.WriteLine($"Verified DLL: marker={hasMarker}, EnterHallShow.IL={enterShow.Body.Instructions.Count}, UpdateVisibleButtons.IL={cull.Body.Instructions.Count}, UpdateDeath.IL={upd.Body.Instructions.Count}");
            return hasMarker ? 0 : 2;
        }

        if (!File.Exists(bakPath))
        {
            File.Copy(dllPath, bakPath);
            Console.WriteLine($"Created backup: {bakPath}");
        }
        else
        {
            Console.WriteLine($"Using existing backup: {bakPath}");
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);

        var readerParams = new ReaderParameters { AssemblyResolver = resolver };
        using var asm = AssemblyDefinition.ReadAssembly(bakPath, readerParams);
        var module = asm.MainModule;

        if (module.Types.Any(t => t.Name == PatchMarkerType))
        {
            Console.WriteLine("Backup already has patch marker; skipping duplicate patch.");
            return 0;
        }

        PatchActivityWindow(module);
        PatchTankSelectList(module);
        PatchDeathCamera(module);

        var marker = new TypeDefinition("", PatchMarkerType,
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(marker);

        string tempPath = dllPath + ".tmp";
        asm.Write(tempPath);
        asm.Dispose();

        File.Copy(tempPath, dllPath, overwrite: true);
        File.Delete(tempPath);
        Console.WriteLine($"Patched successfully: {dllPath}");
        return 0;
    }

    private static void PatchActivityWindow(ModuleDefinition module)
    {
        var type = module.Types.FirstOrDefault(t => t.Name == "ActivityWindowDataManager")
            ?? throw new InvalidOperationException("ActivityWindowDataManager type not found");
        var method = type.Methods.FirstOrDefault(m => m.Name == "EnterHallShow")
            ?? throw new InvalidOperationException("ActivityWindowDataManager.EnterHallShow not found");
        var isActiveOpen = type.Fields.FirstOrDefault(f => f.Name == "IsActiveOpen")
            ?? throw new InvalidOperationException("ActivityWindowDataManager.IsActiveOpen not found");

        var il = method.Body.GetILProcessor();
        method.Body.Instructions.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.InitLocals = false;

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stsfld, isActiveOpen);
        il.Emit(OpCodes.Ret);
        Console.WriteLine("Patched ActivityWindowDataManager.EnterHallShow -> disabled auto-open popup.");
    }

    private static void PatchTankSelectList(ModuleDefinition module)
    {
        var tsl = module.Types.FirstOrDefault(t => t.Name == "TankSelectList")
            ?? throw new InvalidOperationException("TankSelectList type not found");
        var btnType = module.Types.FirstOrDefault(t => t.Name == "TankListButtonMessage")
            ?? throw new InvalidOperationException("TankListButtonMessage type not found");
        var emptyPortType = module.Types.FirstOrDefault(t => t.Name == "EmptyPort")
            ?? throw new InvalidOperationException("EmptyPort type not found");

        var fListTrans = tsl.Fields.First(f => f.Name == "ListTrans");
        var fLeft = tsl.Fields.First(f => f.Name == "Left");
        var fRight = tsl.Fields.First(f => f.Name == "Right");

        var unityAsm = module.AssemblyReferences.First(r => r.Name == "UnityEngine");
        var unityMain = module.AssemblyResolver.Resolve(unityAsm).MainModule;

        var tTransform = unityMain.Types.First(t => t.FullName == "UnityEngine.Transform");
        var tGameObject = unityMain.Types.First(t => t.FullName == "UnityEngine.GameObject");
        var tComponent = unityMain.Types.First(t => t.FullName == "UnityEngine.Component");
        var tVector3 = unityMain.Types.First(t => t.FullName == "UnityEngine.Vector3");
        var tObject = unityMain.Types.First(t => t.FullName == "UnityEngine.Object");
        var tMathf = unityMain.Types.First(t => t.FullName == "UnityEngine.Mathf");

        var mGetLocalPos = module.ImportReference(tTransform.Properties.First(p => p.Name == "localPosition").GetMethod);
        var mGetChildCount = module.ImportReference(tTransform.Properties.First(p => p.Name == "childCount").GetMethod);
        var mGetChild = module.ImportReference(tTransform.Methods.First(m => m.Name == "GetChild" && m.Parameters.Count == 1));
        var fVecX = module.ImportReference(tVector3.Fields.First(f => f.Name == "x"));
        var mGetGameObject = module.ImportReference(tComponent.Properties.First(p => p.Name == "gameObject").GetMethod);
        var mGetActiveSelf = module.ImportReference(tGameObject.Properties.First(p => p.Name == "activeSelf").GetMethod);
        var mSetActive = module.ImportReference(tGameObject.Methods.First(m => m.Name == "SetActive" && m.Parameters.Count == 1));
        var mObjEq = module.ImportReference(tObject.Methods.First(m => m.Name == "op_Equality"));
        var mObjNeq = module.ImportReference(tObject.Methods.First(m => m.Name == "op_Inequality"));
        var mMathfAbs = module.ImportReference(tMathf.Methods.First(m => m.Name == "Abs" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Single));

        var fLastCullX = new FieldDefinition("_lastCullX", FieldAttributes.Private, module.TypeSystem.Single);
        tsl.Fields.Add(fLastCullX);

        var mCull = new MethodDefinition("UpdateVisibleButtons",
            MethodAttributes.Private | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        tsl.Methods.Add(mCull);
        BuildCullMethod(mCull, module, fListTrans, fLeft, fRight, fLastCullX,
            tTransform, tVector3, tGameObject, mGetLocalPos, mGetChildCount, mGetChild,
            fVecX, mGetGameObject, mGetActiveSelf, mSetActive, mObjEq, mObjNeq, mMathfAbs);

        // Hook UpdateVisibleButtons() at start and end of AlignListToBounds(float)
        var mAlign = tsl.Methods.First(m => m.Name == "AlignListToBounds");
        var ilAlign = mAlign.Body.GetILProcessor();
        var firstAlign = mAlign.Body.Instructions[0];
        ilAlign.InsertBefore(firstAlign, ilAlign.Create(OpCodes.Ldarg_0));
        ilAlign.InsertBefore(firstAlign, ilAlign.Create(OpCodes.Call, mCull));
        var lastRet = mAlign.Body.Instructions.LastOrDefault(ins => ins.OpCode == OpCodes.Ret);
        if (lastRet != null && lastRet != firstAlign)
        {
            ilAlign.InsertBefore(lastRet, ilAlign.Create(OpCodes.Ldarg_0));
            ilAlign.InsertBefore(lastRet, ilAlign.Create(OpCodes.Call, mCull));
        }

        // Reset _lastCullX = 999999f when list changes or viewport resizes
        foreach (string name in new[] { "InitList", "UpdateList", "ReSortButton", "OnScreenChange" })
        {
            var m = tsl.Methods.First(x => x.Name == name);
            var il = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 999999f));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, fLastCullX));
        }

        // Ensure DeleteButtons and DeleteEmptyCarport pass includeInactive=true
        var genericGetCompBool = tGameObject.Methods.First(m =>
            m.Name == "GetComponentsInChildren" &&
            m.HasGenericParameters &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);

        PatchIncludeInactive(module, tsl, "DeleteButtons", btnType, genericGetCompBool);
        PatchIncludeInactive(module, tsl, "DeleteEmptyCarport", emptyPortType, genericGetCompBool);
        Console.WriteLine("Patched TankSelectList with viewport culling (UpdateVisibleButtons).");
    }

    private static void PatchIncludeInactive(
        ModuleDefinition module,
        TypeDefinition tsl,
        string methodName,
        TypeDefinition elementType,
        MethodDefinition genericGetCompBool)
    {
        var method = tsl.Methods.First(m => m.Name == methodName);
        var inst = new GenericInstanceMethod(module.ImportReference(genericGetCompBool));
        inst.GenericArguments.Add(elementType);
        var il = method.Body.GetILProcessor();
        var callInstr = method.Body.Instructions.FirstOrDefault(ins =>
            ins.OpCode == OpCodes.Callvirt &&
            ins.Operand is MethodReference mr &&
            mr.Name == "GetComponentsInChildren");
        if (callInstr != null)
        {
            il.InsertBefore(callInstr, il.Create(OpCodes.Ldc_I4_1));
            callInstr.Operand = inst;
        }
    }

    private static void BuildCullMethod(
        MethodDefinition mCull,
        ModuleDefinition module,
        FieldDefinition fListTrans,
        FieldDefinition fLeft,
        FieldDefinition fRight,
        FieldDefinition fLastCullX,
        TypeDefinition tTransform,
        TypeDefinition tVector3,
        TypeDefinition tGameObject,
        MethodReference mGetLocalPos,
        MethodReference mGetChildCount,
        MethodReference mGetChild,
        FieldReference fVecX,
        MethodReference mGetGameObject,
        MethodReference mGetActiveSelf,
        MethodReference mSetActive,
        MethodReference mObjEq,
        MethodReference mObjNeq,
        MethodReference mMathfAbs)
    {
        mCull.Body.InitLocals = true;
        var floatType = module.TypeSystem.Single;
        var intType = module.TypeSystem.Int32;
        var boolType = module.TypeSystem.Boolean;
        var importedTransform = module.ImportReference(tTransform);
        var importedVec3 = module.ImportReference(tVector3);
        var importedGo = module.ImportReference(tGameObject);

        var vCurX = new VariableDefinition(floatType);
        var vMinX = new VariableDefinition(floatType);
        var vMaxX = new VariableDefinition(floatType);
        var vCount = new VariableDefinition(intType);
        var vIdx = new VariableDefinition(intType);
        var vChild = new VariableDefinition(importedTransform);
        var vGo = new VariableDefinition(importedGo);
        var vScreenX = new VariableDefinition(floatType);
        var vVis = new VariableDefinition(boolType);
        var vTmpVec = new VariableDefinition(importedVec3);

        mCull.Body.Variables.Add(vCurX);
        mCull.Body.Variables.Add(vMinX);
        mCull.Body.Variables.Add(vMaxX);
        mCull.Body.Variables.Add(vCount);
        mCull.Body.Variables.Add(vIdx);
        mCull.Body.Variables.Add(vChild);
        mCull.Body.Variables.Add(vGo);
        mCull.Body.Variables.Add(vScreenX);
        mCull.Body.Variables.Add(vVis);
        mCull.Body.Variables.Add(vTmpVec);

        var il = mCull.Body.GetILProcessor();

        var retLabel = il.Create(OpCodes.Ret);
        var boundsOkLabel = il.Create(OpCodes.Nop);
        var loopCheckLabel = il.Create(OpCodes.Nop);
        var loopBodyLabel = il.Create(OpCodes.Nop);
        var loopNextLabel = il.Create(OpCodes.Nop);

        // if (ListTrans == null) return;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fListTrans);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, mObjEq);
        il.Emit(OpCodes.Brtrue, retLabel);

        // float curX = ListTrans.localPosition.x;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fListTrans);
        il.Emit(OpCodes.Callvirt, mGetLocalPos);
        il.Emit(OpCodes.Stloc, vTmpVec);
        il.Emit(OpCodes.Ldloca, vTmpVec);
        il.Emit(OpCodes.Ldfld, fVecX);
        il.Emit(OpCodes.Stloc, vCurX);

        // if (Mathf.Abs(curX - _lastCullX) < 20f) return;
        il.Emit(OpCodes.Ldloc, vCurX);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fLastCullX);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Call, mMathfAbs);
        il.Emit(OpCodes.Ldc_R4, 20f);
        il.Emit(OpCodes.Blt, retLabel);

        // _lastCullX = curX;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, vCurX);
        il.Emit(OpCodes.Stfld, fLastCullX);

        // float minX = -1100f; float maxX = 1100f;
        il.Emit(OpCodes.Ldc_R4, -1100f);
        il.Emit(OpCodes.Stloc, vMinX);
        il.Emit(OpCodes.Ldc_R4, 1100f);
        il.Emit(OpCodes.Stloc, vMaxX);

        // if (Left != null && Right != null) { minX = Left.localPosition.x - 280f; maxX = Right.localPosition.x + 280f; }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fLeft);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, mObjNeq);
        il.Emit(OpCodes.Brfalse, boundsOkLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fRight);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, mObjNeq);
        il.Emit(OpCodes.Brfalse, boundsOkLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fLeft);
        il.Emit(OpCodes.Callvirt, mGetLocalPos);
        il.Emit(OpCodes.Stloc, vTmpVec);
        il.Emit(OpCodes.Ldloca, vTmpVec);
        il.Emit(OpCodes.Ldfld, fVecX);
        il.Emit(OpCodes.Ldc_R4, 280f);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, vMinX);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fRight);
        il.Emit(OpCodes.Callvirt, mGetLocalPos);
        il.Emit(OpCodes.Stloc, vTmpVec);
        il.Emit(OpCodes.Ldloca, vTmpVec);
        il.Emit(OpCodes.Ldfld, fVecX);
        il.Emit(OpCodes.Ldc_R4, 280f);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, vMaxX);

        il.Append(boundsOkLabel);

        // int count = ListTrans.childCount;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fListTrans);
        il.Emit(OpCodes.Callvirt, mGetChildCount);
        il.Emit(OpCodes.Stloc, vCount);

        // for (int i = 0; i < count; i++)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, vIdx);
        il.Emit(OpCodes.Br, loopCheckLabel);

        il.Append(loopBodyLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fListTrans);
        il.Emit(OpCodes.Ldloc, vIdx);
        il.Emit(OpCodes.Callvirt, mGetChild);
        il.Emit(OpCodes.Stloc, vChild);

        il.Emit(OpCodes.Ldloc, vChild);
        il.Emit(OpCodes.Callvirt, mGetGameObject);
        il.Emit(OpCodes.Stloc, vGo);

        // float screenX = child.localPosition.x + curX;
        il.Emit(OpCodes.Ldloc, vChild);
        il.Emit(OpCodes.Callvirt, mGetLocalPos);
        il.Emit(OpCodes.Stloc, vTmpVec);
        il.Emit(OpCodes.Ldloca, vTmpVec);
        il.Emit(OpCodes.Ldfld, fVecX);
        il.Emit(OpCodes.Ldloc, vCurX);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, vScreenX);

        // bool vis = screenX >= minX && screenX <= maxX;
        var falseVisLabel = il.Create(OpCodes.Ldc_I4_0);
        var storeVisLabel = il.Create(OpCodes.Stloc, vVis);
        il.Emit(OpCodes.Ldloc, vScreenX);
        il.Emit(OpCodes.Ldloc, vMinX);
        il.Emit(OpCodes.Blt, falseVisLabel);
        il.Emit(OpCodes.Ldloc, vScreenX);
        il.Emit(OpCodes.Ldloc, vMaxX);
        il.Emit(OpCodes.Cgt);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Br, storeVisLabel);
        il.Append(falseVisLabel);
        il.Append(storeVisLabel);

        // if (go.activeSelf != vis) go.SetActive(vis);
        il.Emit(OpCodes.Ldloc, vGo);
        il.Emit(OpCodes.Callvirt, mGetActiveSelf);
        il.Emit(OpCodes.Ldloc, vVis);
        il.Emit(OpCodes.Beq, loopNextLabel);

        il.Emit(OpCodes.Ldloc, vGo);
        il.Emit(OpCodes.Ldloc, vVis);
        il.Emit(OpCodes.Callvirt, mSetActive);

        il.Append(loopNextLabel);
        il.Emit(OpCodes.Ldloc, vIdx);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, vIdx);

        il.Append(loopCheckLabel);
        il.Emit(OpCodes.Ldloc, vIdx);
        il.Emit(OpCodes.Ldloc, vCount);
        il.Emit(OpCodes.Blt, loopBodyLabel);

        il.Append(retLabel);
    }

    private static void PatchDeathCamera(ModuleDefinition module)
    {
        var dc = module.Types.FirstOrDefault(t => t.Name == "DeathCamera")
            ?? throw new InvalidOperationException("DeathCamera type not found");

        var fCurIndex = dc.Fields.First(f => f.Name == "_curIndex");
        var fEmy = dc.Fields.First(f => f.Name == "_emy");
        var fEmyTurret = dc.Fields.First(f => f.Name == "_emyTurret");
        var fIsBackToMe = dc.Fields.First(f => f.Name == "_isBackToMe");
        var mSightAndCamMove = dc.Methods.First(m => m.Name == "SightAndCamMove");

        // 1) DeathCamera.ChangeToDeath(long emyGuid): guard _emy?.TankStateSync?.TurretSync before reading .Trans
        var mChange = dc.Methods.First(m => m.Name == "ChangeToDeath" && m.Parameters.Count == 1);
        {
            var il = mChange.Body.GetILProcessor();
            var instrs = mChange.Body.Instructions;
            var callTurret = instrs.First(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr && mr.Name == "get_Trans");
            int idxLoadThis = -1;
            for (int i = instrs.IndexOf(callTurret) - 1; i >= 0; i--)
            {
                if (instrs[i].OpCode == OpCodes.Ldarg_0 && i + 1 < instrs.Count && instrs[i + 1].OpCode == OpCodes.Ldarg_0)
                {
                    idxLoadThis = i;
                    break;
                }
            }
            if (idxLoadThis < 0)
                throw new InvalidOperationException("Could not locate _emyTurret assignment in DeathCamera.ChangeToDeath(long)");

            var stfldTurret = instrs.First(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference fr && fr.Name == "_emyTurret");
            var nextAfterStore = stfldTurret.Next;
            var targetStart = instrs[idxLoadThis];

            var mGetStateSync = ((MethodReference)instrs.First(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr && mr.Name == "get_TankStateSync").Operand);
            var mGetTurretSync = ((MethodReference)instrs.First(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr && mr.Name == "get_TurretSync").Operand);

            var unityAsm = module.AssemblyReferences.First(r => r.Name == "UnityEngine");
            var unityMain = module.AssemblyResolver.Resolve(unityAsm).MainModule;
            var tObject = unityMain.Types.First(t => t.FullName == "UnityEngine.Object");
            var mObjNeq = module.ImportReference(tObject.Methods.First(m => m.Name == "op_Inequality"));

            // Insert null guards before targetStart and redirect branches targeting targetStart to guardStart
            var guardStart = il.Create(OpCodes.Ldarg_0);
            foreach (var ins in instrs)
            {
                if (ins.Operand == targetStart)
                    ins.Operand = guardStart;
            }

            il.InsertBefore(targetStart, guardStart);
            il.InsertBefore(targetStart, il.Create(OpCodes.Ldfld, fEmy));
            il.InsertBefore(targetStart, il.Create(OpCodes.Ldnull));
            il.InsertBefore(targetStart, il.Create(OpCodes.Call, mObjNeq));
            il.InsertBefore(targetStart, il.Create(OpCodes.Brfalse, nextAfterStore));

            il.InsertBefore(targetStart, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(targetStart, il.Create(OpCodes.Ldfld, fEmy));
            il.InsertBefore(targetStart, il.Create(OpCodes.Callvirt, mGetStateSync));
            il.InsertBefore(targetStart, il.Create(OpCodes.Ldnull));
            il.InsertBefore(targetStart, il.Create(OpCodes.Call, mObjNeq));
            il.InsertBefore(targetStart, il.Create(OpCodes.Brfalse, nextAfterStore));

            il.InsertBefore(targetStart, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(targetStart, il.Create(OpCodes.Ldfld, fEmy));
            il.InsertBefore(targetStart, il.Create(OpCodes.Callvirt, mGetStateSync));
            il.InsertBefore(targetStart, il.Create(OpCodes.Callvirt, mGetTurretSync));
            il.InsertBefore(targetStart, il.Create(OpCodes.Brfalse, nextAfterStore));
        }

        // 2) DeathCamera.UpdateDeath():
        //    - If _curIndex == 0 (already spectating an ally), go straight to SightAndCamMove() so killer cam never steals focus.
        //    - Set _curIndex = 1 BEFORE SightAndCamMove() on killer cam (eliminates 1-frame snap back to _deathFocus).
        //    - Remove the dead 4.5s freeze guard before else-branch SightAndCamMove().
        var mUpdate = dc.Methods.First(m => m.Name == "UpdateDeath" && m.Parameters.Count == 0);
        {
            var il = mUpdate.Body.GetILProcessor();
            var instrs = mUpdate.Body.Instructions;

            // Find call to SightAndCamMove in killer branch and in fallback branch
            var sightCalls = instrs.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == mSightAndCamMove.Name).ToList();
            if (sightCalls.Count != 2)
                throw new InvalidOperationException($"Expected 2 SightAndCamMove calls in DeathCamera.UpdateDeath, found {sightCalls.Count}");

            var fallbackSightCall = sightCalls[1].Previous; // ldarg.0 before last SightAndCamMove()

            // Skip lookSlf/lookEmy when _curIndex == 0: insert after initial `brfalse ret`
            var firstBrfalse = instrs.First(i => i.OpCode == OpCodes.Brfalse || i.OpCode == OpCodes.Brfalse_S);
            var afterDeathCheck = firstBrfalse.Next;
            il.InsertBefore(afterDeathCheck, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(afterDeathCheck, il.Create(OpCodes.Ldfld, fCurIndex));
            il.InsertBefore(afterDeathCheck, il.Create(OpCodes.Brfalse, fallbackSightCall));

            // In fallback branch, bypass the `> 0.5f` time freeze by redirecting branches targeting the time check directly to fallbackSightCall
            var bleFallback = instrs.Last(i => i.OpCode == OpCodes.Ble_Un || i.OpCode == OpCodes.Ble_Un_S || i.OpCode == OpCodes.Ble || i.OpCode == OpCodes.Ble_S);
            // Redirect any branch landing between end of killer branch and bleFallback -> fallbackSightCall
            // Find the branch target originally pointing to `call Time.get_time()` of the `else if` (immediately after `br` from `TKLog.Log`)
            var tkLogCall = instrs.First(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == "Log");
            var endOfEmyBr = tkLogCall.Next; // br IL_0164
            var elseIfStart = endOfEmyBr.Next;
            foreach (var ins in instrs)
            {
                if (ins.Operand == elseIfStart)
                    ins.Operand = fallbackSightCall;
            }
            // Replace bleFallback condition so else-branch runs unconditionally if reached linearly
            bleFallback.OpCode = OpCodes.Nop;
            bleFallback.Operand = null;
            for (var cur = elseIfStart; cur != fallbackSightCall; cur = cur.Next)
            {
                cur.OpCode = OpCodes.Nop;
                cur.Operand = null;
            }

            // Before the first SightAndCamMove (killer branch), set _curIndex = 1 before calling SightAndCamMove
            var killerSightLdarg = sightCalls[0].Previous;
            var curIdxStart = il.Create(OpCodes.Ldarg_0);
            foreach (var ins in instrs)
            {
                if (ins.Operand == killerSightLdarg)
                    ins.Operand = curIdxStart;
            }
            il.InsertBefore(killerSightLdarg, curIdxStart);
            il.InsertBefore(killerSightLdarg, il.Create(OpCodes.Ldfld, fIsBackToMe));
            il.InsertBefore(killerSightLdarg, il.Create(OpCodes.Brtrue, killerSightLdarg));
            il.InsertBefore(killerSightLdarg, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(killerSightLdarg, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(killerSightLdarg, il.Create(OpCodes.Stfld, fCurIndex));
        }

        Console.WriteLine("Patched DeathCamera.ChangeToDeath & DeathCamera.UpdateDeath -> smooth ally switching & null safety.");
    }
}

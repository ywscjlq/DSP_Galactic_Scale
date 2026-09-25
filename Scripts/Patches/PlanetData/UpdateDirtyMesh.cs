using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace GalacticScale
{
    public partial class PatchOnPlanetData
    {
        //Strategy: 1) Remove checks for PlanetData.scale; 2) Convert GetModPlane to use our Int version
        // 1) find all calls to ldArg.0
        // if the following instruction is ldfld float32 PlanetData::scale
        //    change ldarg.0 to OpCodes.Nop
        //    change the following instruction to ldc.r4 1
        // 2) find all calls to GetModPlane
        //
        // 0.10.35 note: the game split PlanetData.UpdateDirtyMesh into UpdateDirtyMesh(int) plus the
        // extracted helpers UpdateDirtyMeshVertices(int) / UpdateDirtyMeshCollider(int). Both targets
        // of this transpiler (PlanetData.scale loads and PlanetRawData.GetModPlane calls) now live in
        // UpdateDirtyMeshVertices, so the patch must be anchored there. Anchoring on UpdateDirtyMesh
        // still resolves (it exists) but matches nothing, which is why this used to report
        // "this.scale loads not found" / "GetModPlane calls not found" on 0.10.35.
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(PlanetData), "UpdateDirtyMeshVertices")]
        public static IEnumerable<CodeInstruction> UpdateDirtyMeshTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            // NOTE: heightData offset now applied in ModelingPlanetMain prefix, not needed here
            // because heightData is already in absolute values when UpdateDirtyMesh runs
            var scalePatched = 0;
            var modPlanePatched = 0;

            for (var i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Ldarg_0 && i < codes.Count - 1)
                    // This condition removes references to this.scale. First we just check for the IL equivalent of "this."
                    // We stop checking this 1 early because we operate on both the current AND following line when we do anything
                {
                    // Check if the field we're reading from "this." is scale
                    if (codes[i + 1].LoadsField(typeof(PlanetData).GetField("scale")))
                    {
                        // Prevent "this." from being added to the stack (ordinarily, the field reference would remove it from the stack)
                        codes[i] = new CodeInstruction(OpCodes.Nop);
                        // Instead load the fixed value 1, as a float32
                        codes[i + 1] = new CodeInstruction(OpCodes.Ldc_R4, 1f);
                        scalePatched++;
                    }
                }
                else if (IsCallToGetModPlane(codes[i]))
                    // This condition finds calls to PlanetRawData.GetModPlane (which returns a short).
                    // The JIT/compiler emits this as `callvirt`, not `call` -- Roslyn uses callvirt for
                    // instance calls on classes (null-check semantics) even when the method is not
                    // virtual, and PlanetRawData.GetModPlane is a plain non-virtual class method. So a
                    // CodeInstructionExtensions.Calls(...) check (which only matches OpCodes.Call) can
                    // never see it. That is why this branch reported "GetModPlane calls not found" on
                    // 0.10.35. Match both opcodes and preserve whichever one was there.
                {
                    // We instead call PlanetRawDataExtension.GetModPlaneInt (which returns an int).
                    // All existing calls to GetModPlane cast the result to a float, anyway, so the
                    // short -> int return-type change is absorbed by that existing cast. Stack shape is
                    // identical (receiver objectref + int32 index), so this is a drop-in replacement --
                    // but keep the original `call` vs `callvirt` form so the rewritten IL stays valid.
                    codes[i] = new CodeInstruction(codes[i].opcode, typeof(PlanetRawDataExtension).GetMethod("GetModPlaneInt"));
                    modPlanePatched++;
                }
            }

            if (scalePatched == 0)
                GS2.Error("PlanetData.UpdateDirtyMesh transpiler: this.scale loads not found (game update changed the method?). Mesh updates may double-apply planet scale.");
            if (modPlanePatched == 0)
                GS2.Error("PlanetData.UpdateDirtyMesh transpiler: GetModPlane calls not found (game update changed the method?). Height mods on planets larger than ~327 radius may overflow.");

            return codes.AsEnumerable();
        }

        // Matches both `call` and `callvirt` forms of PlanetRawData.GetModPlane.
        // CodeInstructionExtensions.Calls() only matches OpCodes.Call, but Roslyn emits callvirt for
        // instance calls on classes even when the target is not virtual -- which is the case here.
        private static bool IsCallToGetModPlane(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
                return false;

            var target = instruction.operand as MethodInfo;
            if (target == null || target.Name != "GetModPlane")
                return false;

            var declaringType = target.DeclaringType;
            return declaringType != null && declaringType.FullName == typeof(PlanetRawData).FullName;
        }
    }
}

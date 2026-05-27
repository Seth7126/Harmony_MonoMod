using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib
{
#pragma warning disable CS1570 // XML comment has badly formed XML
    /// <summary>
    /// Post-transpiler IL fixup for Mono runtime compatibility.
    /// Runs after all transpilers complete but before IL emission.
    ///
    /// Fix 1: int*float mul type mismatch
    /// CoreCLR's JIT silently coerces int*float mul results; Mono's JIT doesn't,
    /// causing "Invalid IL code" errors. Inserts conv.r4 + conv.i4 where needed.
    ///
    /// Fix 2: Stale Nullable<T> constructor after call replacement
    /// When a transpiler replaces Method(Nullable<T>) with Method(T) without removing
    /// the preceding newobj Nullable<T>::.ctor(T), the stack carries Nullable<T> where
    /// T is expected. On Mono, the extra hasValue bytes corrupt the value type's fields.
    /// Uses stack-depth tracking to distinguish this from legitimate Nullable usage.
    ///
    /// Fix 3: Raw local variable index type mismatch
    /// Transpilers may use raw integer indices (e.g., Stloc_S, 20) instead of
    /// LocalBuilder references. If the original local at that index has a different type,
    /// CoreCLR ignores it but Mono throws InvalidProgramException. This infers the
    /// correct type from surrounding IL, declares new typed locals, and redirects
    /// references. Falls back to searching existing LocalBuilders when inference fails,
    /// and handles shifted local indices from game recompilation.
    ///
    /// Fix 4: Bitwise op with object reference operand
    /// Transpilers commonly use `isinst Type` followed by `or`/`and` to combine a
    /// type-check with a boolean. CoreCLR's JIT treats the reference as a pointer-width
    /// integer; Mono's verifier rejects it as invalid IL. Inserts `ldnull` + `cgt.un`
    /// after `isinst` to convert the reference to int32 (0 or 1) before the bitwise op.
    /// </summary>
    internal static class MonoILFixup
#pragma warning restore CS1570 // XML comment has badly formed XML
    {
        private const string Tag = "[MonoILFixup]";

        // Detect Mono at startup: .NET 8 Mono has internal RuntimeStructs in System.Private.CoreLib
        private static readonly bool IsMono =
            typeof(object).Assembly.GetType("Mono.RuntimeStructs") != null;

        internal static List<CodeInstruction> Apply(
            List<CodeInstruction> instructions,
            ILGenerator il = null
        )
        {
            if (!IsMono)
                return instructions;

            // Fix 1: int*float mul type mismatch
            for (int i = 3; i < instructions.Count; i++)
            {
                // Match: ldc.i4.4 + mul + ldloc(float) + mul
                // Only match when the ldloc loads a float local (SpaceCore's scale factors).
                // This avoids false positives on int*int patterns in other methods.
                if (
                    instructions[i - 3].opcode == OpCodes.Ldc_I4_4
                    && instructions[i - 2].opcode == OpCodes.Mul
                    && IsLdlocFloat(instructions[i - 1])
                    && instructions[i].opcode == OpCodes.Mul
                )
                {
                    // Insert conv.r4 before ldloc (int->float) so mul is float*float (valid IL),
                    // then conv.i4 after mul (float->int) since the consumer expects int.
                    instructions.Insert(i - 1, new CodeInstruction(OpCodes.Conv_R4));
                    i++; // i now points to the mul again after shift
                    instructions.Insert(i + 1, new CodeInstruction(OpCodes.Conv_I4));
                    i++; // skip past inserted conv.i4
                }
            }

            // Fix 2: Nullable<T> -> T mismatch
            FixNullableMismatch(instructions);

            // Fix 3: Raw local variable index type mismatch
            if (il != null)
                FixRawLocalIndices(instructions, il);

            // Fix 4: isinst feeding into bitwise or/and/xor
            FixIsinstBitwiseOp(instructions);

            return instructions;
        }

        /// <summary>
        /// For each newobj Nullable(T)::ctor(T), use stack-depth tracking to find
        /// the instruction that actually consumes this Nullable value. If that consumer
        /// is a call/callvirt expecting T (not Nullable(T)), the wrapper is stale
        /// (left over from before a transpiler replaced the call target). Nop it out.
        ///
        /// Stack tracking prevents false positives where a Nullable is legitimately
        /// stored to a field (stfld) but a later unrelated call happens to take T.
        /// </summary>
        private static void FixNullableMismatch(List<CodeInstruction> instructions)
        {
            var nullableGenericDef = typeof(Nullable<>);

            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].opcode != OpCodes.Newobj)
                    continue;
                if (instructions[i].operand is not ConstructorInfo ctor)
                    continue;

                var declType = ctor.DeclaringType;
                if (
                    declType == null
                    || !declType.IsGenericType
                    || declType.GetGenericTypeDefinition() != nullableGenericDef
                )
                    continue;

                var valueType = Nullable.GetUnderlyingType(declType);
                if (valueType == null)
                    continue;

                // Track how many items are on the stack ABOVE our Nullable<T>.
                // When an instruction needs to pop more than 'above', it consumes our value.
                int above = 0;

                for (int j = i + 1; j < instructions.Count; j++)
                {
                    var op = instructions[j].opcode;

                    // Stop at throw/return -- execution doesn't continue.
                    // Don't break at branches: NPC.draw has ternary expressions
                    // (flip ? FlipH : None) that generate branch patterns between
                    // newobj and callvirt Draw. The stack tracking tolerates the
                    // slight inflation from processing both branches linearly.
                    // Leave/PopAll bails via GetStackPop returning -1.
                    if (op.FlowControl == FlowControl.Throw || op.FlowControl == FlowControl.Return)
                        break;

                    int popCount = GetStackPop(instructions[j]);
                    int pushCount = GetStackPush(instructions[j]);

                    if (popCount < 0 || pushCount < 0)
                        break;

                    if (popCount > above)
                    {
                        // This instruction reaches our Nullable<T> on the stack.
                        if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                        {
                            ParameterInfo[] parameters;
                            string methodName = "?";
                            if (instructions[j].operand is MethodInfo mi)
                            {
                                parameters = mi.GetParameters();
                                methodName = $"{mi.DeclaringType?.Name}.{mi.Name}";
                            }
                            else if (instructions[j].operand is ConstructorInfo ci)
                            {
                                parameters = ci.GetParameters();
                                methodName = $"{ci.DeclaringType?.Name}::.ctor";
                            }
                            else
                                break;

                            bool consumesNullable = false;
                            bool consumesValue = false;
                            foreach (var p in parameters)
                            {
                                if (p.ParameterType == declType)
                                    consumesNullable = true;
                                else if (p.ParameterType == valueType)
                                    consumesValue = true;
                            }

                            if (consumesValue && !consumesNullable)
                            {
                                instructions[i].opcode = OpCodes.Nop;
                                instructions[i].operand = null;
                                Console.WriteLine(
                                    $"{Tag} FIXED: Nopped newobj Nullable<{valueType.Name}> at {i} (consumer {methodName} expects {valueType.Name})"
                                );
                            }
                        }
                        break;
                    }

                    above -= popCount;
                    above += pushCount;
                }
            }
        }

        /// <summary>
        /// Finds `isinst` instructions whose result feeds into a bitwise op (or/and/xor)
        /// and inserts `ldnull` + `cgt.un` to convert the object reference to int32.
        /// Uses stack-depth tracking to match each isinst to its consumer.
        /// </summary>
        private static void FixIsinstBitwiseOp(List<CodeInstruction> instructions)
        {
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].opcode != OpCodes.Isinst)
                    continue;

                // Track stack depth above our isinst result to find its consumer
                int above = 0;
                for (int j = i + 1; j < instructions.Count; j++)
                {
                    var op = instructions[j].opcode;

                    if (op.FlowControl == FlowControl.Throw || op.FlowControl == FlowControl.Return)
                        break;

                    int popCount = GetStackPop(instructions[j]);
                    int pushCount = GetStackPush(instructions[j]);
                    if (popCount < 0 || pushCount < 0)
                        break;

                    if (popCount > above)
                    {
                        if (op == OpCodes.Or || op == OpCodes.And || op == OpCodes.Xor)
                        {
                            // Insert ldnull + cgt.un right after isinst to convert ref to 0/1
                            instructions.Insert(i + 1, new CodeInstruction(OpCodes.Ldnull));
                            instructions.Insert(i + 2, new CodeInstruction(OpCodes.Cgt_Un));
                            Console.WriteLine(
                                $"{Tag} FIXED: Inserted ref->int conversion after isinst at {i} (consumed by {op} at {j + 2})"
                            );
                            i += 2; // skip past inserted instructions
                        }
                        break;
                    }

                    above -= popCount;
                    above += pushCount;
                }
            }
        }

        /// <summary>Returns how many values an instruction pops, or -1 if unknown.</summary>
        private static int GetStackPop(CodeInstruction instr)
        {
            switch (instr.opcode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0:
                    return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref:
                    return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi:
                    return 2;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref:
                case StackBehaviour.Popref_popi_pop1:
                    return 3;
                case StackBehaviour.Varpop:
                    if (instr.operand is MethodInfo mi)
                        return mi.GetParameters().Length + (mi.IsStatic ? 0 : 1);
                    if (instr.operand is ConstructorInfo ci)
                        return ci.GetParameters().Length;
                    return -1;
                default:
                    return -1;
            }
        }

        /// <summary>Returns how many values an instruction pushes, or -1 if unknown.</summary>
        private static int GetStackPush(CodeInstruction instr)
        {
            switch (instr.opcode.StackBehaviourPush)
            {
                case StackBehaviour.Push0:
                    return 0;
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref:
                    return 1;
                case StackBehaviour.Push1_push1:
                    return 2;
                case StackBehaviour.Varpush:
                    if (instr.operand is MethodInfo mi)
                        return mi.ReturnType == typeof(void) ? 0 : 1;
                    if (instr.operand is ConstructorInfo)
                        return 1;
                    return -1;
                default:
                    return -1;
            }
        }

        /// <summary>
        /// Check if the instruction is a ldloc that loads a float (System.Single) local.
        /// </summary>
        private static bool IsLdlocFloat(CodeInstruction instr)
        {
            if (instr.opcode == OpCodes.Ldloc || instr.opcode == OpCodes.Ldloc_S)
            {
                if (instr.operand is LocalBuilder lb)
                    return lb.LocalType == typeof(float);
                if (instr.operand is LocalVariableInfo lvi)
                    return lvi.LocalType == typeof(float);
            }
            return false;
        }

        /// <summary>
        /// Fix 3: Transpilers create instructions with raw integer local indices
        /// (e.g., new CodeInstruction(OpCodes.Stloc_S, 20)) that reuse locals from
        /// the original method. When the original local at that index has a different
        /// type than what the transpiler stores, Mono rejects it. Original method
        /// instructions use LocalBuilder operands (from MethodBodyReader), so raw
        /// integer operands reliably identify transpiler-injected instructions.
        /// </summary>
        private static void FixRawLocalIndices(List<CodeInstruction> instructions, ILGenerator il)
        {
            // Group instructions by raw local index
            var rawIndexUsages = new Dictionary<int, List<int>>();

            for (int i = 0; i < instructions.Count; i++)
            {
                int rawIndex = GetRawLocalIndex(instructions[i]);
                if (rawIndex < 0)
                    continue;

                if (!rawIndexUsages.TryGetValue(rawIndex, out var list))
                {
                    list = new List<int>();
                    rawIndexUsages[rawIndex] = list;
                }
                list.Add(i);
            }

            if (rawIndexUsages.Count == 0)
                return;

            foreach (var kvp in rawIndexUsages)
            {
                int rawIndex = kvp.Key;
                var instrIndices = kvp.Value;

                // Infer type from stloc: look at the preceding instruction's result type
                Type localType = null;
                foreach (int idx in instrIndices)
                {
                    if (IsStlocOpcode(instructions[idx].opcode) && idx > 0)
                    {
                        localType = InferStackType(instructions[idx - 1]);
                        if (localType != null)
                            break;
                    }
                }

                // Fallback: infer from ldloca usage (instance method call on the local)
                if (localType == null)
                {
                    foreach (int idx in instrIndices)
                    {
                        if (
                            IsLdlocaOpcode(instructions[idx].opcode)
                            && idx + 1 < instructions.Count
                        )
                        {
                            localType = InferLdlocaType(instructions[idx + 1]);
                            if (localType != null)
                                break;
                        }
                    }
                }

                if (localType == null)
                {
                    // Fallback: find an existing LocalBuilder for this index from the
                    // original method's instructions. The transpiler used a raw int because
                    // it doesn't have access to the LocalBuilder, but MethodBodyReader
                    // created LocalBuilder objects for all original locals.
                    var existingLocal = FindLocalBuilderForIndex(instructions, rawIndex);
                    if (existingLocal != null)
                    {
                        // Verify the local's type is compatible with how it's consumed.
                        // Game updates can shift local variable indices (e.g., mod targets
                        // index 17 expecting Item, but a new bool was inserted at 17 and
                        // Item moved to 18). CoreCLR tolerates this; Mono doesn't.
                        Type consumerExpectedType = null;
                        foreach (int idx in instrIndices)
                        {
                            if (
                                !IsStlocOpcode(instructions[idx].opcode)
                                && idx + 1 < instructions.Count
                            )
                            {
                                var expected = InferConsumerThisType(instructions[idx + 1]);
                                if (
                                    expected != null
                                    && !expected.IsAssignableFrom(existingLocal.LocalType)
                                )
                                {
                                    consumerExpectedType = expected;
                                    break;
                                }
                            }
                        }

                        if (consumerExpectedType != null)
                        {
                            // Type mismatch: search nearby locals for one with a compatible type
                            var betterLocal = FindCompatibleLocalNearIndex(
                                instructions,
                                rawIndex,
                                consumerExpectedType
                            );
                            if (betterLocal != null)
                            {
                                Console.WriteLine(
                                    $"{Tag} FIXED: Raw local index {rawIndex} type mismatch "
                                        + $"(existing: {existingLocal.LocalType.Name}, consumer expects: {consumerExpectedType.Name}). "
                                        + $"Redirected to nearby local {betterLocal.LocalIndex} (type: {betterLocal.LocalType.Name})"
                                );
                                foreach (int idx in instrIndices)
                                    instructions[idx].operand = betterLocal;
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"{Tag} WARNING: Raw local index {rawIndex} type mismatch "
                                        + $"(existing: {existingLocal.LocalType.Name}, consumer expects: {consumerExpectedType.Name}). "
                                        + $"No compatible local found nearby, using existing local"
                                );
                                foreach (int idx in instrIndices)
                                    instructions[idx].operand = existingLocal;
                            }
                        }
                        else
                        {
                            Console.WriteLine(
                                $"{Tag} FIXED: Resolved raw local index {rawIndex} -> existing LocalBuilder {existingLocal.LocalIndex} (type: {existingLocal.LocalType.Name})"
                            );
                            foreach (int idx in instrIndices)
                                instructions[idx].operand = existingLocal;
                        }
                    }
                    else
                    {
                        Console.WriteLine(
                            $"{Tag} WARNING: Cannot infer type or find LocalBuilder for raw local index {rawIndex}, skipping"
                        );
                    }
                    continue;
                }

                var newLocal = il.DeclareLocal(localType);
                Console.WriteLine(
                    $"{Tag} FIXED: Redirected raw local index {rawIndex} -> new local {newLocal.LocalIndex} (type: {localType.Name})"
                );

                foreach (int idx in instrIndices)
                    instructions[idx].operand = newLocal;
            }
        }

        /// <summary>
        /// Returns the raw integer local index from a stloc/ldloc/ldloca instruction,
        /// or -1 if the operand is already a LocalBuilder/LocalVariableInfo.
        /// </summary>
        private static int GetRawLocalIndex(CodeInstruction instr)
        {
            var op = instr.opcode;
            if (
                op != OpCodes.Stloc
                && op != OpCodes.Stloc_S
                && op != OpCodes.Ldloc
                && op != OpCodes.Ldloc_S
                && op != OpCodes.Ldloca
                && op != OpCodes.Ldloca_S
            )
                return -1;

            if (instr.operand is LocalBuilder || instr.operand is LocalVariableInfo)
                return -1;

            if (instr.operand is int i)
                return i;
            if (instr.operand is byte b)
                return b;
            if (instr.operand is sbyte sb)
                return sb;
            if (instr.operand is short s)
                return s;

            return -1;
        }

        /// <summary>
        /// Scan all instructions for an existing LocalBuilder that matches the given
        /// local variable index. Original method instructions (from MethodBodyReader)
        /// use LocalBuilder operands; transpiler-injected instructions use raw ints.
        /// </summary>
        private static LocalBuilder FindLocalBuilderForIndex(
            List<CodeInstruction> instructions,
            int targetIndex
        )
        {
            foreach (var instr in instructions)
            {
                var op = instr.opcode;
                if (
                    op != OpCodes.Stloc
                    && op != OpCodes.Stloc_S
                    && op != OpCodes.Ldloc
                    && op != OpCodes.Ldloc_S
                    && op != OpCodes.Ldloca
                    && op != OpCodes.Ldloca_S
                )
                    continue;

                if (instr.operand is LocalBuilder lb && lb.LocalIndex == targetIndex)
                    return lb;
            }
            return null;
        }

        /// <summary>
        /// If the instruction is a call/callvirt on an instance method, return the declaring
        /// type (the expected type of 'this' on the stack). Returns null for static methods
        /// or non-call instructions.
        /// </summary>
        private static Type InferConsumerThisType(CodeInstruction consumer)
        {
            if (
                (consumer.opcode == OpCodes.Call || consumer.opcode == OpCodes.Callvirt)
                && consumer.operand is MethodInfo mi
                && !mi.IsStatic
            )
                return mi.DeclaringType;
            return null;
        }

        /// <summary>
        /// Search nearby local variable indices (within ±3 of targetIndex) for a LocalBuilder
        /// whose type is compatible with (assignable to) the expected type.
        /// This handles cases where a game update shifted local variable indices.
        /// Prefers closer indices since they're more likely to be the shifted local.
        /// </summary>
        private static LocalBuilder FindCompatibleLocalNearIndex(
            List<CodeInstruction> instructions,
            int targetIndex,
            Type expectedType
        )
        {
            int[] offsets = { 1, -1, 2, -2, 3 };
            foreach (int offset in offsets)
            {
                int candidateIndex = targetIndex + offset;
                if (candidateIndex < 0)
                    continue;

                var candidate = FindLocalBuilderForIndex(instructions, candidateIndex);
                if (candidate != null && expectedType.IsAssignableFrom(candidate.LocalType))
                    return candidate;
            }
            return null;
        }

        private static bool IsStlocOpcode(OpCode opcode) =>
            opcode == OpCodes.Stloc || opcode == OpCodes.Stloc_S;

        private static bool IsLdlocaOpcode(OpCode opcode) =>
            opcode == OpCodes.Ldloca || opcode == OpCodes.Ldloca_S;

        /// <summary>
        /// Infer the type of the value on top of the stack from the given instruction.
        /// Returns null if the type cannot be determined.
        /// </summary>
        private static Type InferStackType(CodeInstruction instr)
        {
            var op = instr.opcode;

            // call/callvirt -> return type
            if ((op == OpCodes.Call || op == OpCodes.Callvirt) && instr.operand is MethodInfo mi)
                return mi.ReturnType == typeof(void) ? null : mi.ReturnType;

            // newobj -> declaring type
            if (op == OpCodes.Newobj && instr.operand is ConstructorInfo ci)
                return ci.DeclaringType;

            // ldfld/ldsfld -> field type
            if ((op == OpCodes.Ldfld || op == OpCodes.Ldsfld) && instr.operand is FieldInfo fi)
                return fi.FieldType;

            // ldloc with LocalBuilder -> local type
            if ((op == OpCodes.Ldloc || op == OpCodes.Ldloc_S) && instr.operand is LocalBuilder lb)
                return lb.LocalType;

            // conv instructions
            if (op == OpCodes.Conv_I4 || op == OpCodes.Conv_Ovf_I4 || op == OpCodes.Conv_Ovf_I4_Un)
                return typeof(int);
            if (op == OpCodes.Conv_I8 || op == OpCodes.Conv_Ovf_I8 || op == OpCodes.Conv_Ovf_I8_Un)
                return typeof(long);
            if (op == OpCodes.Conv_R4)
                return typeof(float);
            if (op == OpCodes.Conv_R8)
                return typeof(double);

            // unbox.any -> the type
            if (op == OpCodes.Unbox_Any && instr.operand is Type ut)
                return ut;

            // castclass / isinst -> the type
            if ((op == OpCodes.Castclass || op == OpCodes.Isinst) && instr.operand is Type ct)
                return ct;

            // box -> object
            if (op == OpCodes.Box)
                return typeof(object);

            // ldstr -> string
            if (op == OpCodes.Ldstr)
                return typeof(string);

            // ldc constants
            if (
                op == OpCodes.Ldc_I4
                || op == OpCodes.Ldc_I4_S
                || op == OpCodes.Ldc_I4_0
                || op == OpCodes.Ldc_I4_1
                || op == OpCodes.Ldc_I4_2
                || op == OpCodes.Ldc_I4_3
                || op == OpCodes.Ldc_I4_4
                || op == OpCodes.Ldc_I4_5
                || op == OpCodes.Ldc_I4_6
                || op == OpCodes.Ldc_I4_7
                || op == OpCodes.Ldc_I4_8
                || op == OpCodes.Ldc_I4_M1
            )
                return typeof(int);
            if (op == OpCodes.Ldc_R4)
                return typeof(float);
            if (op == OpCodes.Ldc_R8)
                return typeof(double);
            if (op == OpCodes.Ldc_I8)
                return typeof(long);

            // ldnull -> object
            if (op == OpCodes.Ldnull)
                return typeof(object);

            return null;
        }

        /// <summary>
        /// Infer the type of a local from how ldloca's result is consumed.
        /// ldloca pushes a managed pointer (T&amp;) to the local.
        /// </summary>
        private static Type InferLdlocaType(CodeInstruction consumer)
        {
            // Instance method call: ldloca pushes this&, so the type is the declaring type
            if (
                (consumer.opcode == OpCodes.Call || consumer.opcode == OpCodes.Callvirt)
                && consumer.operand is MethodInfo mi
            )
            {
                if (!mi.IsStatic)
                    return mi.DeclaringType;

                // Static method with byref first param
                var parameters = mi.GetParameters();
                if (parameters.Length > 0 && parameters[0].ParameterType.IsByRef)
                    return parameters[0].ParameterType.GetElementType();
            }

            // initobj -> the type
            if (consumer.opcode == OpCodes.Initobj && consumer.operand is Type initType)
                return initType;

            return null;
        }
    }
}

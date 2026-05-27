using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HarmonyLib
{
    internal static class PatchFunctions
    {
        internal static List<MethodInfo> GetSortedPatchMethods(
            MethodBase original,
            Patch[] patches,
            bool debug
        ) => [.. new PatchSorter(patches, debug).Sort().Select(p => p.GetMethod(original))];

        private static List<Infix> GetInfixes(Patch[] patches) =>
            [.. patches.Select(p => new Infix(p))];

        internal static MethodInfo UpdateWrapper(MethodBase original, PatchInfo patchInfo)
        {
            var debug = patchInfo.Debugging || Harmony.DEBUG;

            var sortedPrefixes = GetSortedPatchMethods(original, patchInfo.prefixes, debug);
            var sortedPostfixes = GetSortedPatchMethods(original, patchInfo.postfixes, debug);
            var sortedTranspilers = GetSortedPatchMethods(original, patchInfo.transpilers, debug);
            var sortedFinalizers = GetSortedPatchMethods(original, patchInfo.finalizers, debug);
            var sortedInnerPrefixes = GetInfixes(patchInfo.innerprefixes);
            var sortedInnerPostfixes = GetInfixes(patchInfo.innerpostfixes);

            var patcher = new MethodCreator(
                new MethodCreatorConfig(
                    original,
                    null,
                    sortedPrefixes,
                    sortedPostfixes,
                    sortedTranspilers,
                    sortedFinalizers,
                    sortedInnerPrefixes,
                    sortedInnerPostfixes,
                    debug,
                    reversePatched: false
                )
            );
            var (replacement, finalInstructions) = patcher.CreateReplacement();
            if (replacement is null)
                throw new MissingMethodException(
                    $"Cannot create replacement for {original.FullDescription()}"
                );

            try
            {
                PatchTools.DetourMethod(original, replacement);
            }
            catch (Exception ex)
            {
                if (ex is InvalidProgramException)
                    DumpDMDDiagnostics(replacement, original, ex);
                throw HarmonyException.Create(ex, finalInstructions);
            }
            return replacement;
        }

        private static void DumpDMDDiagnostics(
            MethodInfo replacement,
            MethodBase original,
            Exception ex
        )
        {
            try
            {
                var dmdAsm = replacement?.DeclaringType?.Assembly;
                if (dmdAsm == null)
                    return;

                Console.WriteLine(
                    $"[Harmony.DMD] InvalidProgramException patching {original?.FullDescription()}"
                );
                Console.WriteLine($"[Harmony.DMD] DMD assembly: {dmdAsm.FullName}");
                Console.WriteLine($"[Harmony.DMD] DMD location: {dmdAsm.Location}");
                Console.WriteLine($"[Harmony.DMD] Error: {ex.Message}");

                var refs = dmdAsm.GetReferencedAssemblies();
                var loaded = AppDomain.CurrentDomain.GetAssemblies();
                Console.WriteLine($"[Harmony.DMD] Assembly references ({refs.Length}):");
                foreach (var asmRef in refs)
                {
                    bool found = false;
                    string loadedName = null;
                    foreach (var a in loaded)
                    {
                        if (a.GetName().Name == asmRef.Name)
                        {
                            found = true;
                            if (a.GetName().FullName != asmRef.FullName)
                                loadedName = a.GetName().FullName;
                            break;
                        }
                    }
                    if (!found)
                        Console.WriteLine($"[Harmony.DMD]   NOT FOUND: {asmRef.FullName}");
                    else if (loadedName != null)
                        Console.WriteLine(
                            $"[Harmony.DMD]   MISMATCH:  {asmRef.FullName} (loaded: {loadedName})"
                        );
                }

                var msg = ex.Message;
                var tokenIdx = msg.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
                if (tokenIdx >= 0)
                {
                    var end = tokenIdx + 2;
                    while (
                        end < msg.Length
                        && (
                            (msg[end] >= '0' && msg[end] <= '9')
                            || (msg[end] >= 'a' && msg[end] <= 'f')
                            || (msg[end] >= 'A' && msg[end] <= 'F')
                        )
                    )
                        end++;
                    var tokenStr = msg.Substring(tokenIdx, end - tokenIdx);
                    if (
                        int.TryParse(
                            tokenStr.Substring(2),
                            System.Globalization.NumberStyles.HexNumber,
                            null,
                            out var token
                        )
                    )
                    {
                        try
                        {
                            var resolved = dmdAsm.ManifestModule.ResolveMember(token);
                            Console.WriteLine(
                                $"[Harmony.DMD] Token {tokenStr} resolved to: {resolved}"
                            );
                        }
                        catch (Exception resolveEx)
                        {
                            Console.WriteLine(
                                $"[Harmony.DMD] Token {tokenStr} UNRESOLVABLE: {resolveEx.GetType().Name}: {resolveEx.Message}"
                            );
                        }
                    }
                }
            }
            catch (Exception diagEx)
            {
                Console.WriteLine($"[Harmony.DMD] Diagnostic dump failed: {diagEx.Message}");
            }
        }

        internal static MethodInfo ReversePatch(
            HarmonyMethod standin,
            MethodBase original,
            MethodInfo postTranspiler
        )
        {
            if (standin is null)
                throw new ArgumentNullException(nameof(standin));
            if (standin.method is null)
                throw new ArgumentNullException(
                    nameof(standin),
                    $"{nameof(standin)}.{nameof(standin.method)} is NULL"
                );

            var debug = (standin.debug ?? false) || Harmony.DEBUG;

            var transpilers = new List<MethodInfo>();
            if (standin.reversePatchType == HarmonyReversePatchType.Snapshot)
            {
                var info = Harmony.GetPatchInfo(original);
                transpilers.AddRange(GetSortedPatchMethods(original, [.. info.Transpilers], debug));
            }
            if (postTranspiler is not null)
                transpilers.Add(postTranspiler);

            var emptyFix = new List<MethodInfo>();
            var emptyInner = new List<Infix>();
            var patcher = new MethodCreator(
                new MethodCreatorConfig(
                    standin.method,
                    original,
                    emptyFix,
                    emptyFix,
                    transpilers,
                    emptyFix,
                    emptyInner,
                    emptyInner,
                    debug,
                    reversePatched: true
                )
            );
            var (replacement, finalInstructions) = patcher.CreateReplacement();
            if (replacement is null)
                throw new MissingMethodException(
                    $"Cannot create replacement for {standin.method.FullDescription()}"
                );

            try
            {
                PatchTools.DetourMethod(standin.method, replacement);
            }
            catch (Exception ex)
            {
                throw HarmonyException.Create(ex, finalInstructions);
            }

            return replacement;
        }
    }
}

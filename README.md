**This is a fork of [Pathoschild/Harmony](https://github.com/Pathoschild/Harmony), itself a fork of
[Harmony 2.4.2](https://github.com/pardeike/Harmony).** See
[pardeike/Harmony](https://github.com/pardeike/Harmony) for documentation and usage.

## What's different from Pathoschild's fork

### MonoILFixup

A post-transpiler IL fixup pass that corrects invalid IL that CoreCLR's JIT silently accepts but
Mono's JIT strictly rejects. Runs after all transpilers complete but before IL emission, and fixes:

1. **int\*float mul type mismatch**
   - Inserts `conv.r4`/`conv.i4` where CoreCLR silently coerces int\*float results but Mono throws "Invalid IL code" errors.

2. **Stale `Nullable<T>` constructor after call replacement**
   - When a transpiler replaces `Method(Nullable<T>)` with `Method(T)` without removing the preceding `newobj`, the extra
     `hasValue` bytes corrupt value type fields on Mono. Uses stack-depth tracking to distinguish
     this from legitimate Nullable usage.

3. **Raw local variable index type mismatch**
   - Transpilers using raw integer indices instead of `LocalBuilder` references can produce type mismatches that CoreCLR ignores but Mono throws `InvalidProgramException` for. Infers correct types from surrounding IL, declares new typed locals, and redirects references. Handles shifted local indices from game recompilation.

### Other Mono compatibility fixes

- **By-ref parameter handling**
  - On Mono, `ldnull` is incompatible with by-ref parameters (`type &`), causing "Invalid IL code" errors. Static method patches now use a default-initialized temp local and pass its address instead.

- **HasDefault parameter attribute stripping**
  - DMD wrappers don't need default values, and copying them causes Cecil to resolve the parameter type at write time, which fails for game assembly types. This strips `HasDefault` to prevent crashes on Mono.

- **CodeMatcher insert validation fix**
  - Fixes insert position validation to use `Pos < 0 || Pos > Length` instead of `IsInvalid`, allowing inserts at the end of the instruction list.

- **DMD diagnostic dump**
  - When `InvalidProgramException` occurs during patching, dumps assembly references, mismatched assemblies, and token resolution info for debugging.

### Build changes

- Targets `net8.0` only (removed `netstandard2.0` and multi-targeting)
- SDK 8.0.100 with `latestMajor` rollforward

## What's different from upstream Harmony

Same as Pathoschild's fork: generated method names include the patching Harmony IDs to help
troubleshoot error logs:

```c#
// with original Harmony
   at StardewValley.Farm.resetLocalState_Patch1(Object )

// with fork
   at StardewValley.Farm.resetLocalState_PatchedBy<Pathoschild.SmallBeachFarm>(Object )
```

## How to build

Requires [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) or later.

```bash
git clone https://github.com/Ekyso/Harmony.git
cd Harmony
dotnet build Lib.Harmony.Thin -c Release
```

Output in `Lib.Harmony.Thin/bin/Release/net8.0/`:

- `0Harmony.dll` — Harmony library
- `Mono.Cecil*.dll` — IL/metadata manipulation
- `MonoMod.*.dll` — runtime detour and utilities

## Build variants

The SMAPI community uses the `Lib.Harmony.Thin` version, since SMAPI manages the other
dependencies.

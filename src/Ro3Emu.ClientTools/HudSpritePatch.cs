using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Assembly-CSharp.dll -> Ro3Emu.ClientHooks.SpriteRegion.TextureOf(Sprite):
/// in HUDUber.Image::WriteImageToDynamic(), replaces UnityEngine.Sprite::get_texture() calls with
/// SpriteRegion.TextureOf so packed atlas sprites (such as overhead guild emblems from Guild_Icon) copy only their own
/// cell instead of the whole atlas page.
/// </summary>
public static class HudSpritePatch
{
    internal const string ImageType = "HUDUber.Image";
    internal const string WriteMethod = "WriteImageToDynamic";
    internal const string HookType = "Ro3Emu.ClientHooks.SpriteRegion";
    internal const string HookMethod = "TextureOf";

    public static string Patch(ModuleDefinition module, string hooksDll)
    {
        var image = module.GetType(ImageType) ?? throw new InvalidOperationException($"{ImageType} not found");
        var write = image.Methods.SingleOrDefault(m => m.Name == WriteMethod && !m.HasParameters)
            ?? throw new InvalidOperationException($"{ImageType}.{WriteMethod} not found");
        if (write.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == HookType && m.Name == HookMethod))
            return "hud sprite: already patched";

        using var hooks = AssemblyDefinition.ReadAssembly(hooksDll, new ReaderParameters { InMemory = true });
        var hookType = hooks.MainModule.GetType(HookType) ?? throw new InvalidOperationException($"{HookType} not found in the hooks");
        var textureOf = module.ImportReference(hookType.Methods.Single(m => m.Name == HookMethod && m.Parameters.Count == 1));

        int replaced = 0;
        foreach (var ins in write.Body.Instructions)
        {
            if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call) && ins.Operand is MethodReference mr
                && mr.Name == "get_texture" && mr.DeclaringType.FullName == "UnityEngine.Sprite")
            {
                ins.OpCode = OpCodes.Call;
                ins.Operand = textureOf;
                replaced++;
            }
        }
        if (replaced == 0) throw new InvalidOperationException($"Sprite.get_texture() call not found in {ImageType}.{WriteMethod}");
        return $"hud sprite: {ImageType}.{WriteMethod} x{replaced} -> SpriteRegion.TextureOf";
    }
}

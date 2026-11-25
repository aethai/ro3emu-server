using Mono.Cecil;
using Mono.Cecil.Cil;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// <see cref="HudSpritePatch"/> on synthetic assemblies: HUDUber.Image.WriteImageToDynamic calling
/// UnityEngine.Sprite.get_texture() and a hooks DLL providing Ro3Emu.ClientHooks.SpriteRegion.TextureOf(Sprite).
/// </summary>
public sealed class HudSpritePatchTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-hudsprite-" + Guid.NewGuid().ToString("N"));

    public HudSpritePatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static AssemblyDefinition GameAssembly()
    {
        var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Assembly-CSharp", new Version(0, 0)), "Assembly-CSharp", ModuleKind.Dll);
        var module = assembly.MainModule;
        var core = new AssemblyNameReference("UnityEngine.CoreModule", new Version(0, 0));
        module.AssemblyReferences.Add(core);
        var sprite = new TypeReference("UnityEngine", "Sprite", module, core);
        var tex2d = new TypeReference("UnityEngine", "Texture2D", module, core);
        var getTexture = new MethodReference("get_texture", tex2d, sprite) { HasThis = true };

        var image = new TypeDefinition("HUDUber", "Image", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        var write = new MethodDefinition(HudSpritePatch.WriteMethod, MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
        var il = write.Body.GetILProcessor();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, getTexture);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        image.Methods.Add(write);
        module.Types.Add(image);
        return assembly;
    }

    string HooksDll()
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Ro3Emu.ClientHooks", new Version(0, 0)), "Ro3Emu.ClientHooks", ModuleKind.Dll);
        var module = assembly.MainModule;
        var core = new AssemblyNameReference("UnityEngine.CoreModule", new Version(0, 0));
        module.AssemblyReferences.Add(core);
        var sprite = new TypeReference("UnityEngine", "Sprite", module, core);
        var tex2d = new TypeReference("UnityEngine", "Texture2D", module, core);

        var type = new TypeDefinition("Ro3Emu.ClientHooks", "SpriteRegion", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        var method = new MethodDefinition(HudSpritePatch.HookMethod, MethodAttributes.Public | MethodAttributes.Static, tex2d);
        method.Parameters.Add(new ParameterDefinition("sprite", ParameterAttributes.None, sprite));
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        type.Methods.Add(method);
        module.Types.Add(type);
        var path = Path.Combine(_dir, "Ro3Emu.ClientHooks.dll");
        assembly.Write(path);
        return path;
    }

    static AssemblyDefinition RoundTrip(AssemblyDefinition assembly)
    {
        var stream = new MemoryStream();
        assembly.Write(stream);
        stream.Position = 0;
        return AssemblyDefinition.ReadAssembly(stream);
    }

    [Fact]
    public void ReplacesSpriteGetTextureAndIsIdempotent()
    {
        string hooks = HooksDll();
        using var game = GameAssembly();
        string first = HudSpritePatch.Patch(game.MainModule, hooks);
        Assert.Contains("SpriteRegion.TextureOf", first, StringComparison.Ordinal);

        using var once = RoundTrip(game);
        var write = once.MainModule.GetType(HudSpritePatch.ImageType).Methods.Single(m => m.Name == HudSpritePatch.WriteMethod);
        var call = Assert.Single(write.Body.Instructions, i => i.OpCode == OpCodes.Call && i.Operand is MethodReference);
        Assert.Equal("UnityEngine.Texture2D Ro3Emu.ClientHooks.SpriteRegion::TextureOf(UnityEngine.Sprite)", ((MethodReference)call.Operand).FullName);
        Assert.DoesNotContain(write.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "get_texture");

        Assert.Equal("hud sprite: already patched", HudSpritePatch.Patch(once.MainModule, hooks));
    }

    [ClientDllFact]
    public void PatchesTheRealClientWriteImageToDynamic()
    {
        using var game = ClientDll.Read();
        string repoRoot = RepoRoot();
        string hooks = Path.Combine(repoRoot, "prebuilt", "client", "Ro3Emu.ClientHooks.dll");
        string report = HudSpritePatch.Patch(game.MainModule, hooks);
        Assert.Contains("SpriteRegion.TextureOf", report, StringComparison.Ordinal);

        using var once = RoundTrip(game);
        Assert.Equal("hud sprite: already patched", HudSpritePatch.Patch(once.MainModule, hooks));
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}

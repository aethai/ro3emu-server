using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>The data dir and the gate's private key are the server user's alone on Linux (0700 and 0600).</summary>
public sealed class PrivateFilesTests : IDisposable
{
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-private-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void TheDataDirAndTheGateKeyLoseTheirGroupAndOtherBits()
    {
        if (!OperatingSystem.IsWindows())
        {
            // An older install: a world-readable data dir and key, as a default umask leaves them.
            Directory.CreateDirectory(_dir, (UnixFileMode)0b111_101_101);
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            File.WriteAllText(Path.Combine(_dir, "gate_rsa_private.pem"), rsa.ExportRSAPrivateKeyPem());
            File.SetUnixFileMode(Path.Combine(_dir, "gate_rsa_private.pem"), (UnixFileMode)0b110_100_100);
        }
        GateCrypto.LoadOrCreate(_dir, NullLogger.Instance);
        PrivateFiles.Files(Path.Combine(_dir, "missing.db"));

        Assert.True(File.Exists(Path.Combine(_dir, "gate_rsa_private.pem")));
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(_dir) & Others);
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(Path.Combine(_dir, "gate_rsa_private.pem")) & Others);
        Assert.True(File.GetUnixFileMode(Path.Combine(_dir, "gate_rsa_private.pem")).HasFlag(UnixFileMode.UserRead));
        // The public key is meant to be shared and keeps its mode.
        Assert.True(File.Exists(Path.Combine(_dir, "gate_rsa_public.pem")));
    }
}

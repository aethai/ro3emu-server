namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Files only the server's own user may read: the gate's private key, the realm database (with its WAL) and its
/// backups. On Linux and macOS the group and other bits are taken away (0600 for a file, 0700 for the data dir and the
/// backup dir), so another account on a shared machine cannot copy every password hash or impersonate the gate. Windows
/// keeps its ACLs, which already give a user's files to that user.
/// </summary>
public static class PrivateFiles
{
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Creates <paramref name="path"/> when missing and keeps it to its owner.</summary>
    public static void Directory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Restrict(path);
    }

    /// <summary>Keeps each existing file to its owner; a path that does not exist is skipped.</summary>
    public static void Files(params string[] paths)
    {
        foreach (string path in paths)
            if (File.Exists(path)) Restrict(path);
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & Others) != 0) File.SetUnixFileMode(path, mode & ~Others);
        }
        // A file another user owns (a data dir prepared by root) keeps the mode its owner gave it.
        catch (UnauthorizedAccessException) { }
    }
}

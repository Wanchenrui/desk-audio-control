using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

internal sealed class UpdatePackage
{
    internal const long SizeLimit = 64L * 1024 * 1024;
    internal const string Handoff = @"Local\DeskAudioControl.UpdateHandoff";
    internal readonly long Id, Size;
    internal readonly string Hash;
    internal readonly Version Version;

    internal UpdatePackage(long id,long size,string hash,Version version)
    {
        Id = id; Size = size; Hash = hash; Version = version;
    }

    internal static string Digest(Stream data)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","");
    }

    internal static Version ReadVersion(string path)
    {
        var asm = AssemblyName.GetAssemblyName(path);
        if (!String.Equals(asm.Name,"DeskAudio",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载的文件不是 DeskAudio。");
        return new Version(asm.Version.Major,asm.Version.Minor,asm.Version.Build);
    }

    internal void Verify(string path)
    {
        using (var stream = File.OpenRead(path))
        {
            if (Size <= 0 || Size > SizeLimit || stream.Length != Size ||
                !String.Equals(Hash,Digest(stream),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包 SHA-256 或大小校验失败，请重试。");
            if (!Version.Equals(ReadVersion(path))) throw new InvalidDataException("更新包版本与发布标签不一致。");
        }
    }

    internal static string TempPath(string target,Guid job,string extension)
    {
        return Path.Combine(Path.GetDirectoryName(target),".DeskAudio-" + job.ToString("N") + extension);
    }

    internal static string Signal(Guid job,string phase) { return @"Local\DeskAudio.Update." + job.ToString("N") + "." + phase; }
}

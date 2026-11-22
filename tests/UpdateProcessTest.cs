using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

#if NEXT_RELEASE
[assembly: AssemblyVersion("2.1.0.0")]
#else
[assembly: AssemblyVersion("2.0.0.0")]
#endif

internal static class UpdateProcessTest
{
    public static int Main(string[] args)
    {
        string folder = Environment.GetEnvironmentVariable("DESKAUDIO_UPDATE_TEST");
        try
        {
#if TEST_HELPER
            int result = Updater.Run(args,msg => File.WriteAllText(Path.Combine(folder,"reported"),msg));
            File.WriteAllText(Path.Combine(folder,"helper-finished"),result.ToString());
            return result;
#elif NEXT_RELEASE
            if (File.Exists(Path.Combine(folder,"fail-boot"))) return 1;
            if (args.Length < 2 || args[0] != "--updated") return 2;
            UpdateInstall.Started(args);
            File.WriteAllText(Path.Combine(folder,"new-started"),Assembly.GetEntryAssembly().Location);
            return 0;
#else
            if (args.Length == 0) { File.WriteAllText(Path.Combine(folder,"restored"),Assembly.GetEntryAssembly().Location); return 0; }
            string incoming = Path.Combine(folder,"incoming","DeskAudio.exe");
            string digest;
            using (var file = File.OpenRead(incoming)) digest = UpdatePackage.Digest(file);
            var package = new UpdatePackage(1,new FileInfo(incoming).Length,digest,new Version(2,1,0));
            var release = new UpdateResult(UpdateState.Available,"test") { Package = package };
            bool corrupt = args[0] == "corrupt";
            try
            {
                UpdateInstall.Prepare(release, dest => {
                    File.Copy(incoming,dest);
                    if (corrupt) using (var stream = File.OpenWrite(dest)) stream.WriteByte(0);
                },Path.Combine(folder,"helper cache"),CancellationToken.None,false);
            }
            catch (InvalidDataException) { if (corrupt) return 0; throw; }
            return corrupt ? 1 : 0;
#endif
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 2; }
    }
}

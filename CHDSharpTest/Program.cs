using CHDSharpLib;
using System.Diagnostics;

namespace CHDSharpTest;

internal class Program
{
    static void Main(string[] args)
    {
        Stopwatch sw = new Stopwatch();
        sw.Start();

        if (args.Length == 0)
        {
            Console.WriteLine("Expecting a Directory to Scan");
            return;
        }

        CHD.progress = fileProgress;
        CHD.FileProcessInfo = fileProcessInfo;
        CHD.consoleOut = consoleOut;


        foreach (string arg in args)
        {
            string sDir = arg.Replace("\"", "");

            DirectoryInfo di = new DirectoryInfo(sDir);
            checkdir(di, true);
        }
        Console.WriteLine($"Done:  Time = {sw.Elapsed.TotalSeconds}");
    }

    private static void consoleOut(string message)
    {
        Console.WriteLine(message);
    }

    private static void fileProcessInfo(string message)
    {
        Console.WriteLine(message);
    }

    private static void fileProgress(string message)
    {
        Console.Write(message+"\r");
    }

    static void checkdir(DirectoryInfo di, bool verify)
    {
        FileInfo[] fi = di.GetFiles("*.chd");
        foreach (FileInfo f in fi)
        {
            using (Stream s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 4096))
            {
                CHD.CheckFile(s, f.Name, true, out uint? chdVersion, out byte[] chdSHA1, out byte[] chdMD5); 
            } 
        }

        DirectoryInfo[] arrdi = di.GetDirectories();
        foreach (DirectoryInfo d in arrdi)
        {
            checkdir(d, verify);
        }
    }
}
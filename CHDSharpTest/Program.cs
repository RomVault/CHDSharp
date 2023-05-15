using CHDSharpLib;
using System.Diagnostics;

namespace CHDSharpTest;

internal class Program
{
    static void Main(string[] args)
    {
        Stopwatch sw = new Stopwatch();
        sw.Start();

        //CHD.TestCHD("D:\\bbh_v1.00.14a.chd");
        //Console.WriteLine($"Done:  Time = {sw.Elapsed.TotalSeconds}");
        //return;

        if (args.Length == 0)
        {
            Console.WriteLine("Expecting a Directory to Scan");
            return;
        }

        foreach (string arg in args)
        {
            string sDir = arg.Replace("\"", "");

            DirectoryInfo di = new DirectoryInfo(sDir);
            checkdir(di, true);
        }
        Console.WriteLine($"Done:  Time = {sw.Elapsed.TotalSeconds}");
    }

    static void checkdir(DirectoryInfo di, bool verify)
    {
        FileInfo[] fi = di.GetFiles("*.chd");
        foreach (FileInfo f in fi)
        {
            CHD.TestCHD(f.FullName);
        }

        DirectoryInfo[] arrdi = di.GetDirectories();
        foreach (DirectoryInfo d in arrdi)
        {
            checkdir(d, verify);
        }
    }
}
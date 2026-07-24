using System;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        string root = @"C:\Users\danielpriego\Downloads\Workspace\AssetsManager\AssetsManager_v4.1.0.3\AssetsManager";
        var files = Directory.GetFiles(root, "VfxResourceIndex.cs", SearchOption.AllDirectories);

        foreach (var f in files)
        {
            Console.WriteLine($"Found VfxResourceIndex.cs: {f}");
        }
    }
}

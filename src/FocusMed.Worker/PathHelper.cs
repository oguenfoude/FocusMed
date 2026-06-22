using System;
using System.IO;

namespace FocusMed.Worker;

public static class PathHelper
{
    public static string GetDataDirectory()
    {
        var currentDir = AppContext.BaseDirectory;
        
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FocusMed.slnx")))
            {
                return Path.Combine(dir.FullName, "data");
            }
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }
}

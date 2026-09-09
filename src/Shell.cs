using System;

namespace Rewind
{
    /// <summary>Command lines for the bits of Windows Rewind hands files to. Pure, tested.</summary>
    internal static class Shell
    {
        /// <summary>Arguments for explorer.exe that open a folder with the given file selected.</summary>
        public static string SelectInExplorerArgs(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path");
            return "/select," + FfmpegArgs.Quote(path);
        }
    }
}

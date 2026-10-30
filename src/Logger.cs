using System;
using System.IO;

namespace NetSplitter
{
    internal static class Logger
    {
        private static readonly object Sync = new object();

        public static string Dir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSplitter");
                return dir;
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Dir);
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
                    File.AppendAllText(Path.Combine(Dir, "log.txt"), line, System.Text.Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
    }
}

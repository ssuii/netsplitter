using System;

namespace NetSplitter
{
    internal static class Fmt
    {
        private static readonly string[] Units = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };

        public static string Bytes(ulong value)
        {
            return Bytes((double)value);
        }

        public static string Bytes(double value)
        {
            double v = value;
            int i = 0;
            while (v >= 1024.0 && i < Units.Length - 1)
            {
                v /= 1024.0;
                i++;
            }
            if (i == 0) return string.Format("{0:0} {1}", v, Units[i]);
            if (v >= 100.0) return string.Format("{0:0} {1}", v, Units[i]);
            return string.Format("{0:0.0} {1}", v, Units[i]);
        }

        public static string PerSec(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0.5) return "0 B/s";
            return Bytes(bytesPerSecond) + "/s";
        }

        public static string LinkSpeed(long bitsPerSecond)
        {
            if (bitsPerSecond <= 0) return "-";
            double mbps = bitsPerSecond / 1000000.0;
            if (mbps >= 1000.0) return string.Format("{0:0.0} Gbps", mbps / 1000.0);
            if (mbps >= 1.0) return string.Format("{0:0} Mbps", mbps);
            return string.Format("{0:0} Kbps", bitsPerSecond / 1000.0);
        }
    }
}

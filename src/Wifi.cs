using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NetSplitter
{
    internal sealed class WifiNetwork
    {
        public string Ssid = "";
        public string ProfileName = "";
        public int Signal;
        public bool Secure = true;
        public bool HasProfile;
        public bool Connected;
        public uint AuthAlgorithm = 7;
        public string Auth = "";
    }

    internal sealed class WifiStatus
    {
        public bool Available;
        public bool Connected;
        public string Ssid = "";
        public string ProfileName = "";
        public string InterfaceId = "";
        public int Signal;
        public string Error = "";
    }

    internal sealed class WifiResult
    {
        public bool Ok;
        public string Error = "";
    }

    internal sealed class WifiScanResult
    {
        public List<WifiNetwork> Networks = new List<WifiNetwork>();
        public string Error = "";
    }

    internal static class WifiService
    {
        private const uint ClientVersion = 2;
        private const int OpCodeCurrentConnection = 7;
        private const uint InterfaceStateConnected = 1;
        private const uint AvailableNetworkIncludeAllAdhoc = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Dot11Ssid
        {
            public uint Length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] Bytes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanInterfaceInfo
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Description;
            public uint State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WlanAssociationAttributes
        {
            public Dot11Ssid Ssid;
            public uint BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] Bssid;
            public uint PhyType;
            public uint PhyIndex;
            public uint SignalQuality;
            public uint RxRate;
            public uint TxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WlanSecurityAttributes
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool SecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)]
            public bool OneXEnabled;
            public uint AuthAlgorithm;
            public uint CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanConnectionAttributes
        {
            public uint State;
            public uint ConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ProfileName;
            public WlanAssociationAttributes Association;
            public WlanSecurityAttributes Security;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanAvailableNetwork
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ProfileName;
            public Dot11Ssid Ssid;
            public uint BssType;
            public uint NumberOfBssids;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Connectable;
            public uint NotConnectableReason;
            public uint NumberOfPhyTypes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] PhyTypes;
            [MarshalAs(UnmanagedType.Bool)]
            public bool MorePhyTypes;
            public uint SignalQuality;
            [MarshalAs(UnmanagedType.Bool)]
            public bool SecurityEnabled;
            public uint DefaultAuth;
            public uint DefaultCipher;
            public uint Flags;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanConnectionParameters
        {
            public uint ConnectionMode;
            public string Profile;
            public IntPtr Dot11Ssid;
            public IntPtr DesiredBssidList;
            public uint BssType;
            public uint Flags;
        }

        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr client);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr interfaceList);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanQueryInterface(IntPtr client, ref Guid interfaceGuid, int opCode,
            IntPtr reserved, out uint dataSize, out IntPtr data, out uint opCodeValueType);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanScan(IntPtr client, ref Guid interfaceGuid, IntPtr ssid, IntPtr ieData, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanGetAvailableNetworkList(IntPtr client, ref Guid interfaceGuid,
            uint flags, IntPtr reserved, out IntPtr networkList);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanGetProfileList(IntPtr client, ref Guid interfaceGuid,
            IntPtr reserved, out IntPtr profileList);

        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint WlanSetProfile(IntPtr client, ref Guid interfaceGuid, uint flags,
            string profileXml, string allUserProfileSecurity, bool overwrite, IntPtr reserved, out uint reason);

        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint WlanConnect(IntPtr client, ref Guid interfaceGuid,
            ref WlanConnectionParameters parameters, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanDisconnect(IntPtr client, ref Guid interfaceGuid, IntPtr reserved);

        public static WifiStatus ReadStatus()
        {
            WifiStatus status = new WifiStatus();
            IntPtr client;
            uint negotiated;
            uint rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out negotiated, out client);
            if (rc != 0)
            {
                status.Error = ErrorText(rc);
                return status;
            }
            try
            {
                Guid guid;
                if (!TryGetInterface(client, out guid, status))
                {
                    return status;
                }
                status.Available = true;
                status.InterfaceId = guid.ToString();
                WifiStatus current = QueryStatus(client, guid);
                current.Available = true;
                current.InterfaceId = status.InterfaceId;
                if (!string.IsNullOrEmpty(current.Error)) current.Error = "";
                return current;
            }
            finally
            {
                WlanCloseHandle(client, IntPtr.Zero);
            }
        }

        public static WifiScanResult Scan(int waitMs)
        {
            WifiScanResult result = new WifiScanResult();
            IntPtr client;
            uint negotiated;
            uint rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out negotiated, out client);
            if (rc != 0)
            {
                result.Error = "无法访问无线服务：" + ErrorText(rc);
                return result;
            }
            try
            {
                WifiStatus status = new WifiStatus();
                Guid guid;
                if (!TryGetInterface(client, out guid, status))
                {
                    result.Error = status.Error;
                    return result;
                }

                WlanScan(client, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                DateTime end = DateTime.UtcNow.AddMilliseconds(waitMs < 800 ? 800 : waitMs);
                List<WifiNetwork> nets = new List<WifiNetwork>();
                while (true)
                {
                    List<WifiNetwork> current = QueryAvailable(client, guid);
                    if (current.Count > 0) nets = current;
                    if (DateTime.UtcNow >= end) break;
                    Thread.Sleep(400);
                }
                result.Networks = MergeWithProfiles(nets, GetProfilesInternal(client, guid));
                MarkConnected(result.Networks, QueryStatus(client, guid));
            }
            finally
            {
                WlanCloseHandle(client, IntPtr.Zero);
            }
            return result;
        }

        public static List<string> GetProfiles()
        {
            List<string> list = new List<string>();
            IntPtr client;
            uint negotiated;
            uint rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out negotiated, out client);
            if (rc != 0) return list;
            try
            {
                WifiStatus status = new WifiStatus();
                Guid guid;
                if (!TryGetInterface(client, out guid, status)) return list;
                return GetProfilesInternal(client, guid);
            }
            finally
            {
                WlanCloseHandle(client, IntPtr.Zero);
            }
        }

        public static WifiResult Connect(string ssid, string password)
        {
            WifiResult result = new WifiResult();
            ssid = (ssid ?? "").Trim();
            if (ssid.Length == 0)
            {
                result.Error = "网络名称不能为空";
                return result;
            }
            if (password != null && password.Length > 0 && password.Length < 8)
            {
                result.Error = "Wi-Fi 密码至少需要 8 位";
                return result;
            }

            IntPtr client;
            uint negotiated;
            uint rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out negotiated, out client);
            if (rc != 0)
            {
                result.Error = "无法访问无线服务：" + ErrorText(rc);
                return result;
            }
            try
            {
                WifiStatus status = new WifiStatus();
                Guid guid;
                if (!TryGetInterface(client, out guid, status))
                {
                    result.Error = status.Error;
                    return result;
                }

                WifiStatus current = QueryStatus(client, guid);
                if (current.Connected && current.Ssid == ssid)
                {
                    result.Ok = true;
                    return result;
                }

                string profileName;
                if (password != null)
                {
                    bool secureNet = password.Length > 0;
                    uint auth = 7;
                    if (secureNet)
                    {
                        uint scanned;
                        if (TryFindAuth(client, guid, ssid, out scanned) && scanned == 9) auth = 9;
                    }
                    else
                    {
                        auth = 1;
                    }
                    string xml = BuildProfileXml(ssid, password, auth, secureNet);
                    uint reason;
                    rc = WlanSetProfile(client, ref guid, 0, xml, null, true, IntPtr.Zero, out reason);
                    if (rc != 0)
                    {
                        result.Error = "创建无线配置失败：" + ErrorText(rc) + "（原因码 " + reason + "）";
                        return result;
                    }
                    profileName = ssid;
                }
                else
                {
                    profileName = null;
                    if (GetProfilesInternal(client, guid).Contains(ssid)) profileName = ssid;
                    if (string.IsNullOrEmpty(profileName))
                    {
                        result.Error = "该网络没有已保存的配置，请先输入密码连接一次";
                        return result;
                    }
                }

                WlanConnectionParameters parameters = new WlanConnectionParameters();
                parameters.ConnectionMode = 0;
                parameters.Profile = profileName;
                parameters.BssType = 1;
                rc = WlanConnect(client, ref guid, ref parameters, IntPtr.Zero);
                if (rc != 0)
                {
                    result.Error = "发起连接失败：" + ErrorText(rc);
                    return result;
                }

                DateTime end = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < end)
                {
                    Thread.Sleep(500);
                    WifiStatus check = QueryStatus(client, guid);
                    if (check.Connected && check.Ssid == ssid)
                    {
                        result.Ok = true;
                        return result;
                    }
                    if (!string.IsNullOrEmpty(check.Error))
                    {
                        result.Error = check.Error;
                        return result;
                    }
                }
                result.Error = "连接超时（15 秒），请检查密码、信号强度或该网络是否允许接入";
                return result;
            }
            finally
            {
                WlanCloseHandle(client, IntPtr.Zero);
            }
        }

        public static WifiResult Disconnect()
        {
            WifiResult result = new WifiResult();
            IntPtr client;
            uint negotiated;
            uint rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out negotiated, out client);
            if (rc != 0)
            {
                result.Error = "无法访问无线服务：" + ErrorText(rc);
                return result;
            }
            try
            {
                WifiStatus status = new WifiStatus();
                Guid guid;
                if (!TryGetInterface(client, out guid, status))
                {
                    result.Error = status.Error;
                    return result;
                }
                rc = WlanDisconnect(client, ref guid, IntPtr.Zero);
                if (rc != 0)
                {
                    result.Error = "断开失败：" + ErrorText(rc);
                    return result;
                }
                result.Ok = true;
                return result;
            }
            finally
            {
                WlanCloseHandle(client, IntPtr.Zero);
            }
        }

        internal static string BuildProfileXml(string ssid, string password, uint authAlgorithm, bool secure)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">");
            sb.AppendLine("  <name>" + XmlEscape(ssid) + "</name>");
            sb.AppendLine("  <SSIDConfig>");
            sb.AppendLine("    <SSID>");
            sb.AppendLine("      <name>" + XmlEscape(ssid) + "</name>");
            sb.AppendLine("    </SSID>");
            sb.AppendLine("  </SSIDConfig>");
            sb.AppendLine("  <connectionType>ESS</connectionType>");
            sb.AppendLine("  <connectionMode>auto</connectionMode>");
            sb.AppendLine("  <MSM>");
            sb.AppendLine("    <security>");
            sb.AppendLine("      <authEncryption>");
            if (!secure)
            {
                sb.AppendLine("        <authentication>open</authentication>");
                sb.AppendLine("        <encryption>none</encryption>");
            }
            else
            {
                string auth = authAlgorithm == 9 ? "WPA3SAE" : "WPA2PSK";
                sb.AppendLine("        <authentication>" + auth + "</authentication>");
                sb.AppendLine("        <encryption>AES</encryption>");
            }
            sb.AppendLine("        <useOneX>false</useOneX>");
            sb.AppendLine("      </authEncryption>");
            if (secure)
            {
                sb.AppendLine("      <sharedKey>");
                sb.AppendLine("        <keyType>passPhrase</keyType>");
                sb.AppendLine("        <protected>false</protected>");
                sb.AppendLine("        <keyMaterial>" + XmlEscape(password) + "</keyMaterial>");
                sb.AppendLine("      </sharedKey>");
            }
            sb.AppendLine("    </security>");
            sb.AppendLine("  </MSM>");
            sb.AppendLine("</WLANProfile>");
            return sb.ToString();
        }

        private static string XmlEscape(string text)
        {
            if (text == null) return "";
            StringBuilder sb = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static bool TryGetInterface(IntPtr client, out Guid guid, WifiStatus status)
        {
            guid = Guid.Empty;
            IntPtr list;
            uint rc = WlanEnumInterfaces(client, IntPtr.Zero, out list);
            if (rc != 0)
            {
                status.Error = "枚举无线网卡失败：" + ErrorText(rc);
                return false;
            }
            try
            {
                int count = Marshal.ReadInt32(list);
                if (count <= 0)
                {
                    status.Error = "未检测到无线网卡";
                    return false;
                }
                int size = Marshal.SizeOf(typeof(WlanInterfaceInfo));
                IntPtr item = new IntPtr(list.ToInt64() + 8);
                WlanInterfaceInfo info = (WlanInterfaceInfo)Marshal.PtrToStructure(item, typeof(WlanInterfaceInfo));
                guid = info.InterfaceGuid;
                return true;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }

        private static WifiStatus QueryStatus(IntPtr client, Guid guid)
        {
            WifiStatus status = new WifiStatus();
            Guid g = guid;
            IntPtr data;
            uint size;
            uint opcodeType;
            uint rc = WlanQueryInterface(client, ref g, OpCodeCurrentConnection, IntPtr.Zero, out size, out data, out opcodeType);
            if (rc != 0)
            {
                if (rc != 1168 && rc != 5023)
                {
                    status.Error = "读取无线状态失败：" + ErrorText(rc);
                }
                return status;
            }
            try
            {
                if (size >= Marshal.SizeOf(typeof(WlanConnectionAttributes)))
                {
                    WlanConnectionAttributes attrs = (WlanConnectionAttributes)Marshal.PtrToStructure(data, typeof(WlanConnectionAttributes));
                    status.Connected = attrs.State == InterfaceStateConnected;
                    status.Ssid = SsidText(attrs.Association.Ssid);
                    status.Signal = (int)attrs.Association.SignalQuality;
                    status.ProfileName = attrs.ProfileName ?? "";
                }
            }
            finally
            {
                WlanFreeMemory(data);
            }
            return status;
        }

        private static List<WifiNetwork> QueryAvailable(IntPtr client, Guid guid)
        {
            List<WifiNetwork> list = new List<WifiNetwork>();
            Guid g = guid;
            IntPtr ptr;
            uint rc = WlanGetAvailableNetworkList(client, ref g, AvailableNetworkIncludeAllAdhoc, IntPtr.Zero, out ptr);
            if (rc != 0) return list;
            try
            {
                int count = Marshal.ReadInt32(ptr);
                int size = Marshal.SizeOf(typeof(WlanAvailableNetwork));
                Dictionary<string, WifiNetwork> map = new Dictionary<string, WifiNetwork>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(ptr.ToInt64() + 8 + i * size);
                    WlanAvailableNetwork data = (WlanAvailableNetwork)Marshal.PtrToStructure(item, typeof(WlanAvailableNetwork));
                    string ssid = SsidText(data.Ssid);
                    if (ssid.Length == 0) continue;
                    WifiNetwork net;
                    if (!map.TryGetValue(ssid, out net))
                    {
                        net = new WifiNetwork();
                        net.Ssid = ssid;
                        map[ssid] = net;
                    }
                    if (data.SignalQuality > net.Signal) net.Signal = (int)data.SignalQuality;
                    if (data.SecurityEnabled)
                    {
                        net.Secure = true;
                        net.AuthAlgorithm = data.DefaultAuth;
                    }
                    else if (!net.HasProfile && net.AuthAlgorithm == 0)
                    {
                        net.Secure = false;
                        net.AuthAlgorithm = 1;
                    }
                    if (!string.IsNullOrEmpty(data.ProfileName))
                    {
                        net.HasProfile = true;
                        net.ProfileName = data.ProfileName;
                    }
                    net.Auth = AuthText(net.Secure, net.AuthAlgorithm);
                }
                list.AddRange(map.Values);
            }
            finally
            {
                WlanFreeMemory(ptr);
            }
            return list;
        }

        private static List<string> GetProfilesInternal(IntPtr client, Guid guid)
        {
            List<string> profiles = new List<string>();
            Guid g = guid;
            IntPtr ptr;
            uint rc = WlanGetProfileList(client, ref g, IntPtr.Zero, out ptr);
            if (rc != 0) return profiles;
            try
            {
                int count = Marshal.ReadInt32(ptr);
                int size = Marshal.SizeOf(typeof(ProfileInfo));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(ptr.ToInt64() + 8 + i * size);
                    ProfileInfo info = (ProfileInfo)Marshal.PtrToStructure(item, typeof(ProfileInfo));
                    if (!string.IsNullOrEmpty(info.Name)) profiles.Add(info.Name);
                }
            }
            finally
            {
                WlanFreeMemory(ptr);
            }
            return profiles;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProfileInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Name;
            public uint Flags;
        }

        private static bool TryFindAuth(IntPtr client, Guid guid, string ssid, out uint auth)
        {
            auth = 7;
            foreach (WifiNetwork net in QueryAvailable(client, guid))
            {
                if (net.Ssid != ssid) continue;
                if (!net.Secure)
                {
                    auth = 1;
                    return true;
                }
                auth = net.AuthAlgorithm == 0 ? 7 : net.AuthAlgorithm;
                return true;
            }
            return false;
        }

        private static List<WifiNetwork> MergeWithProfiles(List<WifiNetwork> nets, List<string> profiles)
        {
            List<WifiNetwork> list = new List<WifiNetwork>(nets);
            HashSet<string> known = new HashSet<string>();
            foreach (WifiNetwork net in nets)
            {
                known.Add(net.Ssid);
                if (profiles.Contains(net.Ssid))
                {
                    net.HasProfile = true;
                    if (string.IsNullOrEmpty(net.ProfileName)) net.ProfileName = net.Ssid;
                }
            }
            foreach (string profile in profiles)
            {
                if (known.Contains(profile)) continue;
                WifiNetwork net = new WifiNetwork();
                net.Ssid = profile;
                net.ProfileName = profile;
                net.HasProfile = true;
                net.Signal = 0;
                net.Secure = true;
                net.Auth = "已保存";
                list.Add(net);
                known.Add(profile);
            }
            return list;
        }

        private static void MarkConnected(List<WifiNetwork> nets, WifiStatus status)
        {
            if (status == null || !status.Connected) return;
            foreach (WifiNetwork net in nets)
            {
                if (net.Ssid == status.Ssid)
                {
                    net.Connected = true;
                    if (status.Signal > 0) net.Signal = status.Signal;
                }
            }
        }

        private static string SsidText(Dot11Ssid ssid)
        {
            if (ssid.Bytes == null) return "";
            int len = (int)ssid.Length;
            if (len <= 0) return "";
            if (len > ssid.Bytes.Length) len = ssid.Bytes.Length;
            return Encoding.UTF8.GetString(ssid.Bytes, 0, len);
        }

        private static string AuthText(bool secure, uint auth)
        {
            if (!secure) return "开放";
            switch (auth)
            {
                case 2: return "共享密钥";
                case 3: return "WPA 企业";
                case 4: return "WPA 个人";
                case 6: return "WPA2 企业";
                case 7: return "WPA2 个人";
                case 8: return "WPA3 企业";
                case 9: return "WPA3 个人";
                case 10: return "增强开放";
                default: return "加密";
            }
        }

        private static string ErrorText(uint code)
        {
            switch (code)
            {
                case 5: return "权限不足";
                case 87: return "参数无效";
                case 1062: return "WLAN AutoConfig 服务未运行";
                case 1168: return "找不到元素";
                case 5023: return "无线接口状态不允许该操作";
                default: return "错误码 " + code.ToString();
            }
        }
    }

    internal static class WifiConfig
    {
        public const string TargetKey = "target";
        public const string TargetKey2 = "target2";

        public static string FilePath
        {
            get { return Path.Combine(Logger.Dir, "wifi.tsv"); }
        }

        public static string[] LoadPair()
        {
            return LoadPair(FilePath);
        }

        public static string[] LoadPair(string path)
        {
            string first = "";
            string second = "";
            try
            {
                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        string key = line.Substring(0, tab);
                        if (key == TargetKey) first = line.Substring(tab + 1).Trim();
                        else if (key == TargetKey2) second = line.Substring(tab + 1).Trim();
                    }
                }
            }
            catch
            {
            }
            return new string[] { first, second };
        }

        public static bool SavePair(string first, string second)
        {
            return SavePair(FilePath, first, second);
        }

        public static bool SavePair(string path, string first, string second)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string text = "# NetSplitter wifi config v1" + Environment.NewLine +
                              TargetKey + "\t" + (first ?? "").Trim() + Environment.NewLine +
                              TargetKey2 + "\t" + (second ?? "").Trim() + Environment.NewLine;
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string PickTarget(string first, string second, string current, bool connected)
        {
            first = first ?? "";
            second = second ?? "";
            current = current ?? "";
            if (first.Length == 0 && second.Length == 0) return "";
            if (second.Length == 0) return first;
            if (first.Length == 0) return second;
            if (connected)
            {
                if (current == first) return second;
                if (current == second) return first;
            }
            return first;
        }
    }
}

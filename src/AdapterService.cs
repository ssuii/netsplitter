using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetSplitter
{
    internal static class AdapterService
    {
        // Windows 把 WLAN API 的当前连接查询（SSID/信号）判定为"使用定位"，
        // 周期查询会让任务栏定位图标常亮，所以只在启动、网络变化等时机读取一次。
        private static WifiStatus _wifiCache;
        private static bool _wifiCacheValid;
        private static DateTime _wifiRetryAt = DateTime.MaxValue;
        internal static int WifiCacheMisses;

        public static void InvalidateWifiCache()
        {
            _wifiCacheValid = false;
        }

        private static WifiStatus ReadWifiStatusCached()
        {
            if (!_wifiCacheValid || (!_wifiCache.Available && DateTime.UtcNow >= _wifiRetryAt))
            {
                try { _wifiCache = WifiService.ReadStatus(); }
                catch { _wifiCache = new WifiStatus(); }
                _wifiCacheValid = true;
                WifiCacheMisses++;
                // 查询失败（如无线服务尚未启动）时不长期缓存，60 秒后重试
                _wifiRetryAt = _wifiCache.Available
                    ? DateTime.MaxValue
                    : DateTime.UtcNow.AddSeconds(60);
            }
            return _wifiCache;
        }

        public static List<AdapterInfo> GetAdapters()
        {
            List<AdapterInfo> list = new List<AdapterInfo>();
            WifiStatus wifi = null;
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface ni in nics)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); }
                catch { continue; }

                IPv4InterfaceProperties v4 = null;
                try { v4 = props.GetIPv4Properties(); }
                catch { }
                if (v4 == null) continue;

                AdapterInfo a = new AdapterInfo();
                a.Id = ni.Id;
                a.Name = ni.Name;
                a.Description = ni.Description;
                a.IfIndex = v4.Index;
                a.Up = ni.OperationalStatus == OperationalStatus.Up;
                a.StatusText = a.Up ? "已连接" : "未连接";
                try { a.Speed = ni.Speed; }
                catch { a.Speed = -1; }

                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    a.IPv4 = ua.Address.ToString();
                    a.PrefixLength = ua.PrefixLength;
                    break;
                }
                if (string.IsNullOrEmpty(a.IPv4)) continue;

                foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    a.Gateway = gw.Address.ToString();
                    break;
                }

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    a.IsWifi = true;
                    if (wifi == null)
                    {
                        try { wifi = ReadWifiStatusCached(); }
                        catch { wifi = new WifiStatus(); }
                    }
                    if (wifi.Available && SameInterface(a.Id, wifi.InterfaceId))
                    {
                        a.WifiSsid = wifi.Connected ? wifi.Ssid : "";
                        a.WifiSignal = wifi.Connected ? wifi.Signal : 0;
                    }
                    else if (wifi.Available && wifi.Connected && IsOnlyWifi(nics, ni.Id))
                    {
                        a.WifiSsid = wifi.Ssid;
                        a.WifiSignal = wifi.Signal;
                    }
                }

                if (!a.Up && a.IPv4.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                list.Add(a);
            }

            list.Sort(delegate(AdapterInfo x, AdapterInfo y)
            {
                if (x.Up != y.Up) return x.Up ? -1 : 1;
                return string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        private static bool IsOnlyWifi(NetworkInterface[] nics, string id)
        {
            int count = 0;
            string only = "";
            foreach (NetworkInterface ni in nics)
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                count++;
                only = ni.Id;
            }
            return count == 1 && only == id;
        }

        private static bool SameInterface(string id, string guidText)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(guidText)) return false;
            return string.Equals(id.Trim('{', '}'), guidText.Trim('{', '}'),
                StringComparison.OrdinalIgnoreCase);
        }

        public static NetworkInterface FindNic(string id)
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.Id == id) return ni;
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace NetSplitter
{
    internal sealed class TrafficMonitor : IDisposable
    {
        private sealed class ConnKey : IEquatable<ConnKey>
        {
            public bool V6;
            public string Local;
            public ushort LocalPort;
            public string Remote;
            public ushort RemotePort;
            public uint Pid;

            public bool Equals(ConnKey other)
            {
                if (other == null) return false;
                return V6 == other.V6 && Local == other.Local && LocalPort == other.LocalPort
                    && Remote == other.Remote && RemotePort == other.RemotePort && Pid == other.Pid;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as ConnKey);
            }

            public override int GetHashCode()
            {
                int h = 17;
                h = h * 31 + (V6 ? 1 : 0);
                h = h * 31 + (Local != null ? Local.GetHashCode() : 0);
                h = h * 31 + LocalPort.GetHashCode();
                h = h * 31 + (Remote != null ? Remote.GetHashCode() : 0);
                h = h * 31 + RemotePort.GetHashCode();
                h = h * 31 + (int)Pid;
                return h;
            }
        }

        private sealed class ConnState
        {
            public ulong LastIn;
            public ulong LastOut;
            public bool Enabled;
            public bool Failed;
            public int Misses;
        }

        private sealed class Agg
        {
            public ulong TotalIn;
            public ulong TotalOut;
            public ulong TickIn;
            public ulong TickOut;
        }

        private readonly bool _allowPerProcess;
        private readonly Dictionary<ConnKey, ConnState> _conns = new Dictionary<ConnKey, ConnState>();
        private readonly Dictionary<string, Agg> _procAgg = new Dictionary<string, Agg>();
        private readonly Dictionary<string, Agg> _ipAgg = new Dictionary<string, Agg>();
        private readonly Dictionary<string, Agg> _procIpAgg = new Dictionary<string, Agg>();
        private readonly Dictionary<uint, string> _procNames = new Dictionary<uint, string>();
        private readonly Dictionary<string, ulong> _lastAdapterRx = new Dictionary<string, ulong>();
        private readonly Dictionary<string, ulong> _lastAdapterTx = new Dictionary<string, ulong>();
        private volatile TrafficSnapshot _snapshot;
        private List<AdapterInfo> _adapters = new List<AdapterInfo>();
        private Dictionary<string, NetworkInterface> _nics = new Dictionary<string, NetworkInterface>();
        private Dictionary<string, string> _ipToAdapter = new Dictionary<string, string>();
        private DateTime _lastAdapterRefresh = DateTime.MinValue;
        private DateTime _lastSample = DateTime.MinValue;
        private Thread _thread;
        private volatile bool _running;
        private volatile bool _resetRequested;
        private bool _perProcess;
        private string _statsMessage = "";
        private int _tcpCount;

        public TrafficMonitor(bool allowPerProcess)
        {
            _allowPerProcess = allowPerProcess;
            _perProcess = allowPerProcess;
            _snapshot = new TrafficSnapshot();
            if (!allowPerProcess)
            {
                _statsMessage = "只读模式：进程/IP 流量统计需要管理员权限";
            }
        }

        public void Start()
        {
            if (_running) return;
            try
            {
                RefreshAdapters();
                Sample();
            }
            catch (Exception ex)
            {
                Logger.Write("初始化监控失败: " + ex.Message);
            }
            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "NetSplitter.Traffic";
            _thread.Start();
        }

        public TrafficSnapshot GetSnapshot()
        {
            return _snapshot;
        }

        public List<AdapterInfo> CurrentAdapters()
        {
            return _adapters;
        }

        public void ResetCounters()
        {
            _resetRequested = true;
        }

        public void Dispose()
        {
            _running = false;
            try
            {
                if (_thread != null) _thread.Join(1500);
            }
            catch
            {
            }
        }

        private void Loop()
        {
            while (_running)
            {
                DateTime started = DateTime.UtcNow;
                try
                {
                    Sample();
                }
                catch (Exception ex)
                {
                    Logger.Write("采样失败: " + ex.Message);
                }
                int wait = 1000 - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (wait < 50) wait = 50;
                for (int i = 0; i < wait && _running; i += 100) Thread.Sleep(100);
            }
        }

        private void RefreshAdapters()
        {
            List<AdapterInfo> adapters = AdapterService.GetAdapters();
            Dictionary<string, NetworkInterface> nics = new Dictionary<string, NetworkInterface>();
            Dictionary<string, NetworkInterface> byId = new Dictionary<string, NetworkInterface>();
            Dictionary<string, string> map = new Dictionary<string, string>();

            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                byId[ni.Id] = ni;
            }

            foreach (AdapterInfo a in adapters)
            {
                NetworkInterface ni;
                if (byId.TryGetValue(a.Id, out ni))
                {
                    nics[a.Id] = ni;
                    try
                    {
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            string ip = NormalizeIp(ua.Address.ToString());
                            if (!map.ContainsKey(ip)) map[ip] = a.Name;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            _adapters = adapters;
            _nics = nics;
            _ipToAdapter = map;
            _lastAdapterRefresh = DateTime.UtcNow;
        }

        private const char KeySep = '\u0001';

        private void Sample()
        {
            if (_resetRequested)
            {
                _resetRequested = false;
                _procAgg.Clear();
                _ipAgg.Clear();
                _procIpAgg.Clear();
            }

            DateTime now = DateTime.UtcNow;
            double elapsed = _lastSample == DateTime.MinValue ? 1.0 : (now - _lastSample).TotalSeconds;
            if (elapsed <= 0.05) elapsed = 0.05;
            _lastSample = now;

            if ((now - _lastAdapterRefresh).TotalSeconds > 5)
            {
                try { RefreshAdapters(); }
                catch (Exception ex) { Logger.Write("刷新网卡失败: " + ex.Message); }
            }

            foreach (Agg a in _procAgg.Values) { a.TickIn = 0; a.TickOut = 0; }
            foreach (Agg a in _ipAgg.Values) { a.TickIn = 0; a.TickOut = 0; }
            foreach (Agg a in _procIpAgg.Values) { a.TickIn = 0; a.TickOut = 0; }

            List<AdapterInfo> adapters = new List<AdapterInfo>();
            foreach (AdapterInfo src in _adapters)
            {
                AdapterInfo a = CopyAdapter(src);
                NetworkInterface ni;
                if (_nics.TryGetValue(src.Id, out ni))
                {
                    try
                    {
                        IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                        ulong rx = st.BytesReceived > 0 ? (ulong)st.BytesReceived : 0;
                        ulong tx = st.BytesSent > 0 ? (ulong)st.BytesSent : 0;
                        ulong prevRx;
                        ulong prevTx;
                        if (_lastAdapterRx.TryGetValue(src.Id, out prevRx) && rx >= prevRx)
                            a.RxRate = (rx - prevRx) / elapsed;
                        if (_lastAdapterTx.TryGetValue(src.Id, out prevTx) && tx >= prevTx)
                            a.TxRate = (tx - prevTx) / elapsed;
                        _lastAdapterRx[src.Id] = rx;
                        _lastAdapterTx[src.Id] = tx;
                        a.RxTotal = rx;
                        a.TxTotal = tx;
                    }
                    catch
                    {
                    }
                }
                adapters.Add(a);
            }

            int connCount = 0;
            try
            {
                List<NativeMethods.TcpEntry> entries = NativeMethods.GetConnections();
                HashSet<ConnKey> seen = new HashSet<ConnKey>();
                foreach (NativeMethods.TcpEntry e in entries)
                {
                    if (e.State == 2 || e.State <= 1 || e.State >= 12) continue;
                    if (e.Local == null || e.Remote == null) continue;
                    string local = NormalizeIp(e.Local.ToString());
                    string remote = NormalizeIp(e.Remote.ToString());
                    if (local == "0.0.0.0" || local == "::") continue;
                    if (IsLocalAddress(remote)) continue;

                    ConnKey key = new ConnKey();
                    key.V6 = e.V6;
                    key.Local = local;
                    key.LocalPort = e.LocalPort;
                    key.Remote = remote;
                    key.RemotePort = e.RemotePort;
                    key.Pid = e.Pid;
                    if (!seen.Add(key)) continue;
                    connCount++;

                    if (!_allowPerProcess || !_perProcess) continue;

                    ConnState st;
                    if (!_conns.TryGetValue(key, out st))
                    {
                        st = new ConnState();
                        _conns[key] = st;
                    }
                    st.Misses = 0;
                    if (st.Failed) continue;

                    if (!st.Enabled)
                    {
                        uint rc = e.V6 ? NativeMethods.EnableV6(e.Row6) : NativeMethods.EnableV4(e.Row4);
                        if (rc != 0)
                        {
                            if (rc == 5)
                            {
                                _perProcess = false;
                                _statsMessage = "需要管理员权限才能采集进程/IP 流量";
                                Logger.Write("开启 TCP 扩展统计被拒绝（需要管理员权限）");
                                break;
                            }
                            st.Failed = true;
                            continue;
                        }
                        st.Enabled = true;
                    }

                    ulong inBytes;
                    ulong outBytes;
                    bool ok = e.V6 ? NativeMethods.TryReadV6(e.Row6, out inBytes, out outBytes)
                                   : NativeMethods.TryReadV4(e.Row4, out inBytes, out outBytes);
                    if (!ok)
                    {
                        st.Failed = true;
                        continue;
                    }

                    ulong dIn = inBytes >= st.LastIn ? inBytes - st.LastIn : inBytes;
                    ulong dOut = outBytes >= st.LastOut ? outBytes - st.LastOut : outBytes;
                    st.LastIn = inBytes;
                    st.LastOut = outBytes;
                    if (dIn == 0 && dOut == 0) continue;

                    string adapterName = MapAdapter(local);
                    string proc = ResolveProcess(e.Pid);
                    AddAgg(_procAgg, proc + "|" + adapterName, dIn, dOut);
                    AddAgg(_ipAgg, remote + "|" + adapterName, dIn, dOut);
                    AddAgg(_procIpAgg, proc + KeySep + remote + KeySep + adapterName, dIn, dOut);
                }

                List<ConnKey> dead = new List<ConnKey>();
                foreach (KeyValuePair<ConnKey, ConnState> kv in _conns)
                {
                    if (seen.Contains(kv.Key)) continue;
                    kv.Value.Misses++;
                    if (kv.Value.Misses > 2) dead.Add(kv.Key);
                }
                foreach (ConnKey k in dead) _conns.Remove(k);
            }
            catch (Exception ex)
            {
                Logger.Write("读取 TCP 表失败: " + ex.Message);
            }

            _tcpCount = connCount;

            TrafficSnapshot snap = new TrafficSnapshot();
            snap.Adapters = adapters;
            snap.TcpConnections = _tcpCount;
            snap.PerProcessAvailable = _allowPerProcess && _perProcess;
            snap.StatsMessage = _statsMessage;
            snap.Timestamp = DateTime.Now;
            snap.Processes = BuildProcRows(elapsed);
            snap.Ips = BuildIpRows(elapsed);
            snap.ProcIps = BuildProcIpRows(elapsed);
            _snapshot = snap;
        }

        private List<ProcTrafficRow> BuildProcRows(double elapsed)
        {
            List<ProcTrafficRow> rows = new List<ProcTrafficRow>();
            foreach (KeyValuePair<string, Agg> kv in _procAgg)
            {
                int idx = kv.Key.LastIndexOf('|');
                if (idx <= 0) continue;
                ProcTrafficRow r = new ProcTrafficRow();
                r.Process = kv.Key.Substring(0, idx);
                r.Adapter = kv.Key.Substring(idx + 1);
                r.RxTotal = kv.Value.TotalIn;
                r.TxTotal = kv.Value.TotalOut;
                r.RxRate = kv.Value.TickIn / elapsed;
                r.TxRate = kv.Value.TickOut / elapsed;
                rows.Add(r);
            }
            rows.Sort(delegate(ProcTrafficRow x, ProcTrafficRow y)
            {
                double a = (double)x.RxTotal + (double)x.TxTotal;
                double b = (double)y.RxTotal + (double)y.TxTotal;
                return b.CompareTo(a);
            });
            if (rows.Count > 200) rows = rows.GetRange(0, 200);
            return rows;
        }

        private List<IpTrafficRow> BuildIpRows(double elapsed)
        {
            List<IpTrafficRow> rows = new List<IpTrafficRow>();
            foreach (KeyValuePair<string, Agg> kv in _ipAgg)
            {
                int idx = kv.Key.LastIndexOf('|');
                if (idx <= 0) continue;
                IpTrafficRow r = new IpTrafficRow();
                r.RemoteIp = kv.Key.Substring(0, idx);
                r.Adapter = kv.Key.Substring(idx + 1);
                r.RxTotal = kv.Value.TotalIn;
                r.TxTotal = kv.Value.TotalOut;
                r.RxRate = kv.Value.TickIn / elapsed;
                r.TxRate = kv.Value.TickOut / elapsed;
                rows.Add(r);
            }
            rows.Sort(delegate(IpTrafficRow x, IpTrafficRow y)
            {
                double a = (double)x.RxTotal + (double)x.TxTotal;
                double b = (double)y.RxTotal + (double)y.TxTotal;
                return b.CompareTo(a);
            });
            if (rows.Count > 200) rows = rows.GetRange(0, 200);
            return rows;
        }

        private List<ProcIpRow> BuildProcIpRows(double elapsed)
        {
            List<ProcIpRow> rows = new List<ProcIpRow>();
            foreach (KeyValuePair<string, Agg> kv in _procIpAgg)
            {
                string[] parts = kv.Key.Split(KeySep);
                if (parts.Length != 3) continue;
                ProcIpRow r = new ProcIpRow();
                r.Process = parts[0];
                r.RemoteIp = parts[1];
                r.Adapter = parts[2];
                r.RxTotal = kv.Value.TotalIn;
                r.TxTotal = kv.Value.TotalOut;
                r.RxRate = kv.Value.TickIn / elapsed;
                r.TxRate = kv.Value.TickOut / elapsed;
                rows.Add(r);
            }
            rows.Sort(delegate(ProcIpRow x, ProcIpRow y)
            {
                double a = (double)x.RxTotal + (double)x.TxTotal;
                double b = (double)y.RxTotal + (double)y.TxTotal;
                return b.CompareTo(a);
            });
            if (rows.Count > 5000) rows = rows.GetRange(0, 5000);
            return rows;
        }

        private static void AddAgg(Dictionary<string, Agg> dict, string key, ulong dIn, ulong dOut)
        {
            Agg a;
            if (!dict.TryGetValue(key, out a))
            {
                a = new Agg();
                dict[key] = a;
            }
            a.TotalIn += dIn;
            a.TotalOut += dOut;
            a.TickIn += dIn;
            a.TickOut += dOut;
        }

        private string ResolveProcess(uint pid)
        {
            string name;
            if (_procNames.TryGetValue(pid, out name)) return name;
            try
            {
                if (pid == 0 || pid == 4)
                {
                    name = "System";
                }
                else
                {
                    using (Process p = Process.GetProcessById((int)pid))
                    {
                        name = p.ProcessName;
                    }
                }
            }
            catch
            {
                name = "PID " + pid.ToString();
            }
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            if (_procNames.Count > 4096) _procNames.Clear();
            _procNames[pid] = name;
            return name;
        }

        private string MapAdapter(string ip)
        {
            string name;
            if (_ipToAdapter.TryGetValue(ip, out name)) return name;
            if (ip.StartsWith("127.", StringComparison.Ordinal)) return "回环";
            return "未知网卡";
        }

        private bool IsLocalAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return true;
            if (ip == "0.0.0.0" || ip == "::") return true;
            if (ip.StartsWith("127.", StringComparison.Ordinal) || ip == "::1") return true;
            return _ipToAdapter.ContainsKey(ip);
        }

        private static string NormalizeIp(string ip)
        {
            if (ip == null) return "";
            int pct = ip.IndexOf('%');
            if (pct >= 0) return ip.Substring(0, pct);
            return ip;
        }

        private static AdapterInfo CopyAdapter(AdapterInfo src)
        {
            AdapterInfo a = new AdapterInfo();
            a.Id = src.Id;
            a.Name = src.Name;
            a.Description = src.Description;
            a.IfIndex = src.IfIndex;
            a.IPv4 = src.IPv4;
            a.PrefixLength = src.PrefixLength;
            a.Gateway = src.Gateway;
            a.Speed = src.Speed;
            a.Up = src.Up;
            a.StatusText = src.StatusText;
            a.IsWifi = src.IsWifi;
            a.WifiSsid = src.WifiSsid;
            a.WifiSignal = src.WifiSignal;
            return a;
        }
    }
}

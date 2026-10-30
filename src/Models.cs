using System;
using System.Collections.Generic;

namespace NetSplitter
{
    internal sealed class AdapterInfo
    {
        public string Id;
        public string Name;
        public string Description;
        public int IfIndex;
        public string IPv4;
        public int PrefixLength;
        public string Gateway;
        public long Speed;
        public bool Up;
        public string StatusText;
        public ulong RxTotal;
        public ulong TxTotal;
        public double RxRate;
        public double TxRate;
        public bool IsWifi;
        public string WifiSsid = "";
        public int WifiSignal;

        public string Display
        {
            get { return string.Format("{0}  ·  {1}/{2}  ·  idx {3}", Name, IPv4, PrefixLength, IfIndex); }
        }
    }

    internal enum RuleKind
    {
        Ip,
        Domain
    }

    internal sealed class SplitRule
    {
        public bool Enabled = true;
        public RuleKind Kind = RuleKind.Ip;
        public string Match;
        public string AdapterId;
        public string AdapterName;
        public int Metric = 1;
        public bool Persistent = true;
        public string LastIps = "";
        public string Status = "待应用";
        public bool Ok;

        public bool IsDomain
        {
            get { return Kind == RuleKind.Domain; }
        }
    }

    internal sealed class ProcTrafficRow
    {
        public string Process;
        public string Adapter;
        public double RxRate;
        public double TxRate;
        public ulong RxTotal;
        public ulong TxTotal;
    }

    internal sealed class IpTrafficRow
    {
        public string RemoteIp;
        public string Adapter;
        public double RxRate;
        public double TxRate;
        public ulong RxTotal;
        public ulong TxTotal;
    }

    internal sealed class ProcIpRow
    {
        public string Process;
        public string Adapter;
        public string RemoteIp;
        public double RxRate;
        public double TxRate;
        public ulong RxTotal;
        public ulong TxTotal;
    }

    internal sealed class ProcBinding
    {
        public string Process;
        public string AdapterId;
        public string AdapterName;
        public int Metric = 1;
    }

    internal sealed class TrafficSnapshot
    {
        public List<AdapterInfo> Adapters = new List<AdapterInfo>();
        public List<ProcTrafficRow> Processes = new List<ProcTrafficRow>();
        public List<IpTrafficRow> Ips = new List<IpTrafficRow>();
        public List<ProcIpRow> ProcIps = new List<ProcIpRow>();
        public int TcpConnections;
        public bool PerProcessAvailable;
        public string StatsMessage = "";
        public DateTime Timestamp = DateTime.Now;
    }
}

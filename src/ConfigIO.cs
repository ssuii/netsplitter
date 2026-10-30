using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NetSplitter
{
    public sealed class ConfigRule
    {
        public bool Enabled { get; set; }
        public string Kind { get; set; }
        public string Match { get; set; }
        public string Adapter { get; set; }
        public string AdapterKind { get; set; }
        public int Metric { get; set; }
        public bool Persistent { get; set; }
        public string LastIps { get; set; }

        public ConfigRule()
        {
            Kind = "ip";
            Match = "";
            Adapter = "";
            AdapterKind = "";
            LastIps = "";
            Metric = 1;
        }
    }

    public sealed class ConfigProcess
    {
        public string Process { get; set; }
        public string Adapter { get; set; }
        public string AdapterKind { get; set; }
        public int Metric { get; set; }

        public ConfigProcess()
        {
            Process = "";
            Adapter = "";
            AdapterKind = "";
            Metric = 1;
        }
    }

    public sealed class ConfigBundle
    {
        public string App { get; set; }
        public int Version { get; set; }
        public string ExportedAt { get; set; }
        public List<ConfigRule> Rules { get; set; }
        public List<ConfigProcess> Processes { get; set; }

        public ConfigBundle()
        {
            App = ConfigIO.AppTag;
            Version = ConfigIO.Version;
            Rules = new List<ConfigRule>();
            Processes = new List<ConfigProcess>();
        }
    }

    internal static class ConfigIO
    {
        public const string AppTag = "NetSplitter";
        public const int Version = 1;
        public const string FileFilter = "NetSplitter 配置 (*.json)|*.json|所有文件 (*.*)|*.*";

        public static ConfigBundle Build(List<SplitRule> rules, List<ProcBinding> bindings, List<AdapterInfo> adapters)
        {
            ConfigBundle bundle = new ConfigBundle();
            bundle.ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (rules != null)
            {
                foreach (SplitRule rule in rules)
                {
                    if (rule == null) continue;
                    AdapterInfo adapter = RouteManager.FindAdapter(adapters, rule);
                    ConfigRule item = new ConfigRule();
                    item.Enabled = rule.Enabled;
                    item.Kind = rule.IsDomain ? "domain" : "ip";
                    item.Match = rule.Match ?? "";
                    item.Adapter = adapter != null ? adapter.Name : (rule.AdapterName ?? "");
                    item.AdapterKind = KindOf(adapter, item.Adapter);
                    item.Metric = rule.Metric;
                    item.Persistent = rule.Persistent;
                    item.LastIps = rule.LastIps ?? "";
                    bundle.Rules.Add(item);
                }
            }
            if (bindings != null)
            {
                foreach (ProcBinding binding in bindings)
                {
                    if (binding == null || string.IsNullOrEmpty(binding.Process)) continue;
                    AdapterInfo adapter = null;
                    if (adapters != null)
                    {
                        foreach (AdapterInfo candidate in adapters)
                        {
                            if (candidate.Id == binding.AdapterId) adapter = candidate;
                        }
                    }
                    ConfigProcess item = new ConfigProcess();
                    item.Process = binding.Process;
                    item.Adapter = adapter != null ? adapter.Name : (binding.AdapterName ?? "");
                    item.AdapterKind = KindOf(adapter, item.Adapter);
                    item.Metric = binding.Metric;
                    bundle.Processes.Add(item);
                }
            }
            return bundle;
        }

        public static void Export(string path, List<SplitRule> rules, List<ProcBinding> bindings, List<AdapterInfo> adapters)
        {
            Save(path, Build(rules, bindings, adapters));
        }

        public static void Save(string path, ConfigBundle bundle)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(bundle);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        public static ConfigBundle Load(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            ConfigBundle bundle;
            try
            {
                bundle = serializer.Deserialize<ConfigBundle>(text);
            }
            catch (Exception ex)
            {
                throw new FormatException("配置文件不是有效的 JSON：" + ex.Message);
            }
            if (bundle == null) throw new FormatException("配置文件为空。");
            if (!string.Equals(bundle.App, AppTag, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("不是 NetSplitter 导出的配置文件。");
            if (bundle.Version <= 0 || bundle.Version > Version)
                throw new FormatException("不支持的配置版本：" + bundle.Version);
            if (bundle.Rules == null) bundle.Rules = new List<ConfigRule>();
            if (bundle.Processes == null) bundle.Processes = new List<ConfigProcess>();
            return bundle;
        }

        public static AdapterInfo ResolveAdapter(List<AdapterInfo> adapters, string name, string kind)
        {
            if (adapters == null || adapters.Count == 0) return null;
            if (!string.IsNullOrEmpty(name))
            {
                foreach (AdapterInfo adapter in adapters)
                {
                    if (string.Equals(adapter.Name, name, StringComparison.CurrentCultureIgnoreCase)) return adapter;
                }
            }
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k.Length == 0) return null;
            bool wantWifi = k == "wifi" || k == "wi-fi" || k == "wireless";
            foreach (AdapterInfo adapter in adapters)
            {
                if (adapter.IsWifi == wantWifi && adapter.Up && !string.IsNullOrEmpty(adapter.IPv4)) return adapter;
            }
            foreach (AdapterInfo adapter in adapters)
            {
                if (adapter.IsWifi == wantWifi) return adapter;
            }
            return null;
        }

        private static string KindOf(AdapterInfo adapter, string name)
        {
            if (adapter != null) return adapter.IsWifi ? "wifi" : "ethernet";
            if (string.IsNullOrEmpty(name)) return "";
            if (name.IndexOf("Wi-Fi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("WLAN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("无线", StringComparison.Ordinal) >= 0) return "wifi";
            return "ethernet";
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NetSplitter
{
    internal sealed class RuleStore
    {
        private readonly string _path;

        public RuleStore()
            : this(Path.Combine(Logger.Dir, "rules.tsv"))
        {
        }

        public RuleStore(string path)
        {
            _path = path;
        }

        public string FilePath
        {
            get { return _path; }
        }

        public List<SplitRule> Load()
        {
            List<SplitRule> list = new List<SplitRule>();
            try
            {
                if (!File.Exists(_path)) return list;
                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 4) continue;
                    SplitRule r = new SplitRule();
                    r.Enabled = parts[0] == "1";
                    r.Match = parts[1];
                    r.AdapterId = parts[2];
                    r.AdapterName = parts[3];
                    if (parts.Length > 4)
                    {
                        int metric;
                        if (int.TryParse(parts[4], out metric) && metric > 0) r.Metric = metric;
                    }
                    if (parts.Length > 5) r.Persistent = parts[5] == "1";
                    if (parts.Length > 6) r.LastIps = parts[6] ?? "";
                    if (parts.Length > 7) r.Kind = parts[7] == "d" ? RuleKind.Domain : RuleKind.Ip;
                    else if (LooksLikeDomain(r.Match)) r.Kind = RuleKind.Domain;
                    r.Status = "待应用";
                    list.Add(r);
                }
            }
            catch (Exception ex)
            {
                Logger.Write("读取规则失败: " + ex.Message);
            }
            return list;
        }

        private static bool LooksLikeDomain(string match)
        {
            if (string.IsNullOrEmpty(match)) return false;
            for (int i = 0; i < match.Length; i++)
            {
                char c = match[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
            }
            return false;
        }

        public void Save(List<SplitRule> rules)
        {
            try
            {
                Directory.CreateDirectory(Logger.Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# NetSplitter rules v1");
                foreach (SplitRule r in rules)
                {
                    sb.AppendLine(string.Join("\t", new string[]
                    {
                        r.Enabled ? "1" : "0",
                        r.Match ?? "",
                        r.AdapterId ?? "",
                        r.AdapterName ?? "",
                        r.Metric.ToString(),
                        r.Persistent ? "1" : "0",
                        r.LastIps ?? "",
                        r.IsDomain ? "d" : "i"
                    }));
                }
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                Logger.Write("保存规则失败: " + ex.Message);
            }
        }
    }

    internal sealed class ProcBindingStore
    {
        private readonly string _path;

        public ProcBindingStore()
            : this(Path.Combine(Logger.Dir, "procbind.tsv"))
        {
        }

        public ProcBindingStore(string path)
        {
            _path = path;
        }

        public string FilePath
        {
            get { return _path; }
        }

        public List<ProcBinding> Load()
        {
            List<ProcBinding> list = new List<ProcBinding>();
            try
            {
                if (!File.Exists(_path)) return list;
                foreach (string raw in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    ProcBinding b = new ProcBinding();
                    b.Process = parts[0].Trim();
                    b.AdapterId = parts[1].Trim();
                    b.AdapterName = parts[2].Trim();
                    int metric;
                    if (parts.Length >= 4 && int.TryParse(parts[3], out metric) && metric > 0) b.Metric = metric;
                    if (b.Process.Length == 0 || b.AdapterId.Length == 0) continue;
                    list.Add(b);
                }
            }
            catch (Exception ex)
            {
                Logger.Write("读取进程跟踪失败: " + ex.Message);
            }
            return list;
        }

        public void Save(List<ProcBinding> bindings)
        {
            try
            {
                Directory.CreateDirectory(Logger.Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# NetSplitter proc bindings v1");
                foreach (ProcBinding b in bindings)
                {
                    sb.AppendLine(string.Join("\t", new string[]
                    {
                        b.Process ?? "",
                        b.AdapterId ?? "",
                        b.AdapterName ?? "",
                        b.Metric.ToString()
                    }));
                }
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                Logger.Write("保存进程跟踪失败: " + ex.Message);
            }
        }
    }
}

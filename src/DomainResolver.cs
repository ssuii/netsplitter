using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace NetSplitter
{
    internal static class DomainResolver
    {
        private sealed class CacheEntry
        {
            public List<string> Ips;
            public string Error;
            public DateTime Expires;
        }

        private const int TimeoutMs = 4000;
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        public static string Normalize(string text, out string error)
        {
            error = null;
            text = (text ?? "").Trim();
            if (text.Length == 0)
            {
                error = "请输入 IP、网段或域名";
                return null;
            }
            if (text.IndexOf('/') >= 0)
            {
                error = "域名不能包含斜杠";
                return null;
            }
            if (text.IndexOf(':') >= 0)
            {
                error = "域名不能包含冒号或端口";
                return null;
            }
            if (text.IndexOf('*') >= 0)
            {
                error = "暂不支持通配符，请填写具体域名";
                return null;
            }
            if (text.EndsWith(".", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1);
            text = text.ToLowerInvariant();
            if (text.Length == 0 || text.Length > 253)
            {
                error = "域名长度不合法";
                return null;
            }

            string[] labels = text.Split('.');
            if (labels.Length < 2)
            {
                error = "域名至少需要两段，例如 example.com";
                return null;
            }

            foreach (string label in labels)
            {
                if (label.Length == 0 || label.Length > 63)
                {
                    error = "域名分段长度不合法";
                    return null;
                }
                if (label.StartsWith("-", StringComparison.Ordinal) || label.EndsWith("-", StringComparison.Ordinal))
                {
                    error = "域名分段不能以连字符开头或结尾";
                    return null;
                }
                for (int i = 0; i < label.Length; i++)
                {
                    char c = label[i];
                    bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                    if (!ok)
                    {
                        error = "域名只支持 ASCII 字母、数字和连字符（中文域名请用 xn-- 形式）";
                        return null;
                    }
                }
            }

            string tld = labels[labels.Length - 1];
            if (tld.Length < 2)
            {
                error = "域名后缀不合法";
                return null;
            }
            bool allDigits = true;
            for (int i = 0; i < tld.Length; i++)
            {
                if (tld[i] < '0' || tld[i] > '9')
                {
                    allDigits = false;
                    break;
                }
            }
            if (allDigits)
            {
                error = "看起来不是有效域名（后缀不能全为数字）";
                return null;
            }
            return text;
        }

        public static bool TryResolve(string domain, out List<string> ips, out string error)
        {
            ips = new List<string>();
            error = null;
            string key = (domain ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0)
            {
                error = "域名为空";
                return false;
            }

            CacheEntry cached;
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out cached) && DateTime.UtcNow < cached.Expires)
                {
                    ips = new List<string>(cached.Ips);
                    error = cached.Error;
                    return cached.Ips.Count > 0;
                }
            }

            List<string> result = new List<string>();
            string failure = null;
            try
            {
                IAsyncResult ar = Dns.BeginGetHostAddresses(key, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeoutMs))
                {
                    failure = "解析超时";
                }
                else
                {
                    IPAddress[] addresses = Dns.EndGetHostAddresses(ar);
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (IPAddress address in addresses)
                    {
                        if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = address.ToString();
                        if (seen.Add(ip)) result.Add(ip);
                    }
                    result.Sort(StringComparer.Ordinal);
                    if (result.Count == 0) failure = "没有 IPv4 记录";
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                if (failure.Length > 60) failure = failure.Substring(0, 60);
            }

            CacheEntry entry = new CacheEntry();
            entry.Ips = result;
            entry.Error = failure;
            entry.Expires = DateTime.UtcNow.Add(Ttl);
            lock (_lock)
            {
                if (_cache.Count > 256) _cache.Clear();
                _cache[key] = entry;
            }

            ips = new List<string>(result);
            error = failure;
            return result.Count > 0;
        }
    }
}

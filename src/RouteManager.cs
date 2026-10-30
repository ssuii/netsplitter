using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetSplitter
{
    internal sealed class RouteEntry
    {
        public string Destination;
        public string Mask;
        public string NextHop;
        public int InterfaceIndex;
        public int Metric;

        public string Prefix
        {
            get
            {
                int prefix = RouteManager.MaskToPrefix(Mask);
                if (prefix < 0) return null;
                return Destination + "/" + prefix.ToString();
            }
        }
    }

    internal sealed class ProcResult
    {
        public int ExitCode;
        public string Output = "";
        public string Error = "";
    }

    internal sealed class RuleResult
    {
        public SplitRule Rule;
        public string Status;
        public bool Ok;
    }

    internal static class RouteManager
    {
        public static bool IsAdmin
        {
            get
            {
                try
                {
                    System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    System.Security.Principal.WindowsPrincipal principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }

        public static List<RouteEntry> QueryRoutes()
        {
            List<RouteEntry> list = new List<RouteEntry>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT Destination,Mask,NextHop,InterfaceIndex,Metric1 FROM Win32_IP4RouteTable"))
            {
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in results)
                    {
                        try
                        {
                            RouteEntry e = new RouteEntry();
                            e.Destination = Convert.ToString(mo["Destination"]);
                            e.Mask = Convert.ToString(mo["Mask"]);
                            e.NextHop = Convert.ToString(mo["NextHop"]);
                            e.InterfaceIndex = Convert.ToInt32(mo["InterfaceIndex"]);
                            e.Metric = Convert.ToInt32(mo["Metric1"]);
                            if (!string.IsNullOrEmpty(e.Destination) && !string.IsNullOrEmpty(e.Mask)) list.Add(e);
                        }
                        catch
                        {
                        }
                        finally
                        {
                            mo.Dispose();
                        }
                    }
                }
            }
            return list;
        }

        public static bool TryParseRule(string text, out IPAddress network, out int prefix, out string error)
        {
            network = null;
            prefix = 0;
            error = null;
            text = (text ?? "").Trim();
            if (text.Length == 0)
            {
                error = "请输入 IP 或网段，例如 8.8.8.8 或 1.1.1.0/24";
                return false;
            }

            string ipPart = text;
            int slash = text.IndexOf('/');
            if (slash > 0)
            {
                ipPart = text.Substring(0, slash);
                string p = text.Substring(slash + 1);
                int n;
                if (!int.TryParse(p, out n) || n < 0 || n > 32)
                {
                    error = "前缀长度必须是 0-32 之间的数字";
                    return false;
                }
                prefix = n;
            }
            else
            {
                prefix = 32;
            }

            IPAddress ip;
            if (!IPAddress.TryParse(ipPart, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "仅支持 IPv4 地址（本版本不支持 IPv6 分流）";
                return false;
            }

            byte[] bytes = ip.GetAddressBytes();
            uint value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
            value &= mask;
            byte[] netBytes = new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
            network = new IPAddress(netBytes);
            return true;
        }

        public static string ToCidr(IPAddress network, int prefix)
        {
            if (prefix >= 32) return network.ToString();
            return network.ToString() + "/" + prefix.ToString();
        }

        public static string PrefixToMask(int prefix)
        {
            uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
            return string.Format("{0}.{1}.{2}.{3}", (mask >> 24) & 0xFF, (mask >> 16) & 0xFF, (mask >> 8) & 0xFF, mask & 0xFF);
        }

        public static int MaskToPrefix(string mask)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(mask, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) return -1;
            byte[] b = ip.GetAddressBytes();
            int prefix = 0;
            bool zeroSeen = false;
            foreach (byte by in b)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    bool one = ((by >> bit) & 1) == 1;
                    if (one)
                    {
                        if (zeroSeen) return -1;
                        prefix++;
                    }
                    else
                    {
                        zeroSeen = true;
                    }
                }
            }
            return prefix;
        }

        public static bool IsInSubnet(IPAddress network, IPAddress ip, int prefix)
        {
            if (network == null || ip == null) return false;
            byte[] a = network.GetAddressBytes();
            byte[] b = ip.GetAddressBytes();
            if (a.Length != 4 || b.Length != 4) return false;
            int full = prefix / 8;
            int rem = prefix % 8;
            for (int i = 0; i < full; i++)
            {
                if (a[i] != b[i]) return false;
            }
            if (rem == 0) return true;
            int mask = (0xFF << (8 - rem)) & 0xFF;
            return (a[full] & mask) == (b[full] & mask);
        }

        public static AdapterInfo FindAdapter(List<AdapterInfo> adapters, SplitRule rule)
        {
            foreach (AdapterInfo a in adapters)
            {
                if (a.Id == rule.AdapterId) return a;
            }
            foreach (AdapterInfo a in adapters)
            {
                if (!string.IsNullOrEmpty(rule.AdapterName) && a.Name == rule.AdapterName) return a;
            }
            return null;
        }

        public static List<RuleResult> ApplyRules(List<SplitRule> rules, List<AdapterInfo> adapters, bool admin)
        {
            List<RuleResult> results = new List<RuleResult>();
            List<RouteEntry> routes = null;
            string routeError = null;

            if (admin)
            {
                try { routes = QueryRoutes(); }
                catch (Exception ex)
                {
                    routeError = ex.Message;
                    Logger.Write("查询路由表失败: " + ex.Message);
                }
            }

            Dictionary<SplitRule, List<string>> resolved = new Dictionary<SplitRule, List<string>>();
            Dictionary<SplitRule, string> resolveErrors = new Dictionary<SplitRule, string>();
            HashSet<string> claimed = new HashSet<string>(StringComparer.Ordinal);

            foreach (SplitRule rule in rules)
            {
                if (!rule.Enabled) continue;
                if (rule.IsDomain)
                {
                    List<string> ips;
                    string error;
                    if (DomainResolver.TryResolve(rule.Match, out ips, out error))
                    {
                        resolved[rule] = ips;
                        foreach (string ip in ips) claimed.Add(ip + "/32");
                    }
                    else
                    {
                        resolveErrors[rule] = error;
                        foreach (string ip in SplitList(rule.LastIps)) claimed.Add(ip + "/32");
                    }
                }
                else
                {
                    IPAddress network;
                    int prefix;
                    string error;
                    if (TryParseRule(rule.Match, out network, out prefix, out error))
                        claimed.Add(network.ToString() + "/" + prefix.ToString());
                }
            }

            foreach (SplitRule rule in rules)
            {
                RuleResult rr = new RuleResult();
                rr.Rule = rule;
                results.Add(rr);
                rr.Ok = false;

                if (!rule.Enabled)
                {
                    rr.Status = "已停用";
                    rr.Ok = true;
                    rule.Status = rr.Status;
                    rule.Ok = rr.Ok;
                    continue;
                }

                IPAddress network = null;
                int prefix = 0;
                if (!rule.IsDomain)
                {
                    string parseError;
                    if (!TryParseRule(rule.Match, out network, out prefix, out parseError))
                    {
                        rr.Status = "无效: " + parseError;
                        rule.Status = rr.Status;
                        rule.Ok = false;
                        continue;
                    }
                }

                AdapterInfo adapter = FindAdapter(adapters, rule);
                if (adapter == null)
                {
                    rr.Status = "网卡不可用（已拔出或禁用）";
                    rule.Status = rr.Status;
                    rule.Ok = false;
                    continue;
                }
                if (!adapter.Up)
                {
                    rr.Status = "网卡未连接";
                    rule.Status = rr.Status;
                    rule.Ok = false;
                    continue;
                }
                if (string.IsNullOrEmpty(adapter.IPv4))
                {
                    rr.Status = "网卡没有 IPv4 地址";
                    rule.Status = rr.Status;
                    rule.Ok = false;
                    continue;
                }
                if (!admin)
                {
                    rr.Status = "需要管理员权限";
                    rule.Status = rr.Status;
                    rule.Ok = false;
                    continue;
                }
                if (routeError != null || routes == null)
                {
                    rr.Status = "读取路由表失败: " + Shorten(routeError, 60);
                    rule.Status = rr.Status;
                    rule.Ok = false;
                    continue;
                }

                if (rule.IsDomain)
                {
                    ApplyDomainRule(rule, rr, adapter, routes, resolved, resolveErrors, claimed);
                    continue;
                }

                string prefixText = network.ToString() + "/" + prefix.ToString();
                bool existsOnTarget = false;
                bool conflict = false;
                foreach (RouteEntry re in routes)
                {
                    if (re.Prefix != prefixText) continue;
                    if (re.InterfaceIndex == adapter.IfIndex) existsOnTarget = true;
                    else conflict = true;
                }

                if (!existsOnTarget)
                {
                    string nextHop;
                    bool onLink = IsInSubnet(network, IPAddress.Parse(adapter.IPv4), adapter.PrefixLength);
                    if (onLink)
                    {
                        nextHop = "0.0.0.0";
                    }
                    else if (!string.IsNullOrEmpty(adapter.Gateway))
                    {
                        nextHop = adapter.Gateway;
                    }
                    else
                    {
                        rr.Status = "该网卡没有默认网关，无法到达此网段";
                        rule.Status = rr.Status;
                        rule.Ok = false;
                        continue;
                    }

                    int metric = rule.Metric;
                    if (metric < 1) metric = 1;
                    if (metric > 9999) metric = 9999;

                    ProcResult pr = AddRoute(prefixText, adapter.IfIndex, nextHop, metric, rule.Persistent);
                    if (pr.ExitCode != 0)
                    {
                        rr.Status = "下发失败: " + Shorten(pr.Error, 90);
                        rule.Status = rr.Status;
                        rule.Ok = false;
                        continue;
                    }
                }

                rr.Ok = true;
                if (conflict) rr.Status = "已生效（注意: 其他网卡存在同网段路由）";
                else if (existsOnTarget) rr.Status = "已生效（路由已存在）";
                else rr.Status = "已生效";
                rule.Status = rr.Status;
                rule.Ok = rr.Ok;
            }

            return results;
        }

        private static void ApplyDomainRule(SplitRule rule, RuleResult rr, AdapterInfo adapter, List<RouteEntry> routes,
            Dictionary<SplitRule, List<string>> resolved, Dictionary<SplitRule, string> resolveErrors,
            HashSet<string> claimed)
        {
            List<string> ips;
            if (!resolved.TryGetValue(rule, out ips))
            {
                string error;
                if (!resolveErrors.TryGetValue(rule, out error) || string.IsNullOrEmpty(error)) error = "未知错误";
                rr.Status = "域名解析失败: " + error;
                rule.Status = rr.Status;
                rule.Ok = false;
                return;
            }

            int added = 0;
            int existed = 0;
            int failed = 0;
            int removed = 0;
            string firstError = null;
            bool conflict = false;
            IPAddress local = IPAddress.Parse(adapter.IPv4);

            foreach (string ip in ips)
            {
                string prefixText = ip + "/32";
                bool existsOnTarget = false;
                foreach (RouteEntry re in routes)
                {
                    if (re.Prefix != prefixText) continue;
                    if (re.InterfaceIndex == adapter.IfIndex) existsOnTarget = true;
                    else conflict = true;
                }
                if (existsOnTarget)
                {
                    existed++;
                    continue;
                }

                string nextHop;
                if (IsInSubnet(IPAddress.Parse(ip), local, adapter.PrefixLength))
                {
                    nextHop = "0.0.0.0";
                }
                else if (!string.IsNullOrEmpty(adapter.Gateway))
                {
                    nextHop = adapter.Gateway;
                }
                else
                {
                    failed++;
                    if (firstError == null) firstError = "该网卡没有默认网关";
                    continue;
                }

                int metric = rule.Metric;
                if (metric < 1) metric = 1;
                if (metric > 9999) metric = 9999;

                ProcResult pr = AddRoute(prefixText, adapter.IfIndex, nextHop, metric, rule.Persistent);
                if (pr.ExitCode != 0)
                {
                    failed++;
                    if (firstError == null) firstError = pr.Error;
                }
                else
                {
                    added++;
                }
            }

            HashSet<string> current = new HashSet<string>(ips, StringComparer.Ordinal);
            foreach (string stale in SplitList(rule.LastIps))
            {
                if (current.Contains(stale)) continue;
                string prefixText = stale + "/32";
                if (claimed.Contains(prefixText)) continue;
                ProcResult pr = RemoveRoute(prefixText, adapter.IfIndex);
                if (pr != null && pr.ExitCode == 0) removed++;
            }

            rule.LastIps = string.Join(",", ips.ToArray());

            if (failed == 0)
            {
                rr.Ok = true;
                StringBuilder status = new StringBuilder();
                status.Append("已生效（").Append(ips.Count).Append(" 个 IP");
                if (added > 0) status.Append("，新增 ").Append(added);
                if (removed > 0) status.Append("，清理 ").Append(removed);
                status.Append("）");
                if (conflict) status.Append("（注意: 其他网卡存在同网段路由）");
                rr.Status = status.ToString();
            }
            else
            {
                rr.Status = "部分失败: " + failed + "/" + ips.Count + " 个 IP，" + Shorten(firstError, 60);
            }
            rule.Status = rr.Status;
            rule.Ok = rr.Ok;
        }

        public static ProcResult RemoveRule(SplitRule rule, List<AdapterInfo> adapters)
        {
            if (!IsAdmin) return null;
            AdapterInfo adapter = FindAdapter(adapters, rule);
            if (adapter == null) return null;

            if (rule.IsDomain)
            {
                ProcResult last = new ProcResult();
                List<string> ips = SplitList(rule.LastIps);
                if (ips.Count == 0) return last;
                foreach (string ip in ips)
                {
                    ProcResult pr = RemoveRoute(ip + "/32", adapter.IfIndex);
                    if (pr != null) last = pr;
                }
                return last;
            }

            IPAddress network;
            int prefix;
            string parseError;
            if (!TryParseRule(rule.Match, out network, out prefix, out parseError)) return null;
            return RemoveRoute(network.ToString() + "/" + prefix.ToString(), adapter.IfIndex);
        }

        private static List<string> SplitList(string csv)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(csv)) return list;
            foreach (string part in csv.Split(','))
            {
                string value = (part ?? "").Trim();
                if (value.Length > 0) list.Add(value);
            }
            return list;
        }

        public static ProcResult AddRoute(string prefix, int ifIndex, string nextHop, int metric, bool persistent)
        {
            // 部分 Windows 11 构建（如 26200）用 -PolicyStore PersistentStore 会报 Error 87；
            // 省略该参数时默认同时写入 ActiveStore 与 PersistentStore，效果等同持久化。
            string store = persistent ? "" : " -PolicyStore ActiveStore";
            string command = string.Format(
                "New-NetRoute -DestinationPrefix '{0}' -InterfaceIndex {1} -NextHop '{2}' -RouteMetric {3}{4} -ErrorAction Stop | Out-Null",
                prefix, ifIndex, nextHop, metric, store);
            return RunPowerShell(command);
        }

        public static ProcResult RemoveRoute(string prefix, int ifIndex)
        {
            ProcResult active = RunPowerShell(string.Format(
                "Remove-NetRoute -DestinationPrefix '{0}' -InterfaceIndex {1} -PolicyStore ActiveStore -Confirm:$false -ErrorAction Stop | Out-Null",
                prefix, ifIndex));
            RunPowerShell(string.Format(
                "Remove-NetRoute -DestinationPrefix '{0}' -InterfaceIndex {1} -PolicyStore PersistentStore -Confirm:$false -ErrorAction Stop | Out-Null",
                prefix, ifIndex));
            return active;
        }

        public static string DescribePlan(SplitRule rule, List<AdapterInfo> adapters)
        {
            if (rule.IsDomain)
            {
                AdapterInfo target = FindAdapter(adapters, rule);
                if (target == null) return "网卡不可用";
                List<string> ips;
                string error;
                if (!DomainResolver.TryResolve(rule.Match, out ips, out error))
                    return "域名解析失败: " + error;
                StringBuilder sb = new StringBuilder();
                sb.Append("解析 ").Append(rule.Match).Append(" -> ").Append(string.Join(", ", ips.ToArray()));
                sb.Append("；对每个 IP 执行 New-NetRoute -DestinationPrefix '<ip>/32' -InterfaceIndex ");
                sb.Append(target.IfIndex).Append(rule.Persistent ? "（默认写入活动+持久存储）" : " -PolicyStore ActiveStore");
                return sb.ToString();
            }

            IPAddress network;
            int prefix;
            string parseError;
            if (!TryParseRule(rule.Match, out network, out prefix, out parseError)) return "无效规则: " + parseError;
            AdapterInfo adapter = FindAdapter(adapters, rule);
            if (adapter == null) return "网卡不可用";
            bool onLink = IsInSubnet(network, IPAddress.Parse(adapter.IPv4), adapter.PrefixLength);
            string nextHop = onLink ? "0.0.0.0" : adapter.Gateway;
            if (string.IsNullOrEmpty(nextHop)) nextHop = "(无网关)";
            return string.Format("New-NetRoute -DestinationPrefix '{0}/{1}' -InterfaceIndex {2} -NextHop '{3}' -RouteMetric {4}{5}",
                network, prefix, adapter.IfIndex, nextHop, rule.Metric,
                rule.Persistent ? "（默认写入活动+持久存储）" : " -PolicyStore ActiveStore");
        }

        public static ProcResult RunPowerShell(string scriptBody)
        {
            string command = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"try { " + scriptBody +
                             "; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\"";
            return Run("powershell.exe", command);
        }

        public static ProcResult Run(string exe, string arguments)
        {
            ProcResult result = new ProcResult();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Encoding enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                psi.StandardOutputEncoding = enc;
                psi.StandardErrorEncoding = enc;

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(20000))
                    {
                        try { p.Kill(); } catch { }
                        result.ExitCode = -1;
                        result.Output = output;
                        result.Error = "命令执行超时";
                        return result;
                    }
                    result.ExitCode = p.ExitCode;
                    result.Output = output;
                    result.Error = err;
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.Error = ex.Message;
            }
            return result;
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "未知错误";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > max) text = text.Substring(0, max) + "…";
            return text;
        }
    }
}

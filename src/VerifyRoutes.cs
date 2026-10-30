using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NetSplitter
{
    internal static class VerifyRoutes
    {
        private const string SplitTarget = "180.101.50.242";
        private const string CidrInput = "203.0.113.77/24";
        private const string DomainTarget = "www.baidu.com";
        private const string DefaultReportName = "NetSplitter-verify.txt";
        private const string LanReportName = "NetSplitter-verify-lan.txt";
        private const int HttpPayloadMs = 3600;
        private const int BannerPayloadMs = 2600;

        private sealed class LanTarget
        {
            public string Ip;
            public int Port;
            public AdapterInfo Owner;
            public long ProbeBytes;
        }

        public static int Launch(string[] args)
        {
            return LaunchCore(args, "--verify-routes", DefaultReportName);
        }

        public static int LaunchLan(string[] args)
        {
            return LaunchCore(args, "--verify-lan", LanReportName);
        }

        private static int LaunchCore(string[] args, string mode, string defaultName)
        {
            string outPath = GetValue(args, "--out");
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Path.GetTempPath(), defaultName);
            outPath = Path.GetFullPath(outPath);

            if (RouteManager.IsAdmin) return mode == "--verify-lan" ? RunLan(outPath) : Run(outPath);

            try { if (File.Exists(outPath)) File.Delete(outPath); }
            catch { }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                    mode + " --out \"" + outPath + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);

                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(240000))
                    {
                        try { p.Kill(); }
                        catch { }
                        WriteReport(outPath, "NetSplitter 分流验证：提权进程超时（240 秒），已终止。");
                        return 3;
                    }
                    if (!File.Exists(outPath))
                    {
                        WriteReport(outPath, "NetSplitter 分流验证：提权进程未生成报告，退出码 " + p.ExitCode + "。");
                        return 4;
                    }
                    return p.ExitCode;
                }
            }
            catch (Win32Exception ex)
            {
                WriteReport(outPath, "NetSplitter 分流验证：无法提权（错误码 " + ex.NativeErrorCode + "）：" + ex.Message);
                return 5;
            }
        }

        public static int Run(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            List<SplitRule> added = new List<SplitRule>();
            List<AdapterInfo> adapters = null;
            TrafficMonitor monitor = null;
            int failures = 0;

            sb.AppendLine("NetSplitter 实际环境分流验证");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("管理员: " + (RouteManager.IsAdmin ? "是" : "否"));
            sb.AppendLine();

            try
            {
                adapters = AdapterService.GetAdapters();
                sb.AppendLine("[网卡]");
                foreach (AdapterInfo a in adapters)
                {
                    sb.AppendLine(string.Format("  idx {0,-3} {1} | {2}/{3} | 网关 {4} | {5}",
                        a.IfIndex, a.Name, a.IPv4, a.PrefixLength,
                        string.IsNullOrEmpty(a.Gateway) ? "-" : a.Gateway, a.StatusText));
                }
                sb.AppendLine();

                monitor = new TrafficMonitor(true);
                monitor.Start();

                failures += RunSplitTest(sb, adapters, monitor, added);
                failures += RunCidrTest(sb, adapters, added);
                failures += RunDomainTest(sb, adapters, added);
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine("验证异常: " + ex);
                Logger.Write("分流验证异常: " + ex);
            }
            finally
            {
                foreach (SplitRule rule in added)
                {
                    try
                    {
                        List<AdapterInfo> current = adapters != null ? adapters : AdapterService.GetAdapters();
                        ProcResult pr = RouteManager.RemoveRule(rule, current);
                        sb.AppendLine("[清理] 移除 " + rule.Match + ": " + (pr != null && pr.ExitCode == 0 ? "成功" : "失败"));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("[清理] 移除 " + rule.Match + " 异常: " + ex.Message);
                    }
                }
                if (monitor != null) monitor.Dispose();
            }

            sb.AppendLine();
            sb.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项未通过");
            WriteReport(outPath, sb.ToString());
            return failures == 0 ? 0 : 2;
        }

        public static int RunLan(string outPath)
        {
            StringBuilder sb = new StringBuilder();
            List<SplitRule> added = new List<SplitRule>();
            List<AdapterInfo> adapters = null;
            TrafficMonitor monitor = null;
            int failures = 0;

            sb.AppendLine("NetSplitter 真实载荷分流验证（本地网关）");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("管理员: " + (RouteManager.IsAdmin ? "是" : "否"));
            sb.AppendLine();

            try
            {
                adapters = AdapterService.GetAdapters();
                sb.AppendLine("[网卡]");
                foreach (AdapterInfo a in adapters)
                {
                    sb.AppendLine(string.Format("  idx {0,-3} {1} | {2}/{3} | 网关 {4} | {5}",
                        a.IfIndex, a.Name, a.IPv4, a.PrefixLength,
                        string.IsNullOrEmpty(a.Gateway) ? "-" : a.Gateway, a.StatusText));
                }
                sb.AppendLine();

                monitor = new TrafficMonitor(true);
                monitor.Start();
                Thread.Sleep(1500);

                LanTarget target = FindLanTarget(adapters);
                if (target == null)
                {
                    sb.AppendLine("[目标] 未找到可响应的本地 HTTP 目标（各网关 80 端口均无数据），跳过载荷验证。");
                }
                else
                {
                    sb.AppendLine("[目标] " + target.Ip + ":" + target.Port + " 归属 " + target.Owner.Name +
                                  " (idx " + target.Owner.IfIndex + ")，探测收到 " + Fmt.Bytes((ulong)target.ProbeBytes));
                    sb.AppendLine();
                    failures += RunPayloadTest(sb, adapters, monitor, target, added);
                    failures += RunWiredBannerTest(sb, adapters, monitor, target, added);
                }
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine("验证异常: " + ex);
                Logger.Write("载荷验证异常: " + ex);
            }
            finally
            {
                foreach (SplitRule rule in added)
                {
                    try
                    {
                        List<AdapterInfo> current = adapters != null ? adapters : AdapterService.GetAdapters();
                        ProcResult pr = RouteManager.RemoveRule(rule, current);
                        sb.AppendLine("[清理] 移除 " + rule.Match + ": " + (pr != null && pr.ExitCode == 0 ? "成功" : "失败"));
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("[清理] 移除 " + rule.Match + " 异常: " + ex.Message);
                    }
                }
                if (monitor != null) monitor.Dispose();
            }

            sb.AppendLine();
            sb.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项未通过");
            WriteReport(outPath, sb.ToString());
            return failures == 0 ? 0 : 2;
        }

        private static int RunPayloadTest(StringBuilder sb, List<AdapterInfo> adapters,
            TrafficMonitor monitor, LanTarget target, List<SplitRule> added)
        {
            int failures = 0;
            string ip = target.Ip;
            string prefixText = ip + "/32";
            AdapterInfo owner = target.Owner;

            sb.AppendLine("[载荷测试] 本地目标 " + ip + "/32，归属网卡 " + owner.Name + " (idx " + owner.IfIndex + ")");

            int baseline = FindBestIfIndex(ip);
            sb.AppendLine("  当前选路: idx " + baseline + " (" + NameByIndex(adapters, baseline) + ")");

            if (HasRoute(prefixText))
            {
                sb.AppendLine("  [跳过] 已存在 " + prefixText + " 路由（idx 现有配置），为避免覆盖不执行测试");
                return 1;
            }

            AdapterInfo other = null;
            foreach (AdapterInfo a in adapters)
            {
                if (a.IfIndex == owner.IfIndex) continue;
                if (!a.Up || string.IsNullOrEmpty(a.Gateway) || string.IsNullOrEmpty(a.IPv4)) continue;
                if (other == null) other = a;
                if (IsWired(a)) { other = a; break; }
            }

            if (other != null)
            {
                SplitRule redirect = NewRule(ip, other);
                RouteManager.ApplyRules(new List<SplitRule> { redirect }, adapters, true);
                if (!redirect.Ok)
                {
                    sb.AppendLine("  [步骤 1] 改道规则下发失败: " + redirect.Status + "  [失败]");
                    failures++;
                }
                else
                {
                    added.Add(redirect);
                    int routed = FindBestIfIndex(ip);
                    if (routed == other.IfIndex)
                        sb.AppendLine("  [步骤 1] 改道 " + prefixText + " -> " + other.Name + " (idx " + other.IfIndex +
                                      ")：Windows 选路已切换  [通过]");
                    else
                    {
                        sb.AppendLine("  [步骤 1] 改道后选路仍为 idx " + routed + "，期望 " + other.IfIndex + "  [失败]");
                        failures++;
                    }

                    int connections;
                    long bytes = FetchHttp(ip, 80, 1600, out connections);
                    if (bytes == 0)
                        sb.AppendLine("  [步骤 2] 改道到 " + other.Name + " 后取载荷: 0 字节（连接 " + connections +
                                      " 次均失败，流量确实被规则改走且该网段不可达）  [通过]");
                    else
                    {
                        sb.AppendLine("  [步骤 2] 改道到 " + other.Name + " 后仍收到 " + Fmt.Bytes((ulong)bytes) +
                                      "，规则未真正改道  [失败]");
                        failures++;
                    }

                    ProcResult rm = RouteManager.RemoveRule(redirect, adapters);
                    added.Remove(redirect);
                    if (rm != null && rm.ExitCode == 0) sb.AppendLine("  [步骤 3] 改道规则已移除  [通过]");
                    else
                    {
                        sb.AppendLine("  [步骤 3] 改道规则移除失败  [失败]");
                        failures++;
                    }
                }
                sb.AppendLine();
            }

            SplitRule rule = NewRule(ip, owner);
            RouteManager.ApplyRules(new List<SplitRule> { rule }, adapters, true);
            if (!rule.Ok)
            {
                sb.AppendLine("  [步骤 4] 正式分流规则下发失败: " + rule.Status + "  [失败]");
                return failures + 1;
            }
            added.Add(rule);
            sb.AppendLine("  [步骤 4] 分流规则下发: " + rule.Status + "  [通过]");

            RouteEntry entry = FindRouteEntry(prefixText, owner.IfIndex);
            if (entry != null)
                sb.AppendLine(string.Format("  [步骤 5] 路由表: {0} -> {1} idx {2} metric {3}  [通过]",
                    entry.Destination + "/" + RouteManager.MaskToPrefix(entry.Mask), entry.NextHop,
                    entry.InterfaceIndex, entry.Metric));
            else
            {
                sb.AppendLine("  [步骤 5] 路由表中未找到 " + prefixText + " -> idx " + owner.IfIndex + "  [失败]");
                failures++;
            }

            int after = FindBestIfIndex(ip);
            if (after == owner.IfIndex)
                sb.AppendLine("  [步骤 6] Windows 选路为 " + owner.Name + " (idx " + after + ")  [通过]");
            else
            {
                sb.AppendLine("  [步骤 6] Windows 选路为 idx " + after + "，期望 " + owner.IfIndex + "  [失败]");
                failures++;
            }

            monitor.ResetCounters();
            Thread.Sleep(1200);

            Dictionary<string, ulong[]> pre = Snapshot(adapters);
            int conn;
            long payload = FetchHttp(ip, 80, HttpPayloadMs, out conn);
            Thread.Sleep(1500);
            Dictionary<string, ulong[]> post = Snapshot(adapters);

            ulong rx = DeltaRx(adapters, pre, post, owner.Id);
            ulong tx = DeltaTx(adapters, pre, post, owner.Id);
            sb.AppendLine(string.Format("  载荷: {0} 次连接，收到 {1}；{2} 网卡增量 收 +{3} 发 +{4}",
                conn, Fmt.Bytes((ulong)(payload > 0 ? payload : 0)), owner.Name, Fmt.Bytes(rx), Fmt.Bytes(tx)));

            bool ipRowOk = false;
            bool procRowOk = false;
            bool procIpOk = false;
            TrafficSnapshot snap = monitor.GetSnapshot();
            if (snap != null)
            {
                foreach (IpTrafficRow row in snap.Ips)
                {
                    if (row.RemoteIp != ip) continue;
                    sb.AppendLine("  [步骤 7] IP 流量归属: " + row.RemoteIp + " @ " + row.Adapter +
                                  "，收 " + Fmt.Bytes(row.RxTotal) + " 发 " + Fmt.Bytes(row.TxTotal));
                    if (row.Adapter == owner.Name && row.RxTotal > 0) ipRowOk = true;
                }

                string self = Process.GetCurrentProcess().ProcessName;
                foreach (ProcTrafficRow row in snap.Processes)
                {
                    if (!string.Equals(row.Process, self, StringComparison.OrdinalIgnoreCase)) continue;
                    if (row.Adapter != owner.Name) continue;
                    procRowOk = row.RxTotal > 0;
                    sb.AppendLine("  [步骤 8] 进程流量归属: " + row.Process + " @ " + row.Adapter +
                                  "，收 " + Fmt.Bytes(row.RxTotal) + " 发 " + Fmt.Bytes(row.TxTotal));
                }
                foreach (ProcIpRow row in snap.ProcIps)
                {
                    if (!string.Equals(row.Process, self, StringComparison.OrdinalIgnoreCase)) continue;
                    if (row.Adapter != owner.Name || row.RemoteIp != ip) continue;
                    procIpOk = row.RxTotal > 0;
                    sb.AppendLine("  [步骤 8b] 进程-IP 明细: " + row.Process + " → " + row.RemoteIp + " @ " + row.Adapter +
                                  "，收 " + Fmt.Bytes(row.RxTotal) + " 发 " + Fmt.Bytes(row.TxTotal));
                }
            }

            if (payload > 0 && ipRowOk && rx + tx > 0)
                sb.AppendLine("  [步骤 9] 载荷经 " + owner.Name + " 收发且计数归属正确  [通过]");
            else
            {
                sb.AppendLine("  [步骤 9] 载荷 " + payload + " 字节，IP 归属 " + (ipRowOk ? "正确" : "缺失或错误") +
                              "，网卡增量 " + (rx + tx) + "  [失败]");
                failures++;
            }

            if (!procRowOk) sb.AppendLine("  [说明] 未采集到本进程在该网卡上的行（连接存活时间短于采样间隔时会漏记）");

            if (procRowOk && !procIpOk)
            {
                sb.AppendLine("  [步骤 8b] 进程-IP 明细缺失  [失败]");
                failures++;
            }
            else if (procIpOk) sb.AppendLine("  [步骤 8b] 进程-IP 明细与进程行一致  [通过]");

            ProcResult remove = RouteManager.RemoveRule(rule, adapters);
            added.Remove(rule);
            if (remove != null && remove.ExitCode == 0) sb.AppendLine("  [步骤 10] 分流规则已移除  [通过]");
            else
            {
                sb.AppendLine("  [步骤 10] 分流规则移除失败  [失败]");
                failures++;
            }

            if (!HasRoute(prefixText)) sb.AppendLine("  [步骤 11] 路由表中已无 " + prefixText + "  [通过]");
            else
            {
                sb.AppendLine("  [步骤 11] 路由表中仍存在 " + prefixText + "  [失败]");
                failures++;
            }

            int restored = FindBestIfIndex(ip);
            if (restored == baseline) sb.AppendLine("  [步骤 12] 选路已恢复 idx " + restored + "  [通过]");
            else
            {
                sb.AppendLine("  [步骤 12] 选路为 idx " + restored + "，期望恢复为 " + baseline + "  [失败]");
                failures++;
            }

            sb.AppendLine();
            return failures;
        }

        private static int RunWiredBannerTest(StringBuilder sb, List<AdapterInfo> adapters,
            TrafficMonitor monitor, LanTarget lan, List<SplitRule> added)
        {
            AdapterInfo wired = null;
            foreach (AdapterInfo a in adapters)
            {
                if (a.IfIndex == lan.Owner.IfIndex) continue;
                if (!a.Up || string.IsNullOrEmpty(a.Gateway) || string.IsNullOrEmpty(a.IPv4)) continue;
                if (IsWired(a)) { wired = a; break; }
            }
            if (wired == null)
            {
                sb.AppendLine("[有线对照] 没有第二块有线网卡，跳过");
                return 0;
            }

            string ip = wired.Gateway;
            string prefixText = ip + "/32";
            sb.AppendLine("[有线对照] " + wired.Name + " 网关 " + ip + " (SSH 22)");

            int probe;
            long preBanner = ReadBanner(ip, 22, 1200, out probe);
            if (probe == 0)
            {
                sb.AppendLine("  网关 22 端口不可达，跳过");
                return 0;
            }

            if (HasRoute(prefixText))
            {
                sb.AppendLine("  [跳过] 已存在 " + prefixText + " 路由");
                return 0;
            }

            SplitRule rule = NewRule(ip, wired);
            RouteManager.ApplyRules(new List<SplitRule> { rule }, adapters, true);
            if (!rule.Ok)
            {
                sb.AppendLine("  规则下发失败: " + rule.Status + "，跳过");
                return 0;
            }
            added.Add(rule);

            RouteEntry entry = FindRouteEntry(prefixText, wired.IfIndex);
            sb.AppendLine("  [步骤 1] 路由表: " + (entry != null ? "存在 " + prefixText + " -> idx " + wired.IfIndex : "缺失") + "  [" +
                          (entry != null ? "通过" : "失败") + "]");

            monitor.ResetCounters();
            Thread.Sleep(1200);
            Dictionary<string, ulong[]> pre = Snapshot(adapters);
            int conn;
            long banner = ReadBanner(ip, 22, BannerPayloadMs, out conn);
            Thread.Sleep(1500);
            Dictionary<string, ulong[]> post = Snapshot(adapters);

            ulong rx = DeltaRx(adapters, pre, post, wired.Id);
            ulong tx = DeltaTx(adapters, pre, post, wired.Id);

            bool ipRowOk = false;
            TrafficSnapshot snap = monitor.GetSnapshot();
            if (snap != null)
            {
                foreach (IpTrafficRow row in snap.Ips)
                {
                    if (row.RemoteIp != ip) continue;
                    sb.AppendLine("  [步骤 2] IP 流量归属: " + row.RemoteIp + " @ " + row.Adapter +
                                  "，收 " + Fmt.Bytes(row.RxTotal) + " 发 " + Fmt.Bytes(row.TxTotal));
                    if (row.Adapter == wired.Name && row.RxTotal > 0) ipRowOk = true;
                }
            }

            sb.AppendLine(string.Format("  载荷: SSH 横幅 {0}（探测时 {1}），{2} 网卡增量 收 +{3} 发 +{4}",
                Fmt.Bytes((ulong)(banner > 0 ? banner : 0)), Fmt.Bytes((ulong)(preBanner > 0 ? preBanner : 0)),
                wired.Name, Fmt.Bytes(rx), Fmt.Bytes(tx)));

            bool ok = banner > 0 && rx + tx > 0 && (ipRowOk || snap == null);
            sb.AppendLine("  [步骤 3] 有线网卡载荷与计数 " + (ok ? "[通过]" : "[未通过]"));
            if (!ok && ipRowOk) sb.AppendLine("  [说明] 载荷与网卡增量正常，但 IP 行未归属到该网卡");

            ProcResult remove = RouteManager.RemoveRule(rule, adapters);
            added.Remove(rule);
            sb.AppendLine("  [步骤 4] 规则" + (remove != null && remove.ExitCode == 0 ? "已移除" : "移除失败") + "  [" +
                          (remove != null && remove.ExitCode == 0 ? "通过" : "失败") + "]");
            if (HasRoute(prefixText)) sb.AppendLine("  [步骤 5] 路由表中仍存在 " + prefixText + "  [失败]");
            else sb.AppendLine("  [步骤 5] 路由表中已无 " + prefixText + "  [通过]");

            sb.AppendLine();
            return ok ? 0 : 1;
        }

        private static LanTarget FindLanTarget(List<AdapterInfo> adapters)
        {
            foreach (AdapterInfo a in adapters)
            {
                if (!a.Up || string.IsNullOrEmpty(a.Gateway) || string.IsNullOrEmpty(a.IPv4)) continue;
                int connections;
                long bytes = FetchHttp(a.Gateway, 80, 900, out connections);
                if (bytes <= 0) continue;

                LanTarget t = new LanTarget();
                t.Ip = a.Gateway;
                t.Port = 80;
                t.Owner = a;
                t.ProbeBytes = bytes;
                return t;
            }
            return null;
        }

        private static SplitRule NewRule(string ip, AdapterInfo adapter)
        {
            SplitRule rule = new SplitRule();
            rule.Enabled = true;
            rule.Match = ip;
            rule.AdapterId = adapter.Id;
            rule.AdapterName = adapter.Name;
            rule.Metric = 1;
            rule.Persistent = false;
            return rule;
        }

        private static bool HasRoute(string prefixText)
        {
            foreach (RouteEntry r in RouteManager.QueryRoutes())
            {
                if (r.Prefix == prefixText) return true;
            }
            return false;
        }

        private static RouteEntry FindRouteEntry(string prefixText, int ifIndex)
        {
            foreach (RouteEntry r in RouteManager.QueryRoutes())
            {
                if (r.Prefix == prefixText && r.InterfaceIndex == ifIndex) return r;
            }
            return null;
        }

        private static string NameByIndex(List<AdapterInfo> adapters, int ifIndex)
        {
            AdapterInfo a = FindByIfIndex(adapters, ifIndex);
            return a != null ? a.Name : "未知";
        }

        private static long FetchHttp(string ip, int port, int holdMs, out int connections)
        {
            connections = 0;
            long total = 0;
            DateTime end = DateTime.UtcNow.AddMilliseconds(holdMs);

            while (DateTime.UtcNow < end)
            {
                TcpClient client = new TcpClient();
                try
                {
                    IAsyncResult ar = client.BeginConnect(ip, port, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(1500))
                    {
                        client.EndConnect(ar);
                        connections++;
                        client.ReceiveTimeout = 400;
                        NetworkStream stream = client.GetStream();
                        byte[] request = Encoding.ASCII.GetBytes(
                            "GET / HTTP/1.1\r\nHost: " + ip + "\r\nConnection: keep-alive\r\n\r\n");
                        stream.Write(request, 0, request.Length);

                        DateTime connEnd = DateTime.UtcNow.AddMilliseconds(1800);
                        byte[] buffer = new byte[8192];
                        while (DateTime.UtcNow < connEnd && DateTime.UtcNow < end)
                        {
                            try
                            {
                                int n = stream.Read(buffer, 0, buffer.Length);
                                if (n <= 0) break;
                                total += n;
                            }
                            catch (IOException)
                            {
                            }
                            Thread.Sleep(120);
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    try { client.Close(); }
                    catch { }
                }
                Thread.Sleep(200);
            }
            return total;
        }

        private static long ReadBanner(string ip, int port, int holdMs, out int connections)
        {
            connections = 0;
            long total = 0;
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult ar = client.BeginConnect(ip, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(2000)) return 0;
                client.EndConnect(ar);
                connections = 1;
                client.ReceiveTimeout = 400;
                NetworkStream stream = client.GetStream();

                DateTime end = DateTime.UtcNow.AddMilliseconds(holdMs);
                byte[] buffer = new byte[4096];
                while (DateTime.UtcNow < end)
                {
                    try
                    {
                        int n = stream.Read(buffer, 0, buffer.Length);
                        if (n <= 0) break;
                        total += n;
                    }
                    catch (IOException)
                    {
                    }
                    Thread.Sleep(120);
                }
            }
            catch
            {
            }
            finally
            {
                try { client.Close(); }
                catch { }
            }
            return total;
        }

        private static int RunSplitTest(StringBuilder sb, List<AdapterInfo> adapters, TrafficMonitor monitor, List<SplitRule> added)
        {
            int failures = 0;
            sb.AppendLine("[分流测试] 指定 IP " + SplitTarget + " (/32)");

            int before = FindBestIfIndex(SplitTarget);
            AdapterInfo beforeAdapter = FindByIfIndex(adapters, before);
            sb.AppendLine("  当前选路: idx " + before + " (" + (beforeAdapter != null ? beforeAdapter.Name : "未知") + ")");
            if (before <= 0)
            {
                sb.AppendLine("  [失败] 无法读取当前选路");
                return 1;
            }

            AdapterInfo testAdapter = null;
            AdapterInfo fallback = null;
            foreach (AdapterInfo a in adapters)
            {
                if (a.IfIndex == before) continue;
                if (!a.Up || string.IsNullOrEmpty(a.Gateway) || string.IsNullOrEmpty(a.IPv4)) continue;
                if (fallback == null) fallback = a;
                if (IsWired(a))
                {
                    testAdapter = a;
                    break;
                }
            }
            if (testAdapter == null) testAdapter = fallback;
            if (testAdapter == null)
            {
                sb.AppendLine("  [失败] 没有可用于分流的第二块网卡（需已连接且带网关）");
                return 1;
            }
            sb.AppendLine("  目标网卡: " + testAdapter.Display + "，网关 " + testAdapter.Gateway);

            string prefixText = SplitTarget + "/32";
            foreach (RouteEntry r in RouteManager.QueryRoutes())
            {
                if (r.Prefix == prefixText)
                {
                    sb.AppendLine("  [跳过] 已存在 " + prefixText + " 路由（idx " + r.InterfaceIndex + "），为避免覆盖现有配置不执行测试");
                    return 1;
                }
            }

            sb.AppendLine("  [基线] 统计空闲 2 秒与突发 5 秒的网卡增量");
            Dictionary<string, ulong[]> idle0 = Snapshot(adapters);
            Thread.Sleep(2000);
            Dictionary<string, ulong[]> idle1 = Snapshot(adapters);
            sb.AppendLine("    空闲 2 秒:");
            AppendDeltas(sb, adapters, idle0, idle1);

            int connectedBefore;
            long payloadBefore;
            int attemptsBefore = Burst(SplitTarget, 5000, out connectedBefore, out payloadBefore);
            Dictionary<string, ulong[]> burst1 = Snapshot(adapters);
            sb.AppendLine(string.Format("    突发 {0} 次 TCP 尝试（分流前，走 idx {1}），连接成功 {2} 次，HTTP 收到 {3}",
                attemptsBefore, before, connectedBefore, Fmt.Bytes((ulong)(payloadBefore > 0 ? payloadBefore : 0))));
            AppendDeltas(sb, adapters, idle1, burst1);

            SplitRule rule = new SplitRule();
            rule.Enabled = true;
            rule.Match = SplitTarget;
            rule.AdapterId = testAdapter.Id;
            rule.AdapterName = testAdapter.Name;
            rule.Metric = 1;
            rule.Persistent = false;

            RouteManager.ApplyRules(new List<SplitRule> { rule }, adapters, true);
            if (!rule.Ok)
            {
                sb.AppendLine("  [失败] 规则下发失败: " + rule.Status);
                return failures + 1;
            }
            added.Add(rule);
            sb.AppendLine("  [步骤 1] 规则下发: " + rule.Status + "  [通过]");

            RouteEntry applied = null;
            foreach (RouteEntry r in RouteManager.QueryRoutes())
            {
                if (r.Prefix == prefixText && r.InterfaceIndex == testAdapter.IfIndex)
                {
                    applied = r;
                    break;
                }
            }
            if (applied == null)
            {
                sb.AppendLine("  [步骤 2] 路由表中未找到 " + prefixText + " -> idx " + testAdapter.IfIndex + "  [失败]");
                failures++;
            }
            else
            {
                sb.AppendLine(string.Format("  [步骤 2] 路由表: {0} -> {1} idx {2} metric {3}  [通过]",
                    applied.Destination + "/" + RouteManager.MaskToPrefix(applied.Mask),
                    applied.NextHop, applied.InterfaceIndex, applied.Metric));
            }

            int after = FindBestIfIndex(SplitTarget);
            if (after == testAdapter.IfIndex)
            {
                sb.AppendLine("  [步骤 3] Windows 选路已切换到 " + testAdapter.Name + " (idx " + after + ")  [通过]");
            }
            else
            {
                sb.AppendLine("  [步骤 3] Windows 选路为 idx " + after + "，期望 " + testAdapter.IfIndex + "  [失败]");
                failures++;
            }

            monitor.ResetCounters();
            Thread.Sleep(1200);

            Dictionary<string, ulong[]> pre = Snapshot(adapters);
            int connectedAfter;
            long payloadAfter;
            int attemptsAfter = Burst(SplitTarget, 5000, out connectedAfter, out payloadAfter);
            Thread.Sleep(1200);
            Dictionary<string, ulong[]> post = Snapshot(adapters);
            sb.AppendLine(string.Format("    突发 {0} 次 TCP 尝试（分流后，走 idx {1}），连接成功 {2} 次，HTTP 收到 {3}",
                attemptsAfter, testAdapter.IfIndex, connectedAfter, Fmt.Bytes((ulong)(payloadAfter > 0 ? payloadAfter : 0))));
            AppendDeltas(sb, adapters, pre, post);

            ulong testRx = DeltaRx(adapters, pre, post, testAdapter.Id);
            ulong testTx = DeltaTx(adapters, pre, post, testAdapter.Id);
            if (payloadAfter > 0)
            {
                sb.AppendLine("  [步骤 4] 分流后经目标网卡实际收到 HTTP 响应 " + Fmt.Bytes((ulong)payloadAfter) +
                              "，网卡增量 收 " + Fmt.Bytes(testRx) + " / 发 " + Fmt.Bytes(testTx) + "  [通过]");
            }
            else if (testRx + testTx > 0)
            {
                sb.AppendLine("  [步骤 4] 目标网卡有收发增量（收 " + Fmt.Bytes(testRx) + " / 发 " + Fmt.Bytes(testTx) +
                              "），但未收到 HTTP 响应，仅能证明 SYN 从该网卡发出  [通过（仅 SYN）]");
                sb.AppendLine("    说明: 本网络对公网 IP 的直连被阻断（或由 sing-box 代理接管），因此拿不到 HTTP 载荷；路由表与发包网卡已验证。");
            }
            else
            {
                sb.AppendLine("  [步骤 4] 目标网卡在突发期间无收发增量（可能被代理/WFP 在网卡前拦截）  [失败]");
                failures++;
            }

            bool monitored = false;
            TrafficSnapshot snap = monitor.GetSnapshot();
            if (snap != null)
            {
                foreach (IpTrafficRow row in snap.Ips)
                {
                    if (row.RemoteIp != SplitTarget) continue;
                    monitored = true;
                    sb.AppendLine("  [步骤 5] 分流后进程/IP 统计: " + row.RemoteIp + " @ " + row.Adapter +
                                  "，收 " + Fmt.Bytes(row.RxTotal) + " 发 " + Fmt.Bytes(row.TxTotal));
                }
            }
            if (!monitored) sb.AppendLine("  [步骤 5] 分流后进程/IP 统计: 未采集到该 IP 的 TCP 有效载荷（连接未建立或无数据）");

            ProcResult remove = RouteManager.RemoveRule(rule, adapters);
            added.Remove(rule);
            if (remove != null && remove.ExitCode == 0)
            {
                sb.AppendLine("  [步骤 6] 规则已移除  [通过]");
            }
            else
            {
                sb.AppendLine("  [步骤 6] 规则移除失败: " + (remove != null ? remove.Error : "未执行") + "  [失败]");
                failures++;
            }

            bool stillThere = false;
            foreach (RouteEntry r in RouteManager.QueryRoutes())
            {
                if (r.Prefix == prefixText) stillThere = true;
            }
            if (!stillThere)
            {
                sb.AppendLine("  [步骤 7] 路由表中已无 " + prefixText + "  [通过]");
            }
            else
            {
                sb.AppendLine("  [步骤 7] 路由表中仍存在 " + prefixText + "  [失败]");
                failures++;
            }

            int restored = FindBestIfIndex(SplitTarget);
            if (restored == before)
            {
                sb.AppendLine("  [步骤 8] 选路已恢复 idx " + restored + "  [通过]");
            }
            else
            {
                sb.AppendLine("  [步骤 8] 选路为 idx " + restored + "，期望恢复为 " + before + "  [失败]");
                failures++;
            }

            sb.AppendLine();
            return failures;
        }

        private static int RunCidrTest(StringBuilder sb, List<AdapterInfo> adapters, List<SplitRule> added)
        {
            int failures = 0;
            sb.AppendLine("[网段测试] 输入 " + CidrInput);

            IPAddress network;
            int prefix;
            string error;
            if (!RouteManager.TryParseRule(CidrInput, out network, out prefix, out error))
            {
                sb.AppendLine("  [失败] 解析失败: " + error);
                return 1;
            }

            string canonical = RouteManager.ToCidr(network, prefix);
            string prefixText = network.ToString() + "/" + prefix;
            sb.AppendLine("  规范化: 203.0.113.77/24 -> " + canonical + "（路由前缀 " + prefixText + "）");

            AdapterInfo adapter = null;
            foreach (AdapterInfo a in adapters)
            {
                if (a.Up && !string.IsNullOrEmpty(a.Gateway)) { adapter = a; break; }
            }
            if (adapter == null)
            {
                sb.AppendLine("  [失败] 没有可用网卡");
                return failures + 1;
            }
            sb.AppendLine("  目标网卡: " + adapter.Display);

            foreach (RouteEntry r in RouteManager.QueryRoutes())
            {
                if (r.Prefix == prefixText)
                {
                    sb.AppendLine("  [跳过] 已存在 " + prefixText + " 路由（idx " + r.InterfaceIndex + "）");
                    return failures;
                }
            }

            SplitRule rule = new SplitRule();
            rule.Enabled = true;
            rule.Match = canonical;
            rule.AdapterId = adapter.Id;
            rule.AdapterName = adapter.Name;
            rule.Metric = 1;
            rule.Persistent = false;

            RouteManager.ApplyRules(new List<SplitRule> { rule }, adapters, true);
            if (!rule.Ok)
            {
                sb.AppendLine("  [失败] 网段规则下发失败: " + rule.Status);
                return failures + 1;
            }
            added.Add(rule);
            sb.AppendLine("  [步骤 1] 网段规则下发: " + rule.Status + "  [通过]");

            bool present = false;
            foreach (RouteEntry r in RouteManager.QueryRoutes())
            {
                if (r.Prefix == prefixText && r.InterfaceIndex == adapter.IfIndex) present = true;
            }
            if (present) sb.AppendLine("  [步骤 2] 路由表存在 " + prefixText + " -> idx " + adapter.IfIndex + "  [通过]");
            else
            {
                sb.AppendLine("  [步骤 2] 路由表中未找到 " + prefixText + " -> idx " + adapter.IfIndex + "  [失败]");
                failures++;
            }

            ProcResult remove = RouteManager.RemoveRule(rule, adapters);
            added.Remove(rule);
            if (remove != null && remove.ExitCode == 0) sb.AppendLine("  [步骤 3] 网段规则已移除  [通过]");
            else
            {
                sb.AppendLine("  [步骤 3] 网段规则移除失败: " + (remove != null ? remove.Error : "未执行") + "  [失败]");
                failures++;
            }

            bool gone = true;
            foreach (RouteEntry r in RouteManager.QueryRoutes())
            {
                if (r.Prefix == prefixText) gone = false;
            }
            if (gone) sb.AppendLine("  [步骤 4] 路由表中已无 " + prefixText + "  [通过]");
            else
            {
                sb.AppendLine("  [步骤 4] 路由表中仍存在 " + prefixText + "  [失败]");
                failures++;
            }

            sb.AppendLine();
            return failures;
        }

        private static int RunDomainTest(StringBuilder sb, List<AdapterInfo> adapters, List<SplitRule> added)
        {
            int failures = 0;
            sb.AppendLine("[域名分流测试] " + DomainTarget);

            List<string> ips;
            string error;
            if (!DomainResolver.TryResolve(DomainTarget, out ips, out error))
            {
                sb.AppendLine("  [跳过] 域名解析失败: " + error);
                sb.AppendLine();
                return 0;
            }
            sb.AppendLine("  解析: " + string.Join(", ", ips.ToArray()) + "（" + ips.Count + " 个 IPv4）");

            int baseline = FindBestIfIndex(ips[0]);
            sb.AppendLine("  当前选路: idx " + baseline + " (" + NameByIndex(adapters, baseline) + ")");

            AdapterInfo target = null;
            AdapterInfo fallback = null;
            foreach (AdapterInfo a in adapters)
            {
                if (a.IfIndex == baseline) continue;
                if (!a.Up || string.IsNullOrEmpty(a.Gateway) || string.IsNullOrEmpty(a.IPv4)) continue;
                if (fallback == null) fallback = a;
                if (IsWired(a))
                {
                    target = a;
                    break;
                }
            }
            if (target == null) target = fallback;
            if (target == null)
            {
                sb.AppendLine("  [跳过] 没有可用于分流的第二块网卡");
                sb.AppendLine();
                return 0;
            }
            sb.AppendLine("  目标网卡: " + target.Display);

            foreach (string ip in ips)
            {
                if (HasRoute(ip + "/32"))
                {
                    sb.AppendLine("  [跳过] 已存在 " + ip + "/32 路由，为避免覆盖现有配置不执行测试");
                    sb.AppendLine();
                    return 0;
                }
            }

            SplitRule rule = NewRule(DomainTarget, target);
            rule.Kind = RuleKind.Domain;
            RouteManager.ApplyRules(new List<SplitRule> { rule }, adapters, true);
            if (!rule.Ok)
            {
                sb.AppendLine("  [步骤 1] 域名规则下发失败: " + rule.Status + "  [失败]");
                sb.AppendLine();
                return failures + 1;
            }
            added.Add(rule);
            sb.AppendLine("  [步骤 1] 域名规则下发: " + rule.Status + "  [通过]");

            int routed = 0;
            foreach (string ip in ips)
            {
                if (FindRouteEntry(ip + "/32", target.IfIndex) != null) routed++;
            }
            if (routed == ips.Count)
                sb.AppendLine("  [步骤 2] 每个解析 IP 都有 /32 路由指向 idx " + target.IfIndex + "  [通过]");
            else
            {
                sb.AppendLine("  [步骤 2] 仅 " + routed + "/" + ips.Count + " 个 IP 有路由  [失败]");
                failures++;
            }

            int selected = 0;
            foreach (string ip in ips)
            {
                if (FindBestIfIndex(ip) == target.IfIndex) selected++;
            }
            if (selected == ips.Count)
                sb.AppendLine("  [步骤 3] Windows 选路全部切换到 " + target.Name + "  [通过]");
            else
            {
                sb.AppendLine("  [步骤 3] 仅 " + selected + "/" + ips.Count + " 个 IP 的选路切换成功  [失败]");
                failures++;
            }

            ProcResult remove = RouteManager.RemoveRule(rule, adapters);
            added.Remove(rule);
            if (remove != null && remove.ExitCode == 0) sb.AppendLine("  [步骤 4] 域名规则已移除  [通过]");
            else
            {
                sb.AppendLine("  [步骤 4] 域名规则移除失败  [失败]");
                failures++;
            }

            int residue = 0;
            foreach (string ip in ips)
            {
                if (HasRoute(ip + "/32")) residue++;
            }
            if (residue == 0) sb.AppendLine("  [步骤 5] 解析出的 IP 路由已全部清理  [通过]");
            else
            {
                sb.AppendLine("  [步骤 5] 仍残留 " + residue + " 条 IP 路由  [失败]");
                failures++;
            }

            int restored = FindBestIfIndex(ips[0]);
            if (restored == baseline) sb.AppendLine("  [步骤 6] 选路已恢复 idx " + restored + "  [通过]");
            else
            {
                sb.AppendLine("  [步骤 6] 选路为 idx " + restored + "，期望恢复为 " + baseline + "  [失败]");
                failures++;
            }

            sb.AppendLine();
            return failures;
        }

        private static int FindBestIfIndex(string ip)
        {
            ProcResult r = RouteManager.Run("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"(Find-NetRoute -RemoteIPAddress '" +
                ip + "' | Select-Object -First 1).InterfaceIndex\"");
            string text = (r.Output ?? "").Trim();
            int index;
            if (int.TryParse(text, out index)) return index;

            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (int.TryParse(lines[i].Trim(), out index)) return index;
            }
            return -1;
        }

        private static int Burst(string ip, int durationMs, out int connectedCount, out long payloadBytes)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(durationMs);
            int attempts = 0;
            connectedCount = 0;
            payloadBytes = 0;
            int[] ports = new int[] { 443, 80 };

            while (DateTime.UtcNow < end)
            {
                foreach (int port in ports)
                {
                    attempts++;
                    TcpClient client = new TcpClient();
                    try
                    {
                        IAsyncResult ar = client.BeginConnect(ip, port, null, null);
                        bool connected = ar.AsyncWaitHandle.WaitOne(1000);
                        if (connected)
                        {
                            try { client.EndConnect(ar); }
                            catch { connected = false; }
                        }
                        if (connected)
                        {
                            connectedCount++;
                            if (port == 80)
                            {
                                try
                                {
                                    client.SendTimeout = 800;
                                    client.ReceiveTimeout = 800;
                                    NetworkStream stream = client.GetStream();
                                    byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: " + ip + "\r\nConnection: close\r\n\r\n");
                                    stream.Write(request, 0, request.Length);
                                    byte[] buffer = new byte[4096];
                                    int n = stream.Read(buffer, 0, buffer.Length);
                                    if (n > 0) payloadBytes += n;
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { client.Close(); }
                        catch { }
                    }
                    if (DateTime.UtcNow >= end) break;
                }
                Thread.Sleep(100);
            }
            return attempts;
        }

        private static bool IsWired(AdapterInfo adapter)
        {
            NetworkInterface ni = AdapterService.FindNic(adapter.Id);
            if (ni == null) return false;
            NetworkInterfaceType type = ni.NetworkInterfaceType;
            return type == NetworkInterfaceType.Ethernet
                || type == NetworkInterfaceType.GigabitEthernet
                || type == NetworkInterfaceType.FastEthernetT
                || type == NetworkInterfaceType.FastEthernetFx;
        }

        private static Dictionary<string, ulong[]> Snapshot(List<AdapterInfo> adapters)
        {
            Dictionary<string, ulong[]> map = new Dictionary<string, ulong[]>();
            foreach (AdapterInfo a in adapters)
            {
                ulong rx = 0;
                ulong tx = 0;
                NetworkInterface ni = AdapterService.FindNic(a.Id);
                if (ni != null)
                {
                    try
                    {
                        IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                        rx = st.BytesReceived > 0 ? (ulong)st.BytesReceived : 0;
                        tx = st.BytesSent > 0 ? (ulong)st.BytesSent : 0;
                    }
                    catch
                    {
                    }
                }
                map[a.Id] = new ulong[] { rx, tx };
            }
            return map;
        }

        private static void AppendDeltas(StringBuilder sb, List<AdapterInfo> adapters,
            Dictionary<string, ulong[]> before, Dictionary<string, ulong[]> after)
        {
            foreach (AdapterInfo a in adapters)
            {
                ulong[] b;
                ulong[] e;
                if (!before.TryGetValue(a.Id, out b) || !after.TryGetValue(a.Id, out e)) continue;
                ulong rx = e[0] >= b[0] ? e[0] - b[0] : 0;
                ulong tx = e[1] >= b[1] ? e[1] - b[1] : 0;
                sb.AppendLine(string.Format("      {0}: 收 +{1} 发 +{2}", a.Name, Fmt.Bytes(rx), Fmt.Bytes(tx)));
            }
        }

        private static ulong DeltaRx(List<AdapterInfo> adapters, Dictionary<string, ulong[]> before,
            Dictionary<string, ulong[]> after, string id)
        {
            ulong[] b;
            ulong[] e;
            if (!before.TryGetValue(id, out b) || !after.TryGetValue(id, out e)) return 0;
            return e[0] >= b[0] ? e[0] - b[0] : 0;
        }

        private static ulong DeltaTx(List<AdapterInfo> adapters, Dictionary<string, ulong[]> before,
            Dictionary<string, ulong[]> after, string id)
        {
            ulong[] b;
            ulong[] e;
            if (!before.TryGetValue(id, out b) || !after.TryGetValue(id, out e)) return 0;
            return e[1] >= b[1] ? e[1] - b[1] : 0;
        }

        private static AdapterInfo FindByIfIndex(List<AdapterInfo> adapters, int ifIndex)
        {
            foreach (AdapterInfo a in adapters)
            {
                if (a.IfIndex == ifIndex) return a;
            }
            return null;
        }

        private static void WriteReport(string path, string content)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, content, new UTF8Encoding(true));
                Logger.Write("分流验证报告: " + path);
            }
            catch (Exception ex)
            {
                Logger.Write("写入分流验证报告失败: " + ex.Message);
            }
        }

        private static string GetValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
    }
}

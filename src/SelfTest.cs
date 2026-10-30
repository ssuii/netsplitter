using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace NetSplitter
{
    internal static class SelfTest
    {
        public static int Run(string outPath)
        {
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(Path.GetTempPath(), "NetSplitter-selftest.txt");
            outPath = Path.GetFullPath(outPath);
            StringBuilder sb = new StringBuilder();
            int failures = 0;

            try
            {
                sb.AppendLine("NetSplitter 自检报告");
                sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("系统: " + Environment.OSVersion.VersionString + "  " + (Environment.Is64BitProcess ? "x64" : "x86"));
                sb.AppendLine("管理员: " + (RouteManager.IsAdmin ? "是" : "否（路由下发与进程统计不可用）"));
                sb.AppendLine("TCP 结构体: " + NativeMethods.DescribeStructSizes());
                sb.AppendLine();

                sb.AppendLine("[规则解析]");
                failures += Check(sb, "8.8.8.8 -> 8.8.8.8/32", ParseIs("8.8.8.8", "8.8.8.8", 32));
                failures += Check(sb, "1.1.1.0/24 -> 1.1.1.0/24", ParseIs("1.1.1.0/24", "1.1.1.0", 24));
                failures += Check(sb, "10.1.2.3/16 归一到 10.1.0.0/16", ParseIs("10.1.2.3/16", "10.1.0.0", 16));
                failures += Check(sb, "非法地址 300.1.1.1 被拒绝", !ParseOk("300.1.1.1"));
                failures += Check(sb, "IPv6 被拒绝", !ParseOk("::1"));
                failures += Check(sb, "前缀 33 被拒绝", !ParseOk("1.1.1.1/33"));
                failures += Check(sb, "掩码互转 /24", RouteManager.PrefixToMask(24) == "255.255.255.0" && RouteManager.MaskToPrefix("255.255.255.0") == 24);
                failures += Check(sb, "掩码互转 /0", RouteManager.PrefixToMask(0) == "0.0.0.0" && RouteManager.MaskToPrefix("0.0.0.0") == 0);
                sb.AppendLine();

                sb.AppendLine("[域名规则]");
                string domainError;
                failures += Check(sb, "www.example.com 有效", DomainResolver.Normalize("www.example.com", out domainError) == "www.example.com");
                failures += Check(sb, "Example.COM. 归一到 example.com", DomainResolver.Normalize("Example.COM.", out domainError) == "example.com");
                failures += Check(sb, "300.1.1.1 不会被当成域名", DomainResolver.Normalize("300.1.1.1", out domainError) == null);
                failures += Check(sb, "通配符被拒绝", DomainResolver.Normalize("*.example.com", out domainError) == null);
                failures += Check(sb, "带路径被拒绝", DomainResolver.Normalize("http://example.com/a", out domainError) == null);
                failures += Check(sb, "单段被拒绝", DomainResolver.Normalize("localhost", out domainError) == null);
                List<string> domainIps;
                string resolveError;
                bool domainResolved = DomainResolver.TryResolve("www.baidu.com", out domainIps, out resolveError);
                sb.AppendLine("  解析 www.baidu.com -> " + (domainResolved ? string.Join(", ", domainIps.ToArray()) : "失败: " + resolveError));
                failures += Check(sb, "域名解析返回 IPv4", domainResolved);
                sb.AppendLine();

                sb.AppendLine("[规则存储]");
                string tempRules = Path.Combine(Path.GetTempPath(), "NetSplitter-rules-test.tsv");
                try
                {
                    RuleStore store = new RuleStore(tempRules);
                    List<SplitRule> savedRules = new List<SplitRule>();
                    SplitRule domainRule = new SplitRule();
                    domainRule.Kind = RuleKind.Domain;
                    domainRule.Match = "www.example.com";
                    domainRule.AdapterId = "id-1";
                    domainRule.AdapterName = "WLAN";
                    domainRule.LastIps = "93.184.216.34,93.184.216.35";
                    savedRules.Add(domainRule);
                    SplitRule ipRule = new SplitRule();
                    ipRule.Match = "8.8.8.8";
                    ipRule.AdapterId = "id-2";
                    ipRule.AdapterName = "Ethernet";
                    ipRule.Persistent = false;
                    savedRules.Add(ipRule);

                    store.Save(savedRules);
                    List<SplitRule> loaded = store.Load();
                    bool ok = loaded.Count == 2
                        && loaded[0].IsDomain && loaded[0].LastIps == "93.184.216.34,93.184.216.35"
                        && loaded[0].Persistent
                        && !loaded[1].IsDomain && !loaded[1].Persistent;
                    failures += Check(sb, "域名/IP 规则保存后读回一致", ok);
                }
                finally
                {
                    try { if (File.Exists(tempRules)) File.Delete(tempRules); }
                    catch
                    {
                    }
                }

                string tempBind = Path.Combine(Path.GetTempPath(), "NetSplitter-bind-test-" + Guid.NewGuid().ToString("N") + ".tsv");
                try
                {
                    ProcBindingStore bindStore = new ProcBindingStore(tempBind);
                    List<ProcBinding> savedBinds = new List<ProcBinding>();
                    ProcBinding b = new ProcBinding();
                    b.Process = "chrome";
                    b.AdapterId = "{wifi-id}";
                    b.AdapterName = "WLAN";
                    b.Metric = 3;
                    savedBinds.Add(b);
                    bindStore.Save(savedBinds);
                    List<ProcBinding> loadedBinds = bindStore.Load();
                    bool bindOk = loadedBinds.Count == 1 && loadedBinds[0].Process == "chrome"
                        && loadedBinds[0].AdapterId == "{wifi-id}" && loadedBinds[0].Metric == 3;
                    failures += Check(sb, "进程自动跟踪绑定保存后读回一致", bindOk);
                }
                finally
                {
                    try { if (File.Exists(tempBind)) File.Delete(tempBind); }
                    catch
                    {
                    }
                }

                sb.AppendLine();
                sb.AppendLine("[配置导入导出]");
                string tempConfig = Path.Combine(Path.GetTempPath(), "NetSplitter-config-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    List<AdapterInfo> fakeAdapters = new List<AdapterInfo>();
                    AdapterInfo fake = new AdapterInfo();
                    fake.Id = "{machine-specific-id}";
                    fake.Name = "WLAN";
                    fake.IsWifi = true;
                    fake.Up = true;
                    fake.IPv4 = "192.168.1.10";
                    fakeAdapters.Add(fake);

                    List<SplitRule> cfgRules = new List<SplitRule>();
                    SplitRule cfgDomain = new SplitRule();
                    cfgDomain.Kind = RuleKind.Domain;
                    cfgDomain.Match = "example.com";
                    cfgDomain.AdapterId = fake.Id;
                    cfgDomain.AdapterName = fake.Name;
                    cfgDomain.LastIps = "93.184.216.34";
                    cfgRules.Add(cfgDomain);
                    List<ProcBinding> cfgBinds = new List<ProcBinding>();
                    ProcBinding cfgBind = new ProcBinding();
                    cfgBind.Process = "chrome.exe";
                    cfgBind.AdapterId = fake.Id;
                    cfgBind.AdapterName = fake.Name;
                    cfgBinds.Add(cfgBind);

                    ConfigIO.Export(tempConfig, cfgRules, cfgBinds, fakeAdapters);
                    string cfgJson = File.ReadAllText(tempConfig);
                    ConfigBundle cfg = ConfigIO.Load(tempConfig);
                    bool cfgOk = cfg.Rules.Count == 1 && cfg.Rules[0].Kind == "domain" &&
                        cfg.Rules[0].Match == "example.com" && cfg.Rules[0].LastIps == "93.184.216.34" &&
                        cfg.Processes.Count == 1 && cfg.Processes[0].Process == "chrome.exe" &&
                        !cfgJson.Contains(fake.Id);
                    failures += Check(sb, "规则与进程导出/读回一致且不含本机网卡 ID", cfgOk);
                    AdapterInfo fallback = ConfigIO.ResolveAdapter(fakeAdapters, "另一台机器的网卡", "wifi");
                    failures += Check(sb, "跨机器导入按网卡类型回退匹配", fallback != null && fallback.Id == fake.Id);
                }
                finally
                {
                    try { if (File.Exists(tempConfig)) File.Delete(tempConfig); }
                    catch
                    {
                    }
                }
                sb.AppendLine();

                sb.AppendLine("[开机启动]");
                // 只用临时名称测试，绝不动用户真实的启动项/计划任务
                string startupTag = Guid.NewGuid().ToString("N");
                string tempValue = "NetSplitter-selftest-" + startupTag;
                string tempTask = "NetSplitter-selftest-" + startupTag;
                string startupError;
                bool tempOn = StartupManager.TrySetCore(true, tempValue, tempTask,
                        System.Windows.Forms.Application.ExecutablePath, false, out startupError) &&
                              StartupManager.HasRunEntryFor(tempValue);
                bool tempOff = StartupManager.TrySetCore(false, tempValue, tempTask,
                        System.Windows.Forms.Application.ExecutablePath, false, out startupError) &&
                               !StartupManager.HasRunEntryFor(tempValue);
                failures += Check(sb, "注册表启动项（临时项）可开可关", tempOn && tempOff);
                if (!tempOn || !tempOff) sb.AppendLine("  错误信息: " + startupError);
                failures += Check(sb, "不存在的计划任务查询返回不存在",
                    !StartupManager.TaskExists("NetSplitter-selftest-missing-" + startupTag));
                string taskArgs = StartupManager.BuildCreateTaskArgs("NetSplitter",
                    System.Windows.Forms.Application.ExecutablePath);
                sb.AppendLine("  计划任务: schtasks " + taskArgs);
                failures += Check(sb, "计划任务参数含登录触发、最高权限与提权参数",
                    taskArgs.Contains("/SC ONLOGON") && taskArgs.Contains("/RL HIGHEST") &&
                    taskArgs.Contains("--elevated-child") &&
                    taskArgs.Contains(System.Windows.Forms.Application.ExecutablePath));
                sb.AppendLine("  当前开机启动: " + (StartupManager.IsTaskEnabled()
                    ? "计划任务（登录后直接以管理员身份运行）"
                    : (StartupManager.HasRunEntry() ? "注册表启动项（普通权限）" : "未开启")));
                sb.AppendLine();

                List<AdapterInfo> adapters = AdapterService.GetAdapters();
                sb.AppendLine("[网卡] 共 " + adapters.Count + " 个");
                foreach (AdapterInfo a in adapters)
                {
                    sb.AppendLine(string.Format("  idx {0,-3} {1} | {2} | {3}/{4} | 网关 {5} | {6} | {7}",
                        a.IfIndex, a.Name, a.Description, a.IPv4, a.PrefixLength,
                        string.IsNullOrEmpty(a.Gateway) ? "-" : a.Gateway, a.StatusText, Fmt.LinkSpeed(a.Speed)));
                }
                failures += Check(sb, "检测到至少一个网卡", adapters.Count > 0);
                sb.AppendLine();

                sb.AppendLine("[Wi-Fi]");
                WifiStatus wifi = WifiService.ReadStatus();
                sb.AppendLine("  无线服务: " + (wifi.Available ? "可用" : "不可用（" + wifi.Error + "）"));
                if (wifi.Available)
                {
                    sb.AppendLine("  当前连接: " + (wifi.Connected ? wifi.Ssid + "（信号 " + wifi.Signal + "%）" : "未连接"));
                }
                bool hasWifiCard = false;
                foreach (AdapterInfo a in adapters)
                {
                    if (!a.IsWifi) continue;
                    hasWifiCard = true;
                    sb.AppendLine("  " + a.Name + " 卡片文案: " + AdapterCard.WifiText(a));
                }
                if (wifi.Available && hasWifiCard)
                {
                    bool cardOk = false;
                    foreach (AdapterInfo a in adapters)
                    {
                        if (!a.IsWifi) continue;
                        if (wifi.Connected && a.WifiSsid == wifi.Ssid && AdapterCard.WifiText(a).Contains(wifi.Ssid)) cardOk = true;
                        if (!wifi.Connected && AdapterCard.WifiText(a).Contains("未连接")) cardOk = true;
                    }
                    failures += Check(sb, "无线网卡卡片显示 Wi-Fi 名称/连接状态", cardOk);
                }
                string tempWifi = Path.Combine(Path.GetTempPath(), "NetSplitter-wifi-selftest-" + Guid.NewGuid().ToString("N") + ".tsv");
                try
                {
                    bool saved = WifiConfig.SavePair(tempWifi, "自检网络 & A", "自检网络 B");
                    string[] back = WifiConfig.LoadPair(tempWifi);
                    failures += Check(sb, "两个切换目标保存/读取一致",
                        saved && back[0] == "自检网络 & A" && back[1] == "自检网络 B");
                }
                finally
                {
                    try { if (File.Exists(tempWifi)) File.Delete(tempWifi); }
                    catch
                    {
                    }
                }
                failures += Check(sb, "一键切换在 A/B 之间交替",
                    WifiConfig.PickTarget("A", "B", "A", true) == "B" &&
                    WifiConfig.PickTarget("A", "B", "B", true) == "A" &&
                    WifiConfig.PickTarget("A", "B", "C", true) == "A" &&
                    WifiConfig.PickTarget("A", "", "A", true) == "A" &&
                    WifiConfig.PickTarget("", "", "", false) == "");
                string openXml = WifiService.BuildProfileXml("A&B", "", 1, false);
                failures += Check(sb, "开放网络配置无密钥且已转义",
                    openXml.Contains("<authentication>open</authentication>") && !openXml.Contains("keyMaterial") &&
                    openXml.Contains("A&amp;B"));
                string wpa2Xml = WifiService.BuildProfileXml("Home", "p<ss&word", 7, true);
                failures += Check(sb, "WPA2 配置写入转义后的密码",
                    wpa2Xml.Contains("WPA2PSK") && wpa2Xml.Contains("p&lt;ss&amp;word"));
                failures += Check(sb, "WPA3 网络使用 WPA3SAE",
                    WifiService.BuildProfileXml("Home3", "password1", 9, true).Contains("WPA3SAE"));
                sb.AppendLine();

                List<RouteEntry> routes = RouteManager.QueryRoutes();
                int defaults = 0;
                List<RouteEntry> sample = new List<RouteEntry>();
                foreach (RouteEntry r in routes)
                {
                    if (r.Destination == "0.0.0.0" && r.Mask == "0.0.0.0")
                    {
                        defaults++;
                        if (sample.Count < 8) sample.Add(r);
                    }
                }
                sb.AppendLine("[路由表] 共 " + routes.Count + " 条，默认路由 " + defaults + " 条");
                foreach (RouteEntry r in sample)
                {
                    sb.AppendLine(string.Format("  0.0.0.0/0 -> {0}  idx {1}  metric {2}", r.NextHop, r.InterfaceIndex, r.Metric));
                }
                failures += Check(sb, "读到系统路由表", routes.Count > 0);
                sb.AppendLine();

                if (adapters.Count > 0)
                {
                    sb.AppendLine("[规则下发预览]");
                    SplitRule rule1 = new SplitRule();
                    rule1.Match = "8.8.8.8";
                    rule1.AdapterId = adapters[0].Id;
                    sb.AppendLine("  8.8.8.8 -> " + adapters[0].Name);
                    sb.AppendLine("    " + RouteManager.DescribePlan(rule1, adapters));
                    if (adapters.Count > 1)
                    {
                        SplitRule rule2 = new SplitRule();
                        rule2.Match = "1.1.1.0/24";
                        rule2.AdapterId = adapters[adapters.Count - 1].Id;
                        rule2.Metric = 5;
                        sb.AppendLine("  1.1.1.0/24 -> " + adapters[adapters.Count - 1].Name);
                        sb.AppendLine("    " + RouteManager.DescribePlan(rule2, adapters));
                    }

                    SplitRule domainRule = new SplitRule();
                    domainRule.Kind = RuleKind.Domain;
                    domainRule.Match = "www.baidu.com";
                    domainRule.AdapterId = adapters[0].Id;
                    sb.AppendLine("  www.baidu.com -> " + adapters[0].Name);
                    sb.AppendLine("    " + RouteManager.DescribePlan(domainRule, adapters));
                    sb.AppendLine();
                }

                sb.AppendLine("[流量监控] 采样 6 秒");
                using (TrafficMonitor monitor = new TrafficMonitor(RouteManager.IsAdmin))
                {
                    monitor.Start();
                    for (int i = 0; i < 6; i++) Thread.Sleep(1000);

                    TrafficSnapshot snap = monitor.GetSnapshot();
                    sb.AppendLine("  TCP 连接: " + snap.TcpConnections);
                    sb.AppendLine("  进程/IP 统计: " + (snap.PerProcessAvailable ? "可用" : "不可用"));
                    if (!string.IsNullOrEmpty(snap.StatsMessage)) sb.AppendLine("  提示: " + snap.StatsMessage);

                    foreach (AdapterInfo a in snap.Adapters)
                    {
                        sb.AppendLine(string.Format("  {0}: 收 {1} ({2})  发 {3} ({4})",
                            a.Name, Fmt.Bytes(a.RxTotal), Fmt.PerSec(a.RxRate), Fmt.Bytes(a.TxTotal), Fmt.PerSec(a.TxRate)));
                    }

                    sb.AppendLine("  进程行: " + snap.Processes.Count + "，远端 IP 行: " + snap.Ips.Count + "，进程-IP 行: " + snap.ProcIps.Count);
                    int shown = 0;
                    foreach (ProcTrafficRow p in snap.Processes)
                    {
                        if (shown++ >= 8) break;
                        sb.AppendLine(string.Format("    {0} @ {1}: 收 {2} 发 {3}", p.Process, p.Adapter, Fmt.PerSec(p.RxRate), Fmt.PerSec(p.TxRate)));
                    }
                    shown = 0;
                    foreach (IpTrafficRow ip in snap.Ips)
                    {
                        if (shown++ >= 8) break;
                        sb.AppendLine(string.Format("    {0} @ {1}: 收 {2} 发 {3}", ip.RemoteIp, ip.Adapter, Fmt.PerSec(ip.RxRate), Fmt.PerSec(ip.TxRate)));
                    }

                    failures += Check(sb, "快照包含网卡数据", snap.Adapters.Count > 0);
                    if (RouteManager.IsAdmin) failures += Check(sb, "管理员模式下进程统计已开启", snap.PerProcessAvailable);
                }
            }
            catch (Exception ex)
            {
                failures++;
                sb.AppendLine();
                sb.AppendLine("自检异常: " + ex);
            }

            sb.AppendLine();
            sb.AppendLine(failures == 0 ? "结果: 全部通过" : "结果: " + failures + " 项未通过");

            try
            {
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
                Logger.Write("自检完成: " + outPath + "，未通过 " + failures + " 项");
            }
            catch (Exception ex)
            {
                Logger.Write("写入自检报告失败: " + ex.Message);
            }

            return failures == 0 ? 0 : 2;
        }

        private static int Check(StringBuilder sb, string name, bool passed)
        {
            sb.AppendLine((passed ? "  [通过] " : "  [失败] ") + name);
            return passed ? 0 : 1;
        }

        private static bool ParseOk(string text)
        {
            System.Net.IPAddress network;
            int prefix;
            string error;
            return RouteManager.TryParseRule(text, out network, out prefix, out error);
        }

        private static bool ParseIs(string text, string expectedNetwork, int expectedPrefix)
        {
            System.Net.IPAddress network;
            int prefix;
            string error;
            if (!RouteManager.TryParseRule(text, out network, out prefix, out error)) return false;
            return network.ToString() == expectedNetwork && prefix == expectedPrefix;
        }
    }
}

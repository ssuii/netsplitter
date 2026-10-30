using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetSplitter
{
    internal static class NativeMethods
    {
        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int TcpConnectionEstatsData = 1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct MIB_TCPROW
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MIB_TCP6ROW
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ucLocalAddr;
            public uint dwLocalScopeId;
            public uint dwLocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ucRemoteAddr;
            public uint dwRemoteScopeId;
            public uint dwRemotePort;
            public uint dwState;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ucLocalAddr;
            public uint dwLocalScopeId;
            public uint dwLocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ucRemoteAddr;
            public uint dwRemoteScopeId;
            public uint dwRemotePort;
            public uint dwState;
            public uint dwOwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TCP_ESTATS_DATA_RW_v0
        {
            public byte EnableCollection;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TCP_ESTATS_DATA_ROD_v0
        {
            public ulong DataBytesOut;
            public ulong DataSegsOut;
            public ulong DataBytesIn;
            public ulong DataSegsIn;
            public ulong SegsOut;
            public ulong SegsIn;
            public uint SoftErrors;
            public uint SoftErrorReason;
            public uint SndUna;
            public uint SndNxt;
            public uint SndMax;
            public ulong ThruBytesAcked;
            public uint RcvNxt;
            public ulong ThruBytesReceived;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetPerTcpConnectionEStats(ref MIB_TCPROW row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize,
            IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint SetPerTcpConnectionEStats(ref MIB_TCPROW row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetPerTcp6ConnectionEStats(ref MIB_TCP6ROW row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize,
            IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint SetPerTcp6ConnectionEStats(ref MIB_TCP6ROW row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

        internal sealed class TcpEntry
        {
            public bool V6;
            public uint Pid;
            public uint State;
            public IPAddress Local;
            public ushort LocalPort;
            public IPAddress Remote;
            public ushort RemotePort;
            public MIB_TCPROW Row4;
            public MIB_TCP6ROW Row6;
        }

        internal static List<TcpEntry> GetConnections()
        {
            List<TcpEntry> list = new List<TcpEntry>();
            try { ReadV4(list); }
            catch (Exception ex) { Logger.Write("读取 TCPv4 表失败: " + ex.Message); }
            try { ReadV6(list); }
            catch (Exception ex) { Logger.Write("读取 TCPv6 表失败: " + ex.Message); }
            return list;
        }

        private static void ReadV4(List<TcpEntry> list)
        {
            int size = 0;
            uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != ERROR_INSUFFICIENT_BUFFER) return;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                rc = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (rc != 0) return;
                int count = Marshal.ReadInt32(buffer);
                int stride = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = new IntPtr(buffer.ToInt64() + 4 + (long)i * stride);
                    MIB_TCPROW_OWNER_PID r = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(p, typeof(MIB_TCPROW_OWNER_PID));
                    TcpEntry e = new TcpEntry();
                    e.V6 = false;
                    e.Pid = r.dwOwningPid;
                    e.State = r.dwState;
                    e.Local = new IPAddress((long)r.dwLocalAddr);
                    e.LocalPort = ConvertPort(r.dwLocalPort);
                    e.Remote = new IPAddress((long)r.dwRemoteAddr);
                    e.RemotePort = ConvertPort(r.dwRemotePort);
                    e.Row4.dwState = r.dwState;
                    e.Row4.dwLocalAddr = r.dwLocalAddr;
                    e.Row4.dwLocalPort = r.dwLocalPort;
                    e.Row4.dwRemoteAddr = r.dwRemoteAddr;
                    e.Row4.dwRemotePort = r.dwRemotePort;
                    list.Add(e);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void ReadV6(List<TcpEntry> list)
        {
            int size = 0;
            uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != ERROR_INSUFFICIENT_BUFFER) return;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                rc = GetExtendedTcpTable(buffer, ref size, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
                if (rc != 0) return;
                int count = Marshal.ReadInt32(buffer);
                int stride = Marshal.SizeOf(typeof(MIB_TCP6ROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = new IntPtr(buffer.ToInt64() + 4 + (long)i * stride);
                    MIB_TCP6ROW_OWNER_PID r = (MIB_TCP6ROW_OWNER_PID)Marshal.PtrToStructure(p, typeof(MIB_TCP6ROW_OWNER_PID));
                    if (r.ucLocalAddr == null || r.ucRemoteAddr == null) continue;
                    TcpEntry e = new TcpEntry();
                    e.V6 = true;
                    e.Pid = r.dwOwningPid;
                    e.State = r.dwState;
                    e.Local = new IPAddress(r.ucLocalAddr, r.dwLocalScopeId);
                    e.LocalPort = ConvertPort(r.dwLocalPort);
                    e.Remote = new IPAddress(r.ucRemoteAddr, r.dwRemoteScopeId);
                    e.RemotePort = ConvertPort(r.dwRemotePort);
                    e.Row6.ucLocalAddr = r.ucLocalAddr;
                    e.Row6.dwLocalScopeId = r.dwLocalScopeId;
                    e.Row6.dwLocalPort = r.dwLocalPort;
                    e.Row6.ucRemoteAddr = r.ucRemoteAddr;
                    e.Row6.dwRemoteScopeId = r.dwRemoteScopeId;
                    e.Row6.dwRemotePort = r.dwRemotePort;
                    e.Row6.dwState = r.dwState;
                    list.Add(e);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static ushort ConvertPort(uint port)
        {
            return (ushort)IPAddress.NetworkToHostOrder((short)(port & 0xFFFF));
        }

        internal static uint EnableV4(MIB_TCPROW row)
        {
            TCP_ESTATS_DATA_RW_v0 rw = new TCP_ESTATS_DATA_RW_v0();
            rw.EnableCollection = 1;
            IntPtr p = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.StructureToPtr(rw, p, false);
                return SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, p, 0, 1, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        internal static uint EnableV6(MIB_TCP6ROW row)
        {
            TCP_ESTATS_DATA_RW_v0 rw = new TCP_ESTATS_DATA_RW_v0();
            rw.EnableCollection = 1;
            IntPtr p = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.StructureToPtr(rw, p, false);
                return SetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, p, 0, 1, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        internal static bool TryReadV4(MIB_TCPROW row, out ulong bytesIn, out ulong bytesOut)
        {
            bytesIn = 0;
            bytesOut = 0;
            int size = Marshal.SizeOf(typeof(TCP_ESTATS_DATA_ROD_v0));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
                uint rc = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData,
                    IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, p, 0, (uint)size);
                if (rc != 0) return false;
                TCP_ESTATS_DATA_ROD_v0 rod = (TCP_ESTATS_DATA_ROD_v0)Marshal.PtrToStructure(p, typeof(TCP_ESTATS_DATA_ROD_v0));
                bytesIn = rod.DataBytesIn;
                bytesOut = rod.DataBytesOut;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        internal static bool TryReadV6(MIB_TCP6ROW row, out ulong bytesIn, out ulong bytesOut)
        {
            bytesIn = 0;
            bytesOut = 0;
            int size = Marshal.SizeOf(typeof(TCP_ESTATS_DATA_ROD_v0));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
                uint rc = GetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData,
                    IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, p, 0, (uint)size);
                if (rc != 0) return false;
                TCP_ESTATS_DATA_ROD_v0 rod = (TCP_ESTATS_DATA_ROD_v0)Marshal.PtrToStructure(p, typeof(TCP_ESTATS_DATA_ROD_v0));
                bytesIn = rod.DataBytesIn;
                bytesOut = rod.DataBytesOut;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        internal static string DescribeStructSizes()
        {
            return string.Format("MIB_TCPROW={0} MIB_TCPROW_OWNER_PID={1} MIB_TCP6ROW={2} MIB_TCP6ROW_OWNER_PID={3} ROD={4}",
                Marshal.SizeOf(typeof(MIB_TCPROW)), Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID)),
                Marshal.SizeOf(typeof(MIB_TCP6ROW)), Marshal.SizeOf(typeof(MIB_TCP6ROW_OWNER_PID)),
                Marshal.SizeOf(typeof(TCP_ESTATS_DATA_ROD_v0)));
        }
    }
}

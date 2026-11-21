using System;
using System.Runtime.InteropServices;

namespace FastExplorer.Core
{
    public static partial class Win32Interop
    {
        #region WLAN API (wlanapi.dll)

        public const int WLAN_INTF_OPCODE_CURRENT_CONNECTION = 7;

        public enum WLAN_INTERFACE_STATE
        {
            NotReady = 0,
            Connected = 1,
            AdHocNetworkFormed = 2,
            Disconnecting = 3,
            Disconnected = 4,
            Associating = 5,
            Discovering = 6,
            Authenticating = 7
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public WLAN_INTERFACE_STATE isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public uint dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public uint dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_SECURITY_ATTRIBUTES
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool bSecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bOneXEnabled;
            public uint dot11AuthAlgorithm;
            public uint dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_CONNECTION_ATTRIBUTES
        {
            public WLAN_INTERFACE_STATE isState;
            public uint wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        [DllImport("wlanapi.dll")]
        public static extern uint WlanOpenHandle(
            uint dwClientVersion,
            nint pReserved,
            out uint pdwNegotiatedVersion,
            out nint phClientHandle);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanCloseHandle(
            nint hClientHandle,
            nint pReserved);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanEnumInterfaces(
            nint hClientHandle,
            nint pReserved,
            out nint ppInterfaceList);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanQueryInterface(
            nint hClientHandle,
            ref Guid pInterfaceGuid,
            int OpCode,
            nint pReserved,
            out uint pdwDataSize,
            out nint ppData,
            out int pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")]
        public static extern void WlanFreeMemory(nint pMemory);

        #endregion

        #region IP Helper API (iphlpapi.dll)

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_IPFORWARDROW
        {
            public uint dwForwardDest;
            public uint dwForwardMask;
            public uint dwForwardPolicy;
            public uint dwForwardNextHop;
            public uint dwForwardIfIndex;
            public uint dwForwardType;
            public uint dwForwardProto;
            public uint dwForwardAge;
            public uint dwForwardNextHopAS;
            public uint dwForwardMetric1;
            public uint dwForwardMetric2;
            public uint dwForwardMetric3;
            public uint dwForwardMetric4;
            public uint dwForwardMetric5;
        }

        [DllImport("iphlpapi.dll")]
        public static extern uint GetBestRoute(
            uint dwDestAddr,
            uint dwSourceAddr,
            out MIB_IPFORWARDROW pBestRoute);

        [DllImport("iphlpapi.dll")]
        public static extern uint SendARP(
            uint DestIP,
            uint SrcIP,
            [Out] byte[] pMacAddr,
            ref uint PhyAddrLen);

        #endregion
    }
}

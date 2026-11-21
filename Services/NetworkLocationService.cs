using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public sealed class NetworkLocationService
    {
        private static readonly Lazy<NetworkLocationService> _instance = new(() => new NetworkLocationService());
        public static NetworkLocationService Instance => _instance.Value;

        public event Action? NetworkLocationChanged;

        private readonly object _debounceLock = new();
        private Timer? _debounceTimer;
        private bool _isMonitoring;

        private NetworkLocationService() { }

        public void StartMonitoring()
        {
            if (_isMonitoring) return;
            _isMonitoring = true;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        }

        private void OnNetworkChanged(object? sender, EventArgs e)
        {
            lock (_debounceLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ =>
                {
                    NetworkLocationChanged?.Invoke();
                }, null, 800, Timeout.Infinite);
            }
        }

        public static string NormalizeMac(string mac)
        {
            return mac.Trim().ToUpperInvariant().Replace(':', '-');
        }

        public sealed record WifiConnectionInfo(string Ssid, string Bssid);

        public static WifiConnectionInfo? GetWifiConnectionInfo()
        {
            nint handle = nint.Zero;
            nint ifListPtr = nint.Zero;
            try
            {
                if (Win32Interop.WlanOpenHandle(2, nint.Zero, out _, out handle) != 0 || handle == nint.Zero)
                {
                    return null;
                }

                if (Win32Interop.WlanEnumInterfaces(handle, nint.Zero, out ifListPtr) != 0 || ifListPtr == nint.Zero)
                {
                    return null;
                }

                uint count = (uint)Marshal.ReadInt32(ifListPtr, 0);
                int infoSize = Marshal.SizeOf<Win32Interop.WLAN_INTERFACE_INFO>();
                nint itemPtr = ifListPtr + 8; // dwNumberOfItems (4) + dwIndex (4)

                for (uint i = 0; i < count; i++)
                {
                    var ifInfo = Marshal.PtrToStructure<Win32Interop.WLAN_INTERFACE_INFO>(itemPtr + (int)(i * infoSize));
                    if (ifInfo.isState == Win32Interop.WLAN_INTERFACE_STATE.Connected)
                    {
                        var guid = ifInfo.InterfaceGuid;
                        if (Win32Interop.WlanQueryInterface(
                                handle,
                                ref guid,
                                Win32Interop.WLAN_INTF_OPCODE_CURRENT_CONNECTION,
                                nint.Zero,
                                out _,
                                out nint dataPtr,
                                out _) == 0 && dataPtr != nint.Zero)
                        {
                            try
                            {
                                var connAttr = Marshal.PtrToStructure<Win32Interop.WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                                var assoc = connAttr.wlanAssociationAttributes;

                                int ssidLen = (int)Math.Min(assoc.dot11Ssid.uSSIDLength, 32);
                                string ssid = Encoding.UTF8.GetString(assoc.dot11Ssid.ucSSID, 0, ssidLen);

                                byte[] b = assoc.dot11Bssid;
                                string bssid = $"{b[0]:X2}-{b[1]:X2}-{b[2]:X2}-{b[3]:X2}-{b[4]:X2}-{b[5]:X2}";

                                return new WifiConnectionInfo(ssid, bssid);
                            }
                            finally
                            {
                                Win32Interop.WlanFreeMemory(dataPtr);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetworkLocation] GetWifiConnectionInfo error: {ex.Message}");
            }
            finally
            {
                if (ifListPtr != nint.Zero) Win32Interop.WlanFreeMemory(ifListPtr);
                if (handle != nint.Zero) Win32Interop.WlanCloseHandle(handle, nint.Zero);
            }

            return null;
        }

        public static string? GetDefaultGatewayMac()
        {
            try
            {
                // 1. 最優先のデフォルトルート (0.0.0.0) の NextHop に対して SendARP を実行
                if (Win32Interop.GetBestRoute(0, 0, out var bestRoute) == 0 && bestRoute.dwForwardNextHop != 0)
                {
                    string? mac = ResolveMacViaArp(bestRoute.dwForwardNextHop);
                    if (!string.IsNullOrEmpty(mac)) return mac;
                }

                // 2. フォールバック: 有効なネットワークインターフェースの IPv4 ゲートウェイに対して SendARP
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] bytes = gw.Address.GetAddressBytes();
                        uint ip = BitConverter.ToUInt32(bytes, 0);
                        if (ip == 0) continue;

                        string? mac = ResolveMacViaArp(ip);
                        if (!string.IsNullOrEmpty(mac)) return mac;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetworkLocation] GetDefaultGatewayMac error: {ex.Message}");
            }

            return null;
        }

        private static string? ResolveMacViaArp(uint ipv4Addr)
        {
            byte[] mac = new byte[6];
            uint macLen = 6;
            if (Win32Interop.SendARP(ipv4Addr, 0, mac, ref macLen) == 0 && macLen == 6)
            {
                return $"{mac[0]:X2}-{mac[1]:X2}-{mac[2]:X2}-{mac[3]:X2}-{mac[4]:X2}-{mac[5]:X2}";
            }
            return null;
        }

        /// <summary>
        /// 現在のネットワーク接続が自宅環境かどうかを非同期で判定する (Fail-Safe: 判定不能・オフライン時は自宅外 false)
        /// </summary>
        public static async Task<bool> IsAtHomeAsync(
            IReadOnlyCollection<string> homeSsids,
            IReadOnlyCollection<string> homeMacs,
            int maxRetries = 10,
            CancellationToken cancellationToken = default)
        {
            var ssidSet = new HashSet<string>(
                homeSsids.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
                StringComparer.Ordinal);

            var macSet = new HashSet<string>(
                homeMacs.Where(m => !string.IsNullOrWhiteSpace(m)).Select(NormalizeMac),
                StringComparer.OrdinalIgnoreCase);

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Wi-Fi 接続確認
                var wifi = GetWifiConnectionInfo();
                if (wifi != null)
                {
                    string normalizedBssid = NormalizeMac(wifi.Bssid);
                    return ssidSet.Contains(wifi.Ssid) || macSet.Contains(normalizedBssid);
                }

                // 2. 有線 LAN 等（デフォルトゲートウェイの MAC 確認）
                string? gwMac = GetDefaultGatewayMac();
                if (!string.IsNullOrEmpty(gwMac))
                {
                    return macSet.Contains(NormalizeMac(gwMac));
                }

                if (attempt < maxRetries)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }

            // 3. オフライン / 未接続時は安全第一で自宅外扱い
            return false;
        }

        /// <summary>
        /// wallpaper_changer_rust の config.toml から home_ssids / home_macs を読み込む
        /// </summary>
        public static bool TryImportFromToml(string tomlPath, out List<string> ssids, out List<string> macs)
        {
            ssids = [];
            macs = [];

            try
            {
                if (!File.Exists(tomlPath)) return false;
                string content = File.ReadAllText(tomlPath, Encoding.UTF8);

                ssids = ExtractTomlStringArray(content, "home_ssids");
                macs = ExtractTomlStringArray(content, "home_macs");
                return ssids.Count > 0 || macs.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static List<string> ExtractTomlStringArray(string content, string key)
        {
            var result = new List<string>();
            var match = Regex.Match(content, $@"(?ms)^\s*{Regex.Escape(key)}\s*=\s*\[(?<body>.*?)\]");
            if (!match.Success) return result;

            string body = match.Groups["body"].Value;
            foreach (Match item in Regex.Matches(body, @"""([^""]*)""|'([^']*)'"))
            {
                string val = item.Groups[1].Success ? item.Groups[1].Value : item.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    result.Add(val.Trim());
                }
            }
            return result;
        }
    }
}

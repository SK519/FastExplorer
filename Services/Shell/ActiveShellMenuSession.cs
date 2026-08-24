using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public class ActiveShellMenuSession : IDisposable
    {
        public nint Hwnd { get; }
        public nint HMenu { get; private set; }
        public nint PContextMenu { get; private set; }
        public nint PContextMenu2 { get; private set; }
        public nint PContextMenu3 { get; private set; }
        public List<nint> PidlsToFree { get; private set; } = new List<nint>();
        public List<ExtractedShellItem> ExtractedItems { get; } = new List<ExtractedShellItem>();
        public uint? ShareCommandId { get; private set; }
        private bool _configUpdated = false;

        public ActiveShellMenuSession(nint hwnd)
        {
            Hwnd = hwnd;
        }

        public bool Build(IReadOnlyList<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0) return false;
            var validPaths = filePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (validPaths.Count == 0) return false;

            try
            {
                if (!ShellComHelper.TryGetContextMenuForPaths(Hwnd, validPaths, out var pContextMenu, out var pidlsToFree))
                {
                    return false;
                }

                PContextMenu = pContextMenu;
                PidlsToFree = pidlsToFree;

                Win32Interop.NativeCom.QueryInterface(PContextMenu, in Win32Interop.IID_IContextMenu3, out var pMenu3);
                PContextMenu3 = pMenu3;
                if (PContextMenu3 == nint.Zero)
                {
                    Win32Interop.NativeCom.QueryInterface(PContextMenu, in Win32Interop.IID_IContextMenu2, out var pMenu2);
                    PContextMenu2 = pMenu2;
                }

                HMenu = Win32Interop.CreatePopupMenu();
                if (HMenu == nint.Zero) return false;

                int hrQuery = Win32Interop.NativeCom.ContextMenu_QueryContextMenu(PContextMenu, HMenu, 0, 1, 0x7FFF, Win32Interop.CMF_NORMAL | Win32Interop.CMF_EXPLORE | Win32Interop.CMF_EXTENDEDVERBS);
                if (hrQuery < 0)
                {
                    hrQuery = Win32Interop.NativeCom.ContextMenu_QueryContextMenu(PContextMenu, HMenu, 0, 1, 0x7FFF, Win32Interop.CMF_NORMAL | Win32Interop.CMF_EXPLORE);
                }
                if (hrQuery < 0) return false;

                int itemCount = Win32Interop.GetMenuItemCount(HMenu);
                var config = ConfigService.Current.ShellMenu;

                ExtractMenuItems(HMenu, itemCount, config);
                GroupVendorClusters(config);
                FlattenSingleChildSubmenus();

                if (_configUpdated)
                {
                    ConfigService.Save();
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ActiveSession] Build Error: {ex.Message}");
                return false;
            }
        }

        private void ExtractMenuItems(nint hMenu, int itemCount, Models.ShellMenuConfig config)
        {
            for (int i = 0; i < itemCount; i++)
            {
                var sb = new System.Text.StringBuilder(256);
                Win32Interop.GetMenuString(hMenu, (uint)i, sb, sb.Capacity, Win32Interop.MF_BYPOSITION);
                string rawText = sb.ToString();
                if (string.IsNullOrWhiteSpace(rawText)) continue;

                string cleanText = rawText.Replace("&", "").Trim();
                uint cmdId = Win32Interop.GetMenuItemID(hMenu, i);

                // サブメニュー（cmdId == 0xFFFFFFFF）の場合
                if (cmdId == uint.MaxValue)
                {
                    if (ShellMenuFilter.IsBuiltinDuplicate(cleanText)) continue;

                    nint subMenu = Win32Interop.GetSubMenu(hMenu, i);
                    if (subMenu != nint.Zero)
                    {
                        // WM_INITMENUPOPUP (0x0117) を送信して遅延初期化サブメニュー (PeaZip, Google Drive等) の項目を生成
                        try
                        {
                            if (PContextMenu3 != nint.Zero)
                            {
                                Win32Interop.NativeCom.ContextMenu3_HandleMenuMsg2(PContextMenu3, 0x0117, subMenu, (nint)i, out _);
                            }
                            else if (PContextMenu2 != nint.Zero)
                            {
                                Win32Interop.NativeCom.ContextMenu2_HandleMenuMsg(PContextMenu2, 0x0117, subMenu, (nint)i);
                            }
                        }
                        catch { }

                        int subCount = Win32Interop.GetMenuItemCount(subMenu);
                        if (subCount > 0)
                        {
                            var parentItem = new ExtractedShellItem
                            {
                                CommandId = uint.MaxValue,
                                Text = rawText,
                                CleanText = cleanText,
                                Glyph = ShellMenuFilter.GetSmartGlyph(cleanText),
                                IsSubmenu = true
                            };

                            ExtractSubmenuChildren(subMenu, subCount, config, cleanText, parentItem);

                            if (parentItem.Children.Count > 0)
                            {
                                ExtractedItems.Add(parentItem);
                            }
                        }
                        else if (config.ShowThirdPartyArchiver || config.ShowAllShellItems)
                        {
                            TryAddArchiverItem(cleanText);
                        }
                    }
                    continue;
                }

                if (cleanText.Contains("共有") || cleanText.Equals("Share", StringComparison.OrdinalIgnoreCase))
                {
                    ShareCommandId = cmdId;
                }

                string? verb = (PContextMenu != nint.Zero && cmdId != uint.MaxValue) ? ShellCommandExecutor.GetVerbFromContextMenu(PContextMenu, cmdId) : null;

                if (ShellMenuFilter.IsBuiltinDuplicate(cleanText)) continue;

                // 新しく検出された OS 右クリックメニュー項目を状態辞書に自動登録
                if (!config.ItemVisibilityState.ContainsKey(cleanText))
                {
                    config.ItemVisibilityState[cleanText] = config.ShowAllShellItems;
                    _configUpdated = true;
                }

                if (ShellMenuFilter.MatchesShellConfig(cleanText, config, out string glyph, verb))
                {
                    ExtractedItems.Add(new ExtractedShellItem
                    {
                        CommandId = cmdId,
                        Text = rawText,
                        CleanText = cleanText,
                        Glyph = glyph,
                        Verb = verb
                    });
                }
            }
        }

        private readonly HashSet<string> _addedArchivers = new(StringComparer.OrdinalIgnoreCase);

        private void TryAddArchiverItem(string menuLabel)
        {
            if (_addedArchivers.Contains(menuLabel)) return;

            string? exePath = ShellMenuFilter.FindArchiverExe(menuLabel);
            if (exePath == null) return;

            _addedArchivers.Add(menuLabel);
            ExtractedItems.Add(new ExtractedShellItem
            {
                CommandId = uint.MaxValue,
                Text = menuLabel,
                CleanText = menuLabel,
                Glyph = "\uE8B7",
                DirectLaunchPath = exePath,
                DirectLaunchArgs = "{files}"
            });
        }

        private void GroupVendorClusters(Models.ShellMenuConfig config)
        {
            foreach (var rule in ShellMenuFilter.VendorRules)
            {
                if (!rule.IsClusterable) continue;

                GroupVendorItems(rule.DisplayName, rule.Glyph, item => rule.Matches(item.CleanText, item.Verb));
            }
        }

        private void GroupVendorItems(string groupName, string glyph, Func<ExtractedShellItem, bool> predicate)
        {
            var matchingItems = ExtractedItems
                .Where(item => !item.CleanText.Equals(groupName, StringComparison.OrdinalIgnoreCase) && predicate(item))
                .ToList();

            if (matchingItems.Count >= 2)
            {
                var parentItem = ExtractedItems.FirstOrDefault(i => i.IsSubmenu && i.CleanText.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                if (parentItem == null)
                {
                    parentItem = new ExtractedShellItem
                    {
                        CommandId = uint.MaxValue,
                        Text = groupName,
                        CleanText = groupName,
                        Glyph = glyph,
                        IsSubmenu = true,
                        Children = new List<ExtractedShellItem>()
                    };
                    ExtractedItems.Add(parentItem);
                }

                foreach (var item in matchingItems)
                {
                    ExtractedItems.Remove(item);

                    if (item.IsSubmenu && item.Children.Count >= 2)
                    {
                        // 2個以上の子要素を持つサブメニューは入れ子のサブメニューとして保持
                        parentItem.Children.Add(item);
                    }
                    else if (item.IsSubmenu && item.Children.Count == 1)
                    {
                        // 1個の子要素を持つサブメニューは平坦化して追加
                        var singleChild = item.Children[0];
                        string childText = singleChild.CleanText;
                        if (!childText.StartsWith(item.CleanText, StringComparison.OrdinalIgnoreCase))
                        {
                            singleChild.CleanText = $"{item.CleanText} {childText}";
                            singleChild.Text = singleChild.CleanText;
                        }
                        parentItem.Children.Add(singleChild);
                    }
                    else
                    {
                        parentItem.Children.Add(item);
                    }
                }
            }
            else if (matchingItems.Count == 1)
            {
                var item = matchingItems[0];
                if (!item.IsSubmenu)
                {
                    string text = item.CleanText;
                    if (!text.StartsWith(groupName, StringComparison.OrdinalIgnoreCase) && !text.Contains(groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        item.CleanText = $"{groupName} {text}";
                        item.Text = item.CleanText;
                    }
                }
            }
        }

        private void FlattenSingleChildSubmenus()
        {
            for (int i = ExtractedItems.Count - 1; i >= 0; i--)
            {
                var item = ExtractedItems[i];
                if (item.IsSubmenu && item.Children.Count == 1)
                {
                    var singleChild = item.Children[0];
                    string parentName = item.CleanText.Replace(" >", "").Trim();
                    string childText = singleChild.CleanText;

                    if (!childText.StartsWith(parentName, StringComparison.OrdinalIgnoreCase) &&
                        !childText.Contains(parentName, StringComparison.OrdinalIgnoreCase))
                    {
                        singleChild.CleanText = $"{parentName} {childText}";
                        singleChild.Text = singleChild.CleanText;
                    }

                    if (singleChild.Glyph == "\uE712" && item.Glyph != "\uE712")
                    {
                        singleChild.Glyph = item.Glyph;
                    }

                    singleChild.IsSubmenu = false;
                    ExtractedItems[i] = singleChild;
                }
            }
        }

        private void ExtractSubmenuChildren(nint subMenu, int subCount, Models.ShellMenuConfig config, string parentLabel, ExtractedShellItem parentItem)
        {
            for (int j = 0; j < subCount; j++)
            {
                var sb = new System.Text.StringBuilder(256);
                Win32Interop.GetMenuString(subMenu, (uint)j, sb, sb.Capacity, Win32Interop.MF_BYPOSITION);
                string childRawText = sb.ToString();
                if (string.IsNullOrWhiteSpace(childRawText)) continue;

                string childCleanText = childRawText.Replace("&", "").Trim();
                uint childCmdId = Win32Interop.GetMenuItemID(subMenu, j);
                if (childCmdId == uint.MaxValue) continue;

                string fullText = $"{parentLabel} → {childCleanText}";

                // サブ項目を自動登録
                if (!config.ItemVisibilityState.ContainsKey(fullText))
                {
                    config.ItemVisibilityState[fullText] = config.ShowAllShellItems;
                    _configUpdated = true;
                }

                string? childVerb = (PContextMenu != nint.Zero && childCmdId != uint.MaxValue) ? ShellCommandExecutor.GetVerbFromContextMenu(PContextMenu, childCmdId) : null;

                if (ShellMenuFilter.MatchesShellConfig(parentLabel, childCleanText, config, out string childGlyph, childVerb))
                {
                    parentItem.Children.Add(new ExtractedShellItem
                    {
                        CommandId = childCmdId,
                        Text = childRawText,
                        CleanText = childCleanText,
                        Glyph = childGlyph,
                        Verb = childVerb
                    });
                }
            }
        }

        public bool InvokeCommand(ExtractedShellItem item, string? workingDir = null)
        {
            if (PContextMenu == nint.Zero || item == null) return false;
            return InvokeCommand(item.CommandId, item.Verb, workingDir);
        }

        public bool InvokeCommand(uint cmdId, string? verb = null, string? workingDir = null)
        {
            if (PContextMenu == nint.Zero) return false;

            try
            {
                Win32Interop.GetCursorPos(out var pt);
                int structSize = Marshal.SizeOf<Win32Interop.CMINVOKECOMMANDINFOEX>();
                nint pici = Marshal.AllocHGlobal(structSize);
                nint pVerbAnsi = nint.Zero;
                nint pVerbUnicode = nint.Zero;

                try
                {
                    byte[] zeroBuffer = new byte[structSize];
                    Marshal.Copy(zeroBuffer, 0, pici, structSize);

                    if (!string.IsNullOrEmpty(verb))
                    {
                        pVerbAnsi = Marshal.StringToHGlobalAnsi(verb);
                        pVerbUnicode = Marshal.StringToHGlobalUni(verb);
                    }

                    var invoke = new Win32Interop.CMINVOKECOMMANDINFOEX
                    {
                        cbSize = (uint)structSize,
                        fMask = Win32Interop.CMIC_MASK_UNICODE | Win32Interop.CMIC_MASK_PTINVOKE,
                        hwnd = Hwnd,
                        lpVerb = pVerbAnsi != nint.Zero ? pVerbAnsi : (nint)(cmdId - 1),
                        lpVerbW = pVerbUnicode != nint.Zero ? pVerbUnicode : (nint)(cmdId - 1),
                        lpDirectory = workingDir,
                        lpDirectoryW = workingDir,
                        nShow = Win32Interop.SW_SHOWNORMAL,
                        ptInvoke = pt
                    };

                    Marshal.StructureToPtr(invoke, pici, false);
                    int hr = Win32Interop.NativeCom.ContextMenu_InvokeCommand(PContextMenu, pici);
                    return hr >= 0;
                }
                finally
                {
                    if (pVerbAnsi != nint.Zero) Marshal.FreeHGlobal(pVerbAnsi);
                    if (pVerbUnicode != nint.Zero) Marshal.FreeHGlobal(pVerbUnicode);
                    Marshal.FreeHGlobal(pici);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ActiveSession] Invoke Error: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (HMenu != nint.Zero)
            {
                Win32Interop.DestroyMenu(HMenu);
                HMenu = nint.Zero;
            }
            if (PContextMenu3 != nint.Zero)
            {
                Win32Interop.NativeCom.Release(PContextMenu3);
                PContextMenu3 = nint.Zero;
            }
            if (PContextMenu2 != nint.Zero)
            {
                Win32Interop.NativeCom.Release(PContextMenu2);
                PContextMenu2 = nint.Zero;
            }
            if (PContextMenu != nint.Zero)
            {
                Win32Interop.NativeCom.Release(PContextMenu);
                PContextMenu = nint.Zero;
            }
            ShellComHelper.FreePidls(PidlsToFree);
            PidlsToFree.Clear();
        }
    }
}

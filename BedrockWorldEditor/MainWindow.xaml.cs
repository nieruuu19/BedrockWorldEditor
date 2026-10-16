using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using fNbt;
using Path = System.IO.Path;

namespace BedrockWorldEditor
{
    public sealed class PlayerEntryItem
    {
        public string RawKey { get; }
        public string DisplayName { get; }

        public PlayerEntryItem(string rawKey)
        {
            RawKey = rawKey;
            DisplayName = rawKey.StartsWith("player_server_")
                ? "server_" + rawKey.Substring("player_server_".Length)
                : rawKey;
        }

        public override string ToString() => DisplayName;
    }

    public partial class MainWindow : Window
    {
        private string? _worldFolderPath;
        private string? _levelDatPath;
        private string? _dbPath;
        private NbtFile? _activeNbtFile;
        private NbtTag? _selectedTag;
        private TreeViewItem? _selectedTreeItem;
        private TreeViewItem? _dragAnchorItem;
        private ListBoxItem? _rightClickedPlayerItem;
        private bool _isDraggingSelection;
        private readonly List<TreeViewItem> _multiSelectedItems = new List<TreeViewItem>();
        private bool _suppressSelectionChange;
        private bool _suppressTreeEvents;
        private bool _rightClickHitValidNode;
        private bool _rightClickHitValidPlayer;
        private bool _hasUnsavedChanges;
        private byte[] _levelDatHeader = new byte[8];
        private readonly Dictionary<string, byte[]> _playerCache = new Dictionary<string, byte[]>();
        private string _currentSaveTarget = "";

        private NativeLevelDb? _db;

        // UME Row & Selection Brushes (Frozen for maximum UI thread performance)
        private static readonly SolidColorBrush UmeStripeWhiteBrush = CreateFrozenBrush(255, 255, 255);
        private static readonly SolidColorBrush UmeStripeGreyBrush = CreateFrozenBrush(243, 244, 246);
        private static readonly SolidColorBrush UmeSelectedLightBlueBrush = CreateFrozenBrush(203, 228, 249);
        private static readonly SolidColorBrush UmeHoverDeepBlueBrush = CreateFrozenBrush(140, 195, 242);
        private static readonly Thickness UnselectedFlagThickness = new Thickness(0);
        private static readonly Thickness SelectedFlagThickness = new Thickness(1);
        private Style? _umeTreeViewItemStyle;

        // Cross-Player / Cross-World Multi-Tag NBT Clipboard & Undo History Stack
        private static readonly List<NbtTag> _nbtClipboard = new List<NbtTag>();
        private static bool _clipboardIsRootCompound;
        private readonly Stack<NbtCompound> _undoStack = new Stack<NbtCompound>();
        private const int MaxUndoSteps = 30;

        // Offline Cache & OpenXBL API settings
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        private const string XblApiKey = "25868a1f-fb52-471d-bf14-0a1aed66fcee";
        private readonly string _cacheFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gamertag_cache.json");
        private Dictionary<string, string> _gamertagCache = new Dictionary<string, string>();

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public MainWindow()
        {
            InitializeComponent();
            _umeTreeViewItemStyle = TryFindResource("UmeTreeViewItemStyle") as Style;
            this.Closed += (_, _) => _db?.Dispose();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadGamertagCacheFromDisk();

            ComboAccounts.IsEnabled = false;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string usersPath = Path.Combine(appData, "Minecraft Bedrock", "Users");

            if (!Directory.Exists(usersPath))
            {
                TxtStatusLeft.Text = "Minecraft Bedrock Users folder not found.";
                return;
            }

            string[] accountFolders;
            try
            {
                accountFolders = Directory.GetDirectories(usersPath);
            }
            catch (Exception ex)
            {
                TxtStatusLeft.Text = $"Error accessing Users folder: {ex.Message}";
                return;
            }

            ComboAccounts.Items.Clear();
            var pendingOnlineChecks = new List<(ComboBoxItem Item, string Xuid, string FolderName)>();

            foreach (var folderPath in accountFolders)
            {
                string folderName = Path.GetFileName(folderPath);

                if (ulong.TryParse(folderName, out _) || folderName.Equals("Shared", StringComparison.OrdinalIgnoreCase))
                {
                    var (initialLabel, xuid) = GetOfflineAccountInfo(folderPath, folderName);

                    ComboBoxItem item = new ComboBoxItem
                    {
                        Content = initialLabel,
                        Tag = folderPath,
                        ToolTip = $"Folder: {folderName}\nXUID: {(string.IsNullOrEmpty(xuid) ? "None" : xuid)}"
                    };
                    ComboAccounts.Items.Add(item);

                    if (!string.IsNullOrEmpty(xuid) && ulong.TryParse(xuid, out _))
                    {
                        pendingOnlineChecks.Add((item, xuid, folderName));
                    }
                }
            }

            if (ComboAccounts.Items.Count > 0)
            {
                ComboAccounts.IsEnabled = true;
                ComboAccounts.SelectedIndex = -1;
                TxtStatusLeft.Text = $"Found {ComboAccounts.Items.Count} account(s). Select an account above to load worlds.";
            }
            else
            {
                TxtStatusLeft.Text = "No user folders found.";
                return;
            }

            if (HasInternetConnection() && pendingOnlineChecks.Count > 0)
            {
                await RefreshGamertagsOnlineAsync(pendingOnlineChecks);
            }
        }

        private void LoadGamertagCacheFromDisk()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    string json = File.ReadAllText(_cacheFilePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (loaded != null)
                    {
                        _gamertagCache = loaded;
                    }
                }
            }
            catch
            {
                _gamertagCache = new Dictionary<string, string>();
            }
        }

        private void SaveGamertagCacheToDisk()
        {
            try
            {
                string json = JsonSerializer.Serialize(_gamertagCache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_cacheFilePath, json);
            }
            catch
            {
                // Ignore disk write errors
            }
        }

        private static bool HasInternetConnection()
        {
            try
            {
                return NetworkInterface.GetIsNetworkAvailable();
            }
            catch
            {
                return false;
            }
        }

        private static string ShortFolderId(string folderName)
        {
            if (folderName.Equals("Shared", StringComparison.OrdinalIgnoreCase))
                return "Shared";
            return folderName.Length > 6 ? folderName.Substring(0, 6) + ".." : folderName;
        }

        private (string DisplayLabel, string Xuid) GetOfflineAccountInfo(string accountFolderPath, string folderName)
        {
            string optionsPath = Path.Combine(accountFolderPath, "games", "com.mojang", "minecraftpe", "options.txt");
            string foundXuid = "";
            string localGamertag = "";

            if (File.Exists(optionsPath))
            {
                try
                {
                    using var fs = new FileStream(optionsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("mp_last_username:") || line.StartsWith("mp_username:"))
                        {
                            int idx = line.IndexOf(':');
                            if (idx >= 0)
                            {
                                string gt = line.Substring(idx + 1).Trim();
                                if (!string.IsNullOrEmpty(gt)) localGamertag = gt;
                            }
                        }
                        else if (line.StartsWith("last_xuid:"))
                        {
                            int idx = line.IndexOf(':');
                            if (idx >= 0)
                            {
                                string xuidVal = line.Substring(idx + 1).Trim();
                                if (!string.IsNullOrEmpty(xuidVal) && xuidVal != "0") foundXuid = xuidVal;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore file read errors
                }
            }

            string shortFolder = ShortFolderId(folderName);
            string cacheKey = !string.IsNullOrEmpty(foundXuid) ? foundXuid : folderName;

            if (_gamertagCache.TryGetValue(cacheKey, out string? cachedGt) && !string.IsNullOrWhiteSpace(cachedGt))
            {
                return ($"{cachedGt} [{shortFolder}]", foundXuid);
            }

            if (!string.IsNullOrWhiteSpace(localGamertag))
            {
                _gamertagCache[cacheKey] = localGamertag;
                SaveGamertagCacheToDisk();
                return ($"{localGamertag} [{shortFolder}]", foundXuid);
            }

            if (!string.IsNullOrEmpty(foundXuid))
            {
                return ($"XUID: {foundXuid} [{shortFolder}]", foundXuid);
            }

            return ($"Folder: {folderName}", "");
        }

        private async Task RefreshGamertagsOnlineAsync(List<(ComboBoxItem Item, string Xuid, string FolderName)> accounts)
        {
            bool cacheUpdated = false;

            foreach (var (item, xuid, folderName) in accounts)
            {
                if (!IsLoaded) return;

                string? onlineGamertag = null;

                // Always fetch from OpenXBL if missing in cache or if cache only holds a local options.txt name instead of an official gamertag response
                if (_gamertagCache.TryGetValue(xuid, out string? cached) && !string.IsNullOrWhiteSpace(cached))
                {
                    onlineGamertag = cached;
                }

                if (string.IsNullOrWhiteSpace(onlineGamertag))
                {
                    onlineGamertag = await FetchGamertagOnlineAsync(xuid);
                    if (!string.IsNullOrWhiteSpace(onlineGamertag))
                    {
                        _gamertagCache[xuid] = onlineGamertag;
                        cacheUpdated = true;
                    }
                }

                if (!string.IsNullOrWhiteSpace(onlineGamertag))
                {
                    string shortFolder = ShortFolderId(folderName);
                    item.Content = $"{onlineGamertag} [{shortFolder}]";
                }
            }

            if (cacheUpdated)
            {
                SaveGamertagCacheToDisk();
            }
        }

        private async Task<string?> FetchGamertagOnlineAsync(string xuid)
        {
            if (string.IsNullOrWhiteSpace(XblApiKey))
                return null;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://xbl.io/api/v2/account/{xuid}");
                request.Headers.Add("X-Authorization", XblApiKey);
                request.Headers.Add("Accept", "application/json");

                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    JsonElement targetRoot = root.TryGetProperty("content", out JsonElement contentEl) ? contentEl : root;

                    if (targetRoot.TryGetProperty("profileUsers", out JsonElement users) &&
                        users.ValueKind == JsonValueKind.Array &&
                        users.GetArrayLength() > 0)
                    {
                        JsonElement firstUser = users[0];
                        if (firstUser.TryGetProperty("settings", out JsonElement settings) &&
                            settings.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement setting in settings.EnumerateArray())
                            {
                                if (setting.TryGetProperty("id", out JsonElement id) &&
                                    (id.GetString() == "Gamertag" || id.GetString() == "ModernGamertag") &&
                                    setting.TryGetProperty("value", out JsonElement val))
                                {
                                    string? gt = val.GetString();
                                    if (!string.IsNullOrWhiteSpace(gt)) return gt;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore network/parsing errors
            }

            return null;
        }

        private bool ConfirmDiscardUnsavedChanges()
        {
            if (!_hasUnsavedChanges) return true;

            var result = MessageBox.Show(
                "You have unsaved changes to the active file. Do you want to discard them?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return result == MessageBoxResult.Yes;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_hasUnsavedChanges) return;

            var result = MessageBox.Show(
                "You have unsaved changes. Do you want to exit without saving?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
        }

        // ==================== SCROLLABLE ZEBRA STRIPE ENGINE ====================

        private void WorldListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            WorldStripeTransform.Y = -(e.VerticalOffset % 44.0);
        }

        private void PlayerListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            PlayerStripeTransform.Y = -(e.VerticalOffset % 44.0);
        }

        private void NbtTreeView_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            NbtStripeTransform.Y = -(e.VerticalOffset % 44.0);
        }

        private void NbtTreeView_ItemExpandedOrCollapsed(object sender, RoutedEventArgs e)
        {
            if (_suppressTreeEvents) return;
            UpdateNbtTreeZebraStripes();
        }

        private void UpdateNbtTreeZebraStripes()
        {
            int visibleRowIndex = 0;
            foreach (var item in NbtTreeView.Items)
            {
                if (item is TreeViewItem rootItem)
                {
                    AssignZebraStripeRecursive(rootItem, ref visibleRowIndex);
                }
            }
        }

        private void AssignZebraStripeRecursive(TreeViewItem item, ref int visibleRowIndex)
        {
            SolidColorBrush stripeBrush = (visibleRowIndex % 2 == 0) ? UmeStripeWhiteBrush : UmeStripeGreyBrush;
            visibleRowIndex++;

            item.BorderBrush = stripeBrush;

            if (!_multiSelectedItems.Contains(item))
            {
                item.Background = stripeBrush;
                item.BorderThickness = UnselectedFlagThickness;
            }

            if (item.IsExpanded)
            {
                foreach (var child in item.Items)
                {
                    if (child is TreeViewItem childItem)
                    {
                        AssignZebraStripeRecursive(childItem, ref visibleRowIndex);
                    }
                }
            }
        }

        private static void MarkTreeItemUnselected(TreeViewItem item)
        {
            item.BorderThickness = UnselectedFlagThickness;
            item.Background = item.BorderBrush ?? UmeStripeWhiteBrush;
        }

        private static void MarkTreeItemSelected(TreeViewItem item, bool isDeepBlue = false)
        {
            item.BorderThickness = SelectedFlagThickness;
            item.Background = isDeepBlue ? UmeHoverDeepBlueBrush : UmeSelectedLightBlueBrush;
        }

        // ==================== MULTI-SELECTION & HIGHLIGHT MANAGEMENT ====================

        private void ClearActiveSelectionState()
        {
            foreach (var item in _multiSelectedItems)
            {
                MarkTreeItemUnselected(item);
            }
            _multiSelectedItems.Clear();

            if (_selectedTreeItem != null)
            {
                MarkTreeItemUnselected(_selectedTreeItem);
            }

            _selectedTag = null;
            _selectedTreeItem = null;
            _dragAnchorItem = null;
            _isDraggingSelection = false;
            TxtSelectedTagName.Text = "Selected Value:";
            TxtEditValue.Text = "";
            TxtEditValue.IsEnabled = false;
            BtnApply.IsEnabled = false;
        }

        private void SelectSingleTreeItem(TreeViewItem newItem)
        {
            foreach (var item in _multiSelectedItems)
            {
                if (item != newItem)
                {
                    MarkTreeItemUnselected(item);
                }
            }
            _multiSelectedItems.Clear();

            _multiSelectedItems.Add(newItem);
            _selectedTreeItem = newItem;
            _selectedTag = newItem.Tag as NbtTag;

            MarkTreeItemSelected(newItem);
            if (!newItem.IsSelected)
            {
                _suppressSelectionChange = true;
                newItem.IsSelected = true;
                _suppressSelectionChange = false;
            }

            UpdateValueEditorBar();
        }

        private void ToggleMultiSelectTreeItem(TreeViewItem clickedItem)
        {
            if (_multiSelectedItems.Contains(clickedItem))
            {
                if (_multiSelectedItems.Count > 1)
                {
                    MarkTreeItemUnselected(clickedItem);
                    if (clickedItem.IsSelected)
                    {
                        _suppressSelectionChange = true;
                        clickedItem.IsSelected = false;
                        _suppressSelectionChange = false;
                    }
                    _multiSelectedItems.Remove(clickedItem);
                    _selectedTreeItem = _multiSelectedItems[^1];
                    _selectedTag = _selectedTreeItem.Tag as NbtTag;

                    _suppressSelectionChange = true;
                    _selectedTreeItem.IsSelected = true;
                    _suppressSelectionChange = false;
                }
            }
            else
            {
                _multiSelectedItems.Add(clickedItem);
                MarkTreeItemSelected(clickedItem);
                _selectedTreeItem = clickedItem;
                _selectedTag = clickedItem.Tag as NbtTag;

                _suppressSelectionChange = true;
                clickedItem.IsSelected = true;
                _suppressSelectionChange = false;
            }

            UpdateValueEditorBar();
        }

        private void RangeSelectTreeItems(TreeViewItem anchorItem, TreeViewItem targetItem)
        {
            ItemsControl? parentA = ItemsControl.ItemsControlFromItemContainer(anchorItem);
            if (parentA == null)
            {
                SelectSingleTreeItem(targetItem);
                return;
            }

            TreeViewItem? resolvedTarget = targetItem;
            ItemsControl? parentB = ItemsControl.ItemsControlFromItemContainer(resolvedTarget);
            while (resolvedTarget != null && parentB != null && parentB != parentA)
            {
                resolvedTarget = parentB as TreeViewItem;
                parentB = resolvedTarget != null ? ItemsControl.ItemsControlFromItemContainer(resolvedTarget) : null;
            }

            if (resolvedTarget != null && parentA == parentB)
            {
                int idxA = parentA.Items.IndexOf(anchorItem);
                int idxB = parentA.Items.IndexOf(resolvedTarget);
                if (idxA >= 0 && idxB >= 0)
                {
                    int start = Math.Min(idxA, idxB);
                    int end = Math.Max(idxA, idxB);

                    foreach (var item in _multiSelectedItems)
                    {
                        MarkTreeItemUnselected(item);
                    }
                    _multiSelectedItems.Clear();

                    for (int i = start; i <= end; i++)
                    {
                        if (parentA.Items[i] is TreeViewItem sibling)
                        {
                            _multiSelectedItems.Add(sibling);
                            MarkTreeItemSelected(sibling);
                        }
                    }

                    _selectedTreeItem = resolvedTarget;
                    _selectedTag = resolvedTarget.Tag as NbtTag;

                    _suppressSelectionChange = true;
                    resolvedTarget.IsSelected = true;
                    _suppressSelectionChange = false;

                    UpdateValueEditorBar();
                    return;
                }
            }

            SelectSingleTreeItem(targetItem);
        }

        private void SelectAllInCurrentContext()
        {
            if (NbtTreeView.Items.Count == 0) return;

            TreeViewItem? containerItem = null;

            if (_selectedTreeItem != null)
            {
                if (_selectedTreeItem.Items.Count > 0)
                {
                    containerItem = _selectedTreeItem;
                }
                else if (ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem) is TreeViewItem parentItem)
                {
                    containerItem = parentItem;
                }
            }

            if (containerItem == null && NbtTreeView.Items[0] is TreeViewItem rootItem)
            {
                containerItem = rootItem;
            }

            if (containerItem == null || containerItem.Items.Count == 0) return;

            _suppressTreeEvents = true;
            containerItem.IsExpanded = true;
            _suppressTreeEvents = false;

            foreach (var old in _multiSelectedItems)
            {
                MarkTreeItemUnselected(old);
                if (old.IsSelected)
                {
                    _suppressSelectionChange = true;
                    old.IsSelected = false;
                    _suppressSelectionChange = false;
                }
            }
            _multiSelectedItems.Clear();

            foreach (var child in containerItem.Items)
            {
                if (child is TreeViewItem childItem)
                {
                    _multiSelectedItems.Add(childItem);
                    MarkTreeItemSelected(childItem);
                }
            }

            UpdateNbtTreeZebraStripes();

            if (_multiSelectedItems.Count > 0)
            {
                _selectedTreeItem = _multiSelectedItems[0];
                _selectedTag = _selectedTreeItem.Tag as NbtTag;

                _suppressSelectionChange = true;
                _selectedTreeItem.IsSelected = true;
                _suppressSelectionChange = false;

                UpdateValueEditorBar();
                TxtStatusLeft.Text = $"Selected all {_multiSelectedItems.Count} tags (Ctrl+C to copy, Ctrl+X to cut, Del to delete)";
            }
        }

        private void UpdateValueEditorBar()
        {
            if (_multiSelectedItems.Count > 1)
            {
                TxtSelectedTagName.Text = $"{_multiSelectedItems.Count} tags selected:";
                TxtEditValue.Text = "";
                TxtEditValue.IsEnabled = false;
                BtnApply.IsEnabled = false;
                return;
            }

            if (_selectedTag != null && IsPrimitiveEditableTag(_selectedTag.TagType))
            {
                TxtSelectedTagName.Text = $"{(string.IsNullOrEmpty(_selectedTag.Name) ? _selectedTag.TagType.ToString() : _selectedTag.Name)}:";
                TxtEditValue.Text = GetSafeTagValueString(_selectedTag);
                TxtEditValue.IsEnabled = true;
                BtnApply.IsEnabled = true;
            }
            else
            {
                TxtSelectedTagName.Text = "Selected Value:";
                TxtEditValue.Text = "";
                TxtEditValue.IsEnabled = false;
                BtnApply.IsEnabled = false;
            }
        }

        private static bool IsPrimitiveEditableTag(NbtTagType tagType)
        {
            return tagType == NbtTagType.String ||
                   tagType == NbtTagType.Int ||
                   tagType == NbtTagType.Byte ||
                   tagType == NbtTagType.Float ||
                   tagType == NbtTagType.Double ||
                   tagType == NbtTagType.Short ||
                   tagType == NbtTagType.Long;
        }

        private static string GetSafeTagValueString(NbtTag tag)
        {
            return tag switch
            {
                NbtByteArray byteArr => $"[{byteArr.Value.Length} bytes]",
                NbtIntArray intArr => $"[{intArr.Value.Length} ints]",
                NbtCompound compound => $"{compound.Count} entries",
                NbtList list => $"{list.Count} entries",
                _ => tag.StringValue
            };
        }

        // ==================== ACCOUNT, WORLD & PLAYER MANAGEMENT ====================

        private void ComboAccounts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ConfirmDiscardUnsavedChanges())
            {
                return;
            }

            _hasUnsavedChanges = false;
            _db?.Dispose();
            _db = null;
            _activeNbtFile = null;
            _currentSaveTarget = "";
            _undoStack.Clear();
            ClearActiveSelectionState();

            WorldListBox.Items.Clear();
            PlayerListBox.Items.Clear();
            NbtTreeView.Items.Clear();
            WorldStripeTransform.Y = 0;
            PlayerStripeTransform.Y = 0;
            NbtStripeTransform.Y = 0;

            BtnLevelDat.IsEnabled = false;
            BtnOpenFolder.IsEnabled = false;
            BtnSave.IsEnabled = false;
            BtnSaveBottom.IsEnabled = false;
            TxtActiveFile.Text = "No file selected";
            TxtWorldTabTitle.Text = "No World Selected";
            TxtStatusPath.Text = "";

            if (ComboAccounts.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string accountPath)
            {
                string worldsDir = Path.Combine(accountPath, "games", "com.mojang", "minecraftWorlds");

                if (!Directory.Exists(worldsDir))
                {
                    worldsDir = Path.Combine(accountPath, "minecraftWorlds");
                }

                if (Directory.Exists(worldsDir))
                {
                    try
                    {
                        foreach (var worldFolder in Directory.GetDirectories(worldsDir))
                        {
                            string levelNameFile = Path.Combine(worldFolder, "levelname.txt");
                            string displayName = Path.GetFileName(worldFolder);

                            if (File.Exists(levelNameFile))
                            {
                                try { displayName = File.ReadAllText(levelNameFile).Trim(); }
                                catch { }
                            }

                            ListBoxItem worldItem = new ListBoxItem
                            {
                                Content = displayName,
                                Tag = worldFolder
                            };
                            WorldListBox.Items.Add(worldItem);
                        }
                    }
                    catch { }
                }

                TxtStatusLeft.Text = WorldListBox.Items.Count > 0
                    ? $"Found {WorldListBox.Items.Count} world(s). Select a world to open."
                    : "No worlds found for this account.";
            }
        }

        private void WorldListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WorldListBox.SelectedItem is ListBoxItem selectedWorld && selectedWorld.Tag is string worldFolderPath)
            {
                if (!ConfirmDiscardUnsavedChanges())
                {
                    return;
                }

                _hasUnsavedChanges = false;
                _worldFolderPath = worldFolderPath;
                _levelDatPath = Path.Combine(_worldFolderPath, "level.dat");
                _dbPath = Path.Combine(_worldFolderPath, "db");

                TxtWorldTabTitle.Text = $"{selectedWorld.Content}";
                TxtActiveIcon.Text = "📦";
                TxtActiveFile.Text = "Select player or level.dat";
                TxtStatusPath.Text = _worldFolderPath.Replace('\\', '/');
                BtnLevelDat.IsEnabled = File.Exists(_levelDatPath);
                BtnOpenFolder.IsEnabled = Directory.Exists(_worldFolderPath);
                BtnSave.IsEnabled = false;
                BtnSaveBottom.IsEnabled = false;

                _activeNbtFile = null;
                _currentSaveTarget = "";
                _undoStack.Clear();
                ClearActiveSelectionState();
                NbtTreeView.Items.Clear();
                PlayerStripeTransform.Y = 0;
                NbtStripeTransform.Y = 0;
                LoadPlayersFromDatabase();
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_worldFolderPath) && Directory.Exists(_worldFolderPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _worldFolderPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not open folder: " + ex.Message);
                }
            }
        }

        private void BtnLevelDat_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_levelDatPath) || !File.Exists(_levelDatPath)) return;

            if (!ConfirmDiscardUnsavedChanges())
            {
                return;
            }

            try
            {
                byte[] headerBuffer = new byte[8];
                var nbt = new NbtFile
                {
                    BigEndian = false
                };

                using (FileStream fs = new FileStream(_levelDatPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < 8)
                        throw new InvalidDataException("level.dat is smaller than the required 8-byte Bedrock header.");

                    fs.ReadExactly(headerBuffer, 0, 8);
                    nbt.LoadFromStream(fs, NbtCompression.None, null);
                }

                Buffer.BlockCopy(headerBuffer, 0, _levelDatHeader, 0, 8);
                _activeNbtFile = nbt;
                _currentSaveTarget = "level.dat";
                _hasUnsavedChanges = false;
                _undoStack.Clear();
                PlayerListBox.SelectedItems.Clear();

                TxtActiveIcon.Text = "📦";
                TxtActiveFile.Text = "level.dat";
                TxtStatusLeft.Text = "Editing World Settings (level.dat)";
                RenderNbtTree(TxtSearch.Text);
                BtnSave.IsEnabled = true;
                BtnSaveBottom.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error reading level.dat: " + ex.Message, "Read Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadPlayersFromDatabase()
        {
            PlayerListBox.Items.Clear();
            _playerCache.Clear();

            _db?.Dispose();
            _db = null;

            if (_dbPath == null || !Directory.Exists(_dbPath)) return;

            try
            {
                _db = NativeLevelDb.Open(_dbPath);

                foreach (var kvp in _db.GetAllPlayerEntries())
                {
                    _playerCache[kvp.Key] = kvp.Value;
                    PlayerListBox.Items.Add(new PlayerEntryItem(kvp.Key));
                }

                TxtStatusLeft.Text = $"Loaded {_playerCache.Count} player record(s) from LevelDB";

                if (PlayerListBox.Items.Count > 0)
                {
                    PlayerListBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read LevelDB. Ensure Minecraft is closed.\n\nDetails: " + ex.Message,
                                "LevelDB Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void PlayerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlayerListBox.SelectedItem is not PlayerEntryItem selectedEntry) return;

            string selectedPlayer = selectedEntry.RawKey;

            if (_currentSaveTarget == selectedPlayer && _activeNbtFile != null)
            {
                TxtStatusLeft.Text = PlayerListBox.SelectedItems.Count > 1
                    ? $"Selected {PlayerListBox.SelectedItems.Count} players (Viewing: {selectedEntry.DisplayName})"
                    : $"Editing Player: {selectedEntry.DisplayName}";
                return;
            }

            if (!ConfirmDiscardUnsavedChanges())
            {
                return;
            }

            if (!_playerCache.TryGetValue(selectedPlayer, out byte[]? playerData) || playerData.Length == 0)
                return;

            try
            {
                var nbt = new NbtFile
                {
                    BigEndian = false
                };
                nbt.LoadFromBuffer(playerData, 0, playerData.Length, NbtCompression.None, null);

                _activeNbtFile = nbt;
                _currentSaveTarget = selectedPlayer;
                _hasUnsavedChanges = false;
                _undoStack.Clear();

                TxtActiveIcon.Text = "👤";
                TxtActiveFile.Text = $"db/{selectedEntry.DisplayName}";
                TxtStatusLeft.Text = PlayerListBox.SelectedItems.Count > 1
                    ? $"Selected {PlayerListBox.SelectedItems.Count} players (Viewing: {selectedEntry.DisplayName})"
                    : $"Editing Player: {selectedEntry.DisplayName}";

                RenderNbtTree(TxtSearch.Text);
                BtnSave.IsEnabled = true;
                BtnSaveBottom.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not parse NBT data for '{selectedPlayer}':\n{ex.Message}",
                                "NBT Parse Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== PLAYER LIST RIGHT-CLICK DELETE ====================

        private void PlayerListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not ListBoxItem)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is ListBoxItem targetItem)
            {
                _rightClickHitValidPlayer = true;
                _rightClickedPlayerItem = targetItem;
                if (!targetItem.IsSelected)
                {
                    PlayerListBox.SelectedItems.Clear();
                    targetItem.IsSelected = true;
                }
                targetItem.BorderThickness = SelectedFlagThickness;
                targetItem.Focus();
            }
            else
            {
                _rightClickHitValidPlayer = false;
                _rightClickedPlayerItem = null;
            }
        }

        private void PlayerContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!_rightClickHitValidPlayer || PlayerListBox.SelectedItems.Count == 0)
            {
                PlayerContextMenu.IsOpen = false;
                return;
            }

            if (_rightClickedPlayerItem != null)
            {
                _rightClickedPlayerItem.BorderThickness = SelectedFlagThickness;
            }

            MenuDeletePlayer.Header = PlayerListBox.SelectedItems.Count > 1
                ? $"Delete ({PlayerListBox.SelectedItems.Count} Players)"
                : "Delete";
        }

        private void PlayerContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            if (_rightClickedPlayerItem != null)
            {
                _rightClickedPlayerItem.BorderThickness = UnselectedFlagThickness;
                _rightClickedPlayerItem = null;
            }
        }

        private void PlayerListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && PlayerListBox.SelectedItems.Count > 0)
            {
                MenuDeletePlayer_Click(sender, e);
                e.Handled = true;
            }
        }

        private void MenuDeletePlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null || PlayerListBox.SelectedItems.Count == 0) return;

            var toDelete = PlayerListBox.SelectedItems.Cast<PlayerEntryItem>().ToList();
            string prompt = toDelete.Count == 1
                ? $"Delete player '{toDelete[0].DisplayName}' from LevelDB?"
                : $"Delete {toDelete.Count} selected players from LevelDB?";

            if (MessageBox.Show(prompt, "Delete Player", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                foreach (var player in toDelete)
                {
                    _db.Delete(Encoding.UTF8.GetBytes(player.RawKey));
                    _playerCache.Remove(player.RawKey);
                    PlayerListBox.Items.Remove(player);
                }

                if (toDelete.Any(p => p.RawKey == _currentSaveTarget))
                {
                    _activeNbtFile = null;
                    _currentSaveTarget = "";
                    _hasUnsavedChanges = false;
                    _undoStack.Clear();
                    ClearActiveSelectionState();
                    NbtTreeView.Items.Clear();
                    BtnSave.IsEnabled = false;
                    BtnSaveBottom.IsEnabled = false;
                    TxtActiveFile.Text = "No file selected";
                }

                if (PlayerListBox.Items.Count > 0 && PlayerListBox.SelectedItem == null)
                {
                    PlayerListBox.SelectedIndex = 0;
                }

                TxtStatusLeft.Text = toDelete.Count == 1
                    ? $"Deleted player '{toDelete[0].DisplayName}' from LevelDB"
                    : $"Deleted {toDelete.Count} players from LevelDB";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete player(s): {ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_activeNbtFile != null)
            {
                RenderNbtTree(TxtSearch.Text);
            }
        }

        // ==================== UNDO (CTRL+Z) SYSTEM ====================

        private void PushUndoSnapshot()
        {
            if (_activeNbtFile?.RootTag == null) return;

            if (_undoStack.Count >= MaxUndoSteps)
            {
                var temp = _undoStack.ToArray();
                _undoStack.Clear();
                for (int i = MaxUndoSteps - 2; i >= 0; i--)
                {
                    _undoStack.Push(temp[i]);
                }
            }

            _undoStack.Push((NbtCompound)_activeNbtFile.RootTag.Clone());
        }

        private void PerformUndo()
        {
            if (_undoStack.Count == 0 || _activeNbtFile == null)
            {
                TxtStatusLeft.Text = "Nothing to undo";
                return;
            }

            HashSet<string> expandedPaths = new HashSet<string>();
            string? selectedPath = _selectedTag != null ? GetTagPath(_selectedTag) : null;

            foreach (var item in NbtTreeView.Items)
            {
                if (item is TreeViewItem rootItem)
                {
                    CollectExpandedPaths(rootItem, expandedPaths);
                }
            }

            _activeNbtFile.RootTag = _undoStack.Pop();
            _hasUnsavedChanges = true;

            _suppressTreeEvents = true;
            try
            {
                NbtTreeView.Items.Clear();
                ClearActiveSelectionState();

                TreeViewItem? newRoot = CreateUmeNode(_activeNbtFile.RootTag, TxtSearch.Text.Trim(), 0);
                if (newRoot != null)
                {
                    newRoot.IsExpanded = true;
                    NbtTreeView.Items.Add(newRoot);
                    RestoreExpandedAndSelectedStates(newRoot, expandedPaths, selectedPath);
                }
            }
            finally
            {
                _suppressTreeEvents = false;
            }

            UpdateNbtTreeZebraStripes();
            TxtStatusLeft.Text = $"Undid last change ({_undoStack.Count} undo step(s) remaining)";
        }

        private static string GetTagPath(NbtTag tag)
        {
            var parts = new List<string>();
            NbtTag? curr = tag;
            while (curr != null)
            {
                if (curr.Parent is NbtList parentList)
                {
                    parts.Add($"[{parentList.IndexOf(curr)}]");
                }
                else if (curr.Parent == null)
                {
                    parts.Add("<root>");
                }
                else
                {
                    parts.Add(string.IsNullOrEmpty(curr.Name) ? "<unnamed>" : curr.Name);
                }
                curr = curr.Parent;
            }
            parts.Reverse();
            return string.Join("\u001F", parts);
        }

        private static int GetTagDepth(NbtTag tag)
        {
            int depth = 0;
            NbtTag? curr = tag.Parent;
            while (curr != null)
            {
                depth++;
                curr = curr.Parent;
            }
            return depth;
        }

        private void CollectExpandedPaths(TreeViewItem item, HashSet<string> expandedPaths)
        {
            if (item.Tag is NbtTag tag && item.IsExpanded)
            {
                expandedPaths.Add(GetTagPath(tag));
                foreach (var child in item.Items)
                {
                    if (child is TreeViewItem childItem)
                    {
                        CollectExpandedPaths(childItem, expandedPaths);
                    }
                }
            }
        }

        private void RestoreExpandedAndSelectedStates(TreeViewItem item, HashSet<string> expandedPaths, string? selectedPath)
        {
            if (item.Tag is NbtTag tag)
            {
                string path = GetTagPath(tag);
                if (expandedPaths.Contains(path))
                {
                    item.IsExpanded = true;
                }
                if (selectedPath != null && path == selectedPath)
                {
                    SelectSingleTreeItem(item);
                }
            }

            foreach (var child in item.Items)
            {
                if (child is TreeViewItem childItem)
                {
                    RestoreExpandedAndSelectedStates(childItem, expandedPaths, selectedPath);
                }
            }
        }

        private void RenderNbtTree(string filterText = "")
        {
            _suppressTreeEvents = true;
            try
            {
                NbtTreeView.Items.Clear();
                ClearActiveSelectionState();

                if (_activeNbtFile?.RootTag != null)
                {
                    TreeViewItem? rootNode = CreateUmeNode(_activeNbtFile.RootTag, filterText.Trim(), 0);
                    if (rootNode != null)
                    {
                        rootNode.IsExpanded = true;
                        NbtTreeView.Items.Add(rootNode);
                    }
                }
            }
            finally
            {
                _suppressTreeEvents = false;
            }

            UpdateNbtTreeZebraStripes();
        }

        private TreeViewItem? CreateUmeNode(NbtTag tag, string filter, int depth)
        {
            bool hasFilter = !string.IsNullOrEmpty(filter);
            bool selfMatches = !hasFilter ||
                               (!string.IsNullOrEmpty(tag.Name) && tag.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

            TreeViewItem node = new TreeViewItem
            {
                Tag = tag,
                Style = _umeTreeViewItemStyle,
                Background = UmeStripeWhiteBrush,
                BorderBrush = UmeStripeWhiteBrush,
                BorderThickness = UnselectedFlagThickness,
                Padding = new Thickness(depth * 18 + 6, 0, 6, 0)
            };

            if (tag is NbtCompound compound)
            {
                foreach (var child in compound.Tags)
                {
                    TreeViewItem? childNode = CreateUmeNode(child, hasFilter && !selfMatches ? filter : "", depth + 1);
                    if (childNode != null)
                    {
                        node.Items.Add(childNode);
                    }
                }

                if (hasFilter && !selfMatches && node.Items.Count == 0)
                    return null;

                if (hasFilter && node.Items.Count > 0)
                    node.IsExpanded = true;

                string label = GetTagDisplayText(tag);
                node.Header = BuildUmeNodeHeader(tag.TagType, label, isContainer: true);
            }
            else if (tag is NbtList list)
            {
                foreach (var child in list)
                {
                    TreeViewItem? childNode = CreateUmeNode(child, hasFilter && !selfMatches ? filter : "", depth + 1);
                    if (childNode != null)
                    {
                        node.Items.Add(childNode);
                    }
                }

                if (hasFilter && !selfMatches && node.Items.Count == 0)
                    return null;

                if (hasFilter && node.Items.Count > 0)
                    node.IsExpanded = true;

                string label = GetTagDisplayText(tag);
                node.Header = BuildUmeNodeHeader(tag.TagType, label, isContainer: true);
            }
            else
            {
                string valStr = GetSafeTagValueString(tag);
                if (hasFilter && !selfMatches && !valStr.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    return null;

                string label = GetTagDisplayText(tag);
                node.Header = BuildUmeNodeHeader(tag.TagType, label, isContainer: false);
            }

            return node;
        }

        private static string GetTagDisplayText(NbtTag tag)
        {
            string valText = GetSafeTagValueString(tag);
            return string.IsNullOrEmpty(tag.Name)
                ? valText
                : $"{tag.Name}: {valText}";
        }

        private void RefreshNodeHeader(TreeViewItem item)
        {
            if (item.Tag is NbtTag tag)
            {
                bool isContainer = tag is NbtCompound || tag is NbtList;
                item.Header = BuildUmeNodeHeader(tag.TagType, GetTagDisplayText(tag), isContainer);
            }
        }

        // ==================== MOUSE CLICK, DRAG SWEEP, CONTEXT MENU & SHORTCUTS ====================

        private void NbtTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSelection = false;

            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not TreeViewItem && source is not ToggleButton && source is not ScrollBar)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is ToggleButton || source is ScrollBar) return;

            if (source is TreeViewItem targetItem)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    ToggleMultiSelectTreeItem(targetItem);
                    _dragAnchorItem = targetItem;
                    targetItem.Focus();
                    e.Handled = true;
                }
                else if (Keyboard.Modifiers == ModifierKeys.Shift && _dragAnchorItem != null)
                {
                    RangeSelectTreeItems(_dragAnchorItem, targetItem);
                    targetItem.Focus();
                    e.Handled = true;
                }
                else
                {
                    SelectSingleTreeItem(targetItem);
                    _dragAnchorItem = targetItem;
                    _isDraggingSelection = true;
                }
            }
        }

        private void NbtTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingSelection || e.LeftButton != MouseButtonState.Pressed || _dragAnchorItem == null)
            {
                _isDraggingSelection = false;
                return;
            }

            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not TreeViewItem && source is not ScrollBar)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is ScrollBar) return;

            if (source is TreeViewItem hoveredItem && hoveredItem != _selectedTreeItem)
            {
                RangeSelectTreeItems(_dragAnchorItem, hoveredItem);
            }
        }

        private void NbtTreeView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSelection = false;
        }

        private void NbtTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSelection = false;

            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not TreeViewItem && source is not ScrollBar)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is TreeViewItem targetItem)
            {
                _rightClickHitValidNode = true;

                if (!_multiSelectedItems.Contains(targetItem))
                {
                    SelectSingleTreeItem(targetItem);
                    _dragAnchorItem = targetItem;
                }
                else
                {
                    _selectedTreeItem = targetItem;
                    _selectedTag = targetItem.Tag as NbtTag;
                }

                MarkTreeItemSelected(targetItem, isDeepBlue: true);
                targetItem.Focus();
            }
            else
            {
                _rightClickHitValidNode = false;
            }
        }

        private (TreeViewItem? ContainerItem, NbtTag? ContainerTag) ResolvePasteTargetContainer()
        {
            if (_selectedTreeItem == null || _selectedTag == null)
                return (null, null);

            if (_selectedTag is NbtCompound &&
                _selectedTag.Parent is NbtList parentList &&
                !_clipboardIsRootCompound &&
                _nbtClipboard.Count > 0 &&
                (parentList.Count == 0 || parentList.ListType == NbtTagType.Unknown || parentList.ListType == _nbtClipboard[0].TagType) &&
                ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem) is TreeViewItem listTreeItem)
            {
                return (listTreeItem, parentList);
            }

            if (_selectedTag is NbtCompound || _selectedTag is NbtList)
            {
                return (_selectedTreeItem, _selectedTag);
            }

            if (ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem) is TreeViewItem parentItem &&
                parentItem.Tag is NbtTag parentTag &&
                (parentTag is NbtCompound || parentTag is NbtList))
            {
                return (parentItem, parentTag);
            }

            return (null, null);
        }

        private void NbtContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!_rightClickHitValidNode || _selectedTag == null || _selectedTreeItem == null)
            {
                NbtContextMenu.IsOpen = false;
                return;
            }

            foreach (var item in _multiSelectedItems)
            {
                MarkTreeItemSelected(item, isDeepBlue: item == _selectedTreeItem);
            }

            bool isMulti = _multiSelectedItems.Count > 1;
            bool isRoot = !isMulti && _selectedTag.Parent == null;
            bool isInsideList = !isMulti && _selectedTag.Parent is NbtList;

            MenuUndo.Visibility = _undoStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MenuUndoSeparator.Visibility = _undoStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            MenuCut.Visibility = Visibility.Visible;
            MenuCopy.Visibility = Visibility.Visible;
            MenuSelectAll.Visibility = Visibility.Visible;

            bool hasClipboard = _nbtClipboard.Count > 0;
            var (_, pasteContainerTag) = ResolvePasteTargetContainer();

            if (pasteContainerTag is NbtCompound)
            {
                MenuPaste.Visibility = Visibility.Visible;
                MenuPaste.IsEnabled = hasClipboard;
            }
            else if (pasteContainerTag is NbtList targetList &&
                     (targetList.Count == 0 || (hasClipboard && (targetList.ListType == NbtTagType.Unknown || targetList.ListType == _nbtClipboard[0].TagType || _nbtClipboard[0] is NbtList))))
            {
                MenuPaste.Visibility = Visibility.Visible;
                MenuPaste.IsEnabled = hasClipboard;
            }
            else
            {
                MenuPaste.Visibility = Visibility.Collapsed;
            }

            MenuDelete.Visibility = isRoot ? Visibility.Collapsed : Visibility.Visible;

            bool isFiltered = !string.IsNullOrWhiteSpace(TxtSearch.Text);
            if (!isFiltered && isInsideList && _selectedTag.Parent is NbtList parentList)
            {
                int idx = parentList.IndexOf(_selectedTag);
                bool canUp = idx > 0;
                bool canDown = idx >= 0 && idx < parentList.Count - 1;

                MenuMoveUp.Visibility = canUp ? Visibility.Visible : Visibility.Collapsed;
                MenuMoveDown.Visibility = canDown ? Visibility.Visible : Visibility.Collapsed;
                MenuMoveSeparator.Visibility = (canUp || canDown) ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                MenuMoveUp.Visibility = Visibility.Collapsed;
                MenuMoveDown.Visibility = Visibility.Collapsed;
                MenuMoveSeparator.Visibility = Visibility.Collapsed;
            }
        }

        private void NbtContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            foreach (var item in _multiSelectedItems)
            {
                MarkTreeItemSelected(item, isDeepBlue: false);
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                if (Keyboard.FocusedElement is not TextBox)
                {
                    PerformUndo();
                    e.Handled = true;
                    return;
                }
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                if (BtnSave.IsEnabled) BtnSave_Click(sender, e);
                e.Handled = true;
                return;
            }

            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ListBoxItem) return;

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
            {
                SelectAllInCurrentContext();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                MenuCopy_Click(sender, e);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X)
            {
                MenuCut_Click(sender, e);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
            {
                MenuPaste_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                MenuDelete_Click(sender, e);
                e.Handled = true;
            }
        }

        private void MenuUndo_Click(object sender, RoutedEventArgs e)
        {
            PerformUndo();
        }

        private void MenuSelectAll_Click(object sender, RoutedEventArgs e)
        {
            SelectAllInCurrentContext();
        }

        private List<TreeViewItem> GetEffectiveSelectedItems()
        {
            if (_multiSelectedItems.Count > 0)
                return _multiSelectedItems.ToList();
            if (_selectedTreeItem != null)
                return new List<TreeViewItem> { _selectedTreeItem };
            return new List<TreeViewItem>();
        }

        private void MenuCopy_Click(object sender, RoutedEventArgs e)
        {
            var items = GetEffectiveSelectedItems();
            if (items.Count == 0) return;

            _nbtClipboard.Clear();
            _clipboardIsRootCompound = items.Count == 1 &&
                                       items[0].Tag is NbtCompound rootTag &&
                                       rootTag.Parent == null;

            foreach (var item in items)
            {
                if (item.Tag is NbtTag tag)
                {
                    _nbtClipboard.Add((NbtTag)tag.Clone());
                }
            }

            if (_nbtClipboard.Count == 1)
            {
                string tagName = string.IsNullOrEmpty(_nbtClipboard[0].Name) ? _nbtClipboard[0].TagType.ToString() : _nbtClipboard[0].Name!;
                TxtStatusLeft.Text = $"Copied '{tagName}' to NBT clipboard";
            }
            else
            {
                TxtStatusLeft.Text = $"Copied {_nbtClipboard.Count} tags to NBT clipboard";
            }
        }

        private void MenuCut_Click(object sender, RoutedEventArgs e)
        {
            var items = GetEffectiveSelectedItems();
            if (items.Count == 0) return;

            MenuCopy_Click(sender, e);

            if (items.Count == 1 && items[0].Tag is NbtCompound rootCompound && rootCompound.Parent == null)
            {
                if (rootCompound.Count == 0) return;
                PushUndoSnapshot();
                rootCompound.Clear();
                items[0].Items.Clear();
                RefreshNodeHeader(items[0]);
                UpdateNbtTreeZebraStripes();
                SelectSingleTreeItem(items[0]);
                _hasUnsavedChanges = true;
                TxtStatusLeft.Text = "Cut all entries from root compound (Ctrl+Z to undo)";
                return;
            }

            DeleteSelectedItemsInternal(isCutOperation: true);
        }

        private static string GenerateUniqueTagName(NbtCompound targetCompound, string baseName)
        {
            string candidate = baseName;
            int suffix = 1;
            while (targetCompound.Contains(candidate))
            {
                candidate = $"{baseName}_{suffix}";
                suffix++;
            }
            return candidate;
        }

        private void MenuPaste_Click(object sender, RoutedEventArgs e)
        {
            if (_nbtClipboard.Count == 0 || _selectedTag == null || _selectedTreeItem == null) return;

            var (targetContainerItem, targetContainerTag) = ResolvePasteTargetContainer();
            if (targetContainerItem == null || targetContainerTag == null) return;

            try
            {
                PushUndoSnapshot();

                int childDepth = GetTagDepth(targetContainerTag) + 1;
                string activeFilter = TxtSearch.Text.Trim();

                _suppressTreeEvents = true;
                try
                {
                    if (targetContainerTag is NbtCompound targetCompound)
                    {
                        if (_clipboardIsRootCompound && _nbtClipboard.Count == 1 && _nbtClipboard[0] is NbtCompound sourceCompound)
                        {
                            targetCompound.Clear();
                            targetContainerItem.Items.Clear();

                            foreach (NbtTag child in sourceCompound.Tags)
                            {
                                NbtTag childCopy = (NbtTag)child.Clone();
                                targetCompound.Add(childCopy);
                                TreeViewItem? childNode = CreateUmeNode(childCopy, activeFilter, childDepth);
                                if (childNode != null) targetContainerItem.Items.Add(childNode);
                            }

                            RefreshNodeHeader(targetContainerItem);
                            targetContainerItem.IsExpanded = true;
                            SelectSingleTreeItem(targetContainerItem);
                            _hasUnsavedChanges = true;
                            TxtStatusLeft.Text = $"Pasted {sourceCompound.Count} entries into compound (Ctrl+Z to undo)";
                            return;
                        }

                        TreeViewItem? lastPastedNode = null;
                        int pastedCount = 0;

                        foreach (NbtTag clipTag in _nbtClipboard)
                        {
                            NbtTag cloned = (NbtTag)clipTag.Clone();

                            if (string.IsNullOrEmpty(cloned.Name))
                            {
                                cloned.Name = GenerateUniqueTagName(targetCompound, "PastedTag");
                            }

                            if (targetCompound.Contains(cloned.Name))
                            {
                                targetCompound.Remove(cloned.Name);
                                targetCompound.Add(cloned);

                                bool replacedInUi = false;
                                for (int i = 0; i < targetContainerItem.Items.Count; i++)
                                {
                                    if (targetContainerItem.Items[i] is TreeViewItem existingChild &&
                                        existingChild.Tag is NbtTag existingTag &&
                                        existingTag.Name == cloned.Name)
                                    {
                                        TreeViewItem? replacementNode = CreateUmeNode(cloned, activeFilter, childDepth);
                                        if (replacementNode != null)
                                        {
                                            targetContainerItem.Items[i] = replacementNode;
                                            lastPastedNode = replacementNode;
                                        }
                                        else
                                        {
                                            targetContainerItem.Items.RemoveAt(i);
                                        }
                                        replacedInUi = true;
                                        break;
                                    }
                                }

                                if (!replacedInUi)
                                {
                                    TreeViewItem? newNode = CreateUmeNode(cloned, activeFilter, childDepth);
                                    if (newNode != null)
                                    {
                                        targetContainerItem.Items.Add(newNode);
                                        lastPastedNode = newNode;
                                    }
                                }
                            }
                            else
                            {
                                targetCompound.Add(cloned);
                                TreeViewItem? newNode = CreateUmeNode(cloned, activeFilter, childDepth);
                                if (newNode != null)
                                {
                                    targetContainerItem.Items.Add(newNode);
                                    lastPastedNode = newNode;
                                }
                            }

                            pastedCount++;
                        }

                        targetContainerItem.IsExpanded = true;
                        RefreshNodeHeader(targetContainerItem);
                        if (lastPastedNode != null)
                        {
                            SelectSingleTreeItem(lastPastedNode);
                        }

                        _hasUnsavedChanges = true;
                        TxtStatusLeft.Text = pastedCount == 1
                            ? $"Pasted '{_nbtClipboard[0].Name ?? "PastedTag"}' into compound (Ctrl+Z to undo)"
                            : $"Pasted {pastedCount} tags into compound (Ctrl+Z to undo)";
                    }
                    else if (targetContainerTag is NbtList targetList)
                    {
                        if (_nbtClipboard.Count == 1 && _nbtClipboard[0] is NbtList sourceList &&
                            (targetList.ListType == sourceList.ListType || targetList.Count == 0))
                        {
                            targetList.Clear();
                            if (sourceList.ListType != NbtTagType.Unknown)
                            {
                                targetList.ListType = sourceList.ListType;
                            }
                            targetContainerItem.Items.Clear();

                            foreach (NbtTag item in sourceList)
                            {
                                NbtTag itemCopy = (NbtTag)item.Clone();
                                itemCopy.Name = null;
                                targetList.Add(itemCopy);
                                TreeViewItem? childNode = CreateUmeNode(itemCopy, activeFilter, childDepth);
                                if (childNode != null) targetContainerItem.Items.Add(childNode);
                            }

                            RefreshNodeHeader(targetContainerItem);
                            targetContainerItem.IsExpanded = true;
                            SelectSingleTreeItem(targetContainerItem);
                            _hasUnsavedChanges = true;
                            TxtStatusLeft.Text = $"Replaced list with {sourceList.Count} entries (Ctrl+Z to undo)";
                            return;
                        }

                        int addedCount = 0;
                        TreeViewItem? lastAddedNode = null;

                        foreach (NbtTag clipTag in _nbtClipboard)
                        {
                            if (targetList.Count == 0 || targetList.ListType == NbtTagType.Unknown)
                            {
                                targetList.ListType = clipTag.TagType;
                            }

                            if (targetList.ListType == clipTag.TagType)
                            {
                                NbtTag cloned = (NbtTag)clipTag.Clone();
                                cloned.Name = null;
                                targetList.Add(cloned);
                                TreeViewItem? newNode = CreateUmeNode(cloned, activeFilter, childDepth);
                                if (newNode != null)
                                {
                                    targetContainerItem.Items.Add(newNode);
                                    lastAddedNode = newNode;
                                }
                                addedCount++;
                            }
                        }

                        if (addedCount > 0)
                        {
                            targetContainerItem.IsExpanded = true;
                            RefreshNodeHeader(targetContainerItem);
                            if (lastAddedNode != null)
                            {
                                SelectSingleTreeItem(lastAddedNode);
                            }
                            _hasUnsavedChanges = true;
                            TxtStatusLeft.Text = $"Pasted {addedCount} item(s) into list (Ctrl+Z to undo)";
                        }
                    }
                }
                finally
                {
                    _suppressTreeEvents = false;
                    UpdateNbtTreeZebraStripes();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not paste tag here: " + ex.Message, "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedItemsInternal(isCutOperation: false);
        }

        private static bool HasSelectedAncestor(NbtTag tag, HashSet<NbtTag> selectedTags)
        {
            NbtTag? curr = tag.Parent;
            while (curr != null)
            {
                if (selectedTags.Contains(curr))
                    return true;
                curr = curr.Parent;
            }
            return false;
        }

        private void DeleteSelectedItemsInternal(bool isCutOperation)
        {
            var rawItems = GetEffectiveSelectedItems()
                .Where(item => item.Tag is NbtTag t && t.Parent != null)
                .ToList();

            if (rawItems.Count == 0) return;

            var selectedTagSet = new HashSet<NbtTag>(rawItems.Select(i => (NbtTag)i.Tag));
            var itemsToDelete = rawItems
                .Where(item => item.Tag is NbtTag t && !HasSelectedAncestor(t, selectedTagSet))
                .ToList();

            if (itemsToDelete.Count == 0) return;

            PushUndoSnapshot();

            TreeViewItem? fallbackParent = null;
            string firstTagName = "";

            foreach (var itemToDelete in itemsToDelete)
            {
                if (itemToDelete.Tag is not NbtTag tagToDelete || tagToDelete.Parent == null)
                    continue;

                if (string.IsNullOrEmpty(firstTagName))
                {
                    firstTagName = string.IsNullOrEmpty(tagToDelete.Name) ? tagToDelete.TagType.ToString() : tagToDelete.Name;
                }

                ItemsControl? parentControl = ItemsControl.ItemsControlFromItemContainer(itemToDelete);

                if (tagToDelete.Parent is NbtCompound parentCompound && tagToDelete.Name != null)
                {
                    parentCompound.Remove(tagToDelete.Name);
                }
                else if (tagToDelete.Parent is NbtList parentList)
                {
                    parentList.Remove(tagToDelete);
                }

                if (parentControl is TreeViewItem parentTreeItem)
                {
                    parentTreeItem.Items.Remove(itemToDelete);
                    RefreshNodeHeader(parentTreeItem);
                    fallbackParent = parentTreeItem;
                }
            }

            ClearActiveSelectionState();
            UpdateNbtTreeZebraStripes();

            if (fallbackParent != null)
            {
                SelectSingleTreeItem(fallbackParent);
            }

            _hasUnsavedChanges = true;
            string verb = isCutOperation ? "Cut" : "Deleted";
            TxtStatusLeft.Text = itemsToDelete.Count == 1
                ? $"{verb} '{firstTagName}' (Ctrl+Z to undo)"
                : $"{verb} {itemsToDelete.Count} tags (Ctrl+Z to undo)";
        }

        private void MenuMoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedListTag(-1);
        }

        private void MenuMoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedListTag(1);
        }

        private void MoveSelectedListTag(int direction)
        {
            if (_selectedTag == null || _selectedTreeItem == null) return;
            if (_selectedTag.Parent is not NbtList parentList) return;
            if (ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem) is not TreeViewItem parentTreeItem) return;

            int dataIndex = parentList.IndexOf(_selectedTag);
            int newDataIndex = dataIndex + direction;

            int uiIndex = parentTreeItem.Items.IndexOf(_selectedTreeItem);
            int newUiIndex = uiIndex + direction;

            if (dataIndex < 0 || newDataIndex < 0 || newDataIndex >= parentList.Count) return;
            if (uiIndex < 0 || newUiIndex < 0 || newUiIndex >= parentTreeItem.Items.Count) return;

            PushUndoSnapshot();

            NbtTag movingTag = parentList[dataIndex];
            parentList.RemoveAt(dataIndex);
            parentList.Insert(newDataIndex, movingTag);

            TreeViewItem movingNode = _selectedTreeItem;
            parentTreeItem.Items.RemoveAt(uiIndex);
            parentTreeItem.Items.Insert(newUiIndex, movingNode);

            UpdateNbtTreeZebraStripes();
            SelectSingleTreeItem(movingNode);
            movingNode.Focus();
            _hasUnsavedChanges = true;
            TxtStatusLeft.Text = $"Moved list entry {(direction < 0 ? "up" : "down")} (Ctrl+Z to undo)";
        }

        // ==================== NODE HEADER STYLING ====================

        private UIElement BuildUmeNodeHeader(NbtTagType tagType, string text, bool isContainer)
        {
            StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal };

            if (tagType == NbtTagType.Compound)
            {
                Border box = new Border
                {
                    Width = 15,
                    Height = 15,
                    CornerRadius = new CornerRadius(2),
                    Background = CreateFrozenBrush(212, 155, 94),
                    BorderBrush = CreateFrozenBrush(140, 92, 44),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                box.Child = new Rectangle
                {
                    Width = 4,
                    Height = 6,
                    Fill = CreateFrozenBrush(245, 226, 196),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                panel.Children.Add(box);
            }
            else if (tagType == NbtTagType.List)
            {
                TextBlock listIcon = new TextBlock
                {
                    Text = "☰",
                    FontSize = 11.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = CreateFrozenBrush(70, 80, 90),
                    Margin = new Thickness(1, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(listIcon);
            }
            else if (tagType == NbtTagType.String)
            {
                TextBlock strIcon = new TextBlock
                {
                    Text = "TT",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = CreateFrozenBrush(97, 110, 124),
                    Width = 15,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(strIcon);
            }
            else
            {
                var (letter, color) = GetBadgeInfo(tagType);
                Border badge = new Border
                {
                    Width = 15,
                    Height = 17,
                    Background = CreateFrozenBrush(248, 249, 250),
                    BorderBrush = CreateFrozenBrush(180, 188, 196),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                {
                    Text = letter,
                    Foreground = CreateFrozenBrush(color.R, color.G, color.B),
                    FontWeight = FontWeights.Bold,
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(badge);
            }

            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = CreateFrozenBrush(20, 24, 28),
                FontWeight = isContainer ? FontWeights.Medium : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });

            return panel;
        }

        private static (string Letter, Color Color) GetBadgeInfo(NbtTagType type)
        {
            return type switch
            {
                NbtTagType.Byte => ("B", Color.FromRgb(214, 69, 69)),
                NbtTagType.Short => ("S", Color.FromRgb(39, 174, 96)),
                NbtTagType.Int => ("I", Color.FromRgb(41, 128, 185)),
                NbtTagType.Long => ("L", Color.FromRgb(142, 68, 173)),
                NbtTagType.Float => ("F", Color.FromRgb(155, 89, 182)),
                NbtTagType.Double => ("D", Color.FromRgb(22, 160, 133)),
                _ => ("01", Color.FromRgb(82, 96, 109))
            };
        }

        private void NbtTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressSelectionChange) return;

            if (NbtTreeView.SelectedItem is TreeViewItem item && item.Tag is NbtTag)
            {
                if (Keyboard.Modifiers != ModifierKeys.Control && Keyboard.Modifiers != ModifierKeys.Shift)
                {
                    SelectSingleTreeItem(item);
                    _dragAnchorItem = item;
                }
            }
        }

        private void TxtEditValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnApply.IsEnabled)
            {
                BtnApply_Click(sender, e);
            }
        }

        private static bool TryParseByteOrSByte(string input, out byte result)
        {
            if (byte.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return true;
            if (sbyte.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte signedVal))
            {
                result = unchecked((byte)signedVal);
                return true;
            }
            return false;
        }

        private static bool TryParseFloatInvariant(string input, out float result)
        {
            if (float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
                float.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
            {
                return !float.IsNaN(result) && !float.IsInfinity(result);
            }
            return false;
        }

        private static bool TryParseDoubleInvariant(string input, out double result)
        {
            if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
                double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
            {
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            return false;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTag == null) return;

            string rawText = TxtEditValue.Text.Trim();

            if (_selectedTag is NbtString strTag)
            {
                PushUndoSnapshot();
                strTag.Value = TxtEditValue.Text;
            }
            else if (_selectedTag is NbtByte byteTag && TryParseByteOrSByte(rawText, out byte bVal))
            {
                PushUndoSnapshot();
                byteTag.Value = bVal;
            }
            else if (_selectedTag is NbtShort shortTag && short.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out short sVal))
            {
                PushUndoSnapshot();
                shortTag.Value = sVal;
            }
            else if (_selectedTag is NbtInt intTag && int.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iVal))
            {
                PushUndoSnapshot();
                intTag.Value = iVal;
            }
            else if (_selectedTag is NbtLong longTag && long.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lVal))
            {
                PushUndoSnapshot();
                longTag.Value = lVal;
            }
            else if (_selectedTag is NbtFloat floatTag && TryParseFloatInvariant(rawText, out float fVal))
            {
                PushUndoSnapshot();
                floatTag.Value = fVal;
            }
            else if (_selectedTag is NbtDouble doubleTag && TryParseDoubleInvariant(rawText, out double dVal))
            {
                PushUndoSnapshot();
                doubleTag.Value = dVal;
            }
            else
            {
                MessageBox.Show($"Invalid value for {_selectedTag.TagType}.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_selectedTreeItem != null)
            {
                RefreshNodeHeader(_selectedTreeItem);
            }

            _hasUnsavedChanges = true;
            string displayName = string.IsNullOrEmpty(_selectedTag.Name) ? _selectedTag.TagType.ToString() : _selectedTag.Name;
            TxtStatusLeft.Text = $"Updated '{displayName}' to {GetSafeTagValueString(_selectedTag)} (Ctrl+Z to undo, Ctrl+S to save)";
        }

        private static void SanitizeEmptyListsRecursive(NbtTag tag)
        {
            if (tag is NbtCompound compound)
            {
                foreach (NbtTag child in compound.Tags)
                {
                    SanitizeEmptyListsRecursive(child);
                }
            }
            else if (tag is NbtList list)
            {
                if (list.Count == 0 && list.ListType == NbtTagType.Unknown)
                {
                    list.ListType = NbtTagType.Byte;
                }
                else
                {
                    foreach (NbtTag child in list)
                    {
                        SanitizeEmptyListsRecursive(child);
                    }
                }
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_activeNbtFile?.RootTag == null) return;

            try
            {
                _activeNbtFile.RootTag.Name ??= "";
                SanitizeEmptyListsRecursive(_activeNbtFile.RootTag);

                if (_currentSaveTarget == "level.dat" && _levelDatPath != null)
                {
                    byte[] nbtPayload = _activeNbtFile.SaveToBuffer(NbtCompression.None);

                    byte[] lengthBytes = BitConverter.GetBytes(nbtPayload.Length);
                    Buffer.BlockCopy(lengthBytes, 0, _levelDatHeader, 4, 4);

                    using (FileStream fs = new FileStream(_levelDatPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        fs.Write(_levelDatHeader, 0, 8);
                        fs.Write(nbtPayload, 0, nbtPayload.Length);
                    }

                    _hasUnsavedChanges = false;
                    TxtStatusLeft.Text = "Saved level.dat successfully!";
                    MessageBox.Show("level.dat saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (_db != null && !string.IsNullOrEmpty(_currentSaveTarget))
                {
                    byte[] newNbtBytes = _activeNbtFile.SaveToBuffer(NbtCompression.None);
                    _db.Put(Encoding.UTF8.GetBytes(_currentSaveTarget), newNbtBytes);
                    _playerCache[_currentSaveTarget] = newNbtBytes;
                    _hasUnsavedChanges = false;
                    TxtStatusLeft.Text = $"Saved {_currentSaveTarget} to LevelDB!";
                    MessageBox.Show($"{_currentSaveTarget} saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save '{_currentSaveTarget}':\n{ex.Message}",
                                "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public sealed class NativeLevelDb : IDisposable
    {
        private const string DllName = "leveldb.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr leveldb_options_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_options_destroy(IntPtr options);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_options_set_create_if_missing(IntPtr options, byte val);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_options_set_compression(IntPtr options, int val);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr leveldb_open(IntPtr options, byte[] name, out IntPtr errptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_close(IntPtr db);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr leveldb_readoptions_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_readoptions_destroy(IntPtr options);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr leveldb_writeoptions_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_writeoptions_destroy(IntPtr options);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_put(IntPtr db, IntPtr options, byte[] key, IntPtr keylen, byte[] val, IntPtr vallen, out IntPtr errptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_delete(IntPtr db, IntPtr options, byte[] key, IntPtr keylen, out IntPtr errptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr leveldb_create_iterator(IntPtr db, IntPtr options);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_iter_destroy(IntPtr iter);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte leveldb_iter_valid(IntPtr iter);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_iter_seek_to_first(IntPtr iter);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_iter_next(IntPtr iter);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr leveldb_iter_key(IntPtr iter, out IntPtr klen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr leveldb_iter_value(IntPtr iter, out IntPtr vlen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void leveldb_free(IntPtr ptr);

        private IntPtr _dbPtr;

        private NativeLevelDb(IntPtr dbPtr)
        {
            _dbPtr = dbPtr;
        }

        ~NativeLevelDb()
        {
            Dispose();
        }

        public static NativeLevelDb Open(string dbPath)
        {
            IntPtr options = leveldb_options_create();
            try
            {
                leveldb_options_set_create_if_missing(options, 0);
                leveldb_options_set_compression(options, 4);

                byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
                IntPtr dbPtr = leveldb_open(options, pathBytes, out IntPtr errPtr);

                if (errPtr != IntPtr.Zero)
                {
                    try
                    {
                        string errMsg = Marshal.PtrToStringAnsi(errPtr) ?? "Unknown LevelDB open error";
                        throw new Exception(errMsg);
                    }
                    finally
                    {
                        leveldb_free(errPtr);
                    }
                }

                if (dbPtr == IntPtr.Zero)
                {
                    throw new Exception("Failed to open LevelDB handle.");
                }

                return new NativeLevelDb(dbPtr);
            }
            finally
            {
                leveldb_options_destroy(options);
            }
        }

        public Dictionary<string, byte[]> GetAllPlayerEntries()
        {
            if (_dbPtr == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeLevelDb));

            var results = new Dictionary<string, byte[]>();
            IntPtr readOptions = leveldb_readoptions_create();
            IntPtr iter = leveldb_create_iterator(_dbPtr, readOptions);

            try
            {
                for (leveldb_iter_seek_to_first(iter); leveldb_iter_valid(iter) != 0; leveldb_iter_next(iter))
                {
                    IntPtr keyPtr = leveldb_iter_key(iter, out IntPtr klen);
                    int keyLength = (int)klen;
                    if (keyLength <= 0 || keyPtr == IntPtr.Zero) continue;

                    byte[] keyBytes = new byte[keyLength];
                    Marshal.Copy(keyPtr, keyBytes, 0, keyLength);

                    string key = Encoding.UTF8.GetString(keyBytes);
                    if (key == "~local_player" || key.StartsWith("player_server_"))
                    {
                        IntPtr valPtr = leveldb_iter_value(iter, out IntPtr vlen);
                        int valLength = (int)vlen;
                        if (valLength <= 0 || valPtr == IntPtr.Zero) continue;

                        byte[] valBytes = new byte[valLength];
                        Marshal.Copy(valPtr, valBytes, 0, valLength);

                        results[key] = valBytes;
                    }
                }
            }
            finally
            {
                leveldb_iter_destroy(iter);
                leveldb_readoptions_destroy(readOptions);
            }

            return results;
        }

        public void Put(byte[] key, byte[] value)
        {
            if (_dbPtr == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeLevelDb));
            if (key == null || key.Length == 0)
                throw new ArgumentException("Key cannot be null or empty.", nameof(key));

            byte[] safeVal = (value == null || value.Length == 0) ? new byte[1] : value;
            int valLen = value?.Length ?? 0;

            IntPtr writeOptions = leveldb_writeoptions_create();
            try
            {
                leveldb_put(_dbPtr, writeOptions, key, (IntPtr)key.Length, safeVal, (IntPtr)valLen, out IntPtr errPtr);
                if (errPtr != IntPtr.Zero)
                {
                    try
                    {
                        string errMsg = Marshal.PtrToStringAnsi(errPtr) ?? "Unknown LevelDB write error";
                        throw new Exception(errMsg);
                    }
                    finally
                    {
                        leveldb_free(errPtr);
                    }
                }
            }
            finally
            {
                leveldb_writeoptions_destroy(writeOptions);
            }
        }

        public void Delete(byte[] key)
        {
            if (_dbPtr == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeLevelDb));
            if (key == null || key.Length == 0)
                return;

            IntPtr writeOptions = leveldb_writeoptions_create();
            try
            {
                leveldb_delete(_dbPtr, writeOptions, key, (IntPtr)key.Length, out IntPtr errPtr);
                if (errPtr != IntPtr.Zero)
                {
                    try
                    {
                        string errMsg = Marshal.PtrToStringAnsi(errPtr) ?? "Unknown LevelDB delete error";
                        throw new Exception(errMsg);
                    }
                    finally
                    {
                        leveldb_free(errPtr);
                    }
                }
            }
            finally
            {
                leveldb_writeoptions_destroy(writeOptions);
            }
        }

        public void Dispose()
        {
            if (_dbPtr != IntPtr.Zero)
            {
                leveldb_close(_dbPtr);
                _dbPtr = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }
}
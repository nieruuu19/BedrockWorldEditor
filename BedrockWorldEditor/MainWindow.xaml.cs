using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using fNbt;
using Path = System.IO.Path;

namespace BedrockWorldEditor
{
    public partial class MainWindow : Window
    {
        private string? _worldFolderPath;
        private string? _levelDatPath;
        private string? _dbPath;
        private NbtFile? _activeNbtFile;
        private NbtTag? _selectedTag;
        private TreeViewItem? _selectedTreeItem;
        private byte[] _levelDatHeader = new byte[8];
        private Dictionary<string, byte[]> _playerCache = new Dictionary<string, byte[]>();
        private string _currentSaveTarget = "";

        private NativeLevelDb? _db;

        // Offline Cache & API settings
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        private const string XblApiKey = "YOUR_API_KEY"; // Optional: xbl.io key (falls back to free mcprofile.io if not set)
        private readonly string _cacheFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gamertag_cache.json");
        private Dictionary<string, string> _gamertagCache = new Dictionary<string, string>();

        public MainWindow()
        {
            InitializeComponent();
            this.Closed += (_, _) => _db?.Dispose();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadGamertagCacheFromDisk();

            ComboAccounts.Items.Add("Scanning accounts...");
            ComboAccounts.SelectedIndex = 0;
            ComboAccounts.IsEnabled = false;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string usersPath = Path.Combine(appData, "Minecraft Bedrock", "Users");

            if (!Directory.Exists(usersPath))
            {
                ComboAccounts.Items.Clear();
                ComboAccounts.Items.Add("Users folder not found.");
                return;
            }

            var accountFolders = Directory.GetDirectories(usersPath);
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

                    if (!string.IsNullOrEmpty(xuid))
                    {
                        pendingOnlineChecks.Add((item, xuid, folderName));
                    }
                }
            }

            if (ComboAccounts.Items.Count > 0)
            {
                ComboAccounts.IsEnabled = true;
                ComboAccounts.SelectedIndex = 0;
            }
            else
            {
                ComboAccounts.Items.Add("No user folders found.");
                return;
            }

            if (HasInternetConnection())
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
                    string[] lines = File.ReadAllLines(optionsPath);
                    foreach (string line in lines)
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
                string? onlineGamertag = null;

                if (_gamertagCache.TryGetValue(xuid, out string? cached) && !string.IsNullOrWhiteSpace(cached))
                {
                    onlineGamertag = cached;
                }
                else
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
            if (!string.IsNullOrWhiteSpace(XblApiKey) && XblApiKey != "YOUR_API_KEY")
            {
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
                catch { }
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://mcprofile.io/api/v1/bedrock/fuid/{xuid}");
                request.Headers.Add("User-Agent", "BedrockWorldEditor/1.0");
                request.Headers.Add("Accept", "application/json");

                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("gamertag", out JsonElement gtEl))
                    {
                        string? gt = gtEl.GetString();
                        if (!string.IsNullOrWhiteSpace(gt)) return gt;
                    }
                }
            }
            catch { }

            return null;
        }

        private void ComboAccounts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _db?.Dispose();
            _db = null;

            WorldListBox.Items.Clear();
            PlayerListBox.Items.Clear();
            NbtTreeView.Items.Clear();
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
                            Tag = worldFolder,
                            Padding = new Thickness(4, 3, 4, 3)
                        };
                        WorldListBox.Items.Add(worldItem);
                    }
                }

                if (WorldListBox.Items.Count == 0)
                {
                    WorldListBox.Items.Add(new ListBoxItem { Content = "No worlds found", IsEnabled = false });
                }
            }
        }

        private void WorldListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WorldListBox.SelectedItem is ListBoxItem selectedWorld && selectedWorld.Tag is string worldFolderPath)
            {
                _worldFolderPath = worldFolderPath;
                _levelDatPath = Path.Combine(_worldFolderPath, "level.dat");
                _dbPath = Path.Combine(_worldFolderPath, "db");

                TxtWorldTabTitle.Text = $"{selectedWorld.Content}";
                TxtActiveIcon.Text = "📦";
                TxtActiveFile.Text = "Select player or level.dat";
                TxtStatusPath.Text = _worldFolderPath.Replace('\\', '/');
                BtnLevelDat.IsEnabled = true;
                BtnOpenFolder.IsEnabled = true;

                NbtTreeView.Items.Clear();
                LoadPlayersFromDatabase();
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_worldFolderPath) && Directory.Exists(_worldFolderPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _worldFolderPath,
                    UseShellExecute = true
                });
            }
        }

        private void BtnLevelDat_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_levelDatPath) || !File.Exists(_levelDatPath)) return;

            try
            {
                PlayerListBox.SelectedItem = null;

                using (FileStream fs = new FileStream(_levelDatPath, FileMode.Open, FileAccess.Read))
                {
                    fs.ReadExactly(_levelDatHeader, 0, 8);

                    _activeNbtFile = new NbtFile();
                    _activeNbtFile.BigEndian = false;
                    _activeNbtFile.LoadFromStream(fs, NbtCompression.None, null);
                }

                _currentSaveTarget = "level.dat";
                TxtActiveIcon.Text = "📦";
                TxtActiveFile.Text = "level.dat";
                TxtStatusLeft.Text = "Editing World Settings (level.dat)";
                RenderNbtTree(TxtSearch.Text);
                BtnSave.IsEnabled = true;
                BtnSaveBottom.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error reading level.dat: " + ex.Message);
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
                    PlayerListBox.Items.Add(kvp.Key);
                }

                TxtStatusLeft.Text = $"Loaded {_playerCache.Count} player record(s) from LevelDB";

                if (PlayerListBox.Items.Count > 0)
                {
                    PlayerListBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read LevelDB. Ensure Minecraft is closed.\n\nDetails: " + ex.Message);
            }
        }

        private void PlayerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlayerListBox.SelectedItem == null) return;

            string? selectedPlayer = PlayerListBox.SelectedItem.ToString();
            if (selectedPlayer == null || !_playerCache.ContainsKey(selectedPlayer)) return;

            string shortName = selectedPlayer.StartsWith("player_server_")
                ? "server_" + selectedPlayer.Substring("player_server_".Length)
                : selectedPlayer;

            TxtActiveIcon.Text = "👤";
            TxtActiveFile.Text = $"db/{shortName}";
            TxtStatusLeft.Text = $"Editing Player: {selectedPlayer}";
            _currentSaveTarget = selectedPlayer;

            byte[] playerData = _playerCache[selectedPlayer];
            _activeNbtFile = new NbtFile();
            _activeNbtFile.BigEndian = false;
            _activeNbtFile.LoadFromBuffer(playerData, 0, playerData.Length, NbtCompression.None, null);
            RenderNbtTree(TxtSearch.Text);
            BtnSave.IsEnabled = true;
            BtnSaveBottom.IsEnabled = true;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_activeNbtFile != null)
            {
                RenderNbtTree(TxtSearch.Text);
            }
        }

        private void RenderNbtTree(string filterText = "")
        {
            NbtTreeView.Items.Clear();
            if (_activeNbtFile?.RootTag != null)
            {
                TreeViewItem? rootNode = CreateUmeNode(_activeNbtFile.RootTag, filterText.Trim());
                if (rootNode != null)
                {
                    rootNode.IsExpanded = true;
                    NbtTreeView.Items.Add(rootNode);
                }
            }
        }

        private TreeViewItem? CreateUmeNode(NbtTag tag, string filter)
        {
            bool hasFilter = !string.IsNullOrEmpty(filter);
            bool selfMatches = !hasFilter ||
                               (!string.IsNullOrEmpty(tag.Name) && tag.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

            TreeViewItem node = new TreeViewItem { Tag = tag };

            if (tag is NbtCompound compound)
            {
                foreach (var child in compound.Tags)
                {
                    TreeViewItem? childNode = CreateUmeNode(child, hasFilter && !selfMatches ? filter : "");
                    if (childNode != null)
                    {
                        node.Items.Add(childNode);
                    }
                }

                if (hasFilter && !selfMatches && node.Items.Count == 0)
                    return null;

                if (hasFilter && node.Items.Count > 0)
                    node.IsExpanded = true;

                string label = string.IsNullOrEmpty(tag.Name)
                    ? $"{compound.Count} entries"
                    : $"{tag.Name}: {compound.Count} entries";

                node.Header = BuildUmeNodeHeader(tag.TagType, label, isContainer: true);
            }
            else if (tag is NbtList list)
            {
                foreach (var child in list)
                {
                    TreeViewItem? childNode = CreateUmeNode(child, hasFilter && !selfMatches ? filter : "");
                    if (childNode != null)
                    {
                        node.Items.Add(childNode);
                    }
                }

                if (hasFilter && !selfMatches && node.Items.Count == 0)
                    return null;

                if (hasFilter && node.Items.Count > 0)
                    node.IsExpanded = true;

                string label = string.IsNullOrEmpty(tag.Name)
                    ? $"{list.Count} entries"
                    : $"{tag.Name}: {list.Count} entries";

                node.Header = BuildUmeNodeHeader(tag.TagType, label, isContainer: true);
            }
            else
            {
                string valStr = tag.StringValue;
                if (hasFilter && !selfMatches && !valStr.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    return null;

                string label = string.IsNullOrEmpty(tag.Name)
                    ? valStr
                    : $"{tag.Name}: {valStr}";

                node.Header = BuildUmeNodeHeader(tag.TagType, label, isContainer: false);
            }

            return node;
        }

        private UIElement BuildUmeNodeHeader(NbtTagType tagType, string text, bool isContainer)
        {
            StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal };

            if (tagType == NbtTagType.Compound)
            {
                Border box = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromRgb(212, 155, 94)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(140, 92, 44)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                box.Child = new Rectangle
                {
                    Width = 4,
                    Height = 7,
                    Fill = new SolidColorBrush(Color.FromRgb(245, 226, 196)),
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
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(70, 80, 90)),
                    Margin = new Thickness(1, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(listIcon);
            }
            else
            {
                var (letter, color) = GetBadgeInfo(tagType);
                Border badge = new Border
                {
                    Width = 16,
                    Height = 18,
                    Background = new SolidColorBrush(Color.FromRgb(248, 249, 250)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(180, 188, 196)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                {
                    Text = letter,
                    Foreground = new SolidColorBrush(color),
                    FontWeight = FontWeights.Bold,
                    FontSize = 9.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(badge);
            }

            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(20, 24, 28)),
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
                NbtTagType.Short => ("S", Color.FromRgb(230, 126, 34)),
                NbtTagType.Int => ("I", Color.FromRgb(41, 128, 185)),
                NbtTagType.Long => ("L", Color.FromRgb(142, 68, 173)),
                NbtTagType.Float => ("F", Color.FromRgb(39, 174, 96)),
                NbtTagType.Double => ("D", Color.FromRgb(22, 160, 133)),
                NbtTagType.String => ("T", Color.FromRgb(97, 110, 124)),
                _ => ("[]", Color.FromRgb(82, 96, 109))
            };
        }

        private void NbtTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (NbtTreeView.SelectedItem is TreeViewItem item && item.Tag is NbtTag tag)
            {
                _selectedTag = tag;
                _selectedTreeItem = item;

                if (tag.TagType == NbtTagType.String || tag.TagType == NbtTagType.Int ||
                    tag.TagType == NbtTagType.Byte || tag.TagType == NbtTagType.Float ||
                    tag.TagType == NbtTagType.Double || tag.TagType == NbtTagType.Short ||
                    tag.TagType == NbtTagType.Long)
                {
                    TxtSelectedTagName.Text = $"{(string.IsNullOrEmpty(tag.Name) ? tag.TagType.ToString() : tag.Name)}:";
                    TxtEditValue.Text = tag.StringValue;
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
        }

        private void TxtEditValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnApply.IsEnabled)
            {
                BtnApply_Click(sender, e);
            }
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTag == null) return;

            if (_selectedTag is NbtString strTag) strTag.Value = TxtEditValue.Text;
            else if (_selectedTag is NbtByte byteTag && byte.TryParse(TxtEditValue.Text, out byte bVal)) byteTag.Value = bVal;
            else if (_selectedTag is NbtShort shortTag && short.TryParse(TxtEditValue.Text, out short sVal)) shortTag.Value = sVal;
            else if (_selectedTag is NbtInt intTag && int.TryParse(TxtEditValue.Text, out int iVal)) intTag.Value = iVal;
            else if (_selectedTag is NbtLong longTag && long.TryParse(TxtEditValue.Text, out long lVal)) longTag.Value = lVal;
            else if (_selectedTag is NbtFloat floatTag && float.TryParse(TxtEditValue.Text, out float fVal)) floatTag.Value = fVal;
            else if (_selectedTag is NbtDouble doubleTag && double.TryParse(TxtEditValue.Text, out double dVal)) doubleTag.Value = dVal;
            else
            {
                MessageBox.Show($"Invalid value for {_selectedTag.TagType}.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_selectedTreeItem != null)
            {
                string label = string.IsNullOrEmpty(_selectedTag.Name)
                    ? _selectedTag.StringValue
                    : $"{_selectedTag.Name}: {_selectedTag.StringValue}";
                _selectedTreeItem.Header = BuildUmeNodeHeader(_selectedTag.TagType, label, isContainer: false);
            }

            TxtStatusLeft.Text = $"Updated '{_selectedTag.Name}' to {_selectedTag.StringValue} (Click Save to write to disk)";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSaveTarget == "level.dat" && _levelDatPath != null && _activeNbtFile != null)
            {
                using (FileStream fs = new FileStream(_levelDatPath, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(_levelDatHeader, 0, 8);
                    _activeNbtFile.SaveToStream(fs, NbtCompression.None);
                }
                TxtStatusLeft.Text = "Saved level.dat successfully!";
                MessageBox.Show("level.dat saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (_db != null && _activeNbtFile != null)
            {
                byte[] newNbtBytes = _activeNbtFile.SaveToBuffer(NbtCompression.None);
                _db.Put(Encoding.UTF8.GetBytes(_currentSaveTarget), newNbtBytes);
                _playerCache[_currentSaveTarget] = newNbtBytes;
                TxtStatusLeft.Text = $"Saved {_currentSaveTarget} to LevelDB!";
                MessageBox.Show($"{_currentSaveTarget} saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private IntPtr _dbPtr;

        private NativeLevelDb(IntPtr dbPtr)
        {
            _dbPtr = dbPtr;
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
                    string errMsg = Marshal.PtrToStringAnsi(errPtr) ?? "Unknown LevelDB open error";
                    throw new Exception(errMsg);
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
            var results = new Dictionary<string, byte[]>();
            IntPtr readOptions = leveldb_readoptions_create();
            IntPtr iter = leveldb_create_iterator(_dbPtr, readOptions);

            try
            {
                for (leveldb_iter_seek_to_first(iter); leveldb_iter_valid(iter) != 0; leveldb_iter_next(iter))
                {
                    IntPtr keyPtr = leveldb_iter_key(iter, out IntPtr klen);
                    int keyLength = (int)klen;
                    byte[] keyBytes = new byte[keyLength];
                    Marshal.Copy(keyPtr, keyBytes, 0, keyLength);

                    string key = Encoding.UTF8.GetString(keyBytes);
                    if (key == "~local_player" || key.StartsWith("player_server_"))
                    {
                        IntPtr valPtr = leveldb_iter_value(iter, out IntPtr vlen);
                        int valLength = (int)vlen;
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
            IntPtr writeOptions = leveldb_writeoptions_create();
            try
            {
                leveldb_put(_dbPtr, writeOptions, key, (IntPtr)key.Length, value, (IntPtr)value.Length, out IntPtr errPtr);
                if (errPtr != IntPtr.Zero)
                {
                    string errMsg = Marshal.PtrToStringAnsi(errPtr) ?? "Unknown LevelDB write error";
                    throw new Exception(errMsg);
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
        }
    }
}
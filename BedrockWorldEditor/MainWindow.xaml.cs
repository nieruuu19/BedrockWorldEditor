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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using fNbt;
using Path = System.IO.Path;
using WpfPath = System.Windows.Shapes.Path;

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
        private sealed class TargetViewState
        {
            public HashSet<string> ExpandedPaths { get; } = new HashSet<string>();
            public string? SelectedPath { get; set; }
            public double VerticalOffset { get; set; }
            public double HorizontalOffset { get; set; }
        }

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
        private bool _suppressAccountWorldEvents;
        private ComboBoxItem? _previousAccountItem;
        private ListBoxItem? _previousWorldItem;
        private bool _rightClickHitValidNode;
        private bool _rightClickHitValidPlayer;
        private byte[] _levelDatHeader = new byte[8];
        private string _currentSaveTarget = "";

        // In-Memory Session Cache: Zero persistent file/folder locks!
        // LevelDB and level.dat are read into memory and immediately closed so the world folder is never locked while editing!
        private readonly Dictionary<string, byte[]> _playerCache = new Dictionary<string, byte[]>();
        private readonly Dictionary<string, NbtFile> _openNbtFiles = new Dictionary<string, NbtFile>();
        private readonly HashSet<string> _modifiedTargets = new HashSet<string>();
        private readonly HashSet<string> _deletedPlayers = new HashSet<string>();
        private readonly Dictionary<string, Stack<NbtCompound>> _targetUndoStacks = new Dictionary<string, Stack<NbtCompound>>();
        private readonly Dictionary<string, TargetViewState> _targetViewStates = new Dictionary<string, TargetViewState>();
        private const int MaxUndoSteps = 30;

        // Active Inline Editor State (Double-Click to Edit Value or Rename Tag in-place, plus newly created tag naming)
        private TreeViewItem? _inlineEditingTreeItem;
        private TextBox? _inlineEditingTextBox;
        private Button? _inlineEditingOkButton;
        private bool _inlineEditingIsRename;
        private string? _inlineEditingFallbackNewTagName;
        private bool _isCommittingInlineEdit;

        // UME Save / Error Overlay & Active Player Steve Icon Controls
        private UniformGrid _activePlayerSteveIcon = null!;
        private Grid _saveModalOverlay = null!;
        private Border _savingProgressBox = null!;
        private Border _savingProgressBarFill = null!;
        private Border _saveCompleteBox = null!;
        private Border _saveCompleteTopBar = null!;
        private TextBlock _saveCompleteTitleText = null!;
        private TextBlock _saveCompleteBodyText = null!;
        private Button _btnSaveCompleteOk = null!;

        // UME Row & Selection Brushes (Frozen for maximum UI thread performance)
        private static readonly SolidColorBrush UmeStripeWhiteBrush = CreateFrozenBrush(255, 255, 255);
        private static readonly SolidColorBrush UmeStripeGreyBrush = CreateFrozenBrush(243, 244, 246);
        private static readonly SolidColorBrush UmeSelectedLightBlueBrush = CreateFrozenBrush(203, 228, 249);
        private static readonly SolidColorBrush UmeHoverDeepBlueBrush = CreateFrozenBrush(140, 195, 242);
        private static readonly SolidColorBrush UmeModalMintGreenBrush = CreateFrozenBrush(76, 195, 155);
        private static readonly SolidColorBrush UmeModalErrorRedBrush = CreateFrozenBrush(220, 91, 87);
        private static readonly Thickness UnselectedFlagThickness = new Thickness(0);
        private static readonly Thickness SelectedFlagThickness = new Thickness(1);
        private Style? _umeTreeViewItemStyle;

        // Cross-Player / Cross-World Multi-Tag NBT Clipboard
        private static readonly List<NbtTag> _nbtClipboard = new List<NbtTag>();
        private static bool _clipboardIsRootCompound;

        // Offline Cache & OpenXBL API settings
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        private const string XblApiKey = "YOUR_OPENXBL_API_KEY_HERE";
        private readonly string _cacheFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gamertag_cache.json");
        private Dictionary<string, string> _gamertagCache = new Dictionary<string, string>();

        // 64 Exact Hex Colors for the 8x8 Steve Face Icon
        private static readonly string[] SteveFaceHexPixels =
        {
            "#2E1F14","#2B1D12","#2F2015","#281B11","#24180F","#281B11","#2D1E13","#2A1C12",
            "#2A1C12","#2F2015","#2F2015","#332317","#422C1D","#3A271A","#2C1D13","#261910",
            "#2D1E13","#B6896C","#BD8E70","#C29375","#B88A6C","#BD8E70","#AC7E61","#342317",
            "#AA7D60","#B58668","#B7886A","#B28466","#AD8063","#B68769","#A5775B","#9C7055",
            "#B48668","#FFFFFF","#23199B","#B58668","#BB8C6E","#23199B","#FFFFFF","#AA7D60",
            "#9C6F54","#B38366","#B7896C","#6A4030","#653D2E","#B88A6C","#A27458","#8E634A",
            "#906349","#96674C","#46221A","#84493A","#84493A","#46221A","#8F6147","#80553E",
            "#81553D","#7D523B","#442119","#411F17","#442119","#47231B","#83573E","#7A5039"
        };

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
            InitializeSaveOverlayControls();
        }

        private static UniformGrid CreateSteveHeadUniformGrid(double size)
        {
            var grid = new UniformGrid
            {
                Rows = 8,
                Columns = 8,
                Width = size,
                Height = size,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };

            foreach (string hex in SteveFaceHexPixels)
            {
                Color c = (Color)ColorConverter.ConvertFromString(hex);
                grid.Children.Add(new Rectangle { Fill = CreateFrozenBrush(c.R, c.G, c.B) });
            }

            return grid;
        }

        private void InitializeSaveOverlayControls()
        {
            _activePlayerSteveIcon = CreateSteveHeadUniformGrid(14);
            _activePlayerSteveIcon.Visibility = Visibility.Collapsed;
            if (TxtActiveIcon.Parent is Panel activeTargetPanel)
            {
                activeTargetPanel.Children.Insert(0, _activePlayerSteveIcon);
            }
            TxtActiveIcon.Visibility = Visibility.Collapsed;
            TxtActiveFile.Text = "";

            _saveModalOverlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(38, 0, 0, 0)),
                Visibility = Visibility.Collapsed
            };
            Grid.SetRowSpan(_saveModalOverlay, 3);

            // Box 1: UME "Saving / Please wait..."
            _savingProgressBox = new Border
            {
                Width = 370,
                Height = 124,
                Background = CreateFrozenBrush(234, 234, 234),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.38, Color = Colors.Black }
            };

            Grid savingGrid = new Grid();
            savingGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            savingGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

            StackPanel savingTopPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 4, 20, 0)
            };
            savingTopPanel.Children.Add(new TextBlock
            {
                Text = "Saving",
                FontSize = 15.5,
                Foreground = CreateFrozenBrush(17, 17, 17),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            });

            Border barTrack = new Border
            {
                Height = 12,
                Background = CreateFrozenBrush(212, 212, 212)
            };
            Grid barClipGrid = new Grid { ClipToBounds = true };
            _savingProgressBarFill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
                Background = UmeModalMintGreenBrush
            };
            barClipGrid.Children.Add(_savingProgressBarFill);
            barTrack.Child = barClipGrid;
            savingTopPanel.Children.Add(barTrack);
            Grid.SetRow(savingTopPanel, 0);
            savingGrid.Children.Add(savingTopPanel);

            Border savingFooter = new Border
            {
                Background = CreateFrozenBrush(54, 60, 66),
                Padding = new Thickness(18, 0, 18, 0)
            };
            savingFooter.Child = new TextBlock
            {
                Text = "Please wait...",
                Foreground = CreateFrozenBrush(204, 204, 204),
                FontStyle = FontStyles.Italic,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(savingFooter, 1);
            savingGrid.Children.Add(savingFooter);
            _savingProgressBox.Child = savingGrid;

            // Box 2: UME Result Modal ("Save complete / The world has been saved." OR "Error / The world could not be saved!")
            _saveCompleteBox = new Border
            {
                Width = 284,
                Height = 138,
                Background = CreateFrozenBrush(234, 234, 234),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.38, Color = Colors.Black }
            };

            Grid completeGrid = new Grid();
            completeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            completeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            completeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });

            _saveCompleteTopBar = new Border { Background = UmeModalMintGreenBrush };
            Grid.SetRow(_saveCompleteTopBar, 0);
            completeGrid.Children.Add(_saveCompleteTopBar);

            StackPanel completeBody = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 20, 0)
            };
            _saveCompleteTitleText = new TextBlock
            {
                Text = "Save complete",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _saveCompleteBodyText = new TextBlock
            {
                Text = "The world has been saved.",
                FontSize = 13.5,
                Foreground = CreateFrozenBrush(17, 17, 17)
            };
            completeBody.Children.Add(_saveCompleteTitleText);
            completeBody.Children.Add(_saveCompleteBodyText);
            Grid.SetRow(completeBody, 1);
            completeGrid.Children.Add(completeBody);

            Border completeFooter = new Border
            {
                Background = CreateFrozenBrush(54, 60, 66),
                Padding = new Thickness(10, 0, 10, 0)
            };
            _btnSaveCompleteOk = new Button
            {
                Content = "Ok",
                Width = 84,
                Height = 26,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = CreateFrozenBrush(53, 152, 219),
                Foreground = Brushes.White,
                FontSize = 13.5,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            _btnSaveCompleteOk.Click += BtnSaveCompleteOk_Click;
            completeFooter.Child = _btnSaveCompleteOk;
            Grid.SetRow(completeFooter, 2);
            completeGrid.Children.Add(completeFooter);
            _saveCompleteBox.Child = completeGrid;

            _saveModalOverlay.Children.Add(_savingProgressBox);
            _saveModalOverlay.Children.Add(_saveCompleteBox);

            if (this.Content is Grid rootWindowGrid)
            {
                rootWindowGrid.Children.Add(_saveModalOverlay);
            }
        }

        private void ShowUmeSaveResultModal(bool isSuccess, string? customMessage = null)
        {
            _savingProgressBox.Visibility = Visibility.Collapsed;

            if (isSuccess)
            {
                _saveCompleteTopBar.Background = UmeModalMintGreenBrush;
                _saveCompleteTitleText.Text = "Save complete";
                _saveCompleteBodyText.Text = customMessage ?? "The world has been saved.";
            }
            else
            {
                // Matches your exact UME Error screenshot: Red top bar (#DC5B57), "Error" header, "The world could not be saved!" body
                _saveCompleteTopBar.Background = UmeModalErrorRedBrush;
                _saveCompleteTitleText.Text = "Error";
                _saveCompleteBodyText.Text = customMessage ?? "The world could not be saved!";
            }

            _saveCompleteBox.Visibility = Visibility.Visible;
            _saveModalOverlay.Visibility = Visibility.Visible;
            _btnSaveCompleteOk.Focus();
        }

        // ==================== EXACT UME TAG TYPE ORDERING ====================

        private static int GetUmeTagTypePriority(NbtTagType type)
        {
            return type switch
            {
                NbtTagType.Compound => 0,
                NbtTagType.List => 1,
                NbtTagType.Byte => 2,
                NbtTagType.Short => 3,
                NbtTagType.Int => 4,
                NbtTagType.Long => 5,
                NbtTagType.Float => 6,
                NbtTagType.Double => 7,
                NbtTagType.ByteArray => 8,
                NbtTagType.IntArray => 9,
                NbtTagType.String => 10,
                _ => 11
            };
        }

        private static IEnumerable<NbtTag> GetUmeSortedCompoundChildren(NbtCompound compound)
        {
            return compound.Tags
                .OrderBy(t => GetUmeTagTypePriority(t.TagType))
                .ThenBy(t => t.Name ?? "", StringComparer.OrdinalIgnoreCase);
        }

        private Stack<NbtCompound> CurrentUndoStack
        {
            get
            {
                string key = string.IsNullOrEmpty(_currentSaveTarget) ? "__default__" : _currentSaveTarget;
                if (!_targetUndoStacks.TryGetValue(key, out var stack))
                {
                    stack = new Stack<NbtCompound>();
                    _targetUndoStacks[key] = stack;
                }
                return stack;
            }
        }

        private bool HasUnsavedWorldChanges => _modifiedTargets.Count > 0 || _deletedPlayers.Count > 0;

        private void MarkCurrentTargetModified()
        {
            if (!string.IsNullOrEmpty(_currentSaveTarget))
            {
                _modifiedTargets.Add(_currentSaveTarget);
            }
        }

        private void SaveCurrentTargetViewState()
        {
            if (string.IsNullOrEmpty(_currentSaveTarget) || NbtTreeView.Items.Count == 0)
                return;

            var state = new TargetViewState
            {
                SelectedPath = _selectedTreeItem != null ? GetTreeItemPath(_selectedTreeItem) : null
            };

            foreach (var item in NbtTreeView.Items)
            {
                if (item is TreeViewItem rootItem)
                {
                    CollectExpandedPaths(rootItem, state.ExpandedPaths);
                }
            }

            ScrollViewer? sv = GetTreeViewScrollViewer();
            if (sv != null)
            {
                state.VerticalOffset = sv.VerticalOffset;
                state.HorizontalOffset = sv.HorizontalOffset;
            }

            _targetViewStates[_currentSaveTarget] = state;
        }

        private void RestoreTargetViewStateIfAvailable(string targetKey)
        {
            if (!_targetViewStates.TryGetValue(targetKey, out TargetViewState? state) || NbtTreeView.Items.Count == 0)
                return;

            if (NbtTreeView.Items[0] is TreeViewItem rootItem)
            {
                _suppressTreeEvents = true;
                try
                {
                    RestoreExpandedAndSelectedStates(rootItem, state.ExpandedPaths, state.SelectedPath);
                }
                finally
                {
                    _suppressTreeEvents = false;
                }
                UpdateNbtTreeZebraStripes();
            }

            NbtTreeView.UpdateLayout();
            ScrollViewer? sv = GetTreeViewScrollViewer();
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(state.VerticalOffset);
                sv.ScrollToHorizontalOffset(state.HorizontalOffset);
                NbtStripeTransform.Y = -(state.VerticalOffset % 44.0);

                double vOff = state.VerticalOffset;
                double hOff = state.HorizontalOffset;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    sv.ScrollToVerticalOffset(vOff);
                    sv.ScrollToHorizontalOffset(hOff);
                    NbtStripeTransform.Y = -(vOff % 44.0);
                }));
            }
        }

        private bool PromptSaveOrDiscardWorldChanges()
        {
            CommitInlineEdit(saveChanges: true);

            if (!HasUnsavedWorldChanges)
                return true;

            string worldName = string.IsNullOrWhiteSpace(TxtWorldTabTitle.Text) || TxtWorldTabTitle.Text == "No World Selected"
                ? "this world"
                : $"'{TxtWorldTabTitle.Text}'";

            MessageBoxResult result = MessageBox.Show(
                $"You have unsaved changes in {worldName}.\n\nDo you want to save your changes before leaving this world?",
                "Unsaved World Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
                return false;

            if (result == MessageBoxResult.Yes)
            {
                if (!CommitAllWorldChangesToDiskSynchronously())
                {
                    ShowUmeSaveResultModal(isSuccess: false);
                    return false;
                }
            }

            return true;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!PromptSaveOrDiscardWorldChanges())
            {
                e.Cancel = true;
            }
        }

        private void BtnTogglePlayersSection_Click(object sender, RoutedEventArgs e)
        {
            PlayerListContainerBorder.Visibility = PlayerListContainerBorder.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
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

        private ScrollViewer? GetTreeViewScrollViewer()
        {
            if (NbtTreeView.Template?.FindName("PART_ScrollViewer", NbtTreeView) is ScrollViewer sv)
                return sv;

            if (VisualTreeHelper.GetChildrenCount(NbtTreeView) > 0 &&
                VisualTreeHelper.GetChild(NbtTreeView, 0) is ScrollViewer childSv)
                return childSv;

            return null;
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

        // ==================== INLINE TAG VALUE EDITING & TAG RENAMING (WITH INLINE "OK" BUTTON & DYNAMIC RE-SORT!) ====================

        private void BeginInlineEdit(TreeViewItem item, bool renameMode, string? fallbackNewTagName = null)
        {
            if (item.Tag is not NbtTag tag) return;

            if (renameMode)
            {
                if (IsRootTreeItem(item)) return;
                ItemsControl? parentCtrl = ItemsControl.ItemsControlFromItemContainer(item);
                NbtTag? parentTag = (parentCtrl as TreeViewItem)?.Tag as NbtTag ?? tag.Parent;
                if (parentTag is NbtList) return;
            }
            else
            {
                if (tag is NbtByteArray || tag is NbtIntArray)
                {
                    OpenUmeHexEditorModal(tag, item, isNewTag: false);
                    return;
                }
                if (!IsPrimitiveEditableTag(tag.TagType)) return;
            }

            CommitInlineEdit(saveChanges: true);

            _inlineEditingTreeItem = item;
            _inlineEditingIsRename = renameMode;
            _inlineEditingFallbackNewTagName = fallbackNewTagName;

            StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal };
            AddNodeIconToPanel(panel, tag.TagType);

            _inlineEditingOkButton = new Button
            {
                Content = "OK",
                Height = 19,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(4, 0, 4, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Background = CreateFrozenBrush(235, 238, 242),
                BorderBrush = CreateFrozenBrush(160, 170, 180),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Focusable = false
            };
            _inlineEditingOkButton.Click += (_, _) =>
            {
                TreeViewItem? edited = _inlineEditingTreeItem;
                CommitInlineEdit(saveChanges: true, explicitConfirm: true);
                edited?.Focus();
            };

            if (renameMode)
            {
                string initialText = fallbackNewTagName != null ? "" : (tag.Name ?? "");

                _inlineEditingTextBox = new TextBox
                {
                    Text = initialText,
                    MinWidth = 95,
                    Height = 19,
                    Padding = new Thickness(3, 0, 3, 0),
                    FontSize = 12.5,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    BorderBrush = CreateFrozenBrush(41, 128, 185),
                    BorderThickness = new Thickness(1)
                };

                panel.Children.Add(_inlineEditingTextBox);
                panel.Children.Add(_inlineEditingOkButton);

                if (fallbackNewTagName == null)
                {
                    string suffixValue = GetSafeTagValueString(tag);
                    panel.Children.Add(new TextBlock
                    {
                        Text = $": {suffixValue}",
                        Foreground = CreateFrozenBrush(20, 24, 28),
                        FontWeight = (tag is NbtCompound || tag is NbtList) ? FontWeights.Medium : FontWeights.Normal,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(tag.Name))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"{tag.Name}: ",
                        Foreground = CreateFrozenBrush(20, 24, 28),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                _inlineEditingTextBox = new TextBox
                {
                    Text = GetSafeTagValueString(tag),
                    MinWidth = 85,
                    Height = 19,
                    Padding = new Thickness(3, 0, 3, 0),
                    FontSize = 12.5,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    BorderBrush = CreateFrozenBrush(41, 128, 185),
                    BorderThickness = new Thickness(1)
                };

                panel.Children.Add(_inlineEditingTextBox);
                panel.Children.Add(_inlineEditingOkButton);
            }

            _inlineEditingTextBox.KeyDown += InlineEditingTextBox_KeyDown;
            _inlineEditingTextBox.LostKeyboardFocus += InlineEditingTextBox_LostKeyboardFocus;

            item.Header = panel;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_inlineEditingTextBox != null)
                {
                    _inlineEditingTextBox.Focus();
                    _inlineEditingTextBox.SelectAll();
                }
            }));
        }

        private void InlineEditingTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TreeViewItem? editedItem = _inlineEditingTreeItem;
                CommitInlineEdit(saveChanges: true, explicitConfirm: true);
                editedItem?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                TreeViewItem? editedItem = _inlineEditingTreeItem;
                CommitInlineEdit(saveChanges: false, explicitConfirm: false);
                editedItem?.Focus();
                e.Handled = true;
            }
        }

        private void InlineEditingTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CommitInlineEdit(saveChanges: true, explicitConfirm: false);
        }

        private void CommitInlineEdit(bool saveChanges, bool explicitConfirm = false)
        {
            if (_isCommittingInlineEdit || _inlineEditingTreeItem == null || _inlineEditingTextBox == null)
                return;

            _isCommittingInlineEdit = true;
            TreeViewItem item = _inlineEditingTreeItem;
            TextBox textBox = _inlineEditingTextBox;
            bool isRename = _inlineEditingIsRename;
            string? fallbackNewName = _inlineEditingFallbackNewTagName;

            _inlineEditingTreeItem = null;
            _inlineEditingTextBox = null;
            _inlineEditingOkButton = null;
            _inlineEditingFallbackNewTagName = null;

            try
            {
                textBox.KeyDown -= InlineEditingTextBox_KeyDown;
                textBox.LostKeyboardFocus -= InlineEditingTextBox_LostKeyboardFocus;

                if (item.Tag is NbtTag tag)
                {
                    if (isRename)
                    {
                        string enteredName = textBox.Text.Trim();
                        string oldName = tag.Name ?? "";

                        if (fallbackNewName != null)
                        {
                            ItemsControl? parentCtrl = ItemsControl.ItemsControlFromItemContainer(item);
                            TreeViewItem? parentTreeItem = parentCtrl as TreeViewItem;
                            NbtCompound? parentCompound = parentTreeItem?.Tag as NbtCompound ?? tag.Parent as NbtCompound;

                            if (!saveChanges || (!explicitConfirm && string.IsNullOrEmpty(enteredName)))
                            {
                                if (parentCompound != null && !string.IsNullOrEmpty(oldName))
                                {
                                    parentCompound.Remove(oldName);
                                }
                                if (parentTreeItem != null)
                                {
                                    parentTreeItem.Items.Remove(item);
                                    RefreshNodeHeader(parentTreeItem);
                                    UpdateNbtTreeZebraStripes();
                                    SelectSingleTreeItem(parentTreeItem);
                                }
                                return;
                            }

                            string finalName = !string.IsNullOrEmpty(enteredName) ? enteredName : fallbackNewName;

                            if (parentCompound != null)
                            {
                                if (finalName != oldName && parentCompound.Contains(finalName))
                                {
                                    finalName = GenerateUniqueTagName(parentCompound, finalName);
                                }

                                PushUndoSnapshot();

                                var allTags = parentCompound.Tags.ToList();
                                parentCompound.Clear();
                                foreach (var child in allTags)
                                {
                                    if (ReferenceEquals(child, tag))
                                    {
                                        child.Name = finalName;
                                    }
                                    parentCompound.Add(child);
                                }

                                MarkCurrentTargetModified();
                                RefreshNodeHeader(item);
                                if (parentTreeItem != null)
                                {
                                    ResortChildTreeItems(parentTreeItem);
                                    RefreshNodeHeader(parentTreeItem);
                                }
                                if (tag is NbtCompound)
                                {
                                    item.IsExpanded = true;
                                }
                                UpdateNbtTreeZebraStripes();
                                SelectSingleTreeItem(item);
                                TxtStatusLeft.Text = $"Added {tag.TagType} tag '{finalName}' (Ctrl+Z to undo)";
                            }
                            return;
                        }
                        else if (saveChanges && !string.IsNullOrEmpty(enteredName) && !string.Equals(enteredName, oldName, StringComparison.Ordinal))
                        {
                            ItemsControl? parentCtrl = ItemsControl.ItemsControlFromItemContainer(item);
                            TreeViewItem? parentTreeItem = parentCtrl as TreeViewItem;
                            NbtCompound? parentCompound = parentTreeItem?.Tag as NbtCompound ?? tag.Parent as NbtCompound;

                            if (parentCompound != null && parentCompound.Contains(enteredName))
                            {
                                MessageBox.Show($"A tag named '{enteredName}' already exists in this compound.",
                                                "Duplicate Tag Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                            else
                            {
                                PushUndoSnapshot();

                                if (parentCompound != null)
                                {
                                    var allTags = parentCompound.Tags.ToList();
                                    parentCompound.Clear();
                                    foreach (var child in allTags)
                                    {
                                        if (ReferenceEquals(child, tag))
                                        {
                                            child.Name = enteredName;
                                        }
                                        parentCompound.Add(child);
                                    }
                                }
                                else
                                {
                                    tag.Name = enteredName;
                                }

                                MarkCurrentTargetModified();
                                RefreshNodeHeader(item);

                                if (parentTreeItem != null)
                                {
                                    ResortChildTreeItems(parentTreeItem);
                                }

                                UpdateNbtTreeZebraStripes();
                                SelectSingleTreeItem(item);
                                item.BringIntoView();
                                TxtStatusLeft.Text = $"Renamed tag '{oldName}' to '{enteredName}' (Ctrl+Z to undo)";
                                return;
                            }
                        }
                    }
                    else if (saveChanges)
                    {
                        string rawText = textBox.Text;
                        if (TryApplyPrimitiveTagValue(tag, rawText))
                        {
                            if (ItemsControl.ItemsControlFromItemContainer(item) is TreeViewItem parentTreeItem)
                            {
                                RefreshNodeHeader(parentTreeItem);
                            }
                            TxtStatusLeft.Text = $"Updated '{(string.IsNullOrEmpty(tag.Name) ? tag.TagType.ToString() : tag.Name)}' to {GetSafeTagValueString(tag)} (Ctrl+Z to undo)";
                        }
                    }
                }

                RefreshNodeHeader(item);
                UpdateValueEditorBar();
            }
            finally
            {
                _isCommittingInlineEdit = false;
            }
        }

        private void ResortChildTreeItems(TreeViewItem parentTreeItem)
        {
            if (parentTreeItem.Tag is not NbtCompound) return;

            var sortedNodes = parentTreeItem.Items.Cast<TreeViewItem>()
                .OrderBy(node => node.Tag is NbtTag t ? GetUmeTagTypePriority(t.TagType) : 99)
                .ThenBy(node => (node.Tag as NbtTag)?.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();

            _suppressTreeEvents = true;
            try
            {
                parentTreeItem.Items.Clear();
                foreach (var node in sortedNodes)
                {
                    parentTreeItem.Items.Add(node);
                }
            }
            finally
            {
                _suppressTreeEvents = false;
            }
        }

        private void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTreeItem != null)
            {
                BeginInlineEdit(_selectedTreeItem, renameMode: true);
            }
        }

        private void MenuEditValue_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTreeItem != null)
            {
                BeginInlineEdit(_selectedTreeItem, renameMode: false);
            }
        }

        // ==================== 3-COLUMN UME HEX EDITOR DIALOG (FOR BOTH BYTEARRAY & INTARRAY TAGS!) ====================

        private bool OpenUmeHexEditorModal(NbtTag arrayTag, TreeViewItem? existingNode, bool isNewTag, NbtCompound? targetParentCompound = null)
        {
            Window hexWin = new Window
            {
                Title = "Hex Editor",
                Width = 560,
                Height = 330,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = CreateFrozenBrush(245, 246, 247),
                FontFamily = new FontFamily("Segoe UI")
            };

            Grid rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

            Border editorBorder = new Border
            {
                Margin = new Thickness(8, 8, 8, 4),
                BorderBrush = CreateFrozenBrush(180, 186, 194),
                BorderThickness = new Thickness(1),
                Background = Brushes.White
            };

            Grid splitGrid = new Grid();
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });

            byte[] initialBytes = Array.Empty<byte>();
            if (arrayTag is NbtByteArray ba && ba.Value != null)
            {
                initialBytes = ba.Value;
            }
            else if (arrayTag is NbtIntArray ia && ia.Value != null && ia.Value.Length > 0)
            {
                initialBytes = new byte[ia.Value.Length * 4];
                Buffer.BlockCopy(ia.Value, 0, initialBytes, 0, initialBytes.Length);
            }

            TextBox offsetPreviewBox = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                IsReadOnly = true,
                Foreground = CreateFrozenBrush(82, 96, 109),
                Background = CreateFrozenBrush(248, 249, 250),
                Text = FormatHexOffsets(initialBytes.Length)
            };

            Border dividerLeft = new Border { Background = CreateFrozenBrush(218, 222, 228) };

            TextBox hexTextBox = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = FormatBytesToHex(initialBytes)
            };

            Border dividerRight = new Border { Background = CreateFrozenBrush(210, 215, 220) };

            TextBox asciiPreviewBox = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Foreground = CreateFrozenBrush(60, 68, 76),
                Background = CreateFrozenBrush(250, 251, 252),
                Text = FormatBytesToAscii(initialBytes)
            };

            hexTextBox.TextChanged += (_, _) =>
            {
                if (TryParseHexToBytes(hexTextBox.Text, out byte[] parsed))
                {
                    offsetPreviewBox.Text = FormatHexOffsets(parsed.Length);
                    asciiPreviewBox.Text = FormatBytesToAscii(parsed);
                }
            };

            Grid.SetColumn(offsetPreviewBox, 0);
            Grid.SetColumn(dividerLeft, 1);
            Grid.SetColumn(hexTextBox, 2);
            Grid.SetColumn(dividerRight, 3);
            Grid.SetColumn(asciiPreviewBox, 4);
            splitGrid.Children.Add(offsetPreviewBox);
            splitGrid.Children.Add(dividerLeft);
            splitGrid.Children.Add(hexTextBox);
            splitGrid.Children.Add(dividerRight);
            splitGrid.Children.Add(asciiPreviewBox);
            editorBorder.Child = splitGrid;
            Grid.SetRow(editorBorder, 0);
            rootGrid.Children.Add(editorBorder);

            // Bottom Bar: Name: [TextBox] [OK]
            StackPanel bottomBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 6)
            };

            TextBlock nameLbl = new TextBlock
            {
                Text = "Name:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 12.5,
                Foreground = CreateFrozenBrush(30, 34, 38)
            };

            TextBox nameBox = new TextBox
            {
                Width = 115,
                Height = 23,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 4, 0),
                FontSize = 12.5,
                Text = isNewTag ? "" : (arrayTag.Name ?? ""),
                IsEnabled = targetParentCompound != null || !string.IsNullOrEmpty(arrayTag.Name)
            };

            Button okBtn = new Button
            {
                Content = "OK",
                Width = 72,
                Height = 23,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
            };

            okBtn.Click += (_, _) =>
            {
                if (!TryParseHexToBytes(hexTextBox.Text, out byte[] parsedBytes))
                {
                    MessageBox.Show("Invalid hex byte sequence. Use pairs of hex digits (e.g., 00 FF 1A).",
                                    "Hex Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!isNewTag)
                {
                    PushUndoSnapshot();
                }

                if (arrayTag is NbtByteArray byteArrTag)
                {
                    byteArrTag.Value = parsedBytes;
                }
                else if (arrayTag is NbtIntArray intArrTag)
                {
                    if (parsedBytes.Length == 0)
                    {
                        intArrTag.Value = Array.Empty<int>();
                    }
                    else
                    {
                        int intCount = (parsedBytes.Length + 3) / 4;
                        byte[] padded = new byte[intCount * 4];
                        Buffer.BlockCopy(parsedBytes, 0, padded, 0, parsedBytes.Length);
                        int[] ints = new int[intCount];
                        Buffer.BlockCopy(padded, 0, ints, 0, padded.Length);
                        intArrTag.Value = ints;
                    }
                }

                if (nameBox.IsEnabled)
                {
                    string enteredName = nameBox.Text.Trim();
                    if (!string.IsNullOrEmpty(enteredName))
                    {
                        arrayTag.Name = enteredName;
                    }
                    else if (isNewTag && targetParentCompound != null)
                    {
                        arrayTag.Name = GenerateUniqueTagName(targetParentCompound, $"New{arrayTag.TagType}");
                    }
                }

                hexWin.DialogResult = true;
            };

            bottomBar.Children.Add(nameLbl);
            bottomBar.Children.Add(nameBox);
            bottomBar.Children.Add(okBtn);
            Grid.SetRow(bottomBar, 1);
            rootGrid.Children.Add(bottomBar);

            hexWin.Content = rootGrid;
            hexWin.Loaded += (_, _) =>
            {
                if (isNewTag && nameBox.IsEnabled)
                    nameBox.Focus();
                else
                    hexTextBox.Focus();
            };

            bool committed = hexWin.ShowDialog() == true;
            if (committed && existingNode != null)
            {
                if (!isNewTag && targetParentCompound == null && ItemsControl.ItemsControlFromItemContainer(existingNode) is TreeViewItem parentTreeItem && parentTreeItem.Tag is NbtCompound parentComp)
                {
                    var allTags = parentComp.Tags.ToList();
                    parentComp.Clear();
                    foreach (var child in allTags)
                    {
                        parentComp.Add(child);
                    }
                    ResortChildTreeItems(parentTreeItem);
                }

                RefreshNodeHeader(existingNode);
                UpdateNbtTreeZebraStripes();
                MarkCurrentTargetModified();
            }
            return committed;
        }

        private static string FormatHexOffsets(int byteCount)
        {
            int rows = Math.Max(1, (byteCount + 15) / 16);
            StringBuilder sb = new StringBuilder(rows * 10);
            for (int r = 0; r < rows; r++)
            {
                if (r > 0) sb.AppendLine();
                sb.Append((r * 16).ToString("X8"));
            }
            return sb.ToString();
        }

        private static string FormatBytesToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            StringBuilder sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(i % 16 == 0 ? Environment.NewLine : " ");
                }
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string FormatBytesToAscii(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            StringBuilder sb = new StringBuilder(bytes.Length + (bytes.Length / 16) * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0 && i % 16 == 0)
                {
                    sb.AppendLine();
                }
                byte b = bytes[i];
                sb.Append((b >= 32 && b <= 126) ? (char)b : '.');
            }
            return sb.ToString();
        }

        private static bool TryParseHexToBytes(string rawHex, out byte[] bytes)
        {
            string cleaned = new string(rawHex.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (cleaned.Length == 0)
            {
                bytes = Array.Empty<byte>();
                return true;
            }
            if (cleaned.Length % 2 != 0)
            {
                bytes = Array.Empty<byte>();
                return false;
            }

            bytes = new byte[cleaned.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(cleaned.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                {
                    return false;
                }
            }
            return true;
        }

        // ==================== MULTI-SELECTION & HIGHLIGHT MANAGEMENT ====================

        private void ClearActiveSelectionState()
        {
            CommitInlineEdit(saveChanges: true);

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
            if (_inlineEditingTreeItem != null && _inlineEditingTreeItem != newItem)
            {
                CommitInlineEdit(saveChanges: true);
            }

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
            CommitInlineEdit(saveChanges: true);

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
            CommitInlineEdit(saveChanges: true);

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
            CommitInlineEdit(saveChanges: true);
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
            if (tag is NbtList list)
            {
                // UME Rich Summary Previews for Pos and Rotation lists!
                if (string.Equals(list.Name, "Pos", StringComparison.Ordinal) && list.Count == 3 && list.ListType == NbtTagType.Float)
                {
                    double x = Math.Round(list[0].FloatValue, 1);
                    double y = Math.Round(list[1].FloatValue, 1);
                    double z = Math.Round(list[2].FloatValue, 1);
                    return string.Format(CultureInfo.InvariantCulture, "3 entries (X: {0:0.0}, Y: {1:0.0}, Z: {2:0.0})", x, y, z);
                }
                if (string.Equals(list.Name, "Rotation", StringComparison.Ordinal) && list.Count == 2 && list.ListType == NbtTagType.Float)
                {
                    string facing = FormatYawToCompass(list[0].FloatValue);
                    string looking = FormatPitchToDescription(list[1].FloatValue);
                    return $"2 entries (Facing {facing}, Looking {looking})";
                }
                return $"{list.Count} entries";
            }

            return tag switch
            {
                // Strip trailing/embedded \r and \n so multi-line strings like FlatWorldLayers stay perfectly aligned on one line!
                NbtString strTag => (strTag.Value ?? "").Replace("\r", "").Replace("\n", ""),
                // In UME, Byte tags are displayed as signed 8-bit integers (-128..127), e.g., Sheared: -1!
                NbtByte byteTag => ((sbyte)byteTag.Value).ToString(CultureInfo.InvariantCulture),
                // In UME, extreme floats like 3.402823e+38 use lowercase 'g7' formatting!
                NbtFloat floatTag => floatTag.Value.ToString("g7", CultureInfo.InvariantCulture),
                NbtDouble doubleTag => doubleTag.Value.ToString("g15", CultureInfo.InvariantCulture),
                NbtByteArray byteArr => byteArr.Value.Length == 1 ? "1 byte" : $"{byteArr.Value.Length} bytes",
                NbtIntArray intArr => intArr.Value.Length == 1 ? "1 integer" : $"{intArr.Value.Length} integers",
                NbtCompound compound => $"{compound.Count} entries",
                _ => (tag.StringValue ?? "").Replace("\r", "").Replace("\n", "")
            };
        }

        private static string FormatYawToCompass(float yaw)
        {
            double normalized = ((yaw % 360.0) + 360.0) % 360.0;
            string[] dirs = { "S", "SW", "W", "NW", "N", "NE", "E", "SE" };
            int idx = (int)Math.Round(normalized / 45.0) % 8;
            return dirs[idx];
        }

        private static string FormatPitchToDescription(float pitch)
        {
            if (pitch >= 75f) return "Straight Down";
            if (pitch >= 35f) return "Down";
            if (pitch >= 10f) return "Slightly Down";
            if (pitch <= -75f) return "Straight Up";
            if (pitch <= -35f) return "Up";
            if (pitch <= -10f) return "Slightly Up";
            return "Straight";
        }

        // ==================== TOP TOOLBAR: ADD NEW TAGS (EXACT UME INLINE NAMING & HEX EDITOR FOR BOTH 01 BUTTONS!) ====================

        private void BtnAddTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string typeStr && Enum.TryParse(typeStr, out NbtTagType tagType))
            {
                AddNewTagOfType(tagType);
            }
        }

        private (TreeViewItem? ContainerItem, NbtTag? ContainerTag) ResolveAddTargetContainer()
        {
            if (_activeNbtFile?.RootTag == null || NbtTreeView.Items.Count == 0)
                return (null, null);

            if (_selectedTreeItem != null && _selectedTag != null)
            {
                if (_selectedTag is NbtCompound || _selectedTag is NbtList)
                    return (_selectedTreeItem, _selectedTag);

                if (ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem) is TreeViewItem parentItem &&
                    parentItem.Tag is NbtTag parentTag &&
                    (parentTag is NbtCompound || parentTag is NbtList))
                {
                    return (parentItem, parentTag);
                }
            }

            if (NbtTreeView.Items[0] is TreeViewItem rootTreeItem && rootTreeItem.Tag is NbtCompound rootCompound)
            {
                return (rootTreeItem, rootCompound);
            }

            return (null, null);
        }

        private static NbtTag CreateDefaultNbtTag(NbtTagType tagType, string? tagName)
        {
            return tagType switch
            {
                NbtTagType.Byte => tagName != null ? new NbtByte(tagName, 0) : new NbtByte(0),
                NbtTagType.Short => tagName != null ? new NbtShort(tagName, 0) : new NbtShort(0),
                NbtTagType.Int => tagName != null ? new NbtInt(tagName, 0) : new NbtInt(0),
                NbtTagType.Long => tagName != null ? new NbtLong(tagName, 0) : new NbtLong(0),
                NbtTagType.Float => tagName != null ? new NbtFloat(tagName, 0f) : new NbtFloat(0f),
                NbtTagType.Double => tagName != null ? new NbtDouble(tagName, 0.0) : new NbtDouble(0.0),
                NbtTagType.ByteArray => tagName != null ? new NbtByteArray(tagName, Array.Empty<byte>()) : new NbtByteArray(Array.Empty<byte>()),
                NbtTagType.IntArray => tagName != null ? new NbtIntArray(tagName, Array.Empty<int>()) : new NbtIntArray(Array.Empty<int>()),
                NbtTagType.String => tagName != null ? new NbtString(tagName, "") : new NbtString(""),
                NbtTagType.List => tagName != null ? new NbtList(tagName, NbtTagType.Byte) : new NbtList(NbtTagType.Byte),
                NbtTagType.Compound => tagName != null ? new NbtCompound(tagName) : new NbtCompound(),
                _ => tagName != null ? new NbtByte(tagName, 0) : new NbtByte(0)
            };
        }

        private void AddNewTagOfType(NbtTagType tagType)
        {
            CommitInlineEdit(saveChanges: true);

            if (_activeNbtFile?.RootTag == null)
            {
                TxtStatusLeft.Text = "Open a player or World Settings first before adding tags.";
                return;
            }

            var (containerItem, containerTag) = ResolveAddTargetContainer();
            if (containerItem == null || containerTag == null) return;

            int childDepth = GetContainerDepthFromTreeItem(containerItem) + 1;

            if (containerTag is NbtCompound targetCompound)
            {
                string fallbackName = GenerateUniqueTagName(targetCompound, $"New{tagType}");

                // Both "01" buttons (ByteArray & IntArray) open the 3-column Hex Editor modal directly!
                if (tagType == NbtTagType.ByteArray || tagType == NbtTagType.IntArray)
                {
                    NbtTag newArrayTag = CreateDefaultNbtTag(tagType, fallbackName);
                    if (!OpenUmeHexEditorModal(newArrayTag, existingNode: null, isNewTag: true, targetParentCompound: targetCompound))
                        return;

                    if (targetCompound.Contains(newArrayTag.Name!))
                    {
                        newArrayTag.Name = GenerateUniqueTagName(targetCompound, newArrayTag.Name!);
                    }

                    PushUndoSnapshot();
                    targetCompound.Add(newArrayTag);

                    _suppressTreeEvents = true;
                    try
                    {
                        containerItem.IsExpanded = true;
                        TreeViewItem? newNode = CreateUmeNode(newArrayTag, TxtSearch.Text.Trim(), childDepth);
                        if (newNode != null)
                        {
                            containerItem.Items.Insert(0, newNode);
                            ResortChildTreeItems(containerItem);
                            RefreshNodeHeader(containerItem);
                            SelectSingleTreeItem(newNode);
                            newNode.BringIntoView();
                        }
                    }
                    finally
                    {
                        _suppressTreeEvents = false;
                        UpdateNbtTreeZebraStripes();
                    }

                    MarkCurrentTargetModified();
                    TxtStatusLeft.Text = $"Added {tagType} tag '{newArrayTag.Name}' (Ctrl+Z to undo)";
                    return;
                }

                // Insert temporary tag at top of container and open inline [TextBox] [OK] editor!
                NbtTag newTag = CreateDefaultNbtTag(tagType, fallbackName);
                targetCompound.Add(newTag);

                TreeViewItem? createdNode = null;
                _suppressTreeEvents = true;
                try
                {
                    containerItem.IsExpanded = true;
                    createdNode = CreateUmeNode(newTag, "", childDepth);
                    if (createdNode != null)
                    {
                        containerItem.Items.Insert(0, createdNode);
                        RefreshNodeHeader(containerItem);
                        SelectSingleTreeItem(createdNode);
                        createdNode.BringIntoView();
                    }
                }
                finally
                {
                    _suppressTreeEvents = false;
                    UpdateNbtTreeZebraStripes();
                }

                if (createdNode != null)
                {
                    BeginInlineEdit(createdNode, renameMode: true, fallbackNewTagName: fallbackName);
                }
            }
            else if (containerTag is NbtList targetList)
            {
                if (targetList.Count > 0 && targetList.ListType != NbtTagType.Unknown && targetList.ListType != tagType)
                {
                    MessageBox.Show($"This list only accepts '{targetList.ListType}' tags.",
                                    "List Type Mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                NbtTag newTag = CreateDefaultNbtTag(tagType, null);
                if (newTag is NbtByteArray || newTag is NbtIntArray)
                {
                    if (!OpenUmeHexEditorModal(newTag, existingNode: null, isNewTag: true, targetParentCompound: null))
                        return;
                }

                PushUndoSnapshot();

                if (targetList.Count == 0 || targetList.ListType == NbtTagType.Unknown)
                {
                    targetList.ListType = tagType;
                }

                targetList.Add(newTag);

                TreeViewItem? createdNode = null;
                _suppressTreeEvents = true;
                try
                {
                    containerItem.IsExpanded = true;
                    createdNode = CreateUmeNode(newTag, "", childDepth);
                    if (createdNode != null)
                    {
                        containerItem.Items.Add(createdNode);
                        RefreshNodeHeader(containerItem);
                        SelectSingleTreeItem(createdNode);
                        createdNode.BringIntoView();
                    }
                }
                finally
                {
                    _suppressTreeEvents = false;
                    UpdateNbtTreeZebraStripes();
                }

                MarkCurrentTargetModified();
                TxtStatusLeft.Text = $"Added {tagType} entry to list (Ctrl+Z to undo)";
                if (createdNode != null && IsPrimitiveEditableTag(tagType))
                {
                    BeginInlineEdit(createdNode, renameMode: false);
                }
            }
        }

        private static int GetContainerDepthFromTreeItem(TreeViewItem item)
        {
            int depth = 0;
            ItemsControl? curr = ItemsControl.ItemsControlFromItemContainer(item);
            while (curr is TreeViewItem parentTreeItem)
            {
                depth++;
                curr = ItemsControl.ItemsControlFromItemContainer(parentTreeItem);
            }
            return depth;
        }

        // ==================== ACCOUNT, WORLD & PLAYER MANAGEMENT (LOCK-FREE IN-MEMORY SESSION CACHE!) ====================

        private void ResetWorldSessionState()
        {
            CommitInlineEdit(saveChanges: true);
            _activeNbtFile = null;
            _currentSaveTarget = "";
            _playerCache.Clear();
            _openNbtFiles.Clear();
            _modifiedTargets.Clear();
            _deletedPlayers.Clear();
            _targetUndoStacks.Clear();
            _targetViewStates.Clear();
            ClearActiveSelectionState();
        }

        private void ComboAccounts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAccountWorldEvents) return;

            if (!PromptSaveOrDiscardWorldChanges())
            {
                _suppressAccountWorldEvents = true;
                ComboAccounts.SelectedItem = _previousAccountItem;
                _suppressAccountWorldEvents = false;
                return;
            }

            _previousAccountItem = ComboAccounts.SelectedItem as ComboBoxItem;
            _previousWorldItem = null;

            ResetWorldSessionState();

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
            _activePlayerSteveIcon.Visibility = Visibility.Collapsed;
            TxtActiveIcon.Visibility = Visibility.Collapsed;
            TxtActiveFile.Text = "";
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
            if (_suppressAccountWorldEvents) return;

            if (WorldListBox.SelectedItem is ListBoxItem selectedWorld && selectedWorld.Tag is string worldFolderPath)
            {
                if (_previousWorldItem != null && _previousWorldItem != selectedWorld)
                {
                    if (!PromptSaveOrDiscardWorldChanges())
                    {
                        _suppressAccountWorldEvents = true;
                        WorldListBox.SelectedItem = _previousWorldItem;
                        _suppressAccountWorldEvents = false;
                        return;
                    }
                }

                _previousWorldItem = selectedWorld;
                ResetWorldSessionState();

                _worldFolderPath = worldFolderPath;
                _levelDatPath = Path.Combine(_worldFolderPath, "level.dat");
                _dbPath = Path.Combine(_worldFolderPath, "db");

                TxtWorldTabTitle.Text = $"{selectedWorld.Content}";
                _activePlayerSteveIcon.Visibility = Visibility.Collapsed;
                TxtActiveIcon.Visibility = Visibility.Collapsed;
                TxtActiveFile.Text = "";
                TxtStatusPath.Text = _worldFolderPath.Replace('\\', '/');
                BtnLevelDat.IsEnabled = File.Exists(_levelDatPath);
                BtnOpenFolder.IsEnabled = Directory.Exists(_worldFolderPath);
                BtnSave.IsEnabled = true;
                BtnSaveBottom.IsEnabled = true;

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
            CommitInlineEdit(saveChanges: true);
            if (string.IsNullOrEmpty(_levelDatPath)) return;

            SaveCurrentTargetViewState();

            try
            {
                // Pre-loaded in memory when the world was selected, or read & immediately close on demand!
                if (!_openNbtFiles.TryGetValue("level.dat", out NbtFile? nbt))
                {
                    if (!File.Exists(_levelDatPath))
                        throw new FileNotFoundException("level.dat not found.");

                    byte[] headerBuffer = new byte[8];
                    nbt = new NbtFile
                    {
                        BigEndian = false
                    };

                    using (FileStream fs = new FileStream(_levelDatPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length < 8)
                            throw new InvalidDataException("level.dat is smaller than the required 8-byte Bedrock header.");

                        fs.ReadExactly(headerBuffer, 0, 8);
                        nbt.LoadFromStream(fs, NbtCompression.None, null);
                    }

                    Buffer.BlockCopy(headerBuffer, 0, _levelDatHeader, 0, 8);
                    _openNbtFiles["level.dat"] = nbt;
                }

                _activeNbtFile = nbt;
                _currentSaveTarget = "level.dat";
                PlayerListBox.SelectedItems.Clear();

                _activePlayerSteveIcon.Visibility = Visibility.Collapsed;
                TxtActiveIcon.Visibility = Visibility.Visible;
                TxtActiveIcon.Text = "📄";
                TxtActiveFile.Text = "level.dat";
                TxtStatusLeft.Text = "Editing World Settings (level.dat)";
                RenderNbtTree(TxtSearch.Text);
                RestoreTargetViewStateIfAvailable("level.dat");
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

            // Pre-load level.dat into memory right away (with FileShare.Delete) so even if the folder is modified later, level.dat is ready in RAM!
            if (!string.IsNullOrEmpty(_levelDatPath) && File.Exists(_levelDatPath))
            {
                try
                {
                    byte[] headerBuffer = new byte[8];
                    var levelNbt = new NbtFile { BigEndian = false };
                    using (FileStream fs = new FileStream(_levelDatPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length >= 8)
                        {
                            fs.ReadExactly(headerBuffer, 0, 8);
                            levelNbt.LoadFromStream(fs, NbtCompression.None, null);
                            Buffer.BlockCopy(headerBuffer, 0, _levelDatHeader, 0, 8);
                            _openNbtFiles["level.dat"] = levelNbt;
                        }
                    }
                }
                catch
                {
                    // Ignore pre-cache error; will retry if user clicks World Settings
                }
            }

            if (_dbPath == null || !Directory.Exists(_dbPath)) return;

            try
            {
                // Open LevelDB ONLY for the few milliseconds needed to read player entries into RAM, then immediately dispose & release all locks!
                using (var db = NativeLevelDb.Open(_dbPath))
                {
                    foreach (var kvp in db.GetAllPlayerEntries()
                        .OrderBy(k => k.Key == "~local_player")
                        .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        _playerCache[kvp.Key] = kvp.Value;
                        PlayerListBox.Items.Add(new PlayerEntryItem(kvp.Key));
                    }
                }

                PlayerListBox.SelectedIndex = -1;
                TxtStatusLeft.Text = $"Loaded {_playerCache.Count} player record(s) into memory (world folder is unlocked).";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read LevelDB. Ensure you have exited the world to the Minecraft main menu.\n\nDetails: " + ex.Message,
                                "LevelDB Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void PlayerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CommitInlineEdit(saveChanges: true);
            if (PlayerListBox.SelectedItem is not PlayerEntryItem selectedEntry) return;

            string selectedPlayer = selectedEntry.RawKey;

            if (_currentSaveTarget == selectedPlayer && _activeNbtFile != null)
            {
                TxtStatusLeft.Text = PlayerListBox.SelectedItems.Count > 1
                    ? $"Selected {PlayerListBox.SelectedItems.Count} players (Viewing: {selectedEntry.DisplayName})"
                    : $"Editing Player: {selectedEntry.DisplayName}";
                return;
            }

            SaveCurrentTargetViewState();

            try
            {
                if (!_openNbtFiles.TryGetValue(selectedPlayer, out NbtFile? nbt))
                {
                    if (!_playerCache.TryGetValue(selectedPlayer, out byte[]? playerData) || playerData.Length == 0)
                        return;

                    nbt = new NbtFile
                    {
                        BigEndian = false
                    };
                    nbt.LoadFromBuffer(playerData, 0, playerData.Length, NbtCompression.None, null);
                    _openNbtFiles[selectedPlayer] = nbt;
                }

                _activeNbtFile = nbt;
                _currentSaveTarget = selectedPlayer;

                _activePlayerSteveIcon.Visibility = Visibility.Visible;
                TxtActiveIcon.Visibility = Visibility.Collapsed;
                TxtActiveFile.Text = $"db/{selectedEntry.DisplayName}";
                TxtStatusLeft.Text = PlayerListBox.SelectedItems.Count > 1
                    ? $"Selected {PlayerListBox.SelectedItems.Count} players (Viewing: {selectedEntry.DisplayName})"
                    : $"Editing Player: {selectedEntry.DisplayName}";

                RenderNbtTree(TxtSearch.Text);
                RestoreTargetViewStateIfAvailable(selectedPlayer);
                BtnSave.IsEnabled = true;
                BtnSaveBottom.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not parse NBT data for '{selectedPlayer}':\n{ex.Message}",
                                "NBT Parse Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== PLAYER LIST RIGHT-CLICK DELETE (STAGED UNTIL SAVE!) ====================

        private void PlayerListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not ListBoxItem)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is ListBoxItem targetItem)
            {
                if (_rightClickedPlayerItem != null && _rightClickedPlayerItem != targetItem)
                {
                    _rightClickedPlayerItem.BorderThickness = UnselectedFlagThickness;
                }

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
            if (PlayerListBox.SelectedItems.Count == 0) return;

            var toDelete = PlayerListBox.SelectedItems.Cast<PlayerEntryItem>().ToList();

            foreach (var player in toDelete)
            {
                _deletedPlayers.Add(player.RawKey);
                _modifiedTargets.Remove(player.RawKey);
                _openNbtFiles.Remove(player.RawKey);
                _playerCache.Remove(player.RawKey);
                _targetUndoStacks.Remove(player.RawKey);
                _targetViewStates.Remove(player.RawKey);
                PlayerListBox.Items.Remove(player);
            }

            if (toDelete.Any(p => p.RawKey == _currentSaveTarget))
            {
                _activeNbtFile = null;
                _currentSaveTarget = "";
                ClearActiveSelectionState();
                NbtTreeView.Items.Clear();
                _activePlayerSteveIcon.Visibility = Visibility.Collapsed;
                TxtActiveIcon.Visibility = Visibility.Collapsed;
                TxtActiveFile.Text = "";
            }

            TxtStatusLeft.Text = toDelete.Count == 1
                ? $"Removed player '{toDelete[0].DisplayName}' (Click Save to commit)"
                : $"Removed {toDelete.Count} players (Click Save to commit)";
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_activeNbtFile != null)
            {
                RenderNbtTree(TxtSearch.Text);
            }
        }

        // ==================== UNDO (CTRL+Z) SYSTEM (PER-TARGET WITH VIEWPORT SCROLL LOCK!) ====================

        private void PushUndoSnapshot()
        {
            if (_activeNbtFile?.RootTag == null) return;

            var stack = CurrentUndoStack;
            if (stack.Count >= MaxUndoSteps)
            {
                var temp = stack.ToArray();
                stack.Clear();
                for (int i = MaxUndoSteps - 2; i >= 0; i--)
                {
                    stack.Push(temp[i]);
                }
            }

            stack.Push((NbtCompound)_activeNbtFile.RootTag.Clone());
        }

        private void PerformUndo()
        {
            CommitInlineEdit(saveChanges: false);

            var stack = CurrentUndoStack;
            if (stack.Count == 0 || _activeNbtFile == null)
            {
                TxtStatusLeft.Text = "Nothing to undo";
                return;
            }

            ScrollViewer? sv = GetTreeViewScrollViewer();
            double savedVerticalOffset = sv?.VerticalOffset ?? 0.0;
            double savedHorizontalOffset = sv?.HorizontalOffset ?? 0.0;

            HashSet<string> expandedPaths = new HashSet<string>();
            string? selectedPath = _selectedTreeItem != null ? GetTreeItemPath(_selectedTreeItem) : null;

            foreach (var item in NbtTreeView.Items)
            {
                if (item is TreeViewItem rootItem)
                {
                    CollectExpandedPaths(rootItem, expandedPaths);
                }
            }

            _activeNbtFile.RootTag = stack.Pop();
            MarkCurrentTargetModified();

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

            // Restore exact scroll position so the user never loses their place in the tree!
            NbtTreeView.UpdateLayout();
            sv = GetTreeViewScrollViewer();
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(savedVerticalOffset);
                sv.ScrollToHorizontalOffset(savedHorizontalOffset);
                NbtStripeTransform.Y = -(savedVerticalOffset % 44.0);

                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    sv.ScrollToVerticalOffset(savedVerticalOffset);
                    sv.ScrollToHorizontalOffset(savedHorizontalOffset);
                    NbtStripeTransform.Y = -(savedVerticalOffset % 44.0);
                }));
            }

            TxtStatusLeft.Text = $"Undid last change ({stack.Count} undo step(s) remaining)";
        }

        private static string GetTreeItemPath(TreeViewItem item)
        {
            var parts = new List<string>();
            TreeViewItem? curr = item;
            while (curr != null)
            {
                ItemsControl? parent = ItemsControl.ItemsControlFromItemContainer(curr);
                if (parent is TreeViewItem parentTreeItem)
                {
                    if (parentTreeItem.Tag is NbtList)
                    {
                        int idx = parentTreeItem.Items.IndexOf(curr);
                        parts.Add($"[{idx}]");
                    }
                    else if (curr.Tag is NbtTag tag)
                    {
                        parts.Add(string.IsNullOrEmpty(tag.Name) ? $"<idx:{parentTreeItem.Items.IndexOf(curr)}>" : tag.Name);
                    }
                    curr = parentTreeItem;
                }
                else
                {
                    parts.Add("<root>");
                    break;
                }
            }
            parts.Reverse();
            return string.Join("\u001F", parts);
        }

        private void CollectExpandedPaths(TreeViewItem item, HashSet<string> expandedPaths)
        {
            if (item.IsExpanded)
            {
                expandedPaths.Add(GetTreeItemPath(item));
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
            string path = GetTreeItemPath(item);
            if (expandedPaths.Contains(path))
            {
                item.IsExpanded = true;
            }
            if (selectedPath != null && path == selectedPath)
            {
                SelectSingleTreeItem(item);
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
                foreach (var child in GetUmeSortedCompoundChildren(compound))
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

                node.Header = BuildUmeNodeHeader(tag, isContainer: true);
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

                node.Header = BuildUmeNodeHeader(tag, isContainer: true);
            }
            else
            {
                string valStr = GetSafeTagValueString(tag);
                if (hasFilter && !selfMatches && !valStr.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    return null;

                node.Header = BuildUmeNodeHeader(tag, isContainer: false);
            }

            return node;
        }

        private void RefreshNodeHeader(TreeViewItem item)
        {
            if (item.Tag is NbtTag tag)
            {
                bool isContainer = tag is NbtCompound || tag is NbtList;
                item.Header = BuildUmeNodeHeader(tag, isContainer);
            }
        }

        // ==================== MOUSE CLICK, DOUBLE-CLICK EDIT/RENAME, DRAG SWEEP & SHORTCUTS ====================

        private void NbtTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSelection = false;

            DependencyObject? orig = e.OriginalSource as DependencyObject;

            if ((_inlineEditingTextBox != null || _inlineEditingOkButton != null) && orig != null)
            {
                DependencyObject? check = orig;
                while (check != null)
                {
                    if (ReferenceEquals(check, _inlineEditingTextBox) || ReferenceEquals(check, _inlineEditingOkButton))
                        return;
                    if (check is Run || check is TextElement)
                        check = LogicalTreeHelper.GetParent(check);
                    else
                        check = VisualTreeHelper.GetParent(check);
                }
            }

            DependencyObject? source = orig;
            while (source != null && source is not TreeViewItem && source is not ToggleButton && source is not ScrollBar)
            {
                if (source is Run || source is TextElement)
                    source = LogicalTreeHelper.GetParent(source);
                else
                    source = VisualTreeHelper.GetParent(source);
            }

            if (source is ToggleButton || source is ScrollBar)
            {
                CommitInlineEdit(saveChanges: true);
                return;
            }

            if (source is TreeViewItem targetItem)
            {
                if (e.ClickCount == 2 && Keyboard.Modifiers == ModifierKeys.None)
                {
                    SelectSingleTreeItem(targetItem);
                    _dragAnchorItem = targetItem;

                    if (targetItem.Tag is NbtTag clickedTag)
                    {
                        bool clickedOnNamePart = false;
                        DependencyObject? walk = orig;
                        while (walk != null && walk != targetItem)
                        {
                            if (walk is FrameworkElement fe && fe.Tag as string == "TagNamePart")
                            {
                                clickedOnNamePart = true;
                                break;
                            }
                            walk = (walk is Run || walk is TextElement)
                                ? LogicalTreeHelper.GetParent(walk)
                                : VisualTreeHelper.GetParent(walk);
                        }

                        ItemsControl? parentCtrl = ItemsControl.ItemsControlFromItemContainer(targetItem);
                        NbtTag? parentTag = (parentCtrl as TreeViewItem)?.Tag as NbtTag ?? clickedTag.Parent;
                        bool canRenameThisTag = !IsRootTreeItem(targetItem) && parentTag is not NbtList && !string.IsNullOrEmpty(clickedTag.Name);

                        if (clickedOnNamePart && canRenameThisTag)
                        {
                            BeginInlineEdit(targetItem, renameMode: true);
                            e.Handled = true;
                            return;
                        }
                        if (clickedTag is NbtByteArray || clickedTag is NbtIntArray)
                        {
                            OpenUmeHexEditorModal(clickedTag, targetItem, isNewTag: false);
                            e.Handled = true;
                            return;
                        }
                        if (IsPrimitiveEditableTag(clickedTag.TagType))
                        {
                            BeginInlineEdit(targetItem, renameMode: false);
                            e.Handled = true;
                            return;
                        }
                        if (canRenameThisTag)
                        {
                            BeginInlineEdit(targetItem, renameMode: true);
                            e.Handled = true;
                            return;
                        }
                    }
                }

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
            if (!_isDraggingSelection || e.LeftButton != MouseButtonState.Pressed || _dragAnchorItem == null || _inlineEditingTextBox != null)
            {
                _isDraggingSelection = false;
                return;
            }

            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not TreeViewItem && source is not ScrollBar)
            {
                if (source is Run || source is TextElement)
                    source = LogicalTreeHelper.GetParent(source);
                else
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
                if (source is Run || source is TextElement)
                    source = LogicalTreeHelper.GetParent(source);
                else
                    source = VisualTreeHelper.GetParent(source);
            }

            if (_inlineEditingTreeItem != null)
            {
                CommitInlineEdit(saveChanges: true);
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

        private static bool IsRootTreeItem(TreeViewItem item)
        {
            return ItemsControl.ItemsControlFromItemContainer(item) is TreeView;
        }

        private (TreeViewItem? ContainerItem, NbtTag? ContainerTag) ResolvePasteTargetContainer()
        {
            if (_selectedTreeItem == null || _selectedTag == null)
                return (null, null);

            ItemsControl? parentControl = ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem);
            TreeViewItem? parentTreeItem = parentControl as TreeViewItem;
            NbtTag? parentTag = parentTreeItem?.Tag as NbtTag ?? _selectedTag.Parent;

            if (_selectedTag is NbtCompound &&
                parentTag is NbtList parentList &&
                parentTreeItem != null &&
                !_clipboardIsRootCompound &&
                _nbtClipboard.Count > 0 &&
                string.IsNullOrEmpty(_nbtClipboard[0].Name) &&
                (parentList.Count == 0 || parentList.ListType == NbtTagType.Unknown || parentList.ListType == _nbtClipboard[0].TagType))
            {
                return (parentTreeItem, parentList);
            }

            if (_selectedTag is NbtCompound || _selectedTag is NbtList)
            {
                return (_selectedTreeItem, _selectedTag);
            }

            if (parentTreeItem != null && (parentTag is NbtCompound || parentTag is NbtList))
            {
                return (parentTreeItem, parentTag);
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
            bool isRoot = !isMulti && IsRootTreeItem(_selectedTreeItem);
            ItemsControl? parentCtrl = ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem);
            NbtList? parentList = (parentCtrl as TreeViewItem)?.Tag as NbtList ?? _selectedTag.Parent as NbtList;
            bool isInsideList = !isMulti && parentList != null;

            bool canUndo = CurrentUndoStack.Count > 0;
            MenuUndo.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
            MenuUndoSeparator.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;

            bool canRename = !isMulti && !isRoot && !isInsideList && !string.IsNullOrEmpty(_selectedTag.Name);
            bool canEditValue = !isMulti && (IsPrimitiveEditableTag(_selectedTag.TagType) || _selectedTag.TagType == NbtTagType.ByteArray || _selectedTag.TagType == NbtTagType.IntArray);
            MenuRename.Visibility = canRename ? Visibility.Visible : Visibility.Collapsed;
            MenuEditValue.Visibility = canEditValue ? Visibility.Visible : Visibility.Collapsed;
            MenuRenameSeparator.Visibility = (canRename || canEditValue) ? Visibility.Visible : Visibility.Collapsed;

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
            if (!isFiltered && isInsideList && parentList != null)
            {
                int idx = parentList.IndexOf(_selectedTag);
                if (idx < 0 && parentCtrl is TreeViewItem pItem) idx = pItem.Items.IndexOf(_selectedTreeItem);

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
            if (_saveModalOverlay.Visibility == Visibility.Visible)
            {
                if (_saveCompleteBox.Visibility == Visibility.Visible && (e.Key == Key.Enter || e.Key == Key.Escape))
                {
                    BtnSaveCompleteOk_Click(sender, e);
                    e.Handled = true;
                }
                return;
            }

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

            if (e.Key == Key.F2 && _selectedTreeItem != null)
            {
                BeginInlineEdit(_selectedTreeItem, renameMode: true);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
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
            CommitInlineEdit(saveChanges: true);
            var items = GetEffectiveSelectedItems();
            if (items.Count == 0) return;

            _nbtClipboard.Clear();
            _clipboardIsRootCompound = items.Count == 1 &&
                                       items[0].Tag is NbtCompound &&
                                       IsRootTreeItem(items[0]);

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
            CommitInlineEdit(saveChanges: true);
            var items = GetEffectiveSelectedItems();
            if (items.Count == 0) return;

            MenuCopy_Click(sender, e);

            if (items.Count == 1 && items[0].Tag is NbtCompound rootCompound && IsRootTreeItem(items[0]))
            {
                if (rootCompound.Count == 0) return;
                PushUndoSnapshot();
                rootCompound.Clear();
                items[0].Items.Clear();
                RefreshNodeHeader(items[0]);
                UpdateNbtTreeZebraStripes();
                SelectSingleTreeItem(items[0]);
                MarkCurrentTargetModified();
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
            CommitInlineEdit(saveChanges: true);
            if (_nbtClipboard.Count == 0 || _selectedTag == null || _selectedTreeItem == null) return;

            var (targetContainerItem, targetContainerTag) = ResolvePasteTargetContainer();
            if (targetContainerItem == null || targetContainerTag == null) return;

            try
            {
                PushUndoSnapshot();

                int childDepth = GetContainerDepthFromTreeItem(targetContainerItem) + 1;
                string activeFilter = TxtSearch.Text.Trim();

                _suppressTreeEvents = true;
                try
                {
                    if (targetContainerTag is NbtCompound targetCompound)
                    {
                        if (_clipboardIsRootCompound && _nbtClipboard.Count == 1 && _nbtClipboard[0] is NbtCompound sourceCompound && IsRootTreeItem(targetContainerItem))
                        {
                            targetCompound.Clear();
                            targetContainerItem.Items.Clear();

                            foreach (NbtTag child in GetUmeSortedCompoundChildren(sourceCompound))
                            {
                                NbtTag childCopy = (NbtTag)child.Clone();
                                targetCompound.Add(childCopy);
                                TreeViewItem? childNode = CreateUmeNode(childCopy, activeFilter, childDepth);
                                if (childNode != null) targetContainerItem.Items.Add(childNode);
                            }

                            RefreshNodeHeader(targetContainerItem);
                            targetContainerItem.IsExpanded = true;
                            SelectSingleTreeItem(targetContainerItem);
                            MarkCurrentTargetModified();
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

                        ResortChildTreeItems(targetContainerItem);
                        targetContainerItem.IsExpanded = true;
                        RefreshNodeHeader(targetContainerItem);
                        if (lastPastedNode != null)
                        {
                            SelectSingleTreeItem(lastPastedNode);
                        }

                        MarkCurrentTargetModified();
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
                            MarkCurrentTargetModified();
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
                            MarkCurrentTargetModified();
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

        private static bool HasSelectedTreeAncestor(TreeViewItem item, HashSet<TreeViewItem> selectedTreeItems)
        {
            ItemsControl? curr = ItemsControl.ItemsControlFromItemContainer(item);
            while (curr is TreeViewItem parentTreeItem)
            {
                if (selectedTreeItems.Contains(parentTreeItem))
                    return true;
                curr = ItemsControl.ItemsControlFromItemContainer(parentTreeItem);
            }
            return false;
        }

        private void DeleteSelectedItemsInternal(bool isCutOperation)
        {
            CommitInlineEdit(saveChanges: false);

            var rawItems = GetEffectiveSelectedItems()
                .Where(item => item.Tag is NbtTag && !IsRootTreeItem(item))
                .ToList();

            if (rawItems.Count == 0) return;

            var selectedTreeItemSet = new HashSet<TreeViewItem>(rawItems);
            var itemsToDelete = rawItems
                .Where(item => !HasSelectedTreeAncestor(item, selectedTreeItemSet))
                .ToList();

            if (itemsToDelete.Count == 0) return;

            PushUndoSnapshot();

            TreeViewItem? fallbackParent = null;
            string firstTagName = "";

            foreach (var itemToDelete in itemsToDelete)
            {
                if (itemToDelete.Tag is not NbtTag tagToDelete)
                    continue;

                ItemsControl? parentControl = ItemsControl.ItemsControlFromItemContainer(itemToDelete);
                TreeViewItem? parentTreeItem = parentControl as TreeViewItem;
                NbtTag? parentNbtTag = parentTreeItem?.Tag as NbtTag ?? tagToDelete.Parent;

                if (string.IsNullOrEmpty(firstTagName))
                {
                    firstTagName = string.IsNullOrEmpty(tagToDelete.Name) ? tagToDelete.TagType.ToString() : tagToDelete.Name;
                }

                if (parentNbtTag is NbtCompound parentCompound)
                {
                    if (tagToDelete.Name != null && parentCompound.Contains(tagToDelete.Name))
                    {
                        parentCompound.Remove(tagToDelete.Name);
                    }
                    else
                    {
                        parentCompound.Remove(tagToDelete);
                    }
                }
                else if (parentNbtTag is NbtList parentList)
                {
                    if (!parentList.Remove(tagToDelete) && parentTreeItem != null)
                    {
                        int uiIdx = parentTreeItem.Items.IndexOf(itemToDelete);
                        if (uiIdx >= 0 && uiIdx < parentList.Count)
                        {
                            parentList.RemoveAt(uiIdx);
                        }
                    }
                }

                if (parentTreeItem != null)
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

            MarkCurrentTargetModified();
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
            CommitInlineEdit(saveChanges: true);
            if (_selectedTag == null || _selectedTreeItem == null) return;
            if (ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem) is not TreeViewItem parentTreeItem) return;
            if (parentTreeItem.Tag is not NbtList parentList) return;

            int uiIndex = parentTreeItem.Items.IndexOf(_selectedTreeItem);
            int newUiIndex = uiIndex + direction;

            int dataIndex = parentList.IndexOf(_selectedTag);
            if (dataIndex < 0) dataIndex = uiIndex;
            int newDataIndex = dataIndex + direction;

            if (dataIndex < 0 || newDataIndex < 0 || newDataIndex >= parentList.Count) return;
            if (uiIndex < 0 || newUiIndex < 0 || newUiIndex >= parentTreeItem.Items.Count) return;

            PushUndoSnapshot();

            NbtTag movingTag = parentList[dataIndex];
            parentList.RemoveAt(dataIndex);
            parentList.Insert(newDataIndex, movingTag);

            TreeViewItem movingNode = _selectedTreeItem;
            parentTreeItem.Items.RemoveAt(uiIndex);
            parentTreeItem.Items.Insert(newUiIndex, movingNode);

            RefreshNodeHeader(parentTreeItem);
            UpdateNbtTreeZebraStripes();
            SelectSingleTreeItem(movingNode);
            movingNode.Focus();
            MarkCurrentTargetModified();
            TxtStatusLeft.Text = $"Moved list entry {(direction < 0 ? "up" : "down")} (Ctrl+Z to undo)";
        }

        // ==================== AUTHENTIC UME NODE HEADER STYLING (FOLDED DOCUMENT BADGES, SERIF TT, 3-DOT LIST) ====================

        private static Grid CreateUmeFoldedDocumentBadge(UIElement badgeLabel)
        {
            Grid docGrid = new Grid
            {
                Tag = "TagNamePart",
                Width = 15,
                Height = 17,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };

            WpfPath pagePath = new WpfPath
            {
                Tag = "TagNamePart",
                Data = Geometry.Parse("M 0.5,0.5 L 10.5,0.5 L 14.5,4.5 L 14.5,16.5 L 0.5,16.5 Z"),
                Fill = CreateFrozenBrush(252, 253, 254),
                Stroke = CreateFrozenBrush(142, 152, 164),
                StrokeThickness = 1
            };

            WpfPath flapPath = new WpfPath
            {
                Tag = "TagNamePart",
                Data = Geometry.Parse("M 10.5,0.5 L 10.5,4.5 L 14.5,4.5"),
                Fill = CreateFrozenBrush(226, 230, 235),
                Stroke = CreateFrozenBrush(142, 152, 164),
                StrokeThickness = 1
            };

            Rectangle line1 = new Rectangle
            {
                Tag = "TagNamePart",
                Width = 6,
                Height = 1,
                Fill = CreateFrozenBrush(200, 206, 214),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2.5, 2.5, 0, 0)
            };
            Rectangle line2 = new Rectangle
            {
                Tag = "TagNamePart",
                Width = 5,
                Height = 1,
                Fill = CreateFrozenBrush(200, 206, 214),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2.5, 4.5, 0, 0)
            };

            docGrid.Children.Add(pagePath);
            docGrid.Children.Add(line1);
            docGrid.Children.Add(line2);
            docGrid.Children.Add(flapPath);
            docGrid.Children.Add(badgeLabel);

            return docGrid;
        }

        private static void AddNodeIconToPanel(StackPanel panel, NbtTagType tagType)
        {
            if (tagType == NbtTagType.Compound)
            {
                Border box = new Border
                {
                    Tag = "TagNamePart",
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
                    Tag = "TagNamePart",
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
                Canvas listCanvas = new Canvas
                {
                    Tag = "TagNamePart",
                    Width = 14,
                    Height = 12,
                    Margin = new Thickness(0.5, 0, 6.5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true
                };
                SolidColorBrush barBrush = CreateFrozenBrush(62, 76, 89);

                for (int i = 0; i < 3; i++)
                {
                    double top = 1 + i * 4;
                    Rectangle dot = new Rectangle { Tag = "TagNamePart", Width = 2, Height = 2, Fill = barBrush };
                    Canvas.SetLeft(dot, 0);
                    Canvas.SetTop(dot, top);

                    Rectangle bar = new Rectangle { Tag = "TagNamePart", Width = 9.5, Height = 2, Fill = barBrush };
                    Canvas.SetLeft(bar, 4);
                    Canvas.SetTop(bar, top);

                    listCanvas.Children.Add(dot);
                    listCanvas.Children.Add(bar);
                }

                panel.Children.Add(listCanvas);
            }
            else if (tagType == NbtTagType.String)
            {
                StackPanel ttPanel = new StackPanel
                {
                    Tag = "TagNamePart",
                    Orientation = Orientation.Horizontal,
                    Width = 15,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ttPanel.Children.Add(new TextBlock
                {
                    Tag = "TagNamePart",
                    Text = "T",
                    FontFamily = new FontFamily("Georgia"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11.5,
                    Foreground = CreateFrozenBrush(62, 76, 89),
                    VerticalAlignment = VerticalAlignment.Bottom
                });
                ttPanel.Children.Add(new TextBlock
                {
                    Tag = "TagNamePart",
                    Text = "T",
                    FontFamily = new FontFamily("Georgia"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 8.5,
                    Foreground = CreateFrozenBrush(62, 76, 89),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0.5, 0, 0, 0.5)
                });
                panel.Children.Add(ttPanel);
            }
            else if (tagType == NbtTagType.ByteArray || tagType == NbtTagType.IntArray)
            {
                TextBlock tb = new TextBlock
                {
                    Tag = "TagNamePart",
                    FontWeight = FontWeights.Bold,
                    FontSize = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 1)
                };
                SolidColorBrush zeroBrush = tagType == NbtTagType.ByteArray
                    ? CreateFrozenBrush(214, 69, 69)
                    : CreateFrozenBrush(41, 128, 185);
                tb.Inlines.Add(new Run("0") { Foreground = zeroBrush });
                tb.Inlines.Add(new Run("1") { Foreground = CreateFrozenBrush(34, 40, 49) });

                panel.Children.Add(CreateUmeFoldedDocumentBadge(tb));
            }
            else
            {
                var (letter, color) = GetBadgeInfo(tagType);
                TextBlock tb = new TextBlock
                {
                    Tag = "TagNamePart",
                    Text = letter,
                    Foreground = CreateFrozenBrush(color.R, color.G, color.B),
                    FontWeight = FontWeights.Bold,
                    FontSize = 8.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 1)
                };
                panel.Children.Add(CreateUmeFoldedDocumentBadge(tb));
            }
        }

        private UIElement BuildUmeNodeHeader(NbtTag tag, bool isContainer)
        {
            StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal };
            AddNodeIconToPanel(panel, tag.TagType);

            string valText = GetSafeTagValueString(tag);

            if (!string.IsNullOrEmpty(tag.Name))
            {
                panel.Children.Add(new TextBlock
                {
                    Tag = "TagNamePart",
                    Text = $"{tag.Name}: ",
                    Foreground = CreateFrozenBrush(20, 24, 28),
                    FontWeight = isContainer ? FontWeights.Medium : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                });

                panel.Children.Add(new TextBlock
                {
                    Tag = "TagValuePart",
                    Text = valText,
                    Foreground = CreateFrozenBrush(20, 24, 28),
                    FontWeight = isContainer ? FontWeights.Medium : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Tag = "TagValuePart",
                    Text = valText,
                    Foreground = CreateFrozenBrush(20, 24, 28),
                    FontWeight = isContainer ? FontWeights.Medium : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

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
            if (sbyte.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte signedVal))
            {
                result = unchecked((byte)signedVal);
                return true;
            }
            if (byte.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return true;
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

        private bool TryApplyPrimitiveTagValue(NbtTag tag, string rawInput)
        {
            string trimmed = rawInput.Trim();

            if (tag is NbtString strTag)
            {
                if (string.Equals(strTag.Value, rawInput, StringComparison.Ordinal)) return false;
                PushUndoSnapshot();
                strTag.Value = rawInput;
                MarkCurrentTargetModified();
                return true;
            }
            if (tag is NbtByte byteTag && TryParseByteOrSByte(trimmed, out byte bVal))
            {
                if (byteTag.Value == bVal) return false;
                PushUndoSnapshot();
                byteTag.Value = bVal;
                MarkCurrentTargetModified();
                return true;
            }
            if (tag is NbtShort shortTag && short.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out short sVal))
            {
                if (shortTag.Value == sVal) return false;
                PushUndoSnapshot();
                shortTag.Value = sVal;
                MarkCurrentTargetModified();
                return true;
            }
            if (tag is NbtInt intTag && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iVal))
            {
                if (intTag.Value == iVal) return false;
                PushUndoSnapshot();
                intTag.Value = iVal;
                MarkCurrentTargetModified();
                return true;
            }
            if (tag is NbtLong longTag && long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lVal))
            {
                if (longTag.Value == lVal) return false;
                PushUndoSnapshot();
                longTag.Value = lVal;
                MarkCurrentTargetModified();
                return true;
            }
            if (tag is NbtFloat floatTag && TryParseFloatInvariant(trimmed, out float fVal))
            {
                if (floatTag.Value == fVal) return false;
                PushUndoSnapshot();
                floatTag.Value = fVal;
                MarkCurrentTargetModified();
                return true;
            }
            if (tag is NbtDouble doubleTag && TryParseDoubleInvariant(trimmed, out double dVal))
            {
                if (doubleTag.Value == dVal) return false;
                PushUndoSnapshot();
                doubleTag.Value = dVal;
                MarkCurrentTargetModified();
                return true;
            }

            MessageBox.Show($"Invalid value for {tag.TagType}.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTag == null) return;

            if (TryApplyPrimitiveTagValue(_selectedTag, TxtEditValue.Text))
            {
                if (_selectedTreeItem != null)
                {
                    RefreshNodeHeader(_selectedTreeItem);
                    if (ItemsControl.ItemsControlFromItemContainer(_selectedTreeItem) is TreeViewItem parentTreeItem)
                    {
                        RefreshNodeHeader(parentTreeItem);
                    }
                }

                string displayName = string.IsNullOrEmpty(_selectedTag.Name) ? _selectedTag.TagType.ToString() : _selectedTag.Name;
                TxtStatusLeft.Text = $"Updated '{displayName}' to {GetSafeTagValueString(_selectedTag)} (Ctrl+Z to undo, Ctrl+S to save)";
            }
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

        // ==================== LOCK-FREE SAVE ALL + UME SAVING PROGRESS & SAVE COMPLETE / ERROR MODALS ====================

        private bool CommitAllWorldChangesToDiskSynchronously()
        {
            try
            {
                if (string.IsNullOrEmpty(_worldFolderPath) || !Directory.Exists(_worldFolderPath))
                    return false;

                bool needsLevelDbWrite = _deletedPlayers.Count > 0 || _modifiedTargets.Any(t => t != "level.dat");

                if (needsLevelDbWrite)
                {
                    if (string.IsNullOrEmpty(_dbPath) || !Directory.Exists(_dbPath))
                        return false;

                    // Open LevelDB ONLY for the few milliseconds needed to write/delete modified player entries, then immediately close & unlock!
                    using (var db = NativeLevelDb.Open(_dbPath))
                    {
                        foreach (string deletedKey in _deletedPlayers.ToList())
                        {
                            db.Delete(Encoding.UTF8.GetBytes(deletedKey));
                            _playerCache.Remove(deletedKey);
                        }
                        _deletedPlayers.Clear();

                        foreach (string target in _modifiedTargets.Where(t => t != "level.dat").ToList())
                        {
                            if (!_openNbtFiles.TryGetValue(target, out NbtFile? nbtFile) || nbtFile.RootTag == null)
                                continue;

                            nbtFile.RootTag.Name ??= "";
                            SanitizeEmptyListsRecursive(nbtFile.RootTag);

                            byte[] newNbtBytes = nbtFile.SaveToBuffer(NbtCompression.None);
                            db.Put(Encoding.UTF8.GetBytes(target), newNbtBytes);
                            _playerCache[target] = newNbtBytes;
                        }
                    }
                }

                if (_modifiedTargets.Contains("level.dat"))
                {
                    if (string.IsNullOrEmpty(_levelDatPath) || !File.Exists(_levelDatPath))
                        return false;

                    if (_openNbtFiles.TryGetValue("level.dat", out NbtFile? levelNbt) && levelNbt.RootTag != null)
                    {
                        levelNbt.RootTag.Name ??= "";
                        SanitizeEmptyListsRecursive(levelNbt.RootTag);

                        byte[] nbtPayload = levelNbt.SaveToBuffer(NbtCompression.None);
                        byte[] lengthBytes = BitConverter.GetBytes(nbtPayload.Length);
                        Buffer.BlockCopy(lengthBytes, 0, _levelDatHeader, 4, 4);

                        using (FileStream fs = new FileStream(_levelDatPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        {
                            fs.Write(_levelDatHeader, 0, 8);
                            fs.Write(nbtPayload, 0, nbtPayload.Length);
                        }
                    }
                }

                _modifiedTargets.Clear();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            CommitInlineEdit(saveChanges: true);

            if (string.IsNullOrEmpty(_worldFolderPath)) return;

            if (_activeNbtFile?.RootTag != null && !string.IsNullOrEmpty(_currentSaveTarget))
            {
                _openNbtFiles[_currentSaveTarget] = _activeNbtFile;
                _modifiedTargets.Add(_currentSaveTarget);
            }

            try
            {
                _saveCompleteBox.Visibility = Visibility.Collapsed;
                _savingProgressBarFill.Width = 0;
                _savingProgressBox.Visibility = Visibility.Visible;
                _saveModalOverlay.Visibility = Visibility.Visible;

                double maxBarWidth = 330.0;
                _savingProgressBarFill.Width = maxBarWidth * 0.25;
                await Task.Delay(110);

                _savingProgressBarFill.Width = maxBarWidth * 0.68;

                bool savedOk = CommitAllWorldChangesToDiskSynchronously();

                await Task.Delay(140);
                _savingProgressBarFill.Width = maxBarWidth;
                await Task.Delay(90);

                if (savedOk)
                {
                    ShowUmeSaveResultModal(isSuccess: true);
                    TxtStatusLeft.Text = "The world has been saved.";
                }
                else
                {
                    ShowUmeSaveResultModal(isSuccess: false);
                    TxtStatusLeft.Text = "Error: The world could not be saved!";
                }
            }
            catch
            {
                ShowUmeSaveResultModal(isSuccess: false);
                TxtStatusLeft.Text = "Error: The world could not be saved!";
            }
        }

        private void BtnSaveCompleteOk_Click(object sender, RoutedEventArgs e)
        {
            _saveCompleteBox.Visibility = Visibility.Collapsed;
            _savingProgressBox.Visibility = Visibility.Collapsed;
            _saveModalOverlay.Visibility = Visibility.Collapsed;
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
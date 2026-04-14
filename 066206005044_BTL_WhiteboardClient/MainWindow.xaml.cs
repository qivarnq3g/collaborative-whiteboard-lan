using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Btl.Whiteboard.Client.Networking;
using Btl.Whiteboard.Contracts;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Text;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow : Window
{
    private const double MinStrokePointDistance = 1.2;
    private const double MinRenderedStrokePointDistance = 0.2;
    private const int MaxStrokePointsPerFlush = 4;
    private const int MaxTrackedLocalStrokes = 20_000;
    private const string DefaultStrokeColorHex = "#1F6FEB";
    private const double DefaultStrokeThickness = 2.4;
    private const double MinStrokeThickness = 1;
    private const double MaxStrokeThickness = 24;
    private const int MaxRecentColors = 12;
    private const int MaxChatMessages = 800;
    private const double ChatAutoScrollThreshold = 28;
    private const double MinLayerOpacity = 0.05;
    private static readonly TimeSpan DiscoveryBackgroundInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StrokePointInterval = TimeSpan.FromMilliseconds(16);

    private enum DiscoveryRefreshOrigin
    {
        Startup,
        Manual,
        Background
    }

    private enum DrawingTool
    {
        Pencil,
        Line,
        Rectangle,
        Ellipse,
        Eraser
    }

    private sealed class LayerState : INotifyPropertyChanged
    {
        private string name = string.Empty;
        private bool isVisible = true;
        private bool isLocked;
        private bool isActive;
        private bool isSelected;
        private bool isNameEditing;
        private int order;
        private double opacity = 1.0;

        public string LayerId { get; init; } = string.Empty;

        public string Name
        {
            get => name;
            set
            {
                if (SetField(ref name, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public bool IsVisible
        {
            get => isVisible;
            set
            {
                if (SetField(ref isVisible, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(VisibilitySlashVisibility));
                    OnPropertyChanged(nameof(VisibilityTooltip));
                }
            }
        }

        public bool IsLocked
        {
            get => isLocked;
            set
            {
                if (SetField(ref isLocked, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(LockGlyph));
                    OnPropertyChanged(nameof(LockTooltip));
                }
            }
        }

        public bool IsActive
        {
            get => isActive;
            set
            {
                if (SetField(ref isActive, value))
                {
                    OnPropertyChanged(nameof(RowBorderThickness));
                    OnPropertyChanged(nameof(RowBackgroundBrush));
                    OnPropertyChanged(nameof(RowBorderBrush));
                    OnPropertyChanged(nameof(NameFontWeight));
                }
            }
        }

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (SetField(ref isSelected, value))
                {
                    OnPropertyChanged(nameof(RowBorderThickness));
                    OnPropertyChanged(nameof(RowBackgroundBrush));
                    OnPropertyChanged(nameof(RowBorderBrush));
                    OnPropertyChanged(nameof(NameFontWeight));
                }
            }
        }

        public bool IsNameEditing
        {
            get => isNameEditing;
            set
            {
                if (SetField(ref isNameEditing, value))
                {
                    OnPropertyChanged(nameof(NameDisplayVisibility));
                    OnPropertyChanged(nameof(NameEditorVisibility));
                }
            }
        }

        public int Order
        {
            get => order;
            set => SetField(ref order, value);
        }

        public double Opacity
        {
            get => opacity;
            set => SetField(ref opacity, value);
        }

        public string LockGlyph => IsLocked ? "\uE72E" : "\uE785";

        public Visibility VisibilitySlashVisibility => IsVisible ? Visibility.Collapsed : Visibility.Visible;

        public string VisibilityTooltip => IsVisible ? "Hide layer" : "Show layer";

        public string LockTooltip => IsLocked ? "Unlock layer" : "Lock layer";

        public Thickness RowBorderThickness => IsActive
            ? new Thickness(2)
            : IsSelected
                ? new Thickness(1.5)
                : new Thickness(1);

        public Brush RowBackgroundBrush => IsActive
            ? ResolveThemeBrush("BadgeBackgroundBrush")
            : IsSelected
                ? ResolveThemeBrush("PanelHeaderBackgroundBrush")
                : ResolveThemeBrush("PanelBackgroundBrush");

        public Brush RowBorderBrush => IsActive
            ? ResolveThemeBrush("BadgeTextBrush")
            : IsSelected
                ? ResolveThemeBrush("ColorSwatchSelectedBorderBrush")
                : ResolveThemeBrush("PanelStrokeBrush");

        public FontWeight NameFontWeight => new()
        {
            Weight = IsActive ? (ushort)600 : IsSelected ? (ushort)500 : (ushort)400
        };

        public Visibility NameDisplayVisibility => IsNameEditing ? Visibility.Collapsed : Visibility.Visible;

        public Visibility NameEditorVisibility => IsNameEditing ? Visibility.Visible : Visibility.Collapsed;

        public string DisplayName => $"{Name}{(IsVisible ? string.Empty : " (Hidden)")}{(IsLocked ? " [Locked]" : string.Empty)}";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void NotifyThemeResourcesChanged()
        {
            OnPropertyChanged(nameof(RowBackgroundBrush));
            OnPropertyChanged(nameof(RowBorderBrush));
        }

        public override string ToString()
        {
            return DisplayName;
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static Brush ResolveThemeBrush(string resourceKey)
        {
            return MainWindow.ResolveThemeBrush(resourceKey);
        }
    }

    private readonly LanDiscoveryClient discoveryClient = new();
    private readonly TcpWhiteboardSession session = new();
    private readonly ObservableCollection<DiscoveredRoom> rooms = new();
    private readonly ObservableCollection<string> onlineUsers = new();
    private readonly ObservableCollection<string> chatMessages = new();
    private readonly HashSet<string> chatMessageKeys = new(StringComparer.Ordinal);
    private readonly Queue<string?> chatMessageKeyOrder = new();
    private readonly HashSet<string> localStrokeIds = new(StringComparer.Ordinal);
    private readonly Queue<string> localStrokeOrder = new();
    private readonly ObservableCollection<LayerState> layers = new();
    private readonly List<string> recentColors = new();
    private readonly Dictionary<string, Polyline> activeStrokes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> strokeLayers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LayerState> layersById = new(StringComparer.Ordinal);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer discoveryTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer strokeSendTimer;

    private int refreshRoomsInProgress;
    private bool isDrawing;
    private string? activeStrokeId;
    private readonly Queue<Point> pendingStrokePoints = new();
    private bool hasLastSentStrokePoint;
    private Point lastSentStrokePoint;
    private bool isStrokeSendInFlight;
    private Task<bool>? activeStrokeStartSendTask;
    private bool hasSentActiveStrokeStart;
    private bool isUpdatingThemeSelection;
    private bool isUpdatingToolSelection;
    private bool isUpdatingLayerSelection;
    private bool isMutatingLayersCollection;
    private bool isUpdatingColorSelection;
    private bool isUpdatingLayerOpacity;
    private bool isRootVisualReady;
    private bool isShapePreviewActive;
    private bool isWindowClosed;
    private readonly CancellationTokenSource refreshRoomsCancellationSource = new();
    private string selectedStrokeColorHex = DefaultStrokeColorHex;
    private double selectedStrokeThickness = DefaultStrokeThickness;
    private string activeStrokeColorHex = DefaultStrokeColorHex;
    private double activeStrokeThickness = DefaultStrokeThickness;
    private string activeLayerId = "layer-1";
    private string activeStrokeLayerId = "layer-1";
    private int nextLayerIndex = 2;
    private DrawingTool selectedDrawingTool = DrawingTool.Pencil;
    private Point shapeStartPoint;
    private Shape? shapePreviewShape;
    private string lastRoomsSignature = string.Empty;
    private string connectionState = "Disconnected";
    private string connectedEndpoint = string.Empty;
    private ScrollViewer? chatListScrollViewer;

    public MainWindow()
    {
        InitializeComponent();

        if (Content is FrameworkElement root)
        {
            root.Loaded += MainRoot_Loaded;
            root.ActualThemeChanged += MainRoot_ActualThemeChanged;
        }

        RoomListView.ItemsSource = rooms;
        OnlineUsersListView.ItemsSource = onlineUsers;
        ChatListView.ItemsSource = chatMessages;
        ActiveLayerComboBox.ItemsSource = layers;
        LayersListView.ItemsSource = layers;

        InitializeLayers();

        UserNameTextBox.Text = Environment.UserName;
        InitializeThemeSelection();
        InitializeStrokeToolSelection();
        SelectDrawingTool(DrawingTool.Pencil);
        UpdateRoomVisuals();
        UpdateOnlineUserVisuals();
        UpdateChatVisuals();

        session.EnvelopeReceived += Session_EnvelopeReceived;
        session.ConnectionStateChanged += Session_ConnectionStateChanged;

        discoveryTimer = DispatcherQueue.CreateTimer();
        discoveryTimer.Interval = DiscoveryBackgroundInterval;
        discoveryTimer.Tick += DiscoveryTimer_Tick;
        discoveryTimer.Start();

        strokeSendTimer = DispatcherQueue.CreateTimer();
        strokeSendTimer.Interval = StrokePointInterval;
        strokeSendTimer.Tick += StrokeSendTimer_Tick;

        Closed += MainWindow_Closed;

        SetConnectionState("Disconnected", "Start server and refresh rooms to join a session.");
        _ = RefreshRoomsAsync(DiscoveryRefreshOrigin.Startup);
    }

    private void MainRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement root)
        {
            root.Loaded -= MainRoot_Loaded;
        }

        isRootVisualReady = true;
        UpdateLayerControls();
        RefreshThemeBoundVisuals();
        EnsureChatScrollViewer();
    }

    private async void DiscoveryTimer_Tick(object? sender, object e)
    {
        try
        {
            await RefreshRoomsAsync(DiscoveryRefreshOrigin.Background).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (refreshRoomsCancellationSource.IsCancellationRequested || isWindowClosed)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Background discovery error: {exception.Message}");
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isUpdatingThemeSelection)
        {
            return;
        }

        if (Content is not FrameworkElement root)
        {
            return;
        }

        ElementTheme requestedTheme = ThemeComboBox.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        root.RequestedTheme = requestedTheme;
        RefreshThemeBoundVisuals();
    }

    private void MainRoot_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (isWindowClosed)
        {
            return;
        }

        RefreshThemeBoundVisuals();
    }

    private async void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsTextInputFocused())
        {
            if (TryHandlePanelTabShortcut(e.Key))
            {
                e.Handled = true;
            }

            return;
        }

        if (TryHandlePanelTabShortcut(e.Key))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.P:
                SelectDrawingTool(DrawingTool.Pencil);
                e.Handled = true;
                break;

            case VirtualKey.L:
                SelectDrawingTool(DrawingTool.Line);
                e.Handled = true;
                break;

            case VirtualKey.R:
                SelectDrawingTool(DrawingTool.Rectangle);
                e.Handled = true;
                break;

            case VirtualKey.O:
                SelectDrawingTool(DrawingTool.Ellipse);
                e.Handled = true;
                break;

            case VirtualKey.E:
                SelectDrawingTool(DrawingTool.Eraser);
                e.Handled = true;
                break;

            case VirtualKey.N:
                LayerPanelAddButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.U:
                await SendUndoAsync();
                e.Handled = true;
                break;

            case VirtualKey.Y:
                await SendRedoAsync();
                e.Handled = true;
                break;

            case VirtualKey.Delete:
                LayerDeleteButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private bool TryHandlePanelTabShortcut(VirtualKey key)
    {
        if (RightPanelTabView is null)
        {
            return false;
        }

        int targetIndex = key switch
        {
            VirtualKey.F1 => 0,
            VirtualKey.F2 => 1,
            VirtualKey.F3 => 2,
            _ => -1
        };

        if (targetIndex < 0 || targetIndex >= RightPanelTabView.TabItems.Count)
        {
            return false;
        }

        RightPanelTabView.SelectedIndex = targetIndex;
        return true;
    }

    private bool IsTextInputFocused()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null)
        {
            return false;
        }

        return FocusManager.GetFocusedElement(root.XamlRoot) is TextBox;
    }

    private void StrokeColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isUpdatingColorSelection)
        {
            return;
        }

        ApplySelectedColorHex(GetSelectedStrokeColorHex(), syncComboSelection: false, trackRecent: true);
    }

    private void StrokeThicknessComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedStrokeThickness = GetSelectedStrokeThickness();
        UpdateToolStatusText();
    }

    private void CustomColorTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        ApplyCustomColorFromInput();
    }

    private void ApplyCustomColorButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCustomColorFromInput();
    }

    private void ColorSwatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button colorButton || colorButton.Tag is not string swatchHex)
        {
            return;
        }

        ApplySelectedColorHex(swatchHex, syncComboSelection: true, trackRecent: true);
    }

    private void AdvancedColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (isUpdatingColorSelection)
        {
            return;
        }

        string colorHex = ToHexColor(args.NewColor);
        ApplySelectedColorHex(colorHex, syncComboSelection: true, trackRecent: false);
    }

    private void AdvancedColorPickerFlyout_Closed(object sender, object e)
    {
        TrackRecentColor(selectedStrokeColorHex);
        RefreshColorSwatchSelection();
    }

    private void ToolToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toolButton)
        {
            return;
        }

        DrawingTool tool = toolButton.Name switch
        {
            nameof(LineToolToggleButton) => DrawingTool.Line,
            nameof(RectangleToolToggleButton) => DrawingTool.Rectangle,
            nameof(EllipseToolToggleButton) => DrawingTool.Ellipse,
            nameof(EraserToolToggleButton) => DrawingTool.Eraser,
            _ => DrawingTool.Pencil
        };

        SelectDrawingTool(tool);
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        await SendUndoAsync();
    }

    private async void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        await SendRedoAsync();
    }

    private async Task SendUndoAsync()
    {
        if (!session.IsConnected)
        {
            SetConnectionState("Disconnected", "Connect to a room before undo.");
            return;
        }

        if (!CanApplyHistoryAction("Undo"))
        {
            return;
        }

        try
        {
            await session.SendUndoAsync(GetSafeUserName(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Undo failed: {exception.Message}");
        }
    }

    private async Task SendRedoAsync()
    {
        if (!session.IsConnected)
        {
            SetConnectionState("Disconnected", "Connect to a room before redo.");
            return;
        }

        if (!CanApplyHistoryAction("Redo"))
        {
            return;
        }

        try
        {
            await session.SendRedoAsync(GetSafeUserName(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Redo failed: {exception.Message}");
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        isWindowClosed = true;
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged -= MainRoot_ActualThemeChanged;
        }

        refreshRoomsCancellationSource.Cancel();
        session.EnvelopeReceived -= Session_EnvelopeReceived;
        session.ConnectionStateChanged -= Session_ConnectionStateChanged;

        discoveryTimer.Stop();
        strokeSendTimer.Stop();
        ResetLocalDrawingState();
        await session.DisposeAsync();
        refreshRoomsCancellationSource.Dispose();
    }

    private void ResetLocalDrawingState()
    {
        isDrawing = false;
        isShapePreviewActive = false;
        RemoveShapePreview();
        activeStrokeId = null;
        activeStrokeLayerId = activeLayerId;
        pendingStrokePoints.Clear();
        hasLastSentStrokePoint = false;
        activeStrokeStartSendTask = null;
        hasSentActiveStrokeStart = false;
        isStrokeSendInFlight = false;
        strokeSendTimer.Stop();
    }

    private bool CanApplyHistoryAction(string actionName)
    {
        bool strokeStartPending = activeStrokeStartSendTask is { IsCompleted: false };
        if (isDrawing || isShapePreviewActive || strokeStartPending || pendingStrokePoints.Count > 0)
        {
            SetStatus($"{actionName} is unavailable while drawing. Release pointer and try again.");
            return false;
        }

        return true;
    }

    private void TrackLocalStroke(string strokeId)
    {
        if (string.IsNullOrWhiteSpace(strokeId))
        {
            return;
        }

        if (localStrokeIds.Add(strokeId))
        {
            localStrokeOrder.Enqueue(strokeId);
        }

        while (localStrokeOrder.Count > MaxTrackedLocalStrokes)
        {
            string expiredStrokeId = localStrokeOrder.Dequeue();
            localStrokeIds.Remove(expiredStrokeId);
        }
    }

    private bool IsLocalStroke(string strokeId)
    {
        return !string.IsNullOrWhiteSpace(strokeId) && localStrokeIds.Contains(strokeId);
    }

    private async void RefreshRoomsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRoomsAsync(DiscoveryRefreshOrigin.Manual);
    }

    private async Task RefreshRoomsAsync(DiscoveryRefreshOrigin origin)
    {
        if (isWindowClosed)
        {
            return;
        }

        if (Interlocked.Exchange(ref refreshRoomsInProgress, 1) == 1)
        {
            return;
        }

        bool isBackgroundRefresh = origin == DiscoveryRefreshOrigin.Background;
        bool shouldDisableRefreshButton = origin == DiscoveryRefreshOrigin.Manual;
        CancellationToken refreshToken = refreshRoomsCancellationSource.Token;

        try
        {
            if (shouldDisableRefreshButton)
            {
                if (DispatcherQueue.HasThreadAccess)
                {
                    RefreshRoomsButton.IsEnabled = false;
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!isWindowClosed)
                        {
                            RefreshRoomsButton.IsEnabled = false;
                        }
                    });
                }
            }

            bool shouldAnnounceScanning = !isBackgroundRefresh && !session.IsConnected;
            if (shouldAnnounceScanning)
            {
                SetConnectionState("Discovering", "Scanning LAN for rooms...");
            }

            IReadOnlyList<DiscoveredRoom> foundRooms = await discoveryClient
                .DiscoverAsync(DiscoveryProtocol.DefaultDiscoveryPort, string.Empty, refreshToken)
                .ConfigureAwait(false);

            if (refreshToken.IsCancellationRequested || isWindowClosed)
            {
                return;
            }

            IReadOnlyList<DiscoveredRoom> safeRooms = foundRooms ?? Array.Empty<DiscoveredRoom>();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (isWindowClosed)
                {
                    return;
                }

                bool roomsChanged = ApplyDiscoveredRooms(safeRooms);

                if (!session.IsConnected && (shouldAnnounceScanning || (isBackgroundRefresh && roomsChanged)))
                {
                    SetConnectionState("Disconnected", BuildDiscoveryStatusMessage(rooms.Count));
                }
            });
        }
        catch (OperationCanceledException) when (refreshRoomsCancellationSource.IsCancellationRequested || isWindowClosed)
        {
        }
        catch (Exception exception)
        {
            if (!isBackgroundRefresh && !isWindowClosed)
            {
                SetConnectionState("Error", $"Discovery error: {exception.Message}");
            }
        }
        finally
        {
            if (shouldDisableRefreshButton && !isWindowClosed)
            {
                if (DispatcherQueue.HasThreadAccess)
                {
                    RefreshRoomsButton.IsEnabled = true;
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!isWindowClosed)
                        {
                            RefreshRoomsButton.IsEnabled = true;
                        }
                    });
                }
            }

            Interlocked.Exchange(ref refreshRoomsInProgress, 0);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (session.IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        if (RoomListView.SelectedItem is not DiscoveredRoom room)
        {
            SetStatus("Select a discovered room first.");
            return;
        }

        if (room.ProtocolVersion is not int protocolVersion ||
            protocolVersion != DiscoveryProtocol.CurrentProtocolVersion)
        {
            string roomProtocolText = room.ProtocolVersion?.ToString() ?? "unknown";
            SetConnectionState(
                "Error",
                $"Incompatible room protocol ({roomProtocolText}). Expected protocol {DiscoveryProtocol.CurrentProtocolVersion}. Rebuild/restart the server.");
            return;
        }

        string userName = GetSafeUserName();

        try
        {
            SetConnectionState("Connecting", $"Connecting to {room.Endpoint}...");
            await session
                .ConnectAsync(room.Address, room.TcpPort, userName, CancellationToken.None);

            connectedEndpoint = room.Endpoint;
            SetConnectionState("Connected", $"Connected to {connectedEndpoint}");
            BeginChatSession(room.Title, room.Endpoint);
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Connect failed: {exception.Message}");
        }
    }

    private async Task DisconnectAsync()
    {
        await session.DisconnectAsync();
        SetConnectionState("Disconnected", "Disconnected by user.");
        AppendChatLine("[System] Disconnected.");
    }

    private async void SendChatButton_Click(object sender, RoutedEventArgs e)
    {
        await SendChatAsync();
    }

    private async void ChatInputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        if (!session.IsConnected)
        {
            SetConnectionState("Disconnected", "Connect to a room before sending messages.");
            return;
        }

        string text = ChatInputTextBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        ChatInputTextBox.Text = string.Empty;

        try
        {
            await session.SendChatAsync(GetSafeUserName(), text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetConnectionState("Error", $"Send chat failed: {exception.Message}");
        }
    }

    private string GetSafeUserName()
    {
        string userName = UserNameTextBox.Text.Trim();
        if (userName.Length == 0)
        {
            userName = $"User-{Environment.MachineName}";
            UserNameTextBox.Text = userName;
        }

        return userName;
    }

    private void SetStatus(string status)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ConnectionStatusTextBlock.Text = status;
            return;
        }

        DispatcherQueue.TryEnqueue(() => ConnectionStatusTextBlock.Text = status);
    }

    private void SetConnectionState(string state, string detail)
    {
        if (string.Equals(connectionState, state, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(string.IsNullOrWhiteSpace(detail) ? state : $"{state}: {detail}");
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateConnectionControls(state);
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => UpdateConnectionControls(state));
            }

            return;
        }

        connectionState = state;

        string statusText = string.IsNullOrWhiteSpace(detail)
            ? state
            : $"{state}: {detail}";

        SetStatus(statusText);

        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateConnectionControls(state);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateConnectionControls(state);
        });
    }

    private void InitializeThemeSelection()
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        int selectedIndex = root.RequestedTheme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0
        };

        isUpdatingThemeSelection = true;
        ThemeComboBox.SelectedIndex = selectedIndex;
        isUpdatingThemeSelection = false;
    }

    private void InitializeStrokeToolSelection()
    {
        selectedStrokeColorHex = NormalizeStrokeColorHex(DefaultStrokeColorHex);
        selectedStrokeThickness = NormalizeStrokeThickness(DefaultStrokeThickness);

        activeStrokeColorHex = selectedStrokeColorHex;
        activeStrokeThickness = selectedStrokeThickness;

        for (int index = 0; index < StrokeColorComboBox.Items.Count; index++)
        {
            if (StrokeColorComboBox.Items[index] is ComboBoxItem item &&
                string.Equals(NormalizeStrokeColorHex(item.Tag as string), selectedStrokeColorHex, StringComparison.Ordinal))
            {
                StrokeColorComboBox.SelectedIndex = index;
                break;
            }
        }

        if (StrokeColorComboBox.SelectedIndex < 0 && StrokeColorComboBox.Items.Count > 0)
        {
            StrokeColorComboBox.SelectedIndex = 0;
        }

        for (int index = 0; index < StrokeThicknessComboBox.Items.Count; index++)
        {
            if (StrokeThicknessComboBox.Items[index] is not ComboBoxItem item)
            {
                continue;
            }

            if (!double.TryParse(item.Tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double thickness))
            {
                continue;
            }

            if (Math.Abs(NormalizeStrokeThickness(thickness) - selectedStrokeThickness) < 0.001)
            {
                StrokeThicknessComboBox.SelectedIndex = index;
                break;
            }
        }

        if (StrokeThicknessComboBox.SelectedIndex < 0 && StrokeThicknessComboBox.Items.Count > 0)
        {
            StrokeThicknessComboBox.SelectedIndex = 0;
        }

        selectedStrokeColorHex = GetSelectedStrokeColorHex();
        selectedStrokeThickness = GetSelectedStrokeThickness();
        activeStrokeColorHex = selectedStrokeColorHex;
        activeStrokeThickness = selectedStrokeThickness;

        ApplySelectedColorHex(selectedStrokeColorHex, syncComboSelection: true, trackRecent: true);
        SelectDrawingTool(selectedDrawingTool);
        UpdateLayerControls();
        UpdateToolStatusText();
        UpdateCanvasPointerText(new Point(0, 0));
    }

    private static string NormalizeStrokeColorHex(string? colorHex)
    {
        string safe = (colorHex ?? string.Empty).Trim();
        if (safe.Length == 0)
        {
            return DefaultStrokeColorHex;
        }

        if (safe[0] != '#')
        {
            safe = $"#{safe}";
        }

        if (!IsValidHexColor(safe))
        {
            return DefaultStrokeColorHex;
        }

        return safe.ToUpperInvariant();
    }

    private static bool TryNormalizeUserHexColor(string? colorInput, out string normalizedColorHex)
    {
        normalizedColorHex = DefaultStrokeColorHex;
        string candidate = (colorInput ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (!candidate.StartsWith('#'))
        {
            candidate = $"#{candidate}";
        }

        if (!IsValidHexColor(candidate))
        {
            return false;
        }

        normalizedColorHex = candidate.ToUpperInvariant();
        return true;
    }

    private static double NormalizeStrokeThickness(double thickness)
    {
        if (double.IsNaN(thickness) || double.IsInfinity(thickness))
        {
            return DefaultStrokeThickness;
        }

        if (thickness < MinStrokeThickness)
        {
            return MinStrokeThickness;
        }

        if (thickness > MaxStrokeThickness)
        {
            return MaxStrokeThickness;
        }

        return thickness;
    }

    private static double NormalizeLayerOpacity(double opacity)
    {
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            return 1.0;
        }

        if (opacity < MinLayerOpacity)
        {
            return MinLayerOpacity;
        }

        if (opacity > 1.0)
        {
            return 1.0;
        }

        return opacity;
    }

    private static bool TryParseHexColor(string colorHex, out Windows.UI.Color color)
    {
        color = default;
        string safe = NormalizeStrokeColorHex(colorHex);
        ReadOnlySpan<char> digits = safe.AsSpan(1);

        if (digits.Length == 6)
        {
            if (!byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red) ||
                !byte.TryParse(digits.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green) ||
                !byte.TryParse(digits.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
            {
                return false;
            }

            color = ColorHelper.FromArgb(255, red, green, blue);
            return true;
        }

        if (digits.Length == 8)
        {
            if (!byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte alpha) ||
                !byte.TryParse(digits.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red) ||
                !byte.TryParse(digits.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green) ||
                !byte.TryParse(digits.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
            {
                return false;
            }

            color = ColorHelper.FromArgb(alpha, red, green, blue);
            return true;
        }

        return false;
    }

    private static bool IsValidHexColor(string colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex) || colorHex[0] != '#')
        {
            return false;
        }

        ReadOnlySpan<char> digits = colorHex.AsSpan(1);
        if (digits.Length is not (6 or 8))
        {
            return false;
        }

        foreach (char digit in digits)
        {
            if (!Uri.IsHexDigit(digit))
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateConnectionControls(string state)
    {
        ConnectButton.Content = session.IsConnected ? "Disconnect" : "Connect";
        ConnectButton.IsEnabled = !string.Equals(state, "Connecting", StringComparison.OrdinalIgnoreCase);
        CanvasHintTextBlock.Visibility = session.IsConnected ? Visibility.Collapsed : Visibility.Visible;

        string compactState = GetCompactState(state);
        ConnectionBadgeTextBlock.Text = compactState;
        CanvasConnectionTextBlock.Text = compactState;

        ApplyStatusBadge(state);

        if (!session.IsConnected)
        {
            CanvasHintTextBlock.Text = string.Equals(state, "Discovering", StringComparison.OrdinalIgnoreCase)
                ? "Scanning LAN for available rooms..."
                : "Connect to a room to start drawing in realtime";
        }
    }

    private void RefreshThemeBoundVisuals()
    {
        RefreshColorSwatchSelection();
        RefreshLayerThemeResources();
        ApplyStatusBadge(connectionState);
    }

    private void RefreshLayerThemeResources()
    {
        foreach (LayerState layer in layers)
        {
            layer.NotifyThemeResourcesChanged();
        }
    }

    private void ApplyStatusBadge(string state)
    {
        string foregroundKey;
        string backgroundKey;

        if (state.StartsWith("Connection error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
        {
            foregroundKey = "ErrorBadgeTextBrush";
            backgroundKey = "ErrorBadgeBackgroundBrush";
        }
        else if (string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase))
        {
            foregroundKey = "PositiveBadgeTextBrush";
            backgroundKey = "PositiveBadgeBackgroundBrush";
        }
        else if (string.Equals(state, "Connecting", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(state, "Discovering", StringComparison.OrdinalIgnoreCase))
        {
            foregroundKey = "WarningBadgeTextBrush";
            backgroundKey = "WarningBadgeBackgroundBrush";
        }
        else
        {
            foregroundKey = "BadgeTextBrush";
            backgroundKey = "BadgeBackgroundBrush";
        }

        Brush foregroundBrush = ResolveThemeBrush(foregroundKey);
        Brush backgroundBrush = ResolveThemeBrush(backgroundKey);

        ConnectionBadgeBorder.Background = backgroundBrush;
        CanvasConnectionBadgeBorder.Background = backgroundBrush;

        ConnectionBadgeBorder.BorderBrush = foregroundBrush;
        CanvasConnectionBadgeBorder.BorderBrush = foregroundBrush;

        ConnectionStateDot.Fill = foregroundBrush;
        CanvasConnectionStateDot.Fill = foregroundBrush;

        ConnectionBadgeTextBlock.Foreground = foregroundBrush;
        CanvasConnectionTextBlock.Foreground = foregroundBrush;
    }

    private static Brush ResolveThemeBrush(string key)
    {
        if (TryResolveThemeResource(key, out object? value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    private static bool TryResolveThemeResource(string key, out object? value)
    {
        value = null;
        if (Application.Current is null)
        {
            return false;
        }

        ResourceDictionary resources = Application.Current.Resources;
        string currentThemeKey = GetCurrentThemeDictionaryKey();

        if (TryGetThemeDictionaryValue(resources, currentThemeKey, key, out value))
        {
            return true;
        }

        if (!string.Equals(currentThemeKey, "Default", StringComparison.Ordinal) &&
            TryGetThemeDictionaryValue(resources, "Default", key, out value))
        {
            return true;
        }

        return resources.TryGetValue(key, out value);
    }

    private static bool TryGetThemeDictionaryValue(
        ResourceDictionary resources,
        string themeKey,
        string key,
        out object? value)
    {
        value = null;
        if (!resources.ThemeDictionaries.TryGetValue(themeKey, out object? themeDictionaryObject) ||
            themeDictionaryObject is not ResourceDictionary themeDictionary)
        {
            return false;
        }

        return themeDictionary.TryGetValue(key, out value);
    }

    private static string GetCurrentThemeDictionaryKey()
    {
        if (App.MainAppWindow?.Content is FrameworkElement root)
        {
            return root.ActualTheme switch
            {
                ElementTheme.Dark => "Dark",
                ElementTheme.Light => "Light",
                _ => "Default"
            };
        }

        return "Default";
    }
}

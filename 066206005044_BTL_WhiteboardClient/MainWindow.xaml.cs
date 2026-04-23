using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
using Windows.UI.Core;
using Windows.System;
using Windows.UI.Text;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow : Window
{
    private sealed class OnlineUserItem
    {
        public string UserId { get; init; } = string.Empty;
        public string UserName { get; init; } = string.Empty;
        public bool IsOwner { get; init; }
        public bool CanModerate { get; init; }

        public string DisplayName => IsOwner ? $"{UserName} ★" : UserName;
        public string OwnerBadge => IsOwner ? "★" : "";
    }
    private sealed class BannedUserItem
    {
        public string UserId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }
    private const double MinStrokePointDistance = 1.2;
    private const double MinRenderedStrokePointDistance = 0.2;
    private const int MaxStrokePointsPerFlush = 8;
    private const int MaxTrackedLocalStrokes = 20_000;
    private const string DefaultStrokeColorHex = "#1F6FEB";
    private const double DefaultStrokeThickness = 2.4;
    private const double MinStrokeThickness = ProtocolConstants.MinStrokeThickness;
    private const double MaxStrokeThickness = ProtocolConstants.MaxStrokeThickness;
    private const int MaxRecentColors = 12;
    private const int MaxChatMessages = 800;
    private const double ChatAutoScrollThreshold = 28;
    private const double MinLayerOpacity = 0.0;
    private const double MinCanvasZoom = 0.25;
    private const double MaxCanvasZoom = 4.0;
    private const double DefaultCanvasZoom = 1.0;
    private const double CanvasZoomStepMultiplier = 1.1;
    private const int MaxPendingStrokePoints = 2048;
    private const int MaxReleaseDrainIterations = 120;
    private static readonly TimeSpan MaxReleaseDrainDuration = TimeSpan.FromMilliseconds(600);
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
    private readonly ObservableCollection<OnlineUserItem> onlineUsers = new();
    private readonly ObservableCollection<BannedUserItem> bannedUsers = new();
    private readonly Dictionary<string, string> knownUserNames = new(StringComparer.Ordinal);
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
    private readonly SemaphoreSlim strokeSendLoopGate = new(1, 1);

    private int refreshRoomsInProgress;
    private bool isDrawing;
    private string? activeStrokeId;
    private readonly Queue<Point> pendingStrokePoints = new();
    private bool hasLastQueuedStrokePoint;
    private Point lastQueuedStrokePoint;
    private bool hasLastSentStrokePoint;
    private Point lastSentStrokePoint;
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
    private bool isStrokeFinalizing;
    private bool isWindowClosed;
    private bool isRoomLocked;
    private bool isConnectInProgress;
    private bool isRoomPasswordPrepared;
    private bool isAltKeyPressed;
    private bool isSpaceKeyPressed;
    private bool isSpacePanning;
    private Point spacePanStartHostPoint;
    private string? roomOwnerId;
    private string? roomOwnerUserName;
    private string? selectedTargetUserId;
    private double spacePanStartX;
    private double spacePanStartY;
    private readonly CancellationTokenSource refreshRoomsCancellationSource = new();
    private CancellationTokenSource? activeStrokeSendCancellationSource;
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
    private string lastKnownUserName = $"User-{Environment.MachineName}";
    private string? preparedRoomPassword;
    private string? preparedRoomKey;
    private ScrollViewer? chatListScrollViewer;
    private readonly CompositeTransform drawingCanvasViewportTransform = new();
    private readonly RectangleGeometry canvasViewportClipGeometry = new();
    private double canvasZoom = DefaultCanvasZoom;
    private double canvasPanX;
    private double canvasPanY;

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
        BannedUsersComboBox.ItemsSource = bannedUsers;
        ChatListView.ItemsSource = chatMessages;
        ActiveLayerComboBox.ItemsSource = layers;
        LayersListView.ItemsSource = layers;
        CanvasViewportGrid.Clip = canvasViewportClipGeometry;
        DrawingCanvas.RenderTransform = drawingCanvasViewportTransform;
        ApplyCanvasViewportTransform();

        InitializeLayers();

        UserNameTextBox.Text = Environment.UserName;
        lastKnownUserName = UserNameTextBox.Text.Trim();
        InitializeThemeSelection();
        InitializeStrokeToolSelection();
        SelectDrawingTool(DrawingTool.Pencil);
        UpdateRoomVisuals();
        UpdatePasswordInputGuidance();
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

        SetConnectionState("Disconnected", "Select a room to connect");
        _ = RefreshRoomsAsync(DiscoveryRefreshOrigin.Startup);
    }

    private void MainRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement root)
        {
            root.Loaded -= MainRoot_Loaded;
        }

        isRootVisualReady = true;
        UpdateCanvasViewportClip();
        ApplyCanvasViewportTransform();
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

    private CancellationToken BeginActiveStrokeSendSession()
    {
        CancelActiveStrokeSendSession();
        activeStrokeSendCancellationSource = new CancellationTokenSource();
        return activeStrokeSendCancellationSource.Token;
    }

    private CancellationToken GetActiveStrokeSendToken()
    {
        return activeStrokeSendCancellationSource?.Token ?? CancellationToken.None;
    }

    private void CancelActiveStrokeSendSession()
    {
        CancellationTokenSource? source = activeStrokeSendCancellationSource;
        if (source is null)
        {
            return;
        }

        activeStrokeSendCancellationSource = null;
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            source.Dispose();
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
        strokeSendLoopGate.Dispose();
        await session.DisposeAsync();
        refreshRoomsCancellationSource.Dispose();
    }

    private void ResetLocalDrawingState()
    {
        isDrawing = false;
        isShapePreviewActive = false;
        isStrokeFinalizing = false;
        RemoveShapePreview();
        activeStrokeId = null;
        activeStrokeLayerId = activeLayerId;
        pendingStrokePoints.Clear();
        hasLastQueuedStrokePoint = false;
        hasLastSentStrokePoint = false;
        isSpacePanning = false;
        activeStrokeStartSendTask = null;
        hasSentActiveStrokeStart = false;
        CancelActiveStrokeSendSession();
        strokeSendTimer.Stop();
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

        StrokeThicknessNumberBox.Value = selectedStrokeThickness;

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

using System.ComponentModel;
using Drawing = System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace VoicemeeterDelay;

public partial class MainWindow : Window
{
    // Current input callback baseline measured with the nbi insert path.
    private const double DefaultInputPathLatencyMilliseconds = 0.0;
    private const double DefaultOutputPathLatencyMilliseconds = 0.0;
    private const double CompactEndpointWindowWidth = 685.0;
    private const double CompactEndpointWindowHeight = 944.0;
    private const double WideEndpointWindowWidth = 1600.0;
    private const double WideEndpointWindowHeight = 872.0;
    private const double MinimumEndpointWindowWidth = 685.0;
    private const double MinimumEndpointWindowHeight = 850.0;
    private const int DefaultVbanControlPort = 6981;
    private const string DefaultVbanControlStreamName = "Command1";
    private static readonly TimeSpan CallbackWarningDelay = TimeSpan.FromSeconds(1);

    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(39, 49, 58));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(34, 166, 179));
    private static readonly Brush ConfiguredBrush = new SolidColorBrush(Color.FromRgb(226, 184, 74));
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(85, 194, 122));
    private static readonly Brush DelayAccentBrush = new SolidColorBrush(Color.FromRgb(34, 166, 179));
    private static readonly Brush RouteAccentBrush = new SolidColorBrush(Color.FromRgb(85, 194, 122));
    private static readonly Brush RouteActiveBrush = new SolidColorBrush(Color.FromRgb(20, 57, 47));
    private static readonly Brush VolumeAccentBrush = new SolidColorBrush(Color.FromRgb(226, 184, 74));
    private static readonly Brush VbanAccentBrush = new SolidColorBrush(Color.FromRgb(58, 108, 218));
    private static readonly Brush StatusIdleBrush = new SolidColorBrush(Color.FromRgb(34, 42, 48));
    private static readonly Brush StatusIdleTextBrush = new SolidColorBrush(Color.FromRgb(154, 168, 178));
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(26, 32, 37));
    private static readonly Brush FieldBrush = new SolidColorBrush(Color.FromRgb(17, 23, 27));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(237, 243, 246));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(154, 168, 178));
    private static readonly Brush SubtleBorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 74));
    private static readonly Brush AccentTextBrush = new SolidColorBrush(Color.FromRgb(6, 19, 22));
    private static readonly Brush WarmTextBrush = new SolidColorBrush(Color.FromRgb(22, 17, 6));

    private readonly Dictionary<string, EndpointDelaySettings> _settingsByEndpoint = [];
    private readonly List<ChannelEditor> _channelEditors = [];
    private readonly DispatcherTimer _callbackStatusTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private VoicemeeterAudioDelay? _delay;
    private AppOptions? _runningOptions;
    private DateTime _lastArmTimeUtc;
    private bool _missingInputCallbackLogged;
    private bool _missingOutputCallbackLogged;
    private CallbackMode? _selectedMode;
    private IoEndpoint? _selectedEndpoint;
    private EndpointDelaySettings? _selectedSettings;
    private double _inputPathLatencyMilliseconds = DefaultInputPathLatencyMilliseconds;
    private double _outputPathLatencyMilliseconds = DefaultOutputPathLatencyMilliseconds;
    private VoicemeeterKind _voicemeeterKind = VoicemeeterKind.Unknown;
    private VoicemeeterKind _settingsProfileKind = VoicemeeterKind.Potato;
    private MainWindowSettings _mainSettings = new();
    private RoundTripCalibrationWindow? _roundTripCalibrationWindow;
    private VbanCommandsWindow? _vbanCommandsWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _applicationIcon;
    private VbanTextListener? _vbanTextListener;
    private bool _loadingSettings;
    private bool _suppressApplicationSessionRefresh;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        StateChanged += MainWindow_StateChanged;
        _settingsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _settingsSaveTimer.Tick += SettingsSaveTimer_Tick;
        _callbackStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _callbackStatusTimer.Tick += CallbackStatusTimer_Tick;
        _callbackStatusTimer.Start();
        DllPathTextBox.LostFocus += SettingsControl_LostFocus;
        ArmBothStreamsCheckBox.Checked += SettingsControl_Changed;
        ArmBothStreamsCheckBox.Unchecked += SettingsControl_Changed;
        VbanEnableCheckBox.Checked += VbanControl_Changed;
        VbanEnableCheckBox.Unchecked += VbanControl_Changed;
        VbanLocalOnlyCheckBox.Checked += VbanControl_Changed;
        VbanLocalOnlyCheckBox.Unchecked += VbanControl_Changed;
        VbanPortTextBox.LostFocus += VbanControl_LostFocus;
        VbanStreamTextBox.LostFocus += VbanControl_LostFocus;

        _mainSettings = MainWindowSettingsStore.Load();
        _settingsProfileKind = GetStartupProfileKind(_mainSettings);
        _voicemeeterKind = _settingsProfileKind;
        _loadingSettings = true;
        _suppressApplicationSessionRefresh = true;
        ApplySavedSettingsBasics(_mainSettings);
        UpdateMixerTypeText();
        RestoreProfileSettings(GetProfileSettings(_mainSettings, _settingsProfileKind));
        _suppressApplicationSessionRefresh = false;
        _loadingSettings = false;

        UpdateApplicationSessionSelectionState();
        AppendLog("Ready.");
        AppendLog("Saved settings restored. Tick or edit a channel to arm the engine.");
        ApplyVbanControlSettingsFromUi(showErrors: false);
    }

    private void InitializeTrayIcon()
    {
        _applicationIcon = LoadApplicationIcon();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Hide to tray", null, (_, _) => Dispatcher.Invoke(() => HideToTray(showTip: false)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Round trip tester", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ShowFromTray();
            OpenRoundTripCalibrationWindow();
        }));
        menu.Items.Add("VBAN commands", null, (_, _) => Dispatcher.Invoke(() =>
        {
            ShowFromTray();
            OpenVbanCommandsWindow();
        }));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(ShowSettingsFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));

        _trayIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _applicationIcon,
            Text = FormatTrayText("Voicemeeter Delay - engine stopped"),
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var extractedIcon = Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (extractedIcon is not null)
                {
                    return extractedIcon;
                }
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "VoicemeeterDelay.ico");
            if (File.Exists(iconPath))
            {
                return new Drawing.Icon(iconPath);
            }
        }
        catch
        {
            // Falling back to the stock icon is better than interrupting startup.
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private static string FormatTrayText(string text)
    {
        const int maximumTrayTextLength = 63;
        return text.Length <= maximumTrayTextLength
            ? text
            : string.Concat(text.AsSpan(0, maximumTrayTextLength - 3), "...");
    }

    private void UpdateTrayText(string text)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Text = FormatTrayText(text);
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        HideToTray(showTip: true);
    }

    private void HideToTray(bool showTip)
    {
        Hide();
        if (showTip)
        {
            _trayIcon?.ShowBalloonTip(
                1200,
                "Voicemeeter Delay",
                "Still running in the system tray.",
                Forms.ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowSettingsFromTray()
    {
        ShowFromTray();
        DllPathTextBox.BringIntoView();
        DllPathTextBox.Focus();
        DllPathTextBox.SelectAll();
    }

    private void SettingsSaveTimer_Tick(object? sender, EventArgs e)
    {
        _settingsSaveTimer.Stop();
        SaveMainSettings();
    }

    private void SettingsControl_LostFocus(object sender, RoutedEventArgs e)
    {
        ScheduleMainSettingsSave();
    }

    private void SettingsControl_Changed(object sender, RoutedEventArgs e)
    {
        ScheduleMainSettingsSave();
    }

    private void VbanControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        ApplyVbanControlSettingsFromUi(showErrors: true);
        ScheduleMainSettingsSave();
    }

    private void VbanControl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        ApplyVbanControlSettingsFromUi(showErrors: true);
        ScheduleMainSettingsSave();
    }

    private void VbanControl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (_loadingSettings)
        {
            return;
        }

        ApplyVbanControlSettingsFromUi(showErrors: true);
        ScheduleMainSettingsSave();
        if (sender is UIElement element)
        {
            element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        e.Handled = true;
    }

    private void ScheduleMainSettingsSave()
    {
        if (_loadingSettings)
        {
            return;
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SaveMainSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        _mainSettings = CaptureMainSettings();
        MainWindowSettingsStore.Save(_mainSettings);
    }

    private MainWindowSettings CaptureMainSettings()
    {
        var settings = new MainWindowSettings
        {
            DllPath = string.IsNullOrWhiteSpace(DllPathTextBox.Text) ? null : DllPathTextBox.Text.Trim(),
            ArmBothStreams = ArmBothStreamsCheckBox.IsChecked == true,
            VbanControlEnabled = VbanEnableCheckBox.IsChecked == true,
            VbanControlPort = ParseVbanControlPortOrDefault(),
            VbanControlStreamName = string.IsNullOrWhiteSpace(VbanStreamTextBox.Text)
                ? DefaultVbanControlStreamName
                : VbanStreamTextBox.Text.Trim(),
            VbanControlLocalOnly = VbanLocalOnlyCheckBox.IsChecked != false,
            LastProfileKind = _settingsProfileKind,
            Profiles = _mainSettings.Profiles
                .Where(profile => profile.Kind != _settingsProfileKind)
                .Select(CloneProfileSettings)
                .ToList()
        };
        settings.Profiles.Add(CaptureCurrentProfileSettings(_settingsProfileKind));
        return settings;
    }

    private MainWindowProfileSettings CaptureCurrentProfileSettings(VoicemeeterKind kind)
    {
        return new MainWindowProfileSettings
        {
            Kind = kind,
            SelectedMode = _selectedMode ?? CallbackMode.None,
            SelectedEndpointName = _selectedEndpoint?.Name,
            InputPathLatencyMilliseconds = RoundDelay(_inputPathLatencyMilliseconds),
            OutputPathLatencyMilliseconds = RoundDelay(_outputPathLatencyMilliseconds),
            Endpoints = _settingsByEndpoint.Values
                .Where(IsEndpointSupported)
                .Select(static settings => new EndpointDelaySettingsSnapshot
                {
                    Mode = settings.Mode,
                    EndpointName = settings.Endpoint.Name,
                    ChannelCount = settings.Endpoint.ChannelCount,
                    Enabled = settings.Enabled.ToArray(),
                    DelayMilliseconds = settings.DelayMilliseconds.ToArray(),
                    VolumePercent = settings.VolumePercent.ToArray(),
                    RouteEnabled = settings.RouteEnabled.ToArray(),
                    RouteDestinationBusIndex = settings.RouteDestinationBusIndex.ToArray(),
                    RouteDestinationChannelOffset = settings.RouteDestinationChannelOffset.ToArray(),
                    RouteMuteNormal = settings.RouteMuteNormal.ToArray(),
                    RouteDestinations = settings.RouteDestinations
                        .Select(static destinations => destinations.Select(CloneRouteDestination).ToList())
                        .ToList()
                })
                .ToList()
        };
    }

    private void ApplySavedSettingsBasics(MainWindowSettings settings)
    {
        DllPathTextBox.Text = settings.DllPath ?? string.Empty;
        ArmBothStreamsCheckBox.IsChecked = settings.ArmBothStreams;
        VbanEnableCheckBox.IsChecked = settings.VbanControlEnabled;
        VbanPortTextBox.Text = SanitizeVbanControlPort(settings.VbanControlPort).ToString(CultureInfo.InvariantCulture);
        VbanStreamTextBox.Text = string.IsNullOrWhiteSpace(settings.VbanControlStreamName)
            ? DefaultVbanControlStreamName
            : settings.VbanControlStreamName.Trim();
        VbanLocalOnlyCheckBox.IsChecked = settings.VbanControlLocalOnly;
    }

    private void RestoreProfileSettings(MainWindowProfileSettings profile)
    {
        _settingsProfileKind = NormalizeProfileKind(profile.Kind);
        _inputPathLatencyMilliseconds = SanitizeDelay(profile.InputPathLatencyMilliseconds, DefaultInputPathLatencyMilliseconds);
        _outputPathLatencyMilliseconds = SanitizeDelay(profile.OutputPathLatencyMilliseconds, DefaultOutputPathLatencyMilliseconds);
        _selectedMode = null;
        _selectedEndpoint = null;
        _selectedSettings = null;
        RestoreEndpointSettings(profile);
        RestoreSavedSelection(profile);

        if (_selectedMode is null)
        {
            EndpointButtonsPanel.Children.Clear();
            BuildChannelStrips(null);
        }

        UpdatePathLatencyControl();
        RefreshEndpointButtonBrushes();
        UpdateApplicationSessionSelectionState();
    }

    private void RestoreEndpointSettings(MainWindowProfileSettings profile)
    {
        _settingsByEndpoint.Clear();
        foreach (var snapshot in profile.Endpoints)
        {
            if (!IsSelectableMode(snapshot.Mode)
                || string.IsNullOrWhiteSpace(snapshot.EndpointName)
                || snapshot.ChannelCount <= 0)
            {
                continue;
            }

            var endpoint = FindEndpoint(snapshot.Mode, snapshot.EndpointName);
            if (endpoint is null || endpoint.ChannelCount != snapshot.ChannelCount)
            {
                continue;
            }

            var restoredSettings = new EndpointDelaySettings(snapshot.Mode, endpoint, GetPathLatencyMilliseconds(snapshot.Mode));
            restoredSettings.ApplySnapshot(snapshot, GetPathLatencyMilliseconds(snapshot.Mode));
            _settingsByEndpoint[GetSettingsKey(snapshot.Mode, endpoint)] = restoredSettings;
        }
    }

    private void RestoreSavedSelection(MainWindowProfileSettings settings)
    {
        if (!IsSelectableMode(settings.SelectedMode))
        {
            return;
        }

        SelectMode(settings.SelectedMode);
        if (!string.IsNullOrWhiteSpace(settings.SelectedEndpointName)
            && FindEndpoint(settings.SelectedMode, settings.SelectedEndpointName) is { } endpoint)
        {
            SelectEndpoint(endpoint);
        }
    }

    private static VoicemeeterKind GetStartupProfileKind(MainWindowSettings settings)
    {
        var normalized = NormalizeProfileKind(settings.LastProfileKind);
        if (settings.Profiles.Any(profile => NormalizeProfileKind(profile.Kind) == normalized))
        {
            return normalized;
        }

        foreach (var profile in settings.Profiles)
        {
            var profileKind = NormalizeProfileKind(profile.Kind);
            if (IsProfileKind(profileKind))
            {
                return profileKind;
            }
        }

        return VoicemeeterKind.Potato;
    }

    private static MainWindowProfileSettings GetProfileSettings(MainWindowSettings settings, VoicemeeterKind kind)
    {
        kind = NormalizeProfileKind(kind);
        var profile = settings.Profiles.FirstOrDefault(profile => NormalizeProfileKind(profile.Kind) == kind);
        if (profile is not null)
        {
            return CloneProfileSettings(profile);
        }

        if (settings.Endpoints.Count > 0 || settings.SelectedMode != CallbackMode.None)
        {
            return new MainWindowProfileSettings
            {
                Kind = kind,
                SelectedMode = settings.SelectedMode,
                SelectedEndpointName = settings.SelectedEndpointName,
                InputPathLatencyMilliseconds = settings.InputPathLatencyMilliseconds,
                OutputPathLatencyMilliseconds = settings.OutputPathLatencyMilliseconds,
                Endpoints = settings.Endpoints.Select(CloneEndpointSnapshot).ToList()
            };
        }

        return new MainWindowProfileSettings { Kind = kind };
    }

    private static MainWindowProfileSettings CloneProfileSettings(MainWindowProfileSettings profile)
    {
        return new MainWindowProfileSettings
        {
            Kind = NormalizeProfileKind(profile.Kind),
            SelectedMode = profile.SelectedMode,
            SelectedEndpointName = profile.SelectedEndpointName,
            InputPathLatencyMilliseconds = profile.InputPathLatencyMilliseconds,
            OutputPathLatencyMilliseconds = profile.OutputPathLatencyMilliseconds,
            Endpoints = profile.Endpoints.Select(CloneEndpointSnapshot).ToList()
        };
    }

    private static EndpointDelaySettingsSnapshot CloneEndpointSnapshot(EndpointDelaySettingsSnapshot snapshot)
    {
        return new EndpointDelaySettingsSnapshot
        {
            Mode = snapshot.Mode,
            EndpointName = snapshot.EndpointName,
            ChannelCount = snapshot.ChannelCount,
            Enabled = snapshot.Enabled.ToArray(),
            DelayMilliseconds = snapshot.DelayMilliseconds.ToArray(),
            VolumePercent = snapshot.VolumePercent.ToArray(),
            RouteEnabled = snapshot.RouteEnabled.ToArray(),
            RouteDestinationBusIndex = snapshot.RouteDestinationBusIndex.ToArray(),
            RouteDestinationChannelOffset = snapshot.RouteDestinationChannelOffset.ToArray(),
            RouteMuteNormal = snapshot.RouteMuteNormal.ToArray(),
            RouteDestinations = snapshot.RouteDestinations
                .Select(static destinations => destinations.Select(CloneRouteDestination).ToList())
                .ToList()
        };
    }

    private static RouteDestinationSnapshot CloneRouteDestination(RouteDestinationSnapshot destination)
    {
        return new RouteDestinationSnapshot
        {
            BusIndex = Math.Max(0, destination.BusIndex),
            ChannelOffset = Math.Max(0, destination.ChannelOffset)
        };
    }

    private static VoicemeeterKind NormalizeProfileKind(VoicemeeterKind kind)
    {
        return IsProfileKind(kind) ? kind : VoicemeeterKind.Potato;
    }

    private static bool IsProfileKind(VoicemeeterKind kind)
    {
        return kind is VoicemeeterKind.Standard or VoicemeeterKind.Banana or VoicemeeterKind.Potato;
    }

    private void RestoreEngineFromSavedSelections()
    {
        if (!_settingsByEndpoint.Values.Any(static settings => settings.HasActiveChannels))
        {
            SetRunningState(null);
            return;
        }

        SyncEngineToSelections(
            allowAutoStart: true,
            startLogPrefix: "Restored saved channel ticks",
            showErrors: false);
    }

    private static bool IsSelectableMode(CallbackMode mode)
    {
        return mode is CallbackMode.Input or CallbackMode.Output;
    }

    private static double SanitizeDelay(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        return RoundDelay(Math.Clamp(value, 0, AppOptions.MaxDelayMilliseconds));
    }

    private static double SanitizeVolume(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 100.0;
        }

        return RoundVolume(Math.Clamp(value, 0, 200));
    }

    private void ApplyVbanControlSettingsFromUi(bool showErrors)
    {
        try
        {
            var enabled = VbanEnableCheckBox.IsChecked == true;
            var port = ParseVbanControlPort(VbanPortTextBox.Text);
            var streamName = string.IsNullOrWhiteSpace(VbanStreamTextBox.Text)
                ? DefaultVbanControlStreamName
                : VbanStreamTextBox.Text.Trim();
            var localOnly = VbanLocalOnlyCheckBox.IsChecked != false;

            VbanPortTextBox.Text = port.ToString(CultureInfo.InvariantCulture);
            VbanStreamTextBox.Text = streamName;
            RestartVbanTextListener(enabled, port, streamName, localOnly);
        }
        catch (Exception ex)
        {
            AppendLog($"VBAN control error: {ex.Message}");
            UpdateVbanControlStatus("VBAN control error");
            if (showErrors)
            {
                MessageBox.Show(this, ex.Message, "Voicemeeter Delay", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RestartVbanTextListener(bool enabled, int port, string streamName, bool localOnly)
    {
        if (_vbanTextListener is not null
            && _vbanTextListener.Port == port
            && string.Equals(_vbanTextListener.StreamName, streamName, StringComparison.OrdinalIgnoreCase)
            && _vbanTextListener.LocalOnly == localOnly
            && enabled)
        {
            UpdateVbanControlStatus($"VBAN listening {port} / {streamName}");
            return;
        }

        _vbanTextListener?.Dispose();
        _vbanTextListener = null;

        if (!enabled)
        {
            UpdateVbanControlStatus("VBAN off");
            return;
        }

        _vbanTextListener = new VbanTextListener(
            port,
            streamName,
            localOnly,
            message => Dispatcher.InvokeAsync(() => HandleVbanTextMessage(message)),
            diagnostic => Dispatcher.InvokeAsync(() =>
            {
                UpdateVbanControlStatus("VBAN packet ignored");
                AppendLog(diagnostic);
            }));
        UpdateVbanControlStatus($"VBAN listening {port} / {streamName}");
        AppendLog($"VBAN control listening on port {port}, stream {streamName}, {(localOnly ? "local only" : "LAN allowed")}.");
    }

    private void UpdateVbanControlStatus(string text)
    {
        VbanStatusTextBlock.Text = text;
    }

    private int ParseVbanControlPortOrDefault()
    {
        try
        {
            return ParseVbanControlPort(VbanPortTextBox.Text);
        }
        catch
        {
            return DefaultVbanControlPort;
        }
    }

    private static int ParseVbanControlPort(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("VBAN control port must be between 1 and 65535.");
        }

        return port;
    }

    private static int SanitizeVbanControlPort(int port)
    {
        return port is >= 1 and <= 65535
            ? port
            : DefaultVbanControlPort;
    }

    private void HandleVbanTextMessage(VbanTextMessage message)
    {
        try
        {
            var commands = VbanDelayCommandParser.ParseScript(message.Text);
            if (commands.Count == 0)
            {
                return;
            }

            var selectedEndpointChanged = false;
            foreach (var command in commands)
            {
                selectedEndpointChanged |= ApplyVbanDelayCommand(command);
            }

            RefreshEndpointButtonBrushes();
            if (selectedEndpointChanged)
            {
                BuildChannelStrips(_selectedSettings);
            }

            ScheduleMainSettingsSave();
            SyncEngineToSelections(allowAutoStart: true, startLogPrefix: "Armed from VBAN command");
            AppendLog($"VBAN {message.RemoteAddress} {message.StreamName}: applied {commands.Count} command(s).");
        }
        catch (Exception ex)
        {
            AppendLog($"VBAN command error from {message.RemoteAddress}: {ex.Message}");
        }
    }

    private bool ApplyVbanDelayCommand(VbanDelayCommand command)
    {
        if (command.Property is VbanDelayProperty.Route or VbanDelayProperty.RouteEnable or VbanDelayProperty.RouteMuteNormal
            && command.TargetKind != VbanDelayTargetKind.Strip)
        {
            throw new InvalidOperationException($"{command.SourceText}: route commands only support Strip targets.");
        }

        var mode = command.TargetKind == VbanDelayTargetKind.Strip
            ? CallbackMode.Input
            : CallbackMode.Output;
        var endpoint = ResolveVbanEndpoint(command);
        var settings = GetOrCreateSettings(mode, endpoint);
        var offsets = command.Channels.GetZeroBasedChannels(endpoint.ChannelCount).ToArray();
        if (offsets.Length == 0)
        {
            throw new InvalidOperationException($"{command.SourceText}: no matching channel in {endpoint.Name}.");
        }

        foreach (var offset in offsets)
        {
            ApplyVbanDelayCommandToChannel(settings, offset, command);
        }

        return _selectedSettings?.Mode == settings.Mode
            && string.Equals(_selectedSettings.Endpoint.Name, settings.Endpoint.Name, StringComparison.OrdinalIgnoreCase);
    }

    private IoEndpoint ResolveVbanEndpoint(VbanDelayCommand command)
    {
        return command.TargetKind == VbanDelayTargetKind.Strip
            ? ResolveVbanStripEndpoint(command.Target)
            : ResolveVbanBusEndpoint(command.Target);
    }

    private IoEndpoint ResolveVbanStripEndpoint(string targetText)
    {
        var endpoints = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Input, GetLayoutKind());
        var target = targetText.Trim();
        if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stripIndex))
        {
            if (stripIndex >= 0 && stripIndex < endpoints.Count)
            {
                return endpoints[stripIndex];
            }

            throw new InvalidOperationException($"Strip({stripIndex}) is not available for {VoicemeeterKindInfo.DisplayName(GetLayoutKind())}.");
        }

        var namedEndpoint = endpoints.FirstOrDefault(endpoint =>
            string.Equals(endpoint.Name, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(endpoint.Name.Replace(" ", string.Empty), target.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));
        return namedEndpoint
            ?? throw new InvalidOperationException($"Unknown strip target: {targetText}");
    }

    private IoEndpoint ResolveVbanBusEndpoint(string targetText)
    {
        var endpoints = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, GetLayoutKind());
        var target = targetText.Trim();
        if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var busIndex))
        {
            if (busIndex >= 0 && busIndex < endpoints.Count)
            {
                return endpoints[busIndex];
            }

            throw new InvalidOperationException($"Bus({busIndex}) is not available for {VoicemeeterKindInfo.DisplayName(GetLayoutKind())}.");
        }

        var label = target.ToUpperInvariant();
        var endpoint = endpoints.FirstOrDefault(item =>
            item.Name.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, label, StringComparison.OrdinalIgnoreCase));
        return endpoint
            ?? throw new InvalidOperationException($"Unknown bus target: {targetText}");
    }

    private void ApplyVbanDelayCommandToChannel(EndpointDelaySettings settings, int offset, VbanDelayCommand command)
    {
        switch (command.Property)
        {
            case VbanDelayProperty.Enable:
                if (command.Operator != VbanDelayOperator.Set)
                {
                    throw new InvalidOperationException("Enable only supports '='.");
                }

                settings.Enabled[offset] = VbanDelayCommandParser.ParseBoolean(command.ValueText);
                break;

            case VbanDelayProperty.Delay:
                settings.DelayMilliseconds[offset] = ApplyNumericVbanCommand(
                    currentValue: settings.DelayMilliseconds[offset],
                    command,
                    valueName: "Delay",
                    sanitize: value => Math.Max(GetPathLatencyMilliseconds(settings.Mode), SanitizeDelay(value, GetPathLatencyMilliseconds(settings.Mode))));
                break;

            case VbanDelayProperty.Volume:
                settings.VolumePercent[offset] = ApplyNumericVbanCommand(
                    currentValue: settings.VolumePercent[offset],
                    command,
                    valueName: "Volume",
                    sanitize: SanitizeVolume);
                break;

            case VbanDelayProperty.Route:
                ApplyVbanRouteDestinationCommand(settings, offset, command);
                break;

            case VbanDelayProperty.RouteEnable:
                if (command.Operator != VbanDelayOperator.Set)
                {
                    throw new InvalidOperationException("RouteEnable only supports '='.");
                }

                settings.RouteEnabled[offset] = VbanDelayCommandParser.ParseBoolean(command.ValueText);
                if (settings.RouteEnabled[offset])
                {
                    settings.EnsureRouteDestination(offset);
                }

                break;

            case VbanDelayProperty.RouteMuteNormal:
                if (command.Operator != VbanDelayOperator.Set)
                {
                    throw new InvalidOperationException("MuteNormal only supports '='.");
                }

                settings.RouteMuteNormal[offset] = VbanDelayCommandParser.ParseBoolean(command.ValueText);
                break;
        }
    }

    private void ApplyVbanRouteDestinationCommand(EndpointDelaySettings settings, int offset, VbanDelayCommand command)
    {
        var destinationText = VbanDelayCommandParser.ParseRouteDestination(command.ValueText);
        var bus = ResolveVbanBusEndpoint(destinationText.BusTarget);
        var destinationOffset = destinationText.OneBasedChannel - 1;
        if (destinationOffset < 0 || destinationOffset >= bus.ChannelCount)
        {
            throw new InvalidOperationException($"{command.SourceText}: {bus.Name} has {bus.ChannelCount} channel(s).");
        }

        var destination = new RouteDestinationSnapshot
        {
            BusIndex = GetVbanBusIndex(bus),
            ChannelOffset = destinationOffset
        };
        var destinations = settings.RouteDestinations[offset];

        switch (command.Operator)
        {
            case VbanDelayOperator.Set:
                destinations.Clear();
                destinations.Add(destination);
                settings.RouteEnabled[offset] = true;
                break;

            case VbanDelayOperator.Add:
                if (!destinations.Any(item => item.BusIndex == destination.BusIndex && item.ChannelOffset == destination.ChannelOffset))
                {
                    destinations.Add(destination);
                }

                settings.RouteEnabled[offset] = true;
                break;

            case VbanDelayOperator.Subtract:
                destinations.RemoveAll(item => item.BusIndex == destination.BusIndex && item.ChannelOffset == destination.ChannelOffset);
                if (destinations.Count == 0)
                {
                    settings.RouteEnabled[offset] = false;
                }

                break;
        }

        settings.SyncLegacyRouteDestination(offset);
    }

    private int GetVbanBusIndex(IoEndpoint bus)
    {
        var endpoints = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, GetLayoutKind());
        for (var index = 0; index < endpoints.Count; index++)
        {
            if (string.Equals(endpoints[index].Name, bus.Name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Unknown route bus: {bus.Name}");
    }

    private static double ApplyNumericVbanCommand(
        double currentValue,
        VbanDelayCommand command,
        string valueName,
        Func<double, double> sanitize)
    {
        var value = VbanDelayCommandParser.ParseNumber(command.ValueText, valueName);
        var result = command.Operator switch
        {
            VbanDelayOperator.Add => currentValue + value,
            VbanDelayOperator.Subtract => currentValue - value,
            _ => value
        };

        return sanitize(result);
    }

    private void InputModeButton_Click(object sender, RoutedEventArgs e)
    {
        SelectMode(CallbackMode.Input);
    }

    private void OutputModeButton_Click(object sender, RoutedEventArgs e)
    {
        SelectMode(CallbackMode.Output);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Voicemeeter Remote DLL",
            Filter = "Voicemeeter Remote DLL|VoicemeeterRemote64.dll;VoicemeeterRemote.dll|DLL files|*.dll|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            DllPathTextBox.Text = dialog.FileName;
            ScheduleMainSettingsSave();
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void RefreshAppsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshApplicationSessions(readSessions: true);
    }

    private void OpenCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        OpenRoundTripCalibrationWindow();
    }

    private void OpenRoundTripCalibrationWindow()
    {
        if (_roundTripCalibrationWindow is { IsVisible: true } existingWindow)
        {
            existingWindow.Activate();
            return;
        }

        _roundTripCalibrationWindow = new RoundTripCalibrationWindow
        {
            Owner = this
        };
        _roundTripCalibrationWindow.Closed += (_, _) => _roundTripCalibrationWindow = null;
        _roundTripCalibrationWindow.Show();
        _roundTripCalibrationWindow.Activate();
    }

    private void OpenVbanCommandsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenVbanCommandsWindow();
    }

    private void OpenVbanCommandsWindow()
    {
        if (_vbanCommandsWindow is { IsVisible: true } existingWindow)
        {
            existingWindow.Activate();
            return;
        }

        _vbanCommandsWindow = new VbanCommandsWindow
        {
            Owner = this
        };
        _vbanCommandsWindow.Closed += (_, _) => _vbanCommandsWindow = null;
        _vbanCommandsWindow.Show();
        _vbanCommandsWindow.Activate();
    }

    private void PathLatencyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ApplyPathLatencyTextBoxValue(textBox);
        }
    }

    private void PathLatencyTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox)
        {
            return;
        }

        ApplyPathLatencyTextBoxValue(textBox);
        textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _settingsSaveTimer.Stop();
        SaveMainSettings();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _applicationIcon?.Dispose();
        _applicationIcon = null;
        _vbanTextListener?.Dispose();
        _vbanTextListener = null;
        _callbackStatusTimer.Stop();
        StopDelay("Stopped on exit.");
        base.OnClosing(e);
    }

    private void CallbackStatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_delay is null || _runningOptions is null)
        {
            return;
        }

        var stats = _delay.GetCallbackStats();
        UpdateRunningStatusText(_runningOptions, stats);
        WarnIfRequestedCallbacksAreMissing(_runningOptions, stats);
    }

    private void SelectMode(CallbackMode mode)
    {
        _selectedMode = mode;
        _selectedEndpoint = null;
        _selectedSettings = null;

        SetButtonTone(InputModeButton, mode == CallbackMode.Input ? ButtonTone.Selected : ButtonTone.Neutral);
        SetButtonTone(OutputModeButton, mode == CallbackMode.Output ? ButtonTone.Selected : ButtonTone.Neutral);

        UpdatePathLatencyControl();
        BuildEndpointButtons(mode);
        BuildChannelStrips(null);
        RefreshApplicationSessions(readSessions: false);
        ScheduleMainSettingsSave();
    }

    private void BuildEndpointButtons(CallbackMode mode)
    {
        EndpointButtonsPanel.Children.Clear();

        foreach (var endpoint in VoicemeeterIoLayout.GetEndpoints(mode, GetLayoutKind()))
        {
            var button = new Button
            {
                Content = endpoint.Name,
                Tag = endpoint,
                Width = 128,
                MinHeight = 34,
                Margin = new Thickness(0, 0, 10, 10),
                Background = NeutralBrush,
                ToolTip = endpoint.DisplayName
            };
            button.Click += EndpointButton_Click;
            EndpointButtonsPanel.Children.Add(button);
        }

        RefreshEndpointButtonBrushes();
    }

    private void EndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IoEndpoint endpoint })
        {
            SelectEndpoint(endpoint);
        }
    }

    private void SelectEndpoint(IoEndpoint endpoint)
    {
        _selectedEndpoint = endpoint;
        _selectedSettings = GetOrCreateSettings(_selectedMode ?? CallbackMode.Output, endpoint);
        RefreshEndpointButtonBrushes();
        BuildChannelStrips(_selectedSettings);
        RefreshApplicationSessions(readSessions: false);
        ScheduleMainSettingsSave();
    }

    private void BuildChannelStrips(EndpointDelaySettings? settings)
    {
        _channelEditors.Clear();
        ChannelStripPanel.Children.Clear();

        if (settings is null)
        {
            return;
        }

        settings.EnsureMinimumDisplayedDelay(GetPathLatencyMilliseconds(settings.Mode));
        AdjustWindowWidthForEndpoint(settings.Endpoint);

        var endpoint = settings.Endpoint;
        for (var offset = 0; offset < endpoint.ChannelCount; offset++)
        {
            var editor = CreateChannelEditor(settings, offset);
            _channelEditors.Add(editor);
            ChannelStripPanel.Children.Add(editor.Container);
        }
    }

    private void AdjustWindowWidthForEndpoint(IoEndpoint endpoint)
    {
        var desiredWidth = endpoint.ChannelCount <= 2
            ? CompactEndpointWindowWidth
            : WideEndpointWindowWidth;
        var desiredHeight = endpoint.ChannelCount <= 2
            ? CompactEndpointWindowHeight
            : WideEndpointWindowHeight;
        var maximumWidth = Math.Max(MinimumEndpointWindowWidth, SystemParameters.WorkArea.Width - 40);
        var maximumHeight = Math.Max(MinimumEndpointWindowHeight, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(desiredWidth, maximumWidth);
        Height = Math.Min(desiredHeight, maximumHeight);
    }

    private ChannelEditor CreateChannelEditor(EndpointDelaySettings settings, int offset)
    {
        var endpoint = settings.Endpoint;
        var absoluteChannel = endpoint.Range.Start + offset;
        var label = GetChannelLabel(endpoint.ChannelCount, offset);
        var enabled = settings.Enabled[offset];
        var pathLatencyMilliseconds = GetPathLatencyMilliseconds(settings.Mode);
        var displayedDelayMilliseconds = settings.GetDisplayedDelay(offset, pathLatencyMilliseconds);
        var routeControlsEnabled = enabled || settings.RouteEnabled[offset];

        var container = new Border
        {
            Width = settings.Mode == CallbackMode.Input ? 156 : 136,
            MinHeight = settings.Mode == CallbackMode.Input ? 344 : 242,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(8),
            BorderBrush = SubtleBorderBrush,
            BorderThickness = new Thickness(1),
            Background = PanelBrush
        };

        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.Child = root;

        var title = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 6)
        };
        root.Children.Add(title);

        var checkBox = new CheckBox
        {
            IsChecked = settings.Enabled[offset],
            Tag = offset,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = $"Select channel {absoluteChannel + 1} for delay or volume processing"
        };
        checkBox.Checked += ChannelEnabledCheckBox_Changed;
        checkBox.Unchecked += ChannelEnabledCheckBox_Changed;
        root.Children.Add(checkBox);

        var controlGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        Grid.SetRow(controlGrid, 1);
        root.Children.Add(controlGrid);

        var delayStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(delayStack, 0);
        controlGrid.Children.Add(delayStack);

        delayStack.Children.Add(new TextBlock
        {
            Text = "Delay",
            FontSize = 11,
            Foreground = DelayAccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var sliderHost = new Grid
        {
            Width = 44,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var sliderBackground = new Border
        {
            Width = 34,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = FieldBrush,
            BorderBrush = DelayAccentBrush,
            BorderThickness = new Thickness(1)
        };
        sliderHost.Children.Add(sliderBackground);

        var slider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = pathLatencyMilliseconds,
            Maximum = GetMaxDisplayedDelayMilliseconds(pathLatencyMilliseconds),
            Value = displayedDelayMilliseconds,
            Height = 128,
            Width = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
            Foreground = DelayAccentBrush,
            BorderBrush = VbanAccentBrush,
            Background = Brushes.Transparent,
            Style = (Style)FindResource("ChannelVerticalSlider"),
            TickFrequency = 10,
            IsSnapToTickEnabled = false,
            SmallChange = 1,
            LargeChange = 10,
            IsEnabled = routeControlsEnabled,
            Tag = offset,
            ToolTip = "Total delay in milliseconds. Use the mouse wheel to change it."
        };
        slider.ValueChanged += ChannelSlider_ValueChanged;
        slider.PreviewMouseWheel += ChannelSlider_PreviewMouseWheel;
        sliderHost.Children.Add(slider);
        delayStack.Children.Add(sliderHost);

        var delayTextBox = new TextBox
        {
            Text = FormatDelay(displayedDelayMilliseconds),
            Width = 54,
            MinHeight = 28,
            Margin = new Thickness(0, 6, 0, 0),
            Background = FieldBrush,
            Foreground = TextBrush,
            BorderBrush = DelayAccentBrush,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = routeControlsEnabled,
            Tag = offset,
            ToolTip = "Total delay in milliseconds"
        };
        delayTextBox.LostFocus += ChannelDelayTextBox_LostFocus;
        delayTextBox.KeyDown += ChannelDelayTextBox_KeyDown;
        delayStack.Children.Add(delayTextBox);

        delayStack.Children.Add(new TextBlock
        {
            Text = "ms",
            FontSize = 11,
            Foreground = DelayAccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var volumeStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(volumeStack, 1);
        controlGrid.Children.Add(volumeStack);

        volumeStack.Children.Add(new TextBlock
        {
            Text = "Volume",
            FontSize = 11,
            Foreground = VolumeAccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var volumeSliderHost = new Grid
        {
            Width = 44,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        volumeSliderHost.Children.Add(new Border
        {
            Width = 34,
            Height = 128,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = FieldBrush,
            BorderBrush = VolumeAccentBrush,
            BorderThickness = new Thickness(1)
        });

        var volumeSlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0,
            Maximum = 200,
            Value = settings.VolumePercent[offset],
            Width = 34,
            Height = 128,
            Margin = new Thickness(0),
            SmallChange = 1,
            LargeChange = 5,
            TickFrequency = 10,
            IsSnapToTickEnabled = false,
            IsEnabled = routeControlsEnabled,
            Tag = offset,
            Foreground = VolumeAccentBrush,
            BorderBrush = VolumeAccentBrush,
            Background = Brushes.Transparent,
            Style = (Style)FindResource("ChannelVerticalSlider"),
            ToolTip = "Channel volume trim. 100% is unity: it follows the current Voicemeeter gain; lower attenuates, higher boosts."
        };
        volumeSlider.ValueChanged += ChannelVolumeSlider_ValueChanged;
        volumeSlider.PreviewMouseWheel += ChannelVolumeSlider_PreviewMouseWheel;
        volumeSliderHost.Children.Add(volumeSlider);
        volumeStack.Children.Add(volumeSliderHost);

        var volumeTextBox = new TextBox
        {
            Text = FormatVolume(settings.VolumePercent[offset]),
            Width = 54,
            MinHeight = 28,
            Margin = new Thickness(0, 6, 0, 0),
            Background = FieldBrush,
            Foreground = TextBrush,
            BorderBrush = VolumeAccentBrush,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = routeControlsEnabled,
            Tag = offset,
            ToolTip = "Channel volume trim. 100% is unity: it follows the current Voicemeeter gain; lower attenuates, higher boosts."
        };
        volumeTextBox.LostFocus += ChannelVolumeTextBox_LostFocus;
        volumeTextBox.KeyDown += ChannelVolumeTextBox_KeyDown;
        volumeStack.Children.Add(volumeTextBox);

        volumeStack.Children.Add(new TextBlock
        {
            Text = "Unity",
            FontSize = 11,
            Foreground = VolumeAccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0)
        });

        CheckBox? routeCheckBox = null;
        Button? routeButton = null;
        CheckBox? routeMuteNormalCheckBox = null;
        if (settings.Mode == CallbackMode.Input)
        {
            (routeCheckBox, routeButton, routeMuteNormalCheckBox) =
                CreateRouteControls(settings, offset, root);
        }

        var channelEditor = new ChannelEditor(
            offset,
            container,
            checkBox,
            slider,
            delayTextBox,
            volumeSlider,
            volumeTextBox,
            routeCheckBox,
            routeButton,
            routeMuteNormalCheckBox);
        UpdateChannelEditorEnabled(channelEditor);
        return channelEditor;
    }

    private (CheckBox RouteCheckBox, Button RouteButton, CheckBox MuteNormalCheckBox) CreateRouteControls(
        EndpointDelaySettings settings,
        int offset,
        Grid root)
    {
        var routePanel = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(routePanel, 2);
        root.Children.Add(routePanel);

        var routeCheckBox = new CheckBox
        {
            Content = "Route",
            IsChecked = settings.RouteEnabled[offset],
            Tag = offset,
            Foreground = RouteAccentBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Send this input channel to one or more output bus channels."
        };
        routePanel.Children.Add(routeCheckBox);

        var routeButton = new Button
        {
            Content = GetRouteButtonText(settings, offset),
            Tag = offset,
            MinHeight = 28,
            Margin = new Thickness(0, 6, 0, 0),
            Background = FieldBrush,
            Foreground = TextBrush,
            BorderBrush = RouteAccentBrush,
            ToolTip = "Edit route destinations"
        };
        routeButton.Click += RouteButton_Click;
        routePanel.Children.Add(routeButton);

        var muteNormalCheckBox = new CheckBox
        {
            Content = "Mute normal",
            IsChecked = settings.RouteMuteNormal[offset],
            Tag = offset,
            Foreground = RouteAccentBrush,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "Silence this input channel's normal path while still routing it to the selected destinations."
        };
        routePanel.Children.Add(muteNormalCheckBox);

        routeCheckBox.Checked += RouteControl_Changed;
        routeCheckBox.Unchecked += RouteControl_Changed;
        muteNormalCheckBox.Checked += RouteControl_Changed;
        muteNormalCheckBox.Unchecked += RouteControl_Changed;

        return (routeCheckBox, routeButton, muteNormalCheckBox);
    }

    private string GetRouteButtonText(EndpointDelaySettings settings, int offset)
    {
        settings.EnsureRouteDestination(offset);
        var destinations = settings.RouteDestinations[offset];
        if (destinations.Count != 1)
        {
            return $"{destinations.Count} outputs";
        }

        var destination = destinations[0];
        var bus = GetRouteBusChoices().FirstOrDefault(choice => choice.Index == destination.BusIndex);
        var busName = bus?.Name ?? "A1";
        return $"{busName} Ch {destination.ChannelOffset + 1}";
    }

    private void RouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSettings is null || sender is not Button { Tag: int offset } routeButton)
        {
            return;
        }

        _selectedSettings.EnsureRouteDestination(offset);
        var window = new RouteEditorWindow(
            $"{_selectedSettings.Endpoint.Name} {GetChannelLabel(_selectedSettings.Endpoint.ChannelCount, offset)}",
            GetRouteBusChoices(),
            _selectedSettings.RouteDestinations[offset],
            () =>
            {
                _selectedSettings.SyncLegacyRouteDestination(offset);
                routeButton.Content = GetRouteButtonText(_selectedSettings, offset);
                ScheduleMainSettingsSave();
                ApplyLiveOptions();
            })
        {
            Owner = this
        };

        window.ShowDialog();
        routeButton.Content = GetRouteButtonText(_selectedSettings, offset);
    }

    private IReadOnlyList<RouteBusChoice> GetRouteBusChoices()
    {
        return VoicemeeterIoLayout
            .GetEndpoints(CallbackMode.Output, GetLayoutKind())
            .Select(static (endpoint, index) => new RouteBusChoice(index, GetRouteBusLabel(endpoint), endpoint.ChannelCount))
            .ToArray();
    }

    private static string GetRouteBusLabel(IoEndpoint endpoint)
    {
        var spaceIndex = endpoint.Name.IndexOf(' ', StringComparison.Ordinal);
        return spaceIndex > 0 ? endpoint.Name[..spaceIndex] : endpoint.Name;
    }

    private void ChannelEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectedSettings is null || sender is not CheckBox { Tag: int offset })
        {
            return;
        }

        var enabled = ((CheckBox)sender).IsChecked == true;
        _selectedSettings.Enabled[offset] = enabled;

        if (FindEditor(offset) is { } editor)
        {
            UpdateChannelEditorEnabled(editor);
        }

        RefreshEndpointButtonBrushes();
        ScheduleMainSettingsSave();
        SyncEngineToSelections(allowAutoStart: enabled, startLogPrefix: "Armed from channel tick");
    }

    private void RouteControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectedSettings is null || sender is not CheckBox { Tag: int offset })
        {
            return;
        }

        var editor = FindEditor(offset);
        if (editor?.RouteCheckBox == sender)
        {
            _selectedSettings.RouteEnabled[offset] = ((CheckBox)sender).IsChecked == true;
            if (_selectedSettings.RouteEnabled[offset])
            {
                _selectedSettings.EnsureRouteDestination(offset);
            }
        }
        else if (editor?.RouteMuteNormalCheckBox == sender)
        {
            _selectedSettings.RouteMuteNormal[offset] = ((CheckBox)sender).IsChecked == true;
        }

        if (editor is not null)
        {
            if (editor.RouteButton is not null)
            {
                editor.RouteButton.Content = GetRouteButtonText(_selectedSettings, offset);
            }

            UpdateChannelEditorEnabled(editor);
        }

        RefreshEndpointButtonBrushes();
        ScheduleMainSettingsSave();
        SyncEngineToSelections(allowAutoStart: true, startLogPrefix: "Armed from route change");
    }

    private void ChannelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedSettings is null || sender is not Slider { Tag: int offset })
        {
            return;
        }

        var delayMilliseconds = RoundDelay(e.NewValue);
        _selectedSettings.DelayMilliseconds[offset] = delayMilliseconds;

        if (FindEditor(offset) is { } editor)
        {
            editor.DelayTextBox.Text = FormatDelay(delayMilliseconds);
        }

        ScheduleMainSettingsSave();
        ApplyLiveOptions();
    }

    private void ChannelSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
        {
            return;
        }

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var delta = e.Delta > 0 ? step : -step;
        slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private void ChannelDelayTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ApplyDelayTextBoxValue(textBox);
        }
    }

    private void ChannelDelayTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox)
        {
            return;
        }

        ApplyDelayTextBoxValue(textBox);
        textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void ChannelVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedSettings is null || sender is not Slider { Tag: int offset })
        {
            return;
        }

        var volumePercent = RoundVolume(e.NewValue);
        _selectedSettings.VolumePercent[offset] = volumePercent;

        if (FindEditor(offset) is { } editor)
        {
            editor.VolumeTextBox.Text = FormatVolume(volumePercent);
        }

        ScheduleMainSettingsSave();
        ApplyLiveOptions();
    }

    private void ChannelVolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
        {
            return;
        }

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var delta = e.Delta > 0 ? step : -step;
        slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private void ChannelVolumeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ApplyVolumeTextBoxValue(textBox);
        }
    }

    private void ChannelVolumeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox)
        {
            return;
        }

        ApplyVolumeTextBoxValue(textBox);
        textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void ApplyDelayTextBoxValue(TextBox textBox)
    {
        if (_selectedSettings is null || textBox.Tag is not int offset)
        {
            return;
        }

        try
        {
            var delayMilliseconds = ParseDisplayedDelay(textBox.Text);
            _selectedSettings.DelayMilliseconds[offset] = delayMilliseconds;

            if (FindEditor(offset) is { } editor)
            {
                editor.Slider.Value = delayMilliseconds;
                editor.DelayTextBox.Text = FormatDelay(delayMilliseconds);
            }

            ScheduleMainSettingsSave();
            ApplyLiveOptions();
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Voicemeeter Delay", MessageBoxButton.OK, MessageBoxImage.Error);
            textBox.Text = FormatDelay(_selectedSettings.GetDisplayedDelay(offset, GetPathLatencyMilliseconds(_selectedSettings.Mode)));
        }
    }

    private void ApplyVolumeTextBoxValue(TextBox textBox)
    {
        if (_selectedSettings is null || textBox.Tag is not int offset)
        {
            return;
        }

        try
        {
            var volumePercent = ParseVolume(textBox.Text);
            _selectedSettings.VolumePercent[offset] = volumePercent;

            if (FindEditor(offset) is { } editor)
            {
                editor.VolumeSlider.Value = volumePercent;
                editor.VolumeTextBox.Text = FormatVolume(volumePercent);
            }

            ScheduleMainSettingsSave();
            ApplyLiveOptions();
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Voicemeeter Delay", MessageBoxButton.OK, MessageBoxImage.Error);
            textBox.Text = FormatVolume(_selectedSettings.VolumePercent[offset]);
        }
    }

    private void ApplyPathLatencyTextBoxValue(TextBox textBox)
    {
        try
        {
            var mode = _selectedMode ?? CallbackMode.Output;
            var oldPathLatencyMilliseconds = GetPathLatencyMilliseconds(mode);
            var newPathLatencyMilliseconds = ParsePathLatency(textBox.Text);
            SetPathLatencyMilliseconds(mode, newPathLatencyMilliseconds);

            foreach (var settings in _settingsByEndpoint.Values)
            {
                if (settings.Mode == mode)
                {
                    settings.AdjustDisplayedDelayFloor(oldPathLatencyMilliseconds, newPathLatencyMilliseconds);
                }
            }

            UpdatePathLatencyControl();
            BuildChannelStrips(_selectedSettings);
            RefreshEndpointButtonBrushes();
            ScheduleMainSettingsSave();
            ApplyLiveOptions();
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Voicemeeter Delay", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdatePathLatencyControl();
        }
    }

    private ChannelEditor? FindEditor(int offset)
    {
        return _channelEditors.FirstOrDefault(editor => editor.Offset == offset);
    }

    private void UpdateChannelEditorEnabled(ChannelEditor editor)
    {
        var channelEnabled = editor.CheckBox.IsChecked == true;
        var routeEnabled = editor.RouteCheckBox?.IsChecked == true;
        var fadersEnabled = channelEnabled || routeEnabled;

        editor.CheckBox.IsEnabled = true;
        editor.Slider.IsEnabled = fadersEnabled;
        editor.DelayTextBox.IsEnabled = fadersEnabled;
        editor.VolumeSlider.IsEnabled = fadersEnabled;
        editor.VolumeTextBox.IsEnabled = fadersEnabled;
        editor.Container.BorderBrush = routeEnabled
            ? RouteAccentBrush
            : channelEnabled
                ? DelayAccentBrush
                : SubtleBorderBrush;
        editor.Container.BorderThickness = new Thickness(fadersEnabled ? 2 : 1);
        editor.CheckBox.Foreground = channelEnabled ? DelayAccentBrush : TextBrush;

        if (editor.RouteCheckBox is not null)
        {
            editor.RouteCheckBox.IsEnabled = true;
            editor.RouteCheckBox.Foreground = routeEnabled ? RouteAccentBrush : MutedBrush;
        }

        if (editor.RouteButton is not null)
        {
            editor.RouteButton.IsEnabled = true;
            editor.RouteButton.Background = routeEnabled ? RouteActiveBrush : FieldBrush;
            editor.RouteButton.BorderBrush = routeEnabled ? RouteAccentBrush : SubtleBorderBrush;
            editor.RouteButton.Foreground = routeEnabled ? RouteAccentBrush : TextBrush;
        }

        if (editor.RouteMuteNormalCheckBox is not null)
        {
            editor.RouteMuteNormalCheckBox.IsEnabled = routeEnabled;
            editor.RouteMuteNormalCheckBox.Foreground = routeEnabled ? RouteAccentBrush : MutedBrush;
        }
    }

    private static string GetChannelLabel(int channelCount, int offset)
    {
        return $"Channel {offset + 1}";
    }

    private void UpdateApplicationSessionSelectionState()
    {
        RefreshApplicationSessions(readSessions: false);
    }

    private void RefreshApplicationSessions(bool readSessions)
    {
        AppSessionsListBox.Items.Clear();

        if (_selectedSettings is not { Mode: CallbackMode.Input } settings
            || !TryGetHardwareInputNumber(settings.Endpoint, out var inputNumber))
        {
            AppSessionsHeaderTextBlock.Text = "VAIO Extension apps appear on hardware inputs";
            RefreshAppsButton.IsEnabled = false;
            return;
        }

        RefreshAppsButton.IsEnabled = true;
        if (!readSessions || _suppressApplicationSessionRefresh)
        {
            AppSessionsHeaderTextBlock.Text = $"Voicemeeter In {inputNumber} apps";
            AppSessionsListBox.Items.Add("Click Refresh to read app sessions");
            return;
        }

        try
        {
            var sessions = AudioSessionDiscovery.GetVoicemeeterInputExtensionSessions(inputNumber);
            if (sessions is null)
            {
                AppSessionsHeaderTextBlock.Text = $"No Voicemeeter In {inputNumber} endpoint found";
                return;
            }

            AppSessionsHeaderTextBlock.Text = sessions.EndpointName;
            if (sessions.Apps.Count == 0)
            {
                AppSessionsListBox.Items.Add("No app sessions detected");
                return;
            }

            AddSessionApps(sessions);
        }
        catch (Exception ex)
        {
            AppSessionsHeaderTextBlock.Text = $"Could not read Voicemeeter In {inputNumber}";
            AppSessionsListBox.Items.Add(ex.Message);
        }
    }

    private void AddSessionApps(AudioSessionList sessions)
    {
        foreach (var app in sessions.Apps)
        {
            AppSessionsListBox.Items.Add($"{app.Name} ({app.State})");
        }
    }

    private static bool TryGetHardwareInputNumber(IoEndpoint endpoint, out int inputNumber)
    {
        const string prefix = "Hardware In ";
        inputNumber = 0;
        return endpoint.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(endpoint.Name[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out inputNumber);
    }

    private void DetectVoicemeeterKind(bool showLog)
    {
        try
        {
            var dllPath = string.IsNullOrWhiteSpace(DllPathTextBox.Text) ? null : DllPathTextBox.Text.Trim();
            using var remote = VoicemeeterRemote.Load(dllPath);
            var loginResult = remote.Login();
            if (loginResult < 0)
            {
                if (showLog)
                {
                    AppendLog($"Could not detect mixer type: VBVMR_Login failed with code {loginResult}.");
                }

                return;
            }

            try
            {
                var typeResult = remote.GetVoicemeeterType(out var detectedKind);
                if (typeResult < 0 || detectedKind == VoicemeeterKind.Unknown)
                {
                    if (showLog)
                    {
                        AppendLog($"Could not detect mixer type: VBVMR_GetVoicemeeterType returned {typeResult}.");
                    }

                    return;
                }

                ApplyDetectedVoicemeeterKind(detectedKind, showLog);
            }
            finally
            {
                remote.Logout();
            }
        }
        catch (Exception ex)
        {
            if (showLog)
            {
                AppendLog($"Could not detect mixer type: {ex.Message}");
            }
        }
    }

    private void ApplyDetectedVoicemeeterKind(VoicemeeterKind detectedKind, bool showLog)
    {
        detectedKind = NormalizeProfileKind(detectedKind);
        if (_voicemeeterKind == detectedKind && _settingsProfileKind == detectedKind)
        {
            if (showLog)
            {
                AppendLog($"Detected {VoicemeeterKindInfo.DisplayName(detectedKind)}.");
            }

            return;
        }

        var previousKind = _voicemeeterKind;
        if (!_loadingSettings)
        {
            _mainSettings = CaptureMainSettings();
        }

        _voicemeeterKind = detectedKind;
        _settingsProfileKind = detectedKind;
        UpdateMixerTypeText();

        _loadingSettings = true;
        _suppressApplicationSessionRefresh = true;
        RestoreProfileSettings(GetProfileSettings(_mainSettings, detectedKind));
        _suppressApplicationSessionRefresh = false;
        _loadingSettings = false;

        if (showLog || previousKind != VoicemeeterKind.Unknown)
        {
            AppendLog($"Detected {VoicemeeterKindInfo.DisplayName(detectedKind)}. Switched to {detectedKind} settings profile.");
        }

        ScheduleMainSettingsSave();
    }

    private void ReconcileEndpointSettings()
    {
        var reconciledSettings = new List<(string Key, EndpointDelaySettings Settings)>();
        foreach (var settings in _settingsByEndpoint.Values)
        {
            var endpoint = FindEndpoint(settings.Mode, settings.Endpoint.Name);
            if (endpoint is null || endpoint.ChannelCount != settings.Endpoint.ChannelCount)
            {
                continue;
            }

            settings.RebindEndpoint(endpoint);
            reconciledSettings.Add((GetSettingsKey(settings.Mode, endpoint), settings));
        }

        _settingsByEndpoint.Clear();
        foreach (var (key, settings) in reconciledSettings)
        {
            _settingsByEndpoint[key] = settings;
        }

        if (_selectedSettings is null)
        {
            _selectedEndpoint = null;
            return;
        }

        var selectedEndpoint = FindEndpoint(_selectedSettings.Mode, _selectedSettings.Endpoint.Name);
        if (selectedEndpoint is null || selectedEndpoint.ChannelCount != _selectedSettings.Endpoint.ChannelCount)
        {
            _selectedEndpoint = null;
            _selectedSettings = null;
            return;
        }

        _selectedSettings.RebindEndpoint(selectedEndpoint);
        _selectedEndpoint = selectedEndpoint;
    }

    private VoicemeeterKind GetLayoutKind()
    {
        return _voicemeeterKind == VoicemeeterKind.Unknown
            ? VoicemeeterKind.Potato
            : _voicemeeterKind;
    }

    private void UpdateMixerTypeText()
    {
        MixerTypeTextBlock.Text = $"{VoicemeeterKindInfo.DisplayName(_voicemeeterKind)} · Callback audio delay";
    }

    private AppOptions BuildOptions(bool allowEmpty)
    {
        var targets = _settingsByEndpoint.Values
            .Where(IsEndpointSupported)
            .SelectMany(settings => settings.ToDelayTargets(GetPathLatencyMilliseconds(settings.Mode)))
            .ToArray();
        var routes = _settingsByEndpoint.Values
            .Where(IsEndpointSupported)
            .SelectMany(settings => settings.ToAudioRoutes(GetLayoutKind(), GetPathLatencyMilliseconds(settings.Mode)))
            .ToArray();
        if (!allowEmpty && targets.Length == 0 && routes.Length == 0)
        {
            throw new InvalidOperationException("Enable at least one input or output channel.");
        }

        var registerMode = GetRegisterMode(targets, routes);
        if (!allowEmpty && registerMode == CallbackMode.None)
        {
            throw new InvalidOperationException("Enable at least one input or output channel.");
        }

        var dllPath = string.IsNullOrWhiteSpace(DllPathTextBox.Text) ? null : DllPathTextBox.Text.Trim();

        return new AppOptions(
            targets,
            routes,
            registerMode,
            dllPath,
            SelfTest: false);
    }

    private void ApplyLiveOptions()
    {
        SyncEngineToSelections(allowAutoStart: true, startLogPrefix: "Armed from live edit");
    }

    private void SyncEngineToSelections(bool allowAutoStart, string startLogPrefix, bool showErrors = true)
    {
        try
        {
            if (_voicemeeterKind == VoicemeeterKind.Unknown)
            {
                DetectVoicemeeterKind(showLog: allowAutoStart);
            }

            var options = BuildOptions(allowEmpty: true);
            if (options.RegisterMode == CallbackMode.None)
            {
                if (_delay is not null)
                {
                    StopDelay("Bypassed: no enabled channels.");
                }
                else
                {
                    SetRunningState(null);
                }

                return;
            }

            if (_delay is null)
            {
                if (!allowAutoStart)
                {
                    return;
                }

                options = BuildOptions(allowEmpty: false);
                StartDelay(options, $"{startLogPrefix}: {DescribeTargets(options)}.");
                return;
            }

            if (_delay.RegisterMode != options.RegisterMode)
            {
                ReconfigureDelay(options);
            }
            else
            {
                _delay.UpdateOptions(options);
                SetRunningState(options);
            }
        }
        catch (Exception ex)
        {
            var prefix = allowAutoStart ? "Engine start error" : "Live update error";
            AppendLog($"{prefix}: {ex.Message}");

            if (allowAutoStart && showErrors)
            {
                MessageBox.Show(this, ex.Message, "Voicemeeter Delay", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private bool IsEndpointSupported(EndpointDelaySettings settings)
    {
        return FindEndpoint(settings.Mode, settings.Endpoint.Name) is { } endpoint
            && endpoint.ChannelCount == settings.Endpoint.ChannelCount;
    }

    private IoEndpoint? FindEndpoint(CallbackMode mode, string endpointName)
    {
        return VoicemeeterIoLayout
            .GetEndpoints(mode, GetLayoutKind())
            .FirstOrDefault(endpoint => endpoint.Name == endpointName);
    }

    private CallbackMode GetRegisterMode(IReadOnlyCollection<DelayTarget> targets, IReadOnlyCollection<AudioRoute> routes)
    {
        if (targets.Count == 0 && routes.Count == 0)
        {
            return CallbackMode.None;
        }

        var mode = CallbackMode.None;
        foreach (var target in targets)
        {
            mode |= target.Mode;
        }

        if (routes.Count > 0)
        {
            mode |= CallbackMode.Input | CallbackMode.Output;
        }

        return mode;
    }

    private static int CountTargets(AppOptions options, CallbackMode mode)
    {
        return options.Targets.Count(target => target.Mode == mode);
    }

    private string DescribeTargets(AppOptions options)
    {
        return $"{options.Targets.Count} target(s), {options.Routes.Count} route(s): input {CountTargets(options, CallbackMode.Input)}, output {CountTargets(options, CallbackMode.Output)}, main {CountTargets(options, CallbackMode.Main)}. Active {GetActiveEndpointSummary(options)}. Armed {options.RegisterMode}";
    }

    private static string DescribeAppExtraDelay(AppOptions options)
    {
        return $"App extra delay: input max {FormatDelay(GetMaxExtraDelay(options, CallbackMode.Input))} ms, output max {FormatDelay(GetMaxExtraDelay(options, CallbackMode.Output))} ms.";
    }

    private static string DescribeTargetChannels(AppOptions options)
    {
        var delayTargets = options.Targets.Select(static target => $"{target.Mode} {target.Name} -> {target.Channels}");
        var routes = options.Routes.Select(static route => $"Route {route.Name}");
        return "Target channels: " + string.Join("; ", delayTargets.Concat(routes));
    }

    private static double GetMaxExtraDelay(AppOptions options, CallbackMode mode)
    {
        return options.Targets
            .Where(target => target.Mode == mode)
            .Select(target => target.DelayMilliseconds)
            .DefaultIfEmpty(0)
            .Max();
    }

    private string GetEngineStatusText(AppOptions options, AudioCallbackStats? stats = null)
    {
        return $"{GetCallbackScopeLabel(options.RegisterMode)} - {GetCallbackDeliveryLabel(options.RegisterMode, stats)} - {GetActiveEndpointSummary(options)}";
    }

    private static string GetCallbackDeliveryLabel(CallbackMode mode, AudioCallbackStats? stats)
    {
        if (stats is null)
        {
            return "waiting for buffers";
        }

        var parts = new List<string>();
        if (mode.HasFlag(CallbackMode.Input))
        {
            parts.Add(stats.Value.InputCallbacks > 0 ? "input live" : "input waiting");
        }

        if (mode.HasFlag(CallbackMode.Output))
        {
            parts.Add(stats.Value.OutputCallbacks > 0 ? "output live" : "output waiting");
        }

        if (mode.HasFlag(CallbackMode.Main))
        {
            parts.Add(stats.Value.MainCallbacks > 0 ? "main live" : "main waiting");
        }

        return parts.Count == 0
            ? "waiting for buffers"
            : string.Join(", ", parts);
    }

    private static string GetCallbackScopeLabel(CallbackMode mode)
    {
        var hasInput = mode.HasFlag(CallbackMode.Input);
        var hasOutput = mode.HasFlag(CallbackMode.Output);

        if (hasInput && hasOutput)
        {
            return "Input+output callbacks armed";
        }

        if (hasInput)
        {
            return "Input callback armed";
        }

        if (hasOutput)
        {
            return "Output callback armed";
        }

        return "No callback armed";
    }

    private string GetActiveEndpointSummary(AppOptions options)
    {
        var activeEndpoints = GetActiveEndpointDetails().ToArray();
        if (activeEndpoints.Length == 1)
        {
            var endpoint = activeEndpoints[0];
            return $"{endpoint.Name} ({endpoint.ChannelCount} ch)";
        }

        return $"{activeEndpoints.Length} I/O, {options.Targets.Count} ch, {options.Routes.Count} route(s)";
    }

    private string GetActiveTargetTooltip()
    {
        var details = GetActiveEndpointDetails().ToArray();
        if (details.Length == 0)
        {
            return "Tick a channel to arm the engine.";
        }

        return "Voicemeeter arms callbacks per insert side, not per I/O endpoint." + Environment.NewLine
            + "This app processes only the selected channels below:" + Environment.NewLine
            + string.Join(Environment.NewLine, details.Select(static detail => $"{detail.Name}: {detail.ChannelList}"));
    }

    private IEnumerable<ActiveEndpointDetail> GetActiveEndpointDetails()
    {
        return _settingsByEndpoint.Values
            .Where(IsEndpointSupported)
            .Where(static settings => settings.HasActiveChannels)
            .Select(static settings =>
            {
                var selectedOffsets = settings.Enabled
                    .Select(static (enabled, offset) => new { enabled, offset })
                    .Where(static item => item.enabled)
                    .Select(item => GetChannelLabel(settings.Endpoint.ChannelCount, item.offset))
                    .ToArray();
                var routedOffsets = settings.RouteEnabled
                    .Select(static (enabled, offset) => new { enabled, offset })
                    .Where(static item => item.enabled)
                    .Select(item => $"{GetChannelLabel(settings.Endpoint.ChannelCount, item.offset)} routed")
                    .ToArray();
                return new ActiveEndpointDetail(
                    settings.Endpoint.Name,
                    selectedOffsets.Length + routedOffsets.Length,
                    string.Join(", ", selectedOffsets.Concat(routedOffsets)));
            })
            .OrderBy(static detail => detail.Name, StringComparer.OrdinalIgnoreCase);
    }

    private double ParseDisplayedDelay(string text)
    {
        var delayMilliseconds = ParseMilliseconds(text, "Delay");
        var minimum = GetSelectedPathLatencyMilliseconds();
        var maximum = GetMaxDisplayedDelayMilliseconds(minimum);
        if (delayMilliseconds > maximum)
        {
            throw new InvalidOperationException($"Delay must be between {FormatDelay(minimum)} and {FormatDelay(maximum)} ms.");
        }

        return RoundDelay(Math.Max(delayMilliseconds, minimum));
    }

    private static double ParsePathLatency(string text)
    {
        var pathLatencyMilliseconds = ParseMilliseconds(text, "Path floor");
        if (pathLatencyMilliseconds < 0 || pathLatencyMilliseconds > AppOptions.MaxDelayMilliseconds)
        {
            throw new InvalidOperationException($"Path floor must be between 0 and {AppOptions.MaxDelayMilliseconds:0} ms.");
        }

        return RoundDelay(pathLatencyMilliseconds);
    }

    private static double ParseVolume(string text)
    {
        var volumePercent = ParseMilliseconds(text.TrimEnd('%'), "Volume");
        if (volumePercent < 0 || volumePercent > 200)
        {
            throw new InvalidOperationException("Volume must be between 0 and 200%.");
        }

        return RoundVolume(volumePercent);
    }

    private static double ParseMilliseconds(string text, string name)
    {
        text = text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var milliseconds)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds))
        {
            throw new InvalidOperationException($"{name} must be a number.");
        }

        return milliseconds;
    }

    private double GetSelectedPathLatencyMilliseconds()
    {
        return GetPathLatencyMilliseconds(_selectedSettings?.Mode ?? _selectedMode ?? CallbackMode.Output);
    }

    private double GetPathLatencyMilliseconds(CallbackMode mode)
    {
        return mode == CallbackMode.Input
            ? _inputPathLatencyMilliseconds
            : _outputPathLatencyMilliseconds;
    }

    private void SetPathLatencyMilliseconds(CallbackMode mode, double value)
    {
        if (mode == CallbackMode.Input)
        {
            _inputPathLatencyMilliseconds = value;
            return;
        }

        _outputPathLatencyMilliseconds = value;
    }

    private static double GetMaxDisplayedDelayMilliseconds(double pathLatencyMilliseconds)
    {
        return pathLatencyMilliseconds + AppOptions.MaxDelayMilliseconds;
    }

    private void UpdatePathLatencyControl()
    {
        var mode = _selectedMode ?? CallbackMode.Output;
        PathLatencyLabel.Text = mode == CallbackMode.Input ? "Input floor" : "Output floor";
        PathLatencyTextBox.Text = FormatDelay(GetPathLatencyMilliseconds(mode));
    }

    private static double RoundDelay(double delayMilliseconds)
    {
        return Math.Round(delayMilliseconds);
    }

    private static string FormatDelay(double delayMilliseconds)
    {
        return RoundDelay(delayMilliseconds).ToString("0", CultureInfo.InvariantCulture);
    }

    private static double RoundVolume(double volumePercent)
    {
        return Math.Round(volumePercent);
    }

    private static string FormatVolume(double volumePercent)
    {
        return RoundVolume(volumePercent).ToString("0", CultureInfo.InvariantCulture);
    }

    private EndpointDelaySettings GetOrCreateSettings(CallbackMode mode, IoEndpoint endpoint)
    {
        var key = GetSettingsKey(mode, endpoint);
        if (!_settingsByEndpoint.TryGetValue(key, out var settings))
        {
            settings = new EndpointDelaySettings(mode, endpoint, GetPathLatencyMilliseconds(mode));
            _settingsByEndpoint.Add(key, settings);
        }

        return settings;
    }

    private EndpointDelaySettings? GetExistingSettings(CallbackMode mode, IoEndpoint endpoint)
    {
        _settingsByEndpoint.TryGetValue(GetSettingsKey(mode, endpoint), out var settings);
        return settings;
    }

    private static string GetSettingsKey(CallbackMode mode, IoEndpoint endpoint)
    {
        return $"{mode}:{endpoint.Name}";
    }

    private void RefreshEndpointButtonBrushes()
    {
        if (_selectedMode is not { } mode)
        {
            return;
        }

        foreach (var child in EndpointButtonsPanel.Children.OfType<Button>())
        {
            if (child.Tag is not IoEndpoint endpoint)
            {
                continue;
            }

            var settings = GetExistingSettings(mode, endpoint);
            var tone = Equals(endpoint, _selectedEndpoint)
                ? ButtonTone.Selected
                : settings?.HasActiveChannels == true
                    ? ButtonTone.Configured
                    : ButtonTone.Neutral;
            SetButtonTone(child, tone);
        }
    }

    private void StopDelay(string message)
    {
        if (_delay is null)
        {
            return;
        }

        _delay.Dispose();
        _delay = null;
        SetRunningState(null);
        AppendLog(message);
    }

    private void StartDelay(AppOptions options, string message)
    {
        VoicemeeterAudioDelay? delay = null;
        try
        {
            delay = new VoicemeeterAudioDelay(options);
            delay.Start();
            _delay = delay;
            delay = null;
            _lastArmTimeUtc = DateTime.UtcNow;
            _missingInputCallbackLogged = false;
            _missingOutputCallbackLogged = false;
            SetRunningState(options);
            AppendLog(message);
            AppendLog(DescribeAppExtraDelay(options));
            AppendLog(DescribeTargetChannels(options));
        }
        finally
        {
            delay?.Dispose();
        }
    }

    private void ReconfigureDelay(AppOptions options)
    {
        _delay?.Dispose();
        _delay = null;

        try
        {
            StartDelay(options, $"Re-armed callback: {DescribeTargets(options)}.");
        }
        catch
        {
            SetRunningState(null);
            throw;
        }
    }

    private void SetRunningState(AppOptions? options)
    {
        var running = options is not null && options.RegisterMode != CallbackMode.None;
        _runningOptions = running ? options : null;
        var statusText = running && options is not null && _delay is not null
            ? GetEngineStatusText(options, _delay.GetCallbackStats())
            : running && options is not null
                ? GetEngineStatusText(options)
                : "Engine stopped";
        StatusTextBlock.Text = statusText;
        StatusTextBlock.ToolTip = running ? GetActiveTargetTooltip() : "Tick a channel to arm the engine.";
        StatusTextBlock.Foreground = running ? Brushes.White : StatusIdleTextBrush;
        UpdateTrayText($"Voicemeeter Delay - {statusText}");

        EngineStatusBorder.Background = running ? RunningBrush : StatusIdleBrush;

        InputModeButton.IsEnabled = true;
        OutputModeButton.IsEnabled = true;
        DllPathTextBox.IsEnabled = !running;
        BrowseButton.IsEnabled = !running;
        ApiFallbackExpander.IsEnabled = !running;
        ArmBothStreamsCheckBox.IsEnabled = !running;

        foreach (var child in EndpointButtonsPanel.Children.OfType<Button>())
        {
            child.IsEnabled = true;
        }

        foreach (var editor in _channelEditors)
        {
            UpdateChannelEditorEnabled(editor);
        }
    }

    private void UpdateRunningStatusText(AppOptions options, AudioCallbackStats stats)
    {
        var statusText = GetEngineStatusText(options, stats);
        StatusTextBlock.Text = statusText;
        StatusTextBlock.ToolTip = GetActiveTargetTooltip() + Environment.NewLine + Environment.NewLine + FormatCallbackStats(stats);
        UpdateTrayText($"Voicemeeter Delay - {statusText}");
    }

    private void WarnIfRequestedCallbacksAreMissing(AppOptions options, AudioCallbackStats stats)
    {
        if (DateTime.UtcNow - _lastArmTimeUtc < CallbackWarningDelay)
        {
            return;
        }

        if (options.RegisterMode.HasFlag(CallbackMode.Input)
            && stats.InputCallbacks == 0
            && !_missingInputCallbackLogged)
        {
            _missingInputCallbackLogged = true;
            AppendLog($"Warning: input callback requested but no input buffers received. Registered {options.RegisterMode}; output buffers {stats.OutputCallbacks}; main buffers {stats.MainCallbacks}.");
        }

        if (options.RegisterMode.HasFlag(CallbackMode.Output)
            && stats.OutputCallbacks == 0
            && !_missingOutputCallbackLogged)
        {
            _missingOutputCallbackLogged = true;
            AppendLog($"Warning: output callback requested but no output buffers received. Registered {options.RegisterMode}; input buffers {stats.InputCallbacks}; main buffers {stats.MainCallbacks}.");
        }
    }

    private static string FormatCallbackStats(AudioCallbackStats stats)
    {
        return "Callback buffers received:"
            + Environment.NewLine
            + FormatCallbackSide("Input", stats.InputCallbacks, stats.Input)
            + Environment.NewLine
            + FormatCallbackSide("Output", stats.OutputCallbacks, stats.Output)
            + Environment.NewLine
            + FormatCallbackSide("Main", stats.MainCallbacks, stats.Main);
    }

    private static string FormatCallbackSide(string label, long count, AudioCallbackSideStats stats)
    {
        if (count == 0)
        {
            return $"{label}: 0";
        }

        return $"{label}: {count}, {stats.SampleRate} Hz, {stats.SampleCount} samples, nbi {stats.InputChannels}, nbo {stats.OutputChannels}, read {stats.ReadableBuffers}, write {stats.WritableBuffers}";
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private static void SetButtonTone(Button button, ButtonTone tone)
    {
        switch (tone)
        {
            case ButtonTone.Selected:
                button.Background = SelectedBrush;
                button.Foreground = AccentTextBrush;
                break;

            case ButtonTone.Configured:
                button.Background = ConfiguredBrush;
                button.Foreground = WarmTextBrush;
                break;

            default:
                button.Background = NeutralBrush;
                button.Foreground = TextBrush;
                break;
        }
    }

    private sealed class EndpointDelaySettings
    {
        public EndpointDelaySettings(CallbackMode mode, IoEndpoint endpoint, double pathLatencyMilliseconds)
        {
            Mode = mode;
            Endpoint = endpoint;
            Enabled = new bool[endpoint.ChannelCount];
            DelayMilliseconds = Enumerable.Repeat(pathLatencyMilliseconds, endpoint.ChannelCount).ToArray();
            VolumePercent = Enumerable.Repeat(100.0, endpoint.ChannelCount).ToArray();
            RouteEnabled = new bool[endpoint.ChannelCount];
            RouteDestinationBusIndex = new int[endpoint.ChannelCount];
            RouteDestinationChannelOffset = new int[endpoint.ChannelCount];
            RouteMuteNormal = new bool[endpoint.ChannelCount];
            RouteDestinations = Enumerable.Range(0, endpoint.ChannelCount)
                .Select(offset => new List<RouteDestinationSnapshot>
                {
                    new()
                    {
                        BusIndex = 0,
                        ChannelOffset = Math.Min(offset, 7)
                    }
                })
                .ToArray();
        }

        public CallbackMode Mode { get; }

        public IoEndpoint Endpoint { get; private set; }

        public bool[] Enabled { get; }

        public double[] DelayMilliseconds { get; }

        public double[] VolumePercent { get; }

        public bool HasSelectedChannels => Enabled.Any(static selected => selected);

        public bool[] RouteEnabled { get; }

        public int[] RouteDestinationBusIndex { get; }

        public int[] RouteDestinationChannelOffset { get; }

        public bool[] RouteMuteNormal { get; }

        public List<RouteDestinationSnapshot>[] RouteDestinations { get; }

        public bool HasActiveRoutes => RouteEnabled.Any(static selected => selected);

        public bool HasActiveChannels => HasSelectedChannels || HasActiveRoutes;

        public void RebindEndpoint(IoEndpoint endpoint)
        {
            if (endpoint.ChannelCount != Endpoint.ChannelCount)
            {
                throw new InvalidOperationException("Cannot rebind endpoint to a different channel count.");
            }

            Endpoint = endpoint;
        }

        public double GetDisplayedDelay(int offset, double pathLatencyMilliseconds)
        {
            return Math.Max(DelayMilliseconds[offset], pathLatencyMilliseconds);
        }

        private double GetExtraDelayMilliseconds(int offset, double pathLatencyMilliseconds)
        {
            return Math.Max(0, GetDisplayedDelay(offset, pathLatencyMilliseconds) - pathLatencyMilliseconds);
        }

        public void EnsureMinimumDisplayedDelay(double pathLatencyMilliseconds)
        {
            for (var offset = 0; offset < DelayMilliseconds.Length; offset++)
            {
                DelayMilliseconds[offset] = GetDisplayedDelay(offset, pathLatencyMilliseconds);
            }
        }

        public void AdjustDisplayedDelayFloor(double oldPathLatencyMilliseconds, double newPathLatencyMilliseconds)
        {
            var delta = newPathLatencyMilliseconds - oldPathLatencyMilliseconds;
            for (var offset = 0; offset < DelayMilliseconds.Length; offset++)
            {
                DelayMilliseconds[offset] = Math.Max(newPathLatencyMilliseconds, DelayMilliseconds[offset] + delta);
            }
        }

        public void ApplySnapshot(EndpointDelaySettingsSnapshot snapshot, double pathLatencyMilliseconds)
        {
            for (var offset = 0; offset < Enabled.Length; offset++)
            {
                Enabled[offset] = offset < snapshot.Enabled.Length && snapshot.Enabled[offset];
                DelayMilliseconds[offset] = offset < snapshot.DelayMilliseconds.Length
                    ? Math.Max(pathLatencyMilliseconds, SanitizeDelay(snapshot.DelayMilliseconds[offset], pathLatencyMilliseconds))
                    : pathLatencyMilliseconds;
                VolumePercent[offset] = offset < snapshot.VolumePercent.Length
                    ? SanitizeVolume(snapshot.VolumePercent[offset])
                    : 100.0;
                RouteEnabled[offset] = offset < snapshot.RouteEnabled.Length && snapshot.RouteEnabled[offset];
                RouteDestinationBusIndex[offset] = offset < snapshot.RouteDestinationBusIndex.Length
                    ? Math.Max(0, snapshot.RouteDestinationBusIndex[offset])
                    : 0;
                RouteDestinationChannelOffset[offset] = offset < snapshot.RouteDestinationChannelOffset.Length
                    ? Math.Max(0, snapshot.RouteDestinationChannelOffset[offset])
                    : 0;
                RouteMuteNormal[offset] = offset < snapshot.RouteMuteNormal.Length && snapshot.RouteMuteNormal[offset];
                RouteDestinations[offset].Clear();
                if (offset < snapshot.RouteDestinations.Count && snapshot.RouteDestinations[offset].Count > 0)
                {
                    RouteDestinations[offset].AddRange(snapshot.RouteDestinations[offset].Select(CloneRouteDestination));
                }
                else
                {
                    RouteDestinations[offset].Add(new RouteDestinationSnapshot
                    {
                        BusIndex = RouteDestinationBusIndex[offset],
                        ChannelOffset = RouteDestinationChannelOffset[offset]
                    });
                }

                SyncLegacyRouteDestination(offset);
            }
        }

        public IEnumerable<DelayTarget> ToDelayTargets(double pathLatencyMilliseconds)
        {
            for (var offset = 0; offset < Enabled.Length; offset++)
            {
                if (!Enabled[offset])
                {
                    continue;
                }

                var delayLineMilliseconds = GetExtraDelayMilliseconds(offset, pathLatencyMilliseconds);
                var gain = VolumePercent[offset] / 100.0;
                var channel = Endpoint.Range.Start + offset;
                var label = Endpoint.ChannelCount == 2
                    ? (offset == 0 ? "L" : "R")
                    : $"Ch {offset + 1}";
                yield return new DelayTarget(
                    Mode,
                    $"{Endpoint.Name} {label}",
                    ChannelSelection.FromZeroBasedChannels([channel]),
                    delayLineMilliseconds,
                    Gain: gain);
            }
        }

        public IEnumerable<AudioRoute> ToAudioRoutes(VoicemeeterKind layoutKind, double pathLatencyMilliseconds)
        {
            if (Mode != CallbackMode.Input)
            {
                yield break;
            }

            var outputEndpoints = VoicemeeterIoLayout.GetEndpoints(CallbackMode.Output, layoutKind);
            if (outputEndpoints.Count == 0)
            {
                yield break;
            }

            for (var offset = 0; offset < RouteEnabled.Length; offset++)
            {
                if (!RouteEnabled[offset])
                {
                    continue;
                }

                var sourceChannel = Endpoint.Range.Start + offset;
                var gain = VolumePercent[offset] / 100.0;
                var delayLineMilliseconds = GetExtraDelayMilliseconds(offset, pathLatencyMilliseconds);
                var label = Endpoint.ChannelCount == 2
                    ? (offset == 0 ? "L" : "R")
                    : $"Ch {offset + 1}";

                EnsureRouteDestination(offset);
                foreach (var destination in RouteDestinations[offset])
                {
                    var busIndex = Math.Clamp(destination.BusIndex, 0, outputEndpoints.Count - 1);
                    var bus = outputEndpoints[busIndex];
                    var destinationOffset = Math.Clamp(destination.ChannelOffset, 0, bus.ChannelCount - 1);
                    var destinationChannel = bus.Range.Start + destinationOffset;

                    yield return new AudioRoute(
                        $"{Endpoint.Name} {label} -> {bus.Name} Ch {destinationOffset + 1}",
                        sourceChannel,
                        destinationChannel,
                        delayLineMilliseconds,
                        gain,
                        RouteMuteNormal[offset]);
                }
            }
        }

        public void EnsureRouteDestination(int offset)
        {
            if (RouteDestinations[offset].Count == 0)
            {
                RouteDestinations[offset].Add(new RouteDestinationSnapshot
                {
                    BusIndex = RouteDestinationBusIndex[offset],
                    ChannelOffset = RouteDestinationChannelOffset[offset]
                });
            }

            SyncLegacyRouteDestination(offset);
        }

        public void SyncLegacyRouteDestination(int offset)
        {
            if (RouteDestinations[offset].Count == 0)
            {
                RouteDestinationBusIndex[offset] = 0;
                RouteDestinationChannelOffset[offset] = 0;
                return;
            }

            RouteDestinationBusIndex[offset] = Math.Max(0, RouteDestinations[offset][0].BusIndex);
            RouteDestinationChannelOffset[offset] = Math.Max(0, RouteDestinations[offset][0].ChannelOffset);
        }
    }

    private sealed record ChannelEditor(
        int Offset,
        Border Container,
        CheckBox CheckBox,
        Slider Slider,
        TextBox DelayTextBox,
        Slider VolumeSlider,
        TextBox VolumeTextBox,
        CheckBox? RouteCheckBox,
        Button? RouteButton,
        CheckBox? RouteMuteNormalCheckBox);

    private sealed record ActiveEndpointDetail(
        string Name,
        int ChannelCount,
        string ChannelList);

    private enum ButtonTone
    {
        Neutral,
        Selected,
        Configured
    }
}

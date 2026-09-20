using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using PulsePal.Core;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace PulsePal.App;

public sealed partial class CompanionWindow : Window
{
    public ViewModels.CompanionViewModel ViewModel { get; }
    private readonly AppController _controller;
    private readonly nint _hwnd;
    private bool _closing;
    private bool _shown;
    private bool _hiding;
    private bool _activated;
    private int _visibilityVersion;
    public bool IsUiReady { get; private set; }
    public bool HasError => ErrorBanner.IsOpen;
    public bool IsPopupVisible => _shown && !_hiding;
    public string DisplayedTitle => MessageTitle.Text;
    public string DisplayedBanner => Banner.IsOpen ? Banner.Message : "";
    public bool IsPeakPromptVisible => PeakPanel.Visibility == Visibility.Visible;
    public string DisplayedIdentity => CompanionIdentity.Text;
    public CompanionProfileId AppliedProfile => Avatar.SelectedProfile;
    public string DisplayedFocusFeedback => FocusEndedText.Text;
    public bool RecoveryCommandsBound =>
        ReferenceEquals(PeakScreenButton.Command, ViewModel.StartScreenBreakCommand) &&
        ReferenceEquals(PeakWaterButton.Command, ViewModel.StartWaterBreakCommand) &&
        ReferenceEquals(PeakStretchButton.Command, ViewModel.StartStretchBreakCommand) &&
        ReferenceEquals(PeakBreathingButton.Command, ViewModel.StartBreathingCommand) &&
        ReferenceEquals(ScreenBreakMenuItem.Command, ViewModel.StartScreenBreakCommand) &&
        ReferenceEquals(WaterBreakMenuItem.Command, ViewModel.StartWaterBreakCommand) &&
        ReferenceEquals(StretchBreakMenuItem.Command, ViewModel.StartStretchBreakCommand) &&
        ReferenceEquals(BreathingMenuItem.Command, ViewModel.StartBreathingCommand) &&
        ReferenceEquals(CancelRecoveryButton.Command, ViewModel.CancelRecoveryCommand) &&
        ReferenceEquals(HideRecoveryButton.Command, ViewModel.HideRecoveryTimerCommand) &&
        ReferenceEquals(ProfileMenuItem.Command, ViewModel.ShowProfileCommand) &&
        ReferenceEquals(FocusButton.Command, ViewModel.ToggleFocusCommand);

    public async Task<bool> CaptureSmokeVisualAsync(string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0 || pixels.Length == 0) return false;
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var png = new byte[(int)stream.Size];
        reader.ReadBytes(png);
        await File.WriteAllBytesAsync(path, png);
        return pixels.Where((_, index) => index % 4 != 3).Distinct().Take(32).Count() == 32;
    }

    public CompanionWindow(AppController controller)
    {
        _controller = controller;
        ViewModel = new ViewModels.CompanionViewModel(controller);
        InitializeComponent();
        Title = "PulsePal companion";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        var corners = 2;
        DwmSetWindowAttribute(_hwnd, 33, ref corners, sizeof(int));
        AppWindow.Closing += (_, args) =>
        {
            if (_closing) return;
            args.Cancel = true;
            if (_controller.ActiveRecovery is not null) _controller.HideRecoveryTimer();
            else _controller.Dismiss();
        };
        Root.Loaded += (_, _) =>
        {
            IsUiReady = true;
            AnimateIn();
        };
        Root.ActualThemeChanged += (_, _) => PositionAtTaskbar();
        Root.SizeChanged += (_, _) =>
        {
            if (Root.XamlRoot is not null)
                ElementCompositionPreview.GetElementVisual(Card).CenterPoint = new Vector3((float)Card.ActualWidth / 2, (float)Card.ActualHeight / 2, 0);
        };
        PositionAtTaskbar();
    }

    public void ShowPopup()
    {
        ++_visibilityVersion;
        var restoreAnimation = _hiding;
        _hiding = false;
        if (!_shown)
        {
            PositionAtTaskbar();
            if (!_activated)
            {
                Activate();
                _activated = true;
            }
            else
            {
                AppWindow.Show(false);
            }
            _shown = true;
            AnimateIn();
        }
        else if (restoreAnimation)
        {
            AnimateIn();
        }
    }

    public async void HidePopup()
    {
        if (!_shown || _closing) return;
        var version = ++_visibilityVersion;
        _hiding = true;
        try
        {
            if (IsUiReady && new UISettings().AnimationsEnabled)
            {
                var visual = ElementCompositionPreview.GetElementVisual(Card);
                using var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(1, 0);
                fade.Duration = TimeSpan.FromMilliseconds(160);
                visual.StartAnimation("Opacity", fade);
                await Task.Delay(170);
            }
            if (version != _visibilityVersion || _closing) return;
            AppWindow.Hide();
            _shown = false;
            _hiding = false;
        }
        catch (Exception exception) { _controller.ReportError("Could not hide companion", exception); }
    }

    private void AnimateIn()
    {
        if (!IsUiReady) return;
        var visual = ElementCompositionPreview.GetElementVisual(Card);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        ElementCompositionPreview.SetIsTranslationEnabled(Card, true);
        if (!new UISettings().AnimationsEnabled) return;
        using var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(280);
        visual.StartAnimation("Opacity", fade);
        using var slide = visual.Compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0, 18);
        slide.InsertKeyFrame(1, 0);
        slide.Duration = TimeSpan.FromMilliseconds(340);
        visual.StartAnimation("Translation.Y", slide);
    }

    private void PositionAtTaskbar(bool useCursor = true)
    {
        GetCursorPos(out var cursor);
        var monitor = useCursor ? MonitorFromPoint(cursor, 2) : MonitorFromWindow(_hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot determine display work area.");
        // Work-area coordinates and AppWindow geometry are physical pixels; XAML uses DIPs.
        var dpiResult = GetDpiForMonitor(monitor, 0, out var dpi, out _);
        if (dpiResult != 0 || dpi == 0) dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96d;
        var margin = (int)Math.Round(14 * scale);
        var width = Math.Min((int)Math.Round(376 * scale), info.Work.Right - info.Work.Left - margin * 2);
        var expanded = _controller.ActiveRecovery is not null || _controller.IsPeakPromptActive;
        var height = Math.Min((int)Math.Round((expanded ? 790 : 630) * scale), info.Work.Bottom - info.Work.Top - margin * 2);
        AppWindow.MoveAndResize(new RectInt32(info.Work.Right - width - margin, info.Work.Bottom - height - margin,
            Math.Max(200, width), Math.Max(200, height)));
    }

    public void SetMessage(CompanionMessage message)
    {
        ViewModel.MessageTitle = message.Title;
        ViewModel.MessageText = message.Text;
        Avatar.SetState(message.Character);
    }
    public void SetCharacter(CharacterState state) => Avatar.SetState(state);
    public void SetFocus(bool active) => ViewModel.FocusActionText = active ? "End Focus Session" : "Start Focus Session";
    public void SetBanner(string text) { Banner.Message = text; Banner.IsOpen = true; }
    public void SetError(string text) { ErrorBanner.Message = text; ErrorBanner.IsOpen = true; }

    public void ApplyPreferences(UserPreferences preferences)
    {
        var profile = CompanionProfiles.Get(preferences.CompanionProfile);
        Avatar.SetProfile(preferences.CompanionProfile);
        ViewModel.CompanionIdentity = profile.Name + " · " + profile.Personality;
        var option = RecoveryActivities.Get(preferences.PreferredBreak);
        ViewModel.RecommendedBreak = $"Your preferred break: {option.Title} · {option.Duration.TotalMinutes:0} min";
    }

    public void SetPeakPrompt(CompanionMessage? message)
    {
        PeakPanel.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        MessagePanel.Visibility = message is null ? Visibility.Visible : Visibility.Collapsed;
        DismissActions.Visibility = message is null ? Visibility.Visible : Visibility.Collapsed;
        Avatar.Width = message is null ? 180 : 126;
        Avatar.Height = message is null ? 160 : 112;
        if (message is not null)
        {
            PeakTitle.Text = message.Title;
            PeakText.Text = message.Text;
            Avatar.SetState(message.Character);
        }
        PositionAtTaskbar(!_shown);
    }

    public void BeginBreathing() => BeginRecovery(RecoveryActivity.Breathing, string.Empty);

    public void BeginRecovery(RecoveryActivity activity, string focusFeedback)
    {
        var option = RecoveryActivities.Get(activity);
        Avatar.SetState(CharacterState.Resting);
        ViewModel.MessageTitle = option.Title;
        ViewModel.MessageText = option.Instructions;
        BreathingPanel.Visibility = activity == RecoveryActivity.Breathing ? Visibility.Visible : Visibility.Collapsed;
        RecoveryPanel.Visibility = activity == RecoveryActivity.Breathing ? Visibility.Collapsed : Visibility.Visible;
        RecoveryActions.Visibility = Visibility.Visible;
        DismissActions.Visibility = Visibility.Collapsed;
        FocusEndedText.Text = focusFeedback;
        FocusEndedText.Visibility = focusFeedback.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Banner.IsOpen = false;
        if (activity == RecoveryActivity.Breathing) UpdateBreathing("Breathe in", 4, 1, 0, 0);
        else UpdateRecovery(option.Duration, 0);
        PositionAtTaskbar(!_shown);
    }

    public void UpdateRecovery(TimeSpan remaining, double progress)
    {
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        RecoveryCountdown.Text = $"{seconds / 60:00}:{seconds % 60:00} remaining";
        RecoveryProgress.Value = progress;
    }

    public void UpdateBreathing(string phase, int seconds, int cycle, double progress, double expansion)
    {
        BreathingPhase.Text = $"{phase} · {seconds}";
        BreathingCycle.Text = $"Cycle {cycle} of 5 · {(int)Math.Ceiling((1 - progress) * 60)}s remaining";
        BreathingProgress.Value = progress;
        Avatar.SetBreathingProgress(expansion);
    }

    public void EndBreathing() => EndRecovery();

    public void EndRecovery()
    {
        BreathingPanel.Visibility = RecoveryPanel.Visibility = RecoveryActions.Visibility = Visibility.Collapsed;
        FocusEndedText.Visibility = Visibility.Collapsed;
        DismissActions.Visibility = Visibility.Visible;
        Avatar.SetBreathingProgress(0);
        PositionAtTaskbar(false);
    }

    public void CloseForExit() { _closing = true; ++_visibilityVersion; Close(); }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

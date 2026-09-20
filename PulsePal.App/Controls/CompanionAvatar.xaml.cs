using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using PulsePal.Core;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PulsePal.App.Controls;

public sealed partial class CompanionAvatar : UserControl
{
    private readonly UISettings _uiSettings = new();
    private Visual? _avatarVisual;
    private Visual? _ringVisual;
    private double _expansion;
    private CharacterState _state = CharacterState.Normal;

    public CompanionProfileId SelectedProfile { get; private set; } = CompanionProfileId.Nova;

    public CompanionAvatar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SetProfile(CompanionProfileId.Nova);
    }

    public void SetProfile(CompanionProfileId profile)
    {
        var identity = CompanionProfiles.Get(profile);
        SelectedProfile = identity.Id;
        ((SolidColorBrush)Resources["IdentityBrush"]).Color = FromRgb(identity.AccentRgb);

        NovaBackHair.Visibility = NovaAppearance.Visibility = profile == CompanionProfileId.Nova
            ? Visibility.Visible : Visibility.Collapsed;
        LumiBackHair.Visibility = LumiAppearance.Visibility = profile == CompanionProfileId.Lumi
            ? Visibility.Visible : Visibility.Collapsed;
        KairoBackHair.Visibility = KairoAppearance.Visibility = profile == CompanionProfileId.Kairo
            ? Visibility.Visible : Visibility.Collapsed;

        SetState(_state);
    }

    public void SetState(CharacterState state)
    {
        if (!Enum.IsDefined(typeof(CharacterState), state))
            state = CharacterState.Normal;

        _state = state;
        var (accent, eyes, emote, description) = state switch
        {
            CharacterState.Focused => (0x59CCFFu, 0x9CDEFFu, "⌖", "Steady and focused"),
            CharacterState.Happy => (0x8CF5D2u, 0xB4FFE5u, "♥", "Happy to be with you"),
            CharacterState.Encouraging => (0xB9EF87u, 0xD8FFB5u, "↑", "You can take the next small step"),
            CharacterState.Concerned => (0xF4C48Bu, 0xFFE2B5u, "♡", "Here for you; a pause is okay"),
            CharacterState.Thinking => (0xC4AEFFu, 0xE0D4FFu, "···", "Considering your next gentle nudge"),
            CharacterState.Celebrating => (0xFFE092u, 0xBFFFF3u, "★", "Celebrating your progress"),
            CharacterState.Resting => (0x9CAFE6u, 0xC7D5FFu, "☾", "Resting and recharging"),
            _ => (0x67E8F9u, 0x8CF5FFu, "✦", "Here with you")
        };

        ((SolidColorBrush)Resources["AccentBrush"]).Color = FromRgb(accent);
        ((SolidColorBrush)Resources["EyeBrush"]).Color = FromRgb(eyes);

        UIElement[] expressions =
        {
            OpenEyes, JoyEyes, FocusedEyes, ThinkingEyes, RestingEyes,
            RelaxedBrows, ConcernedBrows, ThinkingBrows,
            NormalMouth, FocusedMouth, HappyMouth, EncouragingMouth,
            ConcernedMouth, ThinkingMouth, CelebratingMouth, RestingMouth,
            Blush, CelebrationSparks
        };
        foreach (var expression in expressions)
            expression.Visibility = Visibility.Collapsed;

        UIElement activeEyes = state switch
        {
            CharacterState.Focused => FocusedEyes,
            CharacterState.Happy or CharacterState.Celebrating => JoyEyes,
            CharacterState.Thinking => ThinkingEyes,
            CharacterState.Resting => RestingEyes,
            _ => OpenEyes
        };
        UIElement activeBrows = state switch
        {
            CharacterState.Concerned => ConcernedBrows,
            CharacterState.Thinking => ThinkingBrows,
            _ => RelaxedBrows
        };
        UIElement activeMouth = state switch
        {
            CharacterState.Focused => FocusedMouth,
            CharacterState.Happy => HappyMouth,
            CharacterState.Encouraging => EncouragingMouth,
            CharacterState.Concerned => ConcernedMouth,
            CharacterState.Thinking => ThinkingMouth,
            CharacterState.Celebrating => CelebratingMouth,
            CharacterState.Resting => RestingMouth,
            _ => NormalMouth
        };
        activeEyes.Visibility = Visibility.Visible;
        activeBrows.Visibility = Visibility.Visible;
        activeMouth.Visibility = Visibility.Visible;
        Blush.Visibility = state is CharacterState.Happy or CharacterState.Encouraging or CharacterState.Celebrating
            ? Visibility.Visible : Visibility.Collapsed;
        CelebrationSparks.Visibility = state == CharacterState.Celebrating
            ? Visibility.Visible : Visibility.Collapsed;
        BreathingRing.Opacity = state switch
        {
            CharacterState.Resting => 0.4,
            CharacterState.Concerned => 0.65,
            CharacterState.Thinking => 0.75,
            _ => 1.0
        };

        EmoteText.Text = emote;
        StateBadge.Text = state.ToString().ToUpperInvariant();
        var identity = CompanionProfiles.Get(SelectedProfile);
        AutomationProperties.SetName(this, $"{identity.Name}, {identity.Personality}. Companion state: {state}. {description}.");
        AutomationProperties.SetName(StateBadge, $"{identity.Name}: {state}");
        ToolTipService.SetToolTip(this, $"{identity.Name} · {identity.Personality}\n{state} · {description}");
    }

    public void SetBreathingProgress(double expansion)
    {
        _expansion = double.IsNaN(expansion) ? 0 : Math.Clamp(expansion, 0, 1);
        ApplyBreathingProgress(animate: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _avatarVisual = ElementCompositionPreview.GetElementVisual(AvatarArtwork);
        _ringVisual = ElementCompositionPreview.GetElementVisual(BreathingRing);
        _avatarVisual.CenterPoint = new Vector3(90, 78, 0);
        _ringVisual.CenterPoint = new Vector3(90, 71, 0);
        ApplyBreathingProgress(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // XAML owns these visuals; release our references, not the visuals themselves.
        _avatarVisual?.StopAnimation(nameof(Visual.Scale));
        _ringVisual?.StopAnimation(nameof(Visual.Scale));
        _avatarVisual = null;
        _ringVisual = null;
    }

    private void ApplyBreathingProgress(bool animate)
    {
        if (_avatarVisual is null || _ringVisual is null)
            return;

        var useAnimation = animate && _uiSettings.AnimationsEnabled;
        ScaleTo(_avatarVisual, 1f + (float)_expansion * 0.035f, useAnimation);
        ScaleTo(_ringVisual, 1f + (float)_expansion * 0.09f, useAnimation);
    }

    private static void ScaleTo(Visual visual, float scale, bool animate)
    {
        var target = new Vector3(scale, scale, 1);
        if (!animate)
        {
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Scale = target;
            return;
        }

        // An implicit starting keyframe continues from the current presentation value.
        // Finite compositor animations need no timer or completion-event subscription.
        using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        using var easing = visual.Compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0), new Vector2(0.2f, 1));
        animation.Duration = TimeSpan.FromMilliseconds(160);
        animation.InsertKeyFrame(1, target, easing);
        visual.StartAnimation(nameof(Visual.Scale), animation);
    }

    private static Color FromRgb(uint rgb) => Color.FromArgb(
        255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

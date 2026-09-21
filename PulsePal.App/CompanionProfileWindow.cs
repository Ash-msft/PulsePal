using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PulsePal.App.Controls;
using PulsePal.Core;
using Windows.Graphics;
using Windows.UI;

namespace PulsePal.App;

public sealed class CompanionProfileWindow : Window
{
    private readonly AppController _controller;
    private readonly Dictionary<CompanionProfileId, CompanionAvatar> _avatars = new();
    private readonly Dictionary<CompanionProfileId, Border> _cards = new();
    private readonly RadioButtons _profiles = new()
    {
        Header = "Choose your companion", MaxColumns = 1,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox _mood = new()
    {
        Header = "Preview an expression", HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _greeting = Body();
    private readonly TextBlock _previewTitle = Heading("A little hello");
    private readonly TextBox _displayName = new()
    {
        Header = "What should we call you?", MaxLength = 80,
        PlaceholderText = "Your display name", HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox _preferredBreak = new()
    {
        Header = "Your go-to break", HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _breakDetails = Body();
    private readonly ToggleSwitch _hydration = new()
    {
        Header = "Gentle hydration reminders", OnContent = "On", OffContent = "Off"
    };
    private readonly NumberBox _popupSeconds = new()
    {
        Header = "Keep companion popups visible (seconds)",
        Minimum = 5, Maximum = 120, SmallChange = 1, LargeChange = 5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _status = Body("Make yourself at home. Changes stay in preview until you apply them.");
    private readonly TextBlock _storageStatus = Body();
    private readonly Button _meetButton = new() { MinHeight = 40 };
    private CompanionProfileId _selectedProfile;

    public bool IsClosed { get; private set; }

    public CompanionProfileWindow(AppController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Title = "Profile & preferences";

        var preferences = controller.Engine.Preferences;
        _selectedProfile = CompanionProfiles.Get(preferences.CompanionProfile).Id;
        var page = new StackPanel
        {
            Spacing = 20, Margin = new Thickness(24), MaxWidth = 780,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        page.Children.Add(new TextBlock
        {
            Text = "PULSEPAL / MADE FOR YOU", FontSize = 12, CharacterSpacing = 140,
            Foreground = Brush(0x67E8F9), TextWrapping = TextWrapping.Wrap
        });
        page.Children.Add(new TextBlock
        {
            Text = "Your companion", FontSize = 32,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(0xF0F7FF), TextWrapping = TextWrapping.Wrap
        });
        page.Children.Add(Body("A familiar face. A little encouragement. Find the companion and daily rhythm that feel like you."));
        _meetButton.Command = new RelayCommand(_controller.MeetCompanion);
        UpdateMeetLabel();
        page.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Heading("Your current companion"),
                Body("Replay your current companion's introduction. Preview choices below won't change who you meet until you apply them."),
                _meetButton
            }
        }));

        foreach (var profile in CompanionProfiles.All)
        {
            var avatar = new CompanionAvatar { Width = 106, Height = 104, VerticalAlignment = VerticalAlignment.Center };
            avatar.SetProfile(profile.Id);
            _avatars.Add(profile.Id, avatar);

            var copy = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(Heading(profile.Name));
            var personality = Body(profile.Personality);
            personality.Foreground = Brush(profile.AccentRgb);
            personality.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            copy.Children.Add(personality);
            copy.Children.Add(Body(profile.Description));

            var layout = new Grid { ColumnSpacing = 12 };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.Children.Add(avatar);
            Grid.SetColumn(copy, 1);
            layout.Children.Add(copy);
            var card = Card(layout);
            _cards.Add(profile.Id, card);
            var choice = new RadioButton
            {
                Content = card, Tag = profile.Id,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(choice, $"{profile.Name}, {profile.Personality}");
            AutomationProperties.SetHelpText(choice, profile.Description);
            _profiles.Items.Add(choice);
            if (profile.Id == _selectedProfile)
                _profiles.SelectedIndex = _profiles.Items.Count - 1;
        }
        _profiles.SelectionChanged += (_, _) =>
        {
            if (_profiles.SelectedItem is RadioButton { Tag: CompanionProfileId profile })
            {
                _selectedProfile = profile;
                UpdatePreview();
                MarkDraft();
            }
        };
        page.Children.Add(_profiles);

        AddMood(CharacterState.Normal, "Everyday");
        AddMood(CharacterState.Focused, "Focusing");
        AddMood(CharacterState.Happy, "Happy");
        AddMood(CharacterState.Encouraging, "Cheering you on");
        AddMood(CharacterState.Concerned, "Time for a pause");
        AddMood(CharacterState.Thinking, "Thinking");
        AddMood(CharacterState.Celebrating, "Celebrating");
        AddMood(CharacterState.Resting, "Resting");
        _mood.SelectedIndex = 0;
        _mood.SelectionChanged += (_, _) => UpdatePreview();
        page.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children = { _previewTitle, _greeting, _mood, Body("Same companion, every mood. This preview won't change your current session.") }
        }));

        _displayName.Text = preferences.DisplayName;
        _hydration.IsOn = preferences.HydrationReminders;
        _popupSeconds.Value = Math.Clamp(preferences.PopupSeconds, 5, 120);
        foreach (var option in RecoveryActivities.All)
        {
            var minutes = option.Duration.TotalMinutes;
            var item = new ComboBoxItem
            {
                Tag = option.Activity,
                Content = $"{option.Title} · {minutes:0.#} {(minutes == 1 ? "minute" : "minutes")}"
            };
            _preferredBreak.Items.Add(item);
            if (option.Activity == preferences.PreferredBreak)
                _preferredBreak.SelectedItem = item;
        }
        _preferredBreak.SelectionChanged += (_, _) =>
        {
            UpdateBreakDescription();
            MarkDraft();
        };
        _displayName.TextChanged += (_, _) => MarkDraft();
        _hydration.Toggled += (_, _) => MarkDraft();
        _popupSeconds.ValueChanged += (_, _) => MarkDraft();
        page.Children.Add(Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Heading("Your everyday rhythm"),
                Body("Small preferences that make your companion feel more personal."),
                _displayName, _preferredBreak, _breakDetails, _hydration, _popupSeconds,
                Body("Choose 5–120 seconds. You can always bring your companion back from the notification area.")
            }
        }));

        var apply = new Button
        {
            Content = "Apply preferences", MinHeight = 40, AccessKey = "A",
            Background = Brush(0x67E8F9), Foreground = Brush(0x102238)
        };
        apply.Click += (_, _) => ApplyPreferences();
        var close = new Button { Content = "Close", MinHeight = 40, AccessKey = "C" };
        close.Click += (_, _) => Close();
        ToolTipService.SetToolTip(close, "Close without applying any further changes");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_storageStatus, AutomationLiveSetting.Polite);
        _storageStatus.FontSize = 12;
        var footer = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(24, 14, 24, 18), MaxWidth = 780,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { apply, close } },
                _status, _storageStatus
            }
        };
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Background = Brush(0x0E1728) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer
        {
            Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Enabled
        });
        var footerBorder = new Border
        {
            Background = Brush(0x122239), BorderBrush = Brush(0x2A4B69),
            BorderThickness = new Thickness(0, 1, 0, 0), Child = footer
        };
        Grid.SetRow(footerBorder, 1);
        root.Children.Add(footerBorder);
        Content = root;

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(780, workArea.Width), Math.Min(900, workArea.Height)));
        UpdatePreview();
        UpdateBreakDescription();
        UpdateStorageStatus();
        // Observe applied identity and storage only; engine ticks must never replace an unsaved draft.
        _controller.Updated += OnUpdated;
        _controller.PropertyChanged += OnControllerPropertyChanged;
        Closed += OnClosed;
    }

    private void AddMood(CharacterState state, string label) =>
        _mood.Items.Add(new ComboBoxItem { Content = label, Tag = state });

    private void UpdatePreview()
    {
        var profile = CompanionProfiles.Get(_selectedProfile);
        var state = _mood.SelectedItem is ComboBoxItem { Tag: CharacterState mood }
            ? mood : CharacterState.Normal;
        foreach (var identity in CompanionProfiles.All)
        {
            var selected = identity.Id == _selectedProfile;
            _cards[identity.Id].BorderBrush = Brush(selected ? identity.AccentRgb : 0x2A4B69u);
            _cards[identity.Id].Background = Brush(selected ? 0x183552u : 0x122239u);
            _avatars[identity.Id].SetState(selected ? state : CharacterState.Normal);
        }
        _previewTitle.Text = $"A little hello from {profile.Name}";
        _greeting.Text = profile.Greeting;
    }

    private void UpdateBreakDescription()
    {
        _breakDetails.Text = _preferredBreak.SelectedItem is ComboBoxItem { Tag: RecoveryActivity activity }
            ? RecoveryActivities.Get(activity).Instructions
            : "Choose a break that fits your day.";
    }

    private void MarkDraft() => _status.Text = "Preview only · choose Apply preferences to keep your changes.";

    private void ApplyPreferences()
    {
        var name = _displayName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _status.Text = "Please enter a display name before applying.";
            _displayName.Focus(FocusState.Programmatic);
            return;
        }
        if (_preferredBreak.SelectedItem is not ComboBoxItem { Tag: RecoveryActivity activity })
        {
            _status.Text = "Please choose your go-to break.";
            _preferredBreak.Focus(FocusState.Programmatic);
            return;
        }
        if (!double.IsFinite(_popupSeconds.Value))
        {
            _status.Text = "Please enter a popup duration from 5 to 120 seconds.";
            _popupSeconds.Focus(FocusState.Programmatic);
            return;
        }

        var seconds = (int)Math.Clamp(Math.Round(_popupSeconds.Value), 5, 120);
        _popupSeconds.Value = seconds;
        _displayName.Text = name;
        var current = _controller.Engine.Preferences;
        var preferences = current with
        {
            CompanionProfile = _selectedProfile,
            PreferredBreak = activity,
            DisplayName = name,
            HydrationReminders = _hydration.IsOn,
            PopupSeconds = seconds
        };
        _controller.SavePreferences(preferences);
        if (_controller.Engine.Preferences != preferences)
        {
            _status.Text = "Your preferences couldn't be applied. Please try again.";
            UpdateStorageStatus();
            return;
        }
        _status.Text = $"{CompanionProfiles.Get(_selectedProfile).Name} is your companion. Preferences applied.";
        UpdateStorageStatus();
        UpdateMeetLabel();
    }

    private void UpdateMeetLabel()
    {
        var label = $"Meet {CompanionProfiles.Get(_controller.Engine.Preferences.CompanionProfile).Name}";
        _meetButton.Content = label;
        AutomationProperties.SetName(_meetButton, label);
    }

    private void OnUpdated()
    {
        if (!IsClosed)
            UpdateMeetLabel();
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsClosed && e.PropertyName == nameof(AppController.Status))
            UpdateStorageStatus();
    }

    private void UpdateStorageStatus() => _storageStatus.Text = _controller.Status;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _controller.Updated -= OnUpdated;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        Closed -= OnClosed;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(0xEFF7FF), TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Body(string text = "") => new()
    {
        Text = text, FontSize = 14, Foreground = Brush(0xC6D8EB), TextWrapping = TextWrapping.Wrap
    };

    private static Border Card(UIElement content) => new()
    {
        Child = content, Background = Brush(0x122239), BorderBrush = Brush(0x2A4B69),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
        Padding = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static SolidColorBrush Brush(uint rgb) => new(Color.FromArgb(
        255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}

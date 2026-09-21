using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PulsePal.Core;
using Windows.Graphics;
using Windows.UI;

namespace PulsePal.App;

public sealed class DemoControlsWindow : Window
{
    private readonly AppController _controller;
    private readonly TextBlock _status = Body();
    private readonly TextBlock _lastMessage = Body();
    private readonly TextBlock _scenario = Body();
    private readonly TextBlock _context = Body();
    private readonly TextBlock _decision = Body();
    private readonly TextBlock _focusStatus = Body();
    private readonly TextBlock _recoveryStatus = Body();
    private readonly TextBlock _notificationStatus = Body();
    private readonly TextBlock _heartRate = MetricValue();
    private readonly TextBlock _stress = MetricValue();
    private readonly TextBlock _sleep = MetricValue();
    private readonly TextBlock _readiness = MetricValue();
    private readonly TextBlock _focus = MetricValue();
    private readonly TextBlock _burnout = MetricValue();
    private readonly StackPanel _reasons = new() { Spacing = 8 };
    private readonly StackPanel _notifications = new() { Spacing = 8 };
    private readonly StackPanel _history = new() { Spacing = 8 };
    private readonly Button _focusButton = new() { Content = "Start focus", MinHeight = 40 };
    private readonly Button _meetButton;
    private readonly List<IRelayCommand> _stateCommands = new();
    private readonly Dictionary<DemoScenario, Button> _scenarioButtons = new();
    private readonly TextBox _displayName = new() { Header = "Display name", MaxLength = 80 };
    private readonly ToggleSwitch _hydration = new()
    {
        Header = "Hydration reminders", OnContent = "On", OffContent = "Off"
    };
    private readonly NumberBox _popupSeconds = new()
    {
        Header = "Popup duration (seconds)", Minimum = 5, Maximum = 120,
        SmallChange = 1, LargeChange = 5, Value = 18,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
    };

    public bool IsClosed { get; private set; }

    public DemoControlsWindow(AppController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Title = "PulsePal · Developer controls";

        var page = new StackPanel { Spacing = 20, Margin = new Thickness(24), MaxWidth = 1040 };
        page.Children.Add(new TextBlock
        {
            Text = "PULSEPAL / DEVELOPER TOOLS", FontSize = 12,
            CharacterSpacing = 140, Foreground = Brush(120, 220, 205), TextWrapping = TextWrapping.Wrap
        });
        page.Children.Add(new TextBlock
        {
            Text = "Demo control room", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(245, 247, 252), TextWrapping = TextWrapping.Wrap
        });
        page.Children.Add(Body("Drive synthetic scenarios, inspect decisions, and test companion interactions. This is a developer window, not the main dashboard."));
        _meetButton = ActionButton(
            $"Meet {CompanionProfiles.Get(_controller.Engine.Preferences.CompanionProfile).Name}",
            _controller.MeetCompanion);
        page.Children.Add(Card(Section("Presentation & session",
            Body("Open the presenter without starting or resetting a presentation, or review the current session story."),
            ActionButton("Open presenter", _controller.ShowPresenter),
            ActionButton("Session story", _controller.ShowSessionStory),
            _meetButton)));
        page.Children.Add(Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Heading("SYNTHETIC DATA · NOT MEDICAL ADVICE"),
                Body("All metrics and work context are simulated for this demo. Scores are illustrative, not medical measurements, diagnoses, or treatment recommendations."),
                Heading("ATTENTION SHIELD · SIMULATED ONLY"),
                Body("Only demo notifications are queued or released. PulsePal does not intercept, suppress, or manage real Windows, Teams, or email notifications.")
            }
        }, true));

        var metrics = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var column = 0; column < 3; column++)
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddMetric(metrics, 0, "HEART RATE", _heartRate, "bpm · synthetic");
        AddMetric(metrics, 1, "STRESS", _stress, "/ 100 · analysis");
        AddMetric(metrics, 2, "SLEEP", _sleep, "/ 100 · synthetic");
        AddMetric(metrics, 3, "READINESS", _readiness, "/ 100 · synthetic");
        AddMetric(metrics, 4, "FOCUS", _focus, "/ 100 · analysis");
        AddMetric(metrics, 5, "BURNOUT RISK", _burnout, "/ 100 · illustrative");
        page.Children.Add(metrics);

        var scenarios = Section("Scenario & work context", _scenario, _context);
        var scenarioGrid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        scenarioGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scenarioGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scenarioIndex = 0;
        foreach (var scenario in Enum.GetValues<DemoScenario>())
        {
            if (scenarioIndex % 2 == 0)
                scenarioGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var button = ActionButton(ScenarioName(scenario), () => _controller.SetScenario(scenario), CanChangeActivity);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(button, scenarioIndex / 2);
            Grid.SetColumn(button, scenarioIndex % 2);
            scenarioGrid.Children.Add(button);
            _scenarioButtons.Add(scenario, button);
            scenarioIndex++;
        }
        scenarios.Children.Add(scenarioGrid);
        page.Children.Add(Card(scenarios));

        var focusCommand = new RelayCommand(_controller.ToggleFocus, CanChangeActivity);
        _focusButton.Command = focusCommand;
        _stateCommands.Add(focusCommand);
        var focusActions = new StackPanel { Spacing = 10 };
        focusActions.Children.Add(_focusButton);
        focusActions.Children.Add(ActionButton("Simulate escalation · urgent", () => _controller.SimulateNotification(NotificationKind.Escalation), () => !_controller.IsPresentationPaused));
        focusActions.Children.Add(ActionButton("Simulate FYI email · low priority", () => _controller.SimulateNotification(NotificationKind.FyiEmail), () => !_controller.IsPresentationPaused));
        foreach (var option in RecoveryActivities.All)
            focusActions.Children.Add(ActionButton($"{option.Title} · {option.Duration.TotalMinutes:0} min", () => _controller.StartRecovery(option.Activity), CanChangeActivity));
        focusActions.Children.Add(ActionButton("Cancel active break (no benefit)", _controller.CancelRecovery, () => _controller.ActiveRecovery is not null));
        focusActions.Children.Add(ActionButton("Hide timer (break continues)", _controller.HideRecoveryTimer, () => _controller.ActiveRecovery is not null));
        focusActions.Children.Add(ActionButton("Show companion", _controller.ShowCompanion));
        focusActions.Children.Add(ActionButton("Profile & preferences", _controller.ShowProfile));
        page.Children.Add(Card(Section("Focus & companion actions", _focusStatus, _recoveryStatus, focusActions)));
        page.Children.Add(Card(Section("AI decision & reasons", Body("Demo analysis and explanations from the engine; not clinical advice."), _decision, _reasons, _lastMessage)));
        page.Children.Add(Card(Section("Notification & activity log", _notificationStatus,
            Body("Includes simulated notification decisions, queued items, releases, and controller activity. Most recent entries first; up to 100 shown."), _notifications)));
        page.Children.Add(Card(Section("Engine history", Body("Recent synthetic samples · newest first · up to 40 shown"), _history)));

        var save = ActionButton("Save preferences", SavePreferences);
        page.Children.Add(Card(Section("Preferences", _displayName, _hydration, _popupSeconds,
            Body("Save applies these preferences through the controller and persists them locally."), save)));
        page.Children.Add(Card(Section("Storage & runtime status", _status)));

        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = Brush(14, 19, 29)
        };
        root.Children.Add(new ScrollViewer
        {
            Content = page,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled
        });
        Content = root;
        AppWindow.Resize(new SizeInt32(880, 820));

        var preferences = _controller.Engine.Preferences;
        _displayName.Text = preferences.DisplayName;
        _hydration.IsOn = preferences.HydrationReminders;
        _popupSeconds.Value = Math.Clamp(preferences.PopupSeconds, 5, 120);
        _controller.Updated += OnUpdated;
        Closed += OnClosed;
        Refresh(_controller.Engine.Current);
    }

    public void Refresh(DemoSnapshot? snapshot)
    {
        if (IsClosed)
            return;

        foreach (var command in _stateCommands)
            command.NotifyCanExecuteChanged();
        var meetLabel = $"Meet {CompanionProfiles.Get(_controller.Engine.Preferences.CompanionProfile).Name}";
        if (_meetButton.Content is TextBlock meetText)
            meetText.Text = meetLabel;
        AutomationProperties.SetName(_meetButton, meetLabel);
        _status.Text = string.IsNullOrWhiteSpace(_controller.Status) ? "No storage status reported yet." : _controller.Status;
        _lastMessage.Text = string.IsNullOrWhiteSpace(_controller.LastMessage) ? "No companion message yet." : "Latest message: " + _controller.LastMessage;
        _heartRate.Text = Score(snapshot?.Wearable.HeartRate);
        _stress.Text = Score(snapshot?.Analysis.StressScore);
        _sleep.Text = Score(snapshot?.Wearable.SleepScore);
        _readiness.Text = Score(snapshot?.Wearable.ReadinessScore);
        _focus.Text = Score(snapshot?.Analysis.FocusScore);
        _burnout.Text = Score(snapshot?.Analysis.BurnoutRiskScore);
        _focusButton.Content = _controller.Engine.IsFocusActive ? "End focus & release queued notifications" : "Start focus";
        _recoveryStatus.Text = _controller.ActiveRecovery is { } activity
            ? $"Break active: {RecoveryActivities.Get(activity).Title} · {_controller.RecoveryRemaining:mm\\:ss} remaining. Focus has ended; finish or cancel this break before starting focus or changing scenarios."
            : "No recovery timer running. Starting a break ends focus and releases queued notifications.";
        foreach (var item in _scenarioButtons)
        {
            var selected = snapshot?.Scenario == item.Key;
            if (item.Value.Content is TextBlock label)
                label.Text = (selected ? "Active · " : string.Empty) + ScenarioName(item.Key);
            AutomationProperties.SetName(item.Value, ScenarioName(item.Key) + (selected ? ", active scenario" : string.Empty));
        }

        _reasons.Children.Clear();
        if (snapshot is null)
        {
            _scenario.Text = "Waiting for the first synthetic sample…";
            _context.Text = "Work context will appear when the demo engine is ready.";
            _decision.Text = "No analysis available yet.";
            _focusStatus.Text = "Focus status is not available yet.";
            _notificationStatus.Text = "Notification counts are not available yet.";
            _reasons.Children.Add(Body("Reasons will appear with the next engine decision."));
        }
        else
        {
            var context = snapshot.Context;
            _scenario.Text = $"{ScenarioName(snapshot.Scenario)} · {snapshot.Wearable.Timestamp.ToLocalTime():g}";
            _context.Text = $"Application: {context.ActiveApplication}\n" +
                $"Idle: {context.IdleTime.TotalMinutes:0.#} min · App switches: {context.AppSwitchCount}\n" +
                $"Meetings today: {context.MeetingsToday} · Upcoming: {context.UpcomingMeetings} · Priority tasks: {context.PriorityTasks}\n" +
                $"Escalations: {context.EscalationCount} · Important unread items: {context.UnreadImportantItems}";
            _decision.Text = $"State: {StateName(snapshot.Analysis.State)} · Fatigue: {Score(snapshot.Analysis.FatigueScore)} / 100";
            if (snapshot.Message is { } message)
                _decision.Text += $"\n{message.Title}\n{message.Text}\nCategory: {message.Category} · Character: {message.Character}";
            foreach (var reason in snapshot.Analysis.Reasons)
                _reasons.Children.Add(Body("• " + reason));
            if (_reasons.Children.Count == 0)
                _reasons.Children.Add(Body("No additional reasons reported."));
            _focusStatus.Text = snapshot.IsFocusActive
                ? "Focus active" + (snapshot.FocusStartedAt is { } started ? $" · started {started.ToLocalTime():t}" : string.Empty) + ". Low-priority demo notifications may be queued."
                : "Focus inactive · start a session to test the simulated Attention Shield.";
            _notificationStatus.Text = $"Queued now: {snapshot.QueuedNotifications} · Urgent notifications: {snapshot.UrgentNotifications}";
        }

        _notifications.Children.Clear();
        foreach (var entry in _controller.ActivityLog.Reverse().Take(100))
            _notifications.Children.Add(LogEntry(entry));
        if (_notifications.Children.Count == 0)
            _notifications.Children.Add(Body("No activity yet. Start focus, simulate an FYI email, then end focus to test queue and release behavior."));

        _history.Children.Clear();
        foreach (var entry in _controller.Engine.History.OrderByDescending(item => item.Timestamp).Take(40))
        {
            _history.Children.Add(LogEntry($"{entry.Timestamp.ToLocalTime():g} · {ScenarioName(entry.Scenario)}\n" +
                $"{StateName(entry.State)} · Stress {Score(entry.StressScore)} / 100 · Focus {Score(entry.FocusScore)} / 100"));
        }
        if (_history.Children.Count == 0)
            _history.Children.Add(Body("No engine history recorded yet."));
    }

    private void OnUpdated() => Refresh(_controller.Engine.Current);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _controller.Updated -= OnUpdated;
        Closed -= OnClosed;
    }

    private void SavePreferences()
    {
        var seconds = double.IsFinite(_popupSeconds.Value) ? _popupSeconds.Value : 18;
        var popupSeconds = (int)Math.Clamp(Math.Round(seconds), 5, 120);
        _popupSeconds.Value = popupSeconds;
        _controller.SavePreferences(_controller.Engine.Preferences with
        {
            DisplayName = _displayName.Text.Trim(),
            HydrationReminders = _hydration.IsOn,
            PopupSeconds = popupSeconds
        });
    }

    private static string Score(double? value) => value is { } number && double.IsFinite(number)
        ? number.ToString("0", CultureInfo.CurrentCulture) : "—";

    private static string ScenarioName(DemoScenario scenario) => scenario switch
    {
        DemoScenario.HealthyDay => "Healthy day",
        DemoScenario.DeepFocusSession => "Deep focus session",
        DemoScenario.RisingStress => "Rising stress",
        DemoScenario.MeetingOverload => "Meeting overload",
        DemoScenario.RecoveryAfterBreak => "Recovery after break",
        DemoScenario.PoorSleepDay => "Poor sleep day",
        _ => scenario.ToString()
    };

    private static string StateName(WellnessState state) => state switch
    {
        WellnessState.HighStress => "High stress",
        WellnessState.BurnoutRisk => "Burnout risk",
        _ => state.ToString()
    };

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(Color.FromArgb(255, red, green, blue));

    private static TextBlock Body(string text = "") => new()
    {
        Text = text, FontSize = 14, Foreground = Brush(206, 216, 231),
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(243, 246, 252), TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock MetricValue() => new()
    {
        Text = "—", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Brush(135, 232, 216), TextWrapping = TextWrapping.Wrap
    };

    private static StackPanel Section(string title, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Heading(title));
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    private static Border Card(UIElement child, bool notice = false) => new()
    {
        Background = notice ? Brush(37, 34, 24) : Brush(23, 31, 44),
        BorderBrush = notice ? Brush(146, 118, 53) : Brush(48, 62, 81),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(18), Child = child
    };

    private static Border LogEntry(string text) => new()
    {
        BorderBrush = Brush(48, 62, 81), BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(12, 5, 0, 5), Child = Body(text)
    };

    private bool CanChangeActivity() => !_controller.IsPresentationPaused && _controller.ActiveRecovery is null;

    private Button ActionButton(string text, Action action, Func<bool>? canExecute = null)
    {
        var command = canExecute is null ? new RelayCommand(action) : new RelayCommand(action, canExecute);
        if (canExecute is not null) _stateCommands.Add(command);
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            MinHeight = 40, Padding = new Thickness(14, 9, 14, 9),
            HorizontalAlignment = HorizontalAlignment.Left,
            Command = command
        };
        AutomationProperties.SetName(button, text);
        return button;
    }

    private static void AddMetric(Grid grid, int index, string label, TextBlock value, string unit)
    {
        AutomationProperties.SetName(value, label);
        var caption = Body(label);
        caption.FontSize = 12;
        var card = Card(new StackPanel { Spacing = 5, Children = { caption, value, Body(unit) } });
        Grid.SetColumn(card, index % 3);
        Grid.SetRow(card, index / 3);
        grid.Children.Add(card);
    }
}

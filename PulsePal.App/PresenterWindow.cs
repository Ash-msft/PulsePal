using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PulsePal.App.ViewModels;
using PulsePal.Core;
using Windows.Graphics;

namespace PulsePal.App;

public sealed class PresenterWindow : Window
{
    private readonly Grid _root = new();
    private readonly Expander _summary = new()
    {
        Header = "Comparison & Session story",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private ContentDialog? _confirmation;
    private bool _showStoryOnLoad;
    public PresenterViewModel ViewModel { get; }
    public bool IsClosed { get; private set; }

    public PresenterWindow(AppController controller)
    {
        Title = "A day with PulsePal";
        ViewModel = new PresenterViewModel(controller, ConfirmFreshSessionAsync);
        _root.DataContext = ViewModel;
        _root.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        var heading = Body("A day with PulsePal", 26);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        page.Children.Add(heading);
        page.Children.Add(Body("A companion-led synthetic day · not a medical assessment."));
        page.Children.Add(BoundText(nameof(PresenterViewModel.Mode)));
        page.Children.Add(ActionButton("Comparison & Session story", nameof(PresenterViewModel.ShowStoryCommand)));
        page.Children.Add(BoundText(nameof(PresenterViewModel.Step)));
        var title = BoundText(nameof(PresenterViewModel.Title), 21);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        page.Children.Add(title);
        page.Children.Add(BoundText(nameof(PresenterViewModel.Narrative)));
        var status = BoundText(nameof(PresenterViewModel.Status), 17);
        status.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        page.Children.Add(status);
        page.Children.Add(ButtonRow(
            ActionButton("Start…", nameof(PresenterViewModel.StartCommand)),
            ActionButton("Pause", nameof(PresenterViewModel.PauseCommand)),
            ActionButton("Resume", nameof(PresenterViewModel.ResumeCommand))));
        page.Children.Add(ButtonRow(
            ActionButton("Next", nameof(PresenterViewModel.NextCommand)),
            ActionButton("Reset…", nameof(PresenterViewModel.ResetCommand)),
            ActionButton("Stop", nameof(PresenterViewModel.StopCommand))));
        page.Children.Add(Body("Next never skips a required wait or recovery timer. Stop cancels an active break with no benefit and returns to ordinary mode."));

        var accelerated = new ToggleSwitch
        {
            Header = "Accelerated demo", OnContent = "On", OffContent = "Off (default)"
        };
        Bind(accelerated, ToggleSwitch.IsOnProperty, nameof(PresenterViewModel.AcceleratedDemo), BindingMode.TwoWay);
        Bind(accelerated, Control.IsEnabledProperty, nameof(PresenterViewModel.CanConfigure));
        AutomationProperties.SetName(accelerated, "Accelerated demo");
        AutomationProperties.SetHelpText(accelerated, PresenterViewModel.AccelerationHelp);
        page.Children.Add(accelerated);
        page.Children.Add(Body(PresenterViewModel.AccelerationHelp));
        var recovery = new ComboBox
        {
            Header = "Recovery choice (this session only)",
            DisplayMemberPath = nameof(RecoveryOption.Title),
            SelectedValuePath = nameof(RecoveryOption.Activity),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Bind(recovery, ItemsControl.ItemsSourceProperty, nameof(PresenterViewModel.RecoveryChoices));
        Bind(recovery, Microsoft.UI.Xaml.Controls.Primitives.Selector.SelectedValueProperty,
            nameof(PresenterViewModel.SelectedRecovery), BindingMode.TwoWay);
        Bind(recovery, Control.IsEnabledProperty, nameof(PresenterViewModel.CanConfigure));
        AutomationProperties.SetName(recovery, "Recovery choice");
        AutomationProperties.SetHelpText(recovery, "Choose another break during the eight-second selection window before the automatic default starts. This does not change saved preferences.");
        page.Children.Add(recovery);
        page.Children.Add(Body("At the recovery step, choose another break within 8 seconds before the automatic default starts. Saved preferences stay unchanged."));
        page.Children.Add(ButtonRow(
            ActionButton("Start selected recovery", nameof(PresenterViewModel.StartRecoveryCommand)),
            ActionButton("Cancel break · no benefit", nameof(PresenterViewModel.CancelRecoveryCommand))));
        var meet = ActionButton("", nameof(PresenterViewModel.MeetCommand));
        Bind(meet, ContentControl.ContentProperty, nameof(PresenterViewModel.MeetText));
        page.Children.Add(meet);

        var story = new StackPanel { Spacing = 12 };
        story.Children.Add(Body("Synthetic comparison, not a health outcome", 18));
        story.Children.Add(Body("Latest comparison", 16));
        story.Children.Add(BoundText(nameof(PresenterViewModel.Comparison)));
        story.Children.Add(Body("Session story", 18));
        story.Children.Add(BoundText(nameof(PresenterViewModel.Story)));
        story.Children.Add(Body("Individual completed breaks & comparisons", 16));
        story.Children.Add(BoundText(nameof(PresenterViewModel.CompletedBreaks)));
        _summary.Content = story;
        page.Children.Add(_summary);
        _root.Children.Add(new ScrollViewer
        {
            Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = _root;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(620, workArea.Width), Math.Min(800, workArea.Height)));
        _root.Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public void ShowStory()
    {
        if (IsClosed) return;
        _summary.IsExpanded = true;
        if (!_root.IsLoaded)
        {
            _showStoryOnLoad = true;
            return;
        }
        _summary.Focus(FocusState.Programmatic);
        _summary.StartBringIntoView();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_showStoryOnLoad)
        {
            _showStoryOnLoad = false;
            ShowStory();
        }
    }

    private async Task<bool> ConfirmFreshSessionAsync(string action)
    {
        if (IsClosed || _root.XamlRoot is null || _confirmation is not null) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = $"{action} a fresh synthetic session?",
            Content = "This starts a fresh synthetic session, clearing session history, counters, and current focus. Any active recovery is cancelled with NO benefit. Your saved preferences are preserved.",
            PrimaryButtonText = $"{action} fresh session",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        _confirmation = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && !IsClosed; }
        finally { _confirmation = null; }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _confirmation?.Hide();
        ViewModel.Dispose();
        _root.Loaded -= OnLoaded;
        Closed -= OnClosed;
    }

    private static TextBlock Body(string text, double size = 14) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };

    private static TextBlock BoundText(string property, double size = 14)
    {
        var text = Body("", size);
        Bind(text, TextBlock.TextProperty, property);
        return text;
    }

    private static Button ActionButton(string label, string command)
    {
        var button = new Button
        {
            Content = label, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (label.Length > 0) AutomationProperties.SetName(button, label);
        Bind(button, Microsoft.UI.Xaml.Controls.Primitives.ButtonBase.CommandProperty, command);
        return button;
    }

    private static Grid ButtonRow(params Button[] buttons)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        for (var index = 0; index < buttons.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons[index].Content = buttons[index].Content is string label
                ? new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }
                : buttons[index].Content;
            Grid.SetColumn(buttons[index], index);
            grid.Children.Add(buttons[index]);
        }
        return grid;
    }

    private static void Bind(FrameworkElement target, DependencyProperty property, string path,
        BindingMode mode = BindingMode.OneWay) =>
        target.SetBinding(property, new Binding { Path = new PropertyPath(path), Mode = mode });
}

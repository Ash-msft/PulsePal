using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulsePal.Core;

namespace PulsePal.App.ViewModels;

public sealed class PresenterViewModel : ObservableObject, IDisposable
{
    private readonly AppController _controller;
    private readonly Func<string, Task<bool>> _confirm;
    private bool _confirming;
    private bool _disposed;
    private string _step = "";
    private string _title = "";
    private string _narrative = "";
    private string _status = "";
    private string _mode = "";
    private string _story = "";
    private string _comparison = "";
    private string _completedBreaks = "";
    private string _meetText = "";

    public const string AccelerationHelp = "Default off. Every selected recovery takes 15 actual seconds and represents its catalog duration. Breathing is a non-guided illustration preview, not real breathing instruction. Cannot change during a recovery.";
    public string Step { get => _step; private set => SetProperty(ref _step, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Narrative { get => _narrative; private set => SetProperty(ref _narrative, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Mode { get => _mode; private set => SetProperty(ref _mode, value); }
    public string Story { get => _story; private set => SetProperty(ref _story, value); }
    public string Comparison { get => _comparison; private set => SetProperty(ref _comparison, value); }
    public string CompletedBreaks { get => _completedBreaks; private set => SetProperty(ref _completedBreaks, value); }
    public string MeetText { get => _meetText; private set => SetProperty(ref _meetText, value); }
    public IReadOnlyList<RecoveryOption> RecoveryChoices => RecoveryActivities.All;
    public bool CanConfigure => !_disposed && !_confirming && !_controller.IsPresentationPaused && _controller.ActiveRecovery is null;
    public bool AcceleratedDemo
    {
        get => _controller.AcceleratedDemo;
        set
        {
            if (CanConfigure && value != _controller.AcceleratedDemo)
                _controller.AcceleratedDemo = value;
            Refresh();
        }
    }
    public RecoveryActivity SelectedRecovery
    {
        get => _controller.SelectedPresentationBreak;
        set
        {
            if (CanConfigure)
                _controller.SelectedPresentationBreak = value;
            Refresh();
        }
    }

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand ResetCommand { get; }
    public IRelayCommand PauseCommand { get; }
    public IRelayCommand ResumeCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand StartRecoveryCommand { get; }
    public IRelayCommand CancelRecoveryCommand { get; }
    public IRelayCommand ShowStoryCommand { get; }
    public IRelayCommand MeetCommand { get; }

    public PresenterViewModel(AppController controller, Func<string, Task<bool>> confirm)
    {
        _controller = controller;
        _confirm = confirm;
        StartCommand = new AsyncRelayCommand(() => ConfirmFreshSessionAsync(false), CanChangeSession);
        ResetCommand = new AsyncRelayCommand(() => ConfirmFreshSessionAsync(true), CanChangeSession);
        PauseCommand = new RelayCommand(controller.PausePresentation,
            () => CanChangeSession() && controller.Tour.IsActive && !controller.IsPresentationPaused);
        ResumeCommand = new RelayCommand(controller.ResumePresentation,
            () => CanChangeSession() && controller.IsPresentationPaused);
        NextCommand = new RelayCommand(controller.NextPresentation,
            () => CanChangeSession() && controller.Tour.IsActive && !controller.IsPresentationPaused && controller.ActiveRecovery is null);
        StopCommand = new RelayCommand(controller.StopPresentation,
            () => CanChangeSession() && (controller.Tour.Step != TourStep.Idle || controller.IsPresentationPaused || controller.ActiveRecovery is not null));
        StartRecoveryCommand = new RelayCommand(controller.StartSelectedPresentationRecovery,
            () => CanConfigure && (!controller.Tour.IsActive || controller.Tour.Step == TourStep.Recovery));
        CancelRecoveryCommand = new RelayCommand(controller.CancelRecovery,
            () => CanChangeSession() && controller.ActiveRecovery is not null);
        ShowStoryCommand = new RelayCommand(controller.ShowSessionStory);
        MeetCommand = new RelayCommand(controller.MeetCompanion);
        controller.Updated += Refresh;
        Refresh();
    }

    private bool CanChangeSession() => !_disposed && !_confirming;

    private async Task ConfirmFreshSessionAsync(bool reset)
    {
        if (!CanChangeSession()) return;
        _confirming = true;
        Refresh();
        try
        {
            var approved = await _confirm(reset ? "Reset" : "Start");
            if (!approved || _disposed) return;
            if (reset) _controller.ResetPresentation();
            else _controller.StartPresentation();
        }
        finally
        {
            _confirming = false;
            if (!_disposed) Refresh();
        }
    }

    private void Refresh()
    {
        if (_disposed) return;
        var tour = _controller.Tour;
        Step = tour.Step == TourStep.Idle ? "Ready · not started" : $"Step {(int)tour.Step} of 7 · {tour.Step}";
        Title = tour.Title;
        Narrative = tour.Narrative;
        Status = (_controller.IsPresentationPaused ? "Paused · " : "") + _controller.PresentationStatus;
        Mode = _controller.AcceleratedDemo ? "Accelerated demo · 15-second recovery previews" : "Real-time recovery · acceleration off";
        var story = _controller.SessionStory;
        Story = story.Description;
        Comparison = _controller.LastComparison?.Description ?? "No completed recovery comparison yet.";
        CompletedBreaks = story.CompletedBreaks.Count == 0
            ? "No completed breaks. Cancelled breaks receive no benefit."
            : string.Join("\n\n", story.CompletedBreaks.Select((completion, index) =>
                $"Break {index + 1} · {RecoveryActivities.Get(completion.Activity).Title} · {(completion.IsAccelerated ? "Accelerated demo" : "Real-time")}\n" +
                $"Actual elapsed: {completion.ActualElapsed:c} · Represented catalog duration: {completion.RepresentedDuration:c}\n" +
                completion.Comparison.Description));
        MeetText = $"Meet {CompanionProfiles.Get(_controller.Engine.Preferences.CompanionProfile).Name}";
        OnPropertyChanged(nameof(AcceleratedDemo));
        OnPropertyChanged(nameof(SelectedRecovery));
        OnPropertyChanged(nameof(CanConfigure));
        StartCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        StartRecoveryCommand.NotifyCanExecuteChanged();
        CancelRecoveryCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Updated -= Refresh;
    }
}

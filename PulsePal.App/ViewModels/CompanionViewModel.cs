using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulsePal.Core;

namespace PulsePal.App.ViewModels;

public sealed class CompanionViewModel : ObservableObject, IDisposable
{
    private readonly AppController _controller;
    private string _messageTitle = "Hello, Ashwani.";
    private string _messageText = string.Empty;
    private string _focusActionText = "Start Focus Session";
    private string _companionIdentity = "Nova · Calm strategist";
    private string _recommendedBreak = string.Empty;
    private string _meetText = "Meet Nova";
    private string _presentationLabel = string.Empty;

    public string MessageTitle { get => _messageTitle; set => SetProperty(ref _messageTitle, value); }
    public string MessageText { get => _messageText; set => SetProperty(ref _messageText, value); }
    public string FocusActionText { get => _focusActionText; set => SetProperty(ref _focusActionText, value); }
    public string CompanionIdentity { get => _companionIdentity; set => SetProperty(ref _companionIdentity, value); }
    public string RecommendedBreak { get => _recommendedBreak; set => SetProperty(ref _recommendedBreak, value); }
    public string MeetText { get => _meetText; set => SetProperty(ref _meetText, value); }
    public string PresentationLabel { get => _presentationLabel; set => SetProperty(ref _presentationLabel, value); }
    public IRelayCommand ShowPresenterCommand { get; }
    public IRelayCommand ShowStoryCommand { get; }
    public IRelayCommand MeetCommand { get; }
    public IRelayCommand ShowControlsCommand { get; }
    public IRelayCommand ShowConnectedHealthCommand { get; }
    public IRelayCommand ShowProfileCommand { get; }
    public IRelayCommand ExitCommand { get; }
    public IRelayCommand ToggleFocusCommand { get; }
    public IRelayCommand StartBreathingCommand { get; }
    public IRelayCommand StartScreenBreakCommand { get; }
    public IRelayCommand StartWaterBreakCommand { get; }
    public IRelayCommand StartStretchBreakCommand { get; }
    public IRelayCommand CancelRecoveryCommand { get; }
    public IRelayCommand HideRecoveryTimerCommand { get; }
    public IRelayCommand DismissCommand { get; }
    public IRelayCommand RemindLaterCommand { get; }

    public CompanionViewModel(AppController controller)
    {
        _controller = controller;
        ShowPresenterCommand = new RelayCommand(controller.ShowPresenter);
        ShowStoryCommand = new RelayCommand(controller.ShowSessionStory);
        MeetCommand = new RelayCommand(controller.MeetCompanion);
        ShowControlsCommand = new RelayCommand(controller.ShowControls);
        ShowConnectedHealthCommand = new RelayCommand(controller.ShowConnectedHealth);
        ShowProfileCommand = new RelayCommand(controller.ShowProfile);
        ExitCommand = new RelayCommand(controller.RequestExit);
        ToggleFocusCommand = new RelayCommand(controller.ToggleFocus,
            () => !controller.Tour.IsActive && !controller.IsPresentationPaused && controller.ActiveRecovery is null);
        StartBreathingCommand = new RelayCommand(controller.StartBreathing, CanStartRecovery);
        StartScreenBreakCommand = new RelayCommand(() => controller.StartRecovery(RecoveryActivity.ScreenBreak), CanStartRecovery);
        StartWaterBreakCommand = new RelayCommand(() => controller.StartRecovery(RecoveryActivity.WaterBreak), CanStartRecovery);
        StartStretchBreakCommand = new RelayCommand(() => controller.StartRecovery(RecoveryActivity.StretchBreak), CanStartRecovery);
        CancelRecoveryCommand = new RelayCommand(controller.CancelRecovery, () => controller.ActiveRecovery is not null);
        HideRecoveryTimerCommand = new RelayCommand(controller.HideRecoveryTimer);
        DismissCommand = new RelayCommand(controller.Dismiss);
        RemindLaterCommand = new RelayCommand(controller.RemindLater);
        controller.Updated += RefreshCommands;
        RefreshCommands();
    }

    private bool CanStartRecovery() => !_controller.IsPresentationPaused && _controller.ActiveRecovery is null &&
        (!_controller.Tour.IsActive || _controller.Tour.Step == TourStep.Recovery);

    private void RefreshCommands()
    {
        MeetText = $"Meet {CompanionProfiles.Get(_controller.Engine.Preferences.CompanionProfile).Name}";
        ToggleFocusCommand.NotifyCanExecuteChanged();
        StartBreathingCommand.NotifyCanExecuteChanged();
        StartScreenBreakCommand.NotifyCanExecuteChanged();
        StartWaterBreakCommand.NotifyCanExecuteChanged();
        StartStretchBreakCommand.NotifyCanExecuteChanged();
        CancelRecoveryCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _controller.Updated -= RefreshCommands;
}

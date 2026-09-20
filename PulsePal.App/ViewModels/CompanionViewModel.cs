using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulsePal.Core;

namespace PulsePal.App.ViewModels;

public sealed class CompanionViewModel : ObservableObject
{
    private string _messageTitle = "Hello, Ashwani.";
    private string _messageText = string.Empty;
    private string _focusActionText = "Start Focus Session";
    private string _companionIdentity = "Nova · Calm strategist";
    private string _recommendedBreak = string.Empty;

    public string MessageTitle { get => _messageTitle; set => SetProperty(ref _messageTitle, value); }
    public string MessageText { get => _messageText; set => SetProperty(ref _messageText, value); }
    public string FocusActionText { get => _focusActionText; set => SetProperty(ref _focusActionText, value); }
    public string CompanionIdentity { get => _companionIdentity; set => SetProperty(ref _companionIdentity, value); }
    public string RecommendedBreak { get => _recommendedBreak; set => SetProperty(ref _recommendedBreak, value); }
    public IRelayCommand ShowControlsCommand { get; }
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
        ShowControlsCommand = new RelayCommand(controller.ShowControls);
        ShowProfileCommand = new RelayCommand(controller.ShowProfile);
        ExitCommand = new RelayCommand(controller.RequestExit);
        ToggleFocusCommand = new RelayCommand(controller.ToggleFocus);
        StartBreathingCommand = new RelayCommand(controller.StartBreathing);
        StartScreenBreakCommand = new RelayCommand(() => controller.StartRecovery(RecoveryActivity.ScreenBreak));
        StartWaterBreakCommand = new RelayCommand(() => controller.StartRecovery(RecoveryActivity.WaterBreak));
        StartStretchBreakCommand = new RelayCommand(() => controller.StartRecovery(RecoveryActivity.StretchBreak));
        CancelRecoveryCommand = new RelayCommand(controller.CancelRecovery);
        HideRecoveryTimerCommand = new RelayCommand(controller.HideRecoveryTimer);
        DismissCommand = new RelayCommand(controller.Dismiss);
        RemindLaterCommand = new RelayCommand(controller.RemindLater);
    }
}

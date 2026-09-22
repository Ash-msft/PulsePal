using Foundation;
using UIKit;

namespace PulsePal.Phone.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    private PairingViewController? controller;
    private UIView? privacyCover;

    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        controller = new PairingViewController();
        Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = controller
        };
        Window.MakeKeyAndVisible();
        return true;
    }

    public override void OnResignActivation(UIApplication application)
    {
        if (Window is null || privacyCover is not null)
            return;
        privacyCover = new UIView(Window.Bounds)
        {
            BackgroundColor = UIColor.SystemBackground,
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight
        };
        Window.AddSubview(privacyCover);
    }

    public override void OnActivated(UIApplication application)
    {
        privacyCover?.RemoveFromSuperview();
        privacyCover?.Dispose();
        privacyCover = null;
        controller?.ResumeForeground();
    }

    public override void DidEnterBackground(UIApplication application) =>
        controller?.CancelForeground();

    public override void WillTerminate(UIApplication application) =>
        controller?.Dispose();
}

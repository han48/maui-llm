using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace AIAgentLocal;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        On<iOS>().SetUseSafeArea(true);

        // Tap on chat area dismisses keyboard
        var tapGesture = new TapGestureRecognizer();
        tapGesture.Tapped += (s, e) => DismissKeyboard();
        ChatCollectionView.GestureRecognizers.Add(tapGesture);
    }

    private void DismissKeyboard()
    {
#if ANDROID
        if (Platform.CurrentActivity?.CurrentFocus != null)
        {
            var imm = (Android.Views.InputMethods.InputMethodManager?)
                Platform.CurrentActivity.GetSystemService(Android.Content.Context.InputMethodService);
            imm?.HideSoftInputFromWindow(Platform.CurrentActivity.CurrentFocus.WindowToken, 0);
            Platform.CurrentActivity.CurrentFocus.ClearFocus();
        }
#elif IOS
        UIKit.UIApplication.SharedApplication.SendAction(new ObjCRuntime.Selector("resignFirstResponder"), null, null, null);
#endif
    }

#if IOS
    protected override void OnAppearing()
    {
        base.OnAppearing();
        Microsoft.Maui.Platform.KeyboardAutoManagerScroll.Disconnect();

        UIKit.UIKeyboard.Notifications.ObserveWillShow((sender, args) =>
        {
            var keyboardHeight = args.FrameEnd.Height;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MainGrid.Padding = new Thickness(0, 0, 0, keyboardHeight);
            });
        });

        UIKit.UIKeyboard.Notifications.ObserveWillHide((sender, args) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MainGrid.Padding = new Thickness(0);
            });
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Microsoft.Maui.Platform.KeyboardAutoManagerScroll.Connect();
    }
#endif
}

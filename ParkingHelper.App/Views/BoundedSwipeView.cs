using Microsoft.Maui.Controls.Shapes;
using ParkingHelper.App.Layout;

namespace ParkingHelper.App.Views;

public sealed class BoundedSwipeView : SwipeView
{
    private double lastWidth;
    private double lastHeight;
    private const string RevealAnimation = "TicketActionReveal";
    private double revealProgress;
    public bool AnimateActions { get; set; }

    public static bool MotionEnabled
    {
        get
        {
#if IOS || MACCATALYST
            return !UIKit.UIAccessibility.IsReduceMotionEnabled;
#elif ANDROID
            return !OperatingSystem.IsAndroidVersionAtLeast(26) || Android.Animation.ValueAnimator.AreAnimatorsEnabled();
#else
            return true;
#endif
        }
    }

    public BoundedSwipeView()
    {
        HorizontalOptions = LayoutOptions.Fill;
        MinimumWidthRequest = 0;
        // Threshold remains zero: the native reveal distance is the measured tray width.
        SizeChanged += (_, _) => Fit();
        Loaded += (_, _) => Fit();
        SwipeStarted += (_, _) => this.AbortAnimation(RevealAnimation);
        SwipeChanging += (_, e) =>
        {
            if (!AnimateActions) return;
            this.AbortAnimation(RevealAnimation);
            var trayWidth = SwipeLayout.ActionWidth(Width, RightItems.Count) * RightItems.Count;
            ApplyReveal(trayWidth > 0 ? Math.Clamp(Math.Abs(e.Offset) / trayWidth, 0, 1) : 0);
        };
        SwipeEnded += (_, e) =>
        {
            if (!AnimateActions) return;
            this.AbortAnimation(RevealAnimation);
            var target = e.IsOpen ? 1d : 0d;
            if (!MotionEnabled) { ApplyReveal(target); return; }
            // One restrained overshoot, then settle exactly at the native open position.
            var easing = e.IsOpen
                ? new Easing(t => t >= 1 ? 1 : 1 - Math.Exp(-6 * t) * Math.Cos(9 * t))
                : Easing.CubicInOut;
            new Animation(ApplyReveal, revealProgress, target, easing)
                .Commit(this, RevealAnimation, length: e.IsOpen ? 320u : 220u);
        };
        BindingContextChanged += (_, _) => ResetReveal();
        Unloaded += (_, _) => ResetReveal();
    }

    private void ResetReveal()
    {
        this.AbortAnimation(RevealAnimation);
        if (AnimateActions) ApplyReveal(0);
    }

    private void ApplyReveal(double progress)
    {
        revealProgress = progress;
        var actions = RightItems.OfType<CompactSwipeAction>().ToArray();
        var motion = MotionEnabled;
        for (var i = 0; i < actions.Length; i++)
        {
            // Native swipe still owns dragging, thresholds, and hit targets. Each whole
            // surface unfolds progressively instead of showing a fully painted tray at once.
            var delay = (actions.Length - 1 - i) * 0.12;
            var local = Math.Clamp((progress - delay) / (1 - delay), 0, 1);
            var amount = motion ? local * local * (3 - 2 * local) + Math.Max(0, progress - 1) : 1;
            actions[i].SetReveal(amount);
        }
    }

    private void Fit()
    {
        if (Width <= 0 || Height <= 0) return;
        if (lastWidth == Width && lastHeight == Height) return;
        lastWidth = Width;
        lastHeight = Height;
        ResetReveal();
        Close(false); // Resizing must not leave an old native swipe offset behind.
        Clip = new RectangleGeometry(new Rect(0, 0, Width, Height));
        if (Content is { } content)
            content.Clip = new RectangleGeometry(new Rect(0, 0, Width, Height));
        var actionWidth = SwipeLayout.ActionWidth(Width, RightItems.Count);
        foreach (var action in RightItems.OfType<CompactSwipeAction>())
        {
            action.WidthRequest = actionWidth;
            action.HeightRequest = Height;
        }
    }
}

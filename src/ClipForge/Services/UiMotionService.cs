using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClipForge.Services;

/// <summary>
/// Provides short, non-layout UI transitions that honor Windows accessibility
/// preferences and degrade to their final static state when animation is unavailable.
/// </summary>
public static class UiMotionService
{
    private static readonly Duration StartupDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration RefreshDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration ToastInDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration ToastOutDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly TimeSpan StartupStagger = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan MaximumStartupDelay = TimeSpan.FromMilliseconds(210);

    private static readonly DependencyProperty MotionGenerationProperty =
        DependencyProperty.RegisterAttached(
            "MotionGeneration",
            typeof(int),
            typeof(UiMotionService),
            new PropertyMetadata(0));

    /// <summary>
    /// Gets whether optional UI motion is currently appropriate. The value is
    /// evaluated for every transition so changes to Windows accessibility settings
    /// take effect without restarting ClipForge.
    /// </summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try
            {
                var renderingTier = RenderCapability.Tier >> 16;
                return SystemParameters.ClientAreaAnimation &&
                       !SystemParameters.HighContrast &&
                       renderingTier >= 1;
            }
            catch
            {
                // Accessibility and reliability take priority over optional motion.
                return false;
            }
        }
    }

    /// <summary>
    /// Reveals one startup surface. Consecutive sequence indices create a small,
    /// bounded stagger; indices beyond the supported range share the maximum delay.
    /// </summary>
    public static void RevealStartup(FrameworkElement element, int sequenceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(element);
        RunOnElementDispatcher(element, () =>
        {
            var translate = GetOrCreateTranslateTransform(element);
            var nonNegativeIndex = Math.Max(0, sequenceIndex);
            var requestedDelay = TimeSpan.FromTicks(StartupStagger.Ticks * (long)nonNegativeIndex);
            var delay = requestedDelay <= MaximumStartupDelay
                ? requestedDelay
                : MaximumStartupDelay;

            Reveal(element, translate, fromOpacity: 0, fromY: 8, StartupDuration, delay);
        });
    }

    /// <summary>
    /// Briefly crossfades refreshed content without moving or re-laying out it.
    /// </summary>
    public static void CrossFadeRefresh(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        RunOnElementDispatcher(element, () =>
            AnimateOpacity(element, from: 0, to: 1, RefreshDuration));
    }

    /// <summary>
    /// Makes a saved-clip toast visible and gives it a short upward reveal.
    /// </summary>
    public static void ShowSavedToast(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        RunOnElementDispatcher(element, () =>
        {
            element.Visibility = Visibility.Visible;
            var translate = GetOrCreateTranslateTransform(element);
            Reveal(element, translate, fromOpacity: 0, fromY: 6, ToastInDuration, TimeSpan.Zero);
        });
    }

    /// <summary>
    /// Fades out a saved-clip toast and collapses it. With motion disabled, the
    /// toast is collapsed immediately and remains ready for its next static reveal.
    /// </summary>
    public static void HideSavedToast(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        RunOnElementDispatcher(element, () =>
        {
            var translate = GetExistingTranslateTransform(element);
            var generation = Prepare(element, translate, finalOpacity: 0, finalY: 0);

            if (!AnimationsEnabled || element.Visibility != Visibility.Visible)
            {
                CompleteToastHide(element, translate, generation);
                return;
            }

            var animation = CreateAnimation(1, 0, ToastOutDuration, TimeSpan.Zero);
            animation.Completed += (_, _) =>
                CompleteToastHide(element, translate, generation);
            element.BeginAnimation(
                UIElement.OpacityProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        });
    }

    private static void Reveal(
        FrameworkElement element,
        TranslateTransform? translate,
        double fromOpacity,
        double fromY,
        Duration duration,
        TimeSpan delay)
    {
        var generation = Prepare(element, translate, finalOpacity: 1, finalY: 0);
        if (!AnimationsEnabled)
        {
            return;
        }

        var opacityAnimation = CreateAnimation(fromOpacity, 1, duration, delay);
        opacityAnimation.Completed += (_, _) =>
            ClearCompletedAnimations(element, translate, generation);
        element.BeginAnimation(
            UIElement.OpacityProperty,
            opacityAnimation,
            HandoffBehavior.SnapshotAndReplace);

        if (translate is null)
        {
            return;
        }

        var translateAnimation = CreateAnimation(fromY, 0, duration, delay);
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            translateAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateOpacity(
        FrameworkElement element,
        double from,
        double to,
        Duration duration)
    {
        var translate = GetExistingTranslateTransform(element);
        var generation = Prepare(element, translate, finalOpacity: to, finalY: 0);
        if (!AnimationsEnabled)
        {
            return;
        }

        var animation = CreateAnimation(from, to, duration, TimeSpan.Zero);
        animation.Completed += (_, _) =>
            ClearCompletedAnimations(element, translate, generation);
        element.BeginAnimation(
            UIElement.OpacityProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimationUsingKeyFrames CreateAnimation(
        double from,
        double to,
        Duration duration,
        TimeSpan delay)
    {
        var finish = delay + duration.TimeSpan;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(finish),
            FillBehavior = FillBehavior.Stop
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero)
        {
            // Keep staggered surfaces at their starting value from the very first
            // rendered frame; a delayed animation clock would briefly expose the
            // final base value before its BeginTime.
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        }

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(finish))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        return animation;
    }

    private static int Prepare(
        FrameworkElement element,
        TranslateTransform? translate,
        double finalOpacity,
        double finalY)
    {
        var generation = unchecked((int)element.GetValue(MotionGenerationProperty) + 1);
        element.SetValue(MotionGenerationProperty, generation);

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetCurrentValue(UIElement.OpacityProperty, finalOpacity);
        if (translate is not null)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.SetCurrentValue(TranslateTransform.YProperty, finalY);
        }

        return generation;
    }

    private static void ClearCompletedAnimations(
        FrameworkElement element,
        TranslateTransform? translate,
        int generation)
    {
        if ((int)element.GetValue(MotionGenerationProperty) != generation)
        {
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (translate is not null)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    private static void CompleteToastHide(
        FrameworkElement element,
        TranslateTransform? translate,
        int generation)
    {
        if ((int)element.GetValue(MotionGenerationProperty) != generation)
        {
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (translate is not null)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.SetCurrentValue(TranslateTransform.YProperty, 0d);
        }

        element.Visibility = Visibility.Collapsed;
        element.SetCurrentValue(UIElement.OpacityProperty, 1d);
    }

    private static TranslateTransform? GetOrCreateTranslateTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform translate)
        {
            return translate;
        }

        if (element.RenderTransform is TransformGroup group)
        {
            var existing = group.Children.OfType<TranslateTransform>().LastOrDefault();
            if (existing is not null)
            {
                return existing;
            }

            var writableGroup = group.IsFrozen ? group.CloneCurrentValue() : group;
            translate = new TranslateTransform();
            writableGroup.Children.Add(translate);
            if (!ReferenceEquals(writableGroup, group))
            {
                element.SetCurrentValue(UIElement.RenderTransformProperty, writableGroup);
            }

            return translate;
        }

        if (ReferenceEquals(element.RenderTransform, Transform.Identity))
        {
            translate = new TranslateTransform();
            element.SetCurrentValue(UIElement.RenderTransformProperty, translate);
            return translate;
        }

        // Preserve an existing scale/rotate/matrix transform rather than replacing
        // a control's visual contract. Opacity still provides a safe static reveal.
        return null;
    }

    private static TranslateTransform? GetExistingTranslateTransform(FrameworkElement element) =>
        element.RenderTransform switch
        {
            TranslateTransform translate => translate,
            TransformGroup group => group.Children.OfType<TranslateTransform>().LastOrDefault(),
            _ => null
        };

    private static void RunOnElementDispatcher(FrameworkElement element, Action action)
    {
        if (element.Dispatcher.HasShutdownStarted || element.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (element.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = element.Dispatcher.BeginInvoke(action);
    }
}

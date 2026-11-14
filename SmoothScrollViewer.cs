using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleModManager;

public sealed class SmoothScrollViewer : ScrollViewer
{
    private double target;
    private double position;
    private TimeSpan? lastFrame;
    private bool scrolling;
    public bool AnimateWheel { get; set; } = true;

    public SmoothScrollViewer() => Unloaded += (_, _) => StopAnimation();

    public void ResetPosition()
    {
        StopAnimation();
        ScrollToTop();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (!AnimateWheel || !SystemParameters.ClientAreaAnimation || ScrollableHeight <= 0)
        {
            StopAnimation();
            base.OnPreviewMouseWheel(e);
            return;
        }

        target = Math.Clamp((scrolling ? target : VerticalOffset) - e.Delta * .95, 0, ScrollableHeight);
        if (!scrolling)
        {
            position = VerticalOffset;
            scrolling = true;
            CompositionTarget.Rendering += ScrollFrame;
        }
        e.Handled = true;
    }

    private void ScrollFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || lastFrame == frame.RenderingTime)
            return;
        var elapsed = lastFrame is { } previous
            ? Math.Clamp((frame.RenderingTime - previous).TotalSeconds, 0, .05) : 1d / 60;
        lastFrame = frame.RenderingTime;
        target = Math.Clamp(target, 0, ScrollableHeight);
        position += (target - position) * (1 - Math.Exp(-elapsed * 24));
        if (Math.Abs(target - position) < 1)
        {
            ScrollToVerticalOffset(target);
            StopAnimation();
        }
        else if (Math.Abs(position - VerticalOffset) >= .5)
            ScrollToVerticalOffset(position);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        StopAnimation();
        base.OnPreviewMouseDown(e);
    }

    private void StopAnimation()
    {
        if (!scrolling)
            return;
        CompositionTarget.Rendering -= ScrollFrame;
        scrolling = false;
        lastFrame = null;
    }
}

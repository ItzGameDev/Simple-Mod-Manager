using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SimpleModManager;

public partial class MainWindow
{
    private (Page Page, FrameworkElement Target, string Title, string Body)[] TutorialPages =>
    [
        (Page.Library, SetupNav, "Start with Game setup", "Click the highlighted Game setup button to find your Gorilla Tag installation."),
        (Page.Setup, BrowseGameButton, "Choose the game folder", "Use Browse for game if Steam or Meta Horizon PC wasn't detected. The status dot shows whether BepInEx is ready, missing, disabled, or outdated."),
        (Page.Library, SearchBox, "Find a mod", "Try typing a mod name here. Category buttons narrow the library."),
        (Page.Library, Cards.Children.OfType<Button>().FirstOrDefault() ?? (FrameworkElement)CatalogHeading, "Open a mod card", "Click a card to see its details and install options. You can close the details by clicking outside or pressing Escape."),
        (Page.Installed, Cards.Children.OfType<FrameworkElement>().FirstOrDefault() ?? EmptyState, "Manage installed mods", "Installed mods lists catalog entries and local DLLs. Close Gorilla Tag before enabling or disabling them."),
        (Page.Settings, ThemeChoices, "Make it yours", "Choose a theme here. Scroll down for motion and animated banners. You can replay this tour from Settings."),
        (Page.Library, RequestModButton, "Request a mod", "Use this button to open the community Discord and suggest a mod.")
    ];

    private int tutorialPage;
    private IInputElement? tutorialReturnFocus;
    private FrameworkElement? tutorialTarget;
    private Rect lastSpotlightBounds = Rect.Empty;
    private Size lastOverlaySize;

    private bool HasTutorialProgress => preferences.TutorialInProgress ?? (preferences.TutorialPosition > 0);

    private void UpdateTutorialButton() =>
        TutorialLaunchButton.Content = HasTutorialProgress ? "Resume tutorial" : "Open tutorial";

    private void ShowTutorial(object sender, RoutedEventArgs e) => StartTutorial(HasTutorialProgress ? preferences.TutorialPosition : 0);

    private void StartTutorial(int start)
    {
        if (operation is not null || TutorialOverlay.IsVisible)
            return;
        tutorialReturnFocus = Keyboard.FocusedElement;
        tutorialPage = Math.Clamp(start, 0, TutorialPages.Length - 1);
        TutorialOverlay.Visibility = Visibility.Visible;
        CompositionTarget.Rendering += TrackTutorialTarget;
        RenderTutorial();
        TutorialNext.Focus();
    }

    private void RenderTutorial()
    {
        preferences = preferences with { TutorialPosition = tutorialPage, TutorialInProgress = true };
        UpdateTutorialButton();
        SavePreferences();
        var step = TutorialPages[tutorialPage];
        Navigate(step.Page);
        if (tutorialPage == 3)
        {
            SearchBox.Clear();
            SelectCategory("All mods");
            RenderCards();
        }
        step = TutorialPages[tutorialPage];
        tutorialTarget = step.Target;
        lastSpotlightBounds = Rect.Empty;
        tutorialTarget.BringIntoView();
        TutorialStep.Text = $"Getting started · {tutorialPage + 1} of {TutorialPages.Length}";
        TutorialTitle.Text = step.Title;
        TutorialBody.Text = step.Body;
        TutorialBack.IsEnabled = tutorialPage > 0;
        TutorialNext.Content = tutorialPage == TutorialPages.Length - 1 ? "Done" : "Next";
        QueueTutorialLayout();
        Reveal(TutorialCard, 8);

    }

    private void TutorialLayoutChanged(object sender, SizeChangedEventArgs e) => QueueTutorialLayout();

    private void TrackTutorialTarget(object? sender, EventArgs e) => PositionTutorial();

    private void StopTutorialTracking() => CompositionTarget.Rendering -= TrackTutorialTarget;

    private void QueueTutorialLayout() => Dispatcher.InvokeAsync(PositionTutorial, DispatcherPriority.Loaded);

    private void PositionTutorial()
    {
        if (!TutorialOverlay.IsVisible || tutorialTarget is not { IsVisible: true } target ||
            !TourBackground.IsAncestorOf(target))
            return;
        var width = TutorialOverlay.ActualWidth;
        var height = TutorialOverlay.ActualHeight;
        if (width < 1 || height < 1)
            return;

        var bounds = target.TransformToVisual(TutorialOverlay).TransformBounds(new Rect(target.RenderSize));
        var isModCard = ReferenceEquals(VisualTreeHelper.GetParent(target), Cards);
        if (isModCard)
        {
            var viewport = CardScroll.TransformToVisual(TutorialOverlay).TransformBounds(
                new Rect(new Size(CardScroll.ViewportWidth, CardScroll.ViewportHeight)));
            bounds.Intersect(viewport);
        }
        if (bounds.IsEmpty)
            return;
        bounds.Intersect(new Rect(0, 0, width, height));
        if (bounds.IsEmpty)
            return;
        var overlaySize = new Size(width, height);
        if (bounds == lastSpotlightBounds && overlaySize == lastOverlaySize)
            return;
        lastSpotlightBounds = bounds;
        lastOverlaySize = overlaySize;
        var radius = isModCard ? 16 : 9;
        var mask = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width, height)), new RectangleGeometry(bounds, radius, radius));
        TutorialVeil.Width = width;
        TutorialVeil.Height = height;
        TutorialVeil.Clip = mask;
        TutorialShield.Data = mask;
        TutorialSpotlight.Width = bounds.Width;
        TutorialSpotlight.Height = bounds.Height;
        TutorialSpotlight.CornerRadius = new CornerRadius(radius);
        Canvas.SetLeft(TutorialSpotlight, bounds.Left);
        Canvas.SetTop(TutorialSpotlight, bounds.Top);

        TutorialCard.Measure(new Size(TutorialCard.Width, double.PositiveInfinity));
        var tipHeight = TutorialCard.DesiredSize.Height;
        var left = bounds.Right + TutorialCard.Width + 24 < width ? bounds.Right + 18 : bounds.Left;
        var top = left > bounds.Right ? bounds.Top :
            bounds.Bottom + tipHeight + 24 < height ? bounds.Bottom + 18 : bounds.Top - tipHeight - 18;
        Canvas.SetLeft(TutorialCard, Math.Clamp(left, 12, Math.Max(12, width - TutorialCard.Width - 12)));
        Canvas.SetTop(TutorialCard, Math.Clamp(top, 12, Math.Max(12, height - tipHeight - 12)));
    }

    private void NextTutorial(object sender, RoutedEventArgs e)
    {
        if (tutorialPage == TutorialPages.Length - 1)
            FinishTutorial(completed: true);
        else
        {
            tutorialPage++;
            RenderTutorial();
            TutorialNext.Focus();
        }
    }

    private void BackTutorial(object sender, RoutedEventArgs e)
    {
        if (tutorialPage == 0)
            return;
        tutorialPage--;
        RenderTutorial();
        TutorialNext.Focus();
    }

    private void SkipTutorial(object sender, RoutedEventArgs e) => FinishTutorial(completed: true);

    private void FinishTutorial(bool completed = false)
    {
        StopTutorialTracking();
        TutorialOverlay.Visibility = Visibility.Collapsed;
        tutorialTarget = null;
        if (tutorialReturnFocus is UIElement { IsVisible: true } previous)
            Keyboard.Focus(previous);
        tutorialReturnFocus = null;
        preferences = preferences with { TutorialSeen = true, TutorialPosition = completed ? 0 : tutorialPage, TutorialInProgress = !completed };
        UpdateTutorialButton();
        SavePreferences();
    }
}

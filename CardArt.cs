using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SimpleModManager;

internal static class CardArt
{
    private static readonly Lazy<ObjectAnimationUsingKeyFrames> PhoneFrames = new(LoadPhoneFrames);
    public static Color Accent(Mod mod) => (Color)ColorConverter.ConvertFromString(mod.Color);

    public static Grid Banner(Mod mod, bool animateBanner)
    {
        var accent = Accent(mod);
        var grid = new Grid
        {
            ClipToBounds = true,
            Background = new LinearGradientBrush(
                Color.FromRgb(29, 31, 36),
                Color.FromRgb((byte)(accent.R / 4), (byte)(accent.G / 4), (byte)(accent.B / 4)), 22)
        };

        if (mod.Banner is { } banner)
        {
            var isPhoneGif = banner == "gorilla-phone.gif";
            var source = new BitmapImage(new Uri($"pack://application:,,,/SimpleModManager;component/Assets/{banner}", UriKind.Absolute));
            var image = new ImageBrush(source)
            {
                Stretch = mod.Artwork == "hamburbur" ? Stretch.Uniform : Stretch.UniformToFill,
                AlignmentY = AlignmentY.Center
            };
            if (mod.Id == "camera")
                image.Viewbox = new Rect(0, .17, 1, .8);
            if (mod.Artwork == "hamburbur")
                grid.Children.Add(new Rectangle { Fill = image, Margin = new Thickness(8, 12, 8, 12) });
            else
                grid.Background = image;
            if (isPhoneGif && animateBanner && SystemParameters.ClientAreaAnimation)
                AnimateVisiblePhone(grid, image);
            grid.Children.Add(new Border
            {
                Background = new LinearGradientBrush(Color.FromArgb(104, 12, 13, 15),
                    Color.FromArgb(25, 12, 13, 15), 90)
            });
            return grid;
        }

        grid.Children.Add(new Rectangle
        {
            Fill = new DrawingBrush(new GeometryDrawing(null,
                new Pen(new SolidColorBrush(WithAlpha(accent, 23)), 1),
                Geometry.Parse("M 0,0 H 18 V 18")))
            {
                Viewbox = new Rect(0, 0, 18, 18),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 18, 18),
                ViewportUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile
            }
        });

        var drawing = new Canvas { Width = 120, Height = 120 };
        var outer = new Ellipse { Width = 108, Height = 108, Stroke = new SolidColorBrush(WithAlpha(accent, 92)), StrokeThickness = 1 };
        Canvas.SetLeft(outer, 6);
        Canvas.SetTop(outer, 6);
        drawing.Children.Add(outer);

        var inner = new Ellipse { Width = 88, Height = 88, Stroke = new SolidColorBrush(WithAlpha(accent, 58)), StrokeThickness = 1 };
        Canvas.SetLeft(inner, 16);
        Canvas.SetTop(inner, 16);
        drawing.Children.Add(inner);

        var symbol = new Viewbox { Width = 53, Height = 53, Child = Icon(mod, 53) };
        Canvas.SetLeft(symbol, 33);
        Canvas.SetTop(symbol, 33);
        drawing.Children.Add(symbol);

        grid.Children.Add(new Viewbox
        {
            Width = 123, Height = 123, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 19, 0, 0), Child = drawing
        });
        return grid;
    }

    public static FrameworkElement Icon(Mod mod, double size)
    {
        if (mod.Icon is { } icon)
        {
            var brush = new ImageBrush(new BitmapImage(
                new Uri($"pack://application:,,,/SimpleModManager;component/Assets/{icon}", UriKind.Absolute)))
            {
                Stretch = Stretch.Uniform
            };
            if (mod.Artwork == "swap")
                brush.Viewbox = new Rect(.24, .24, .52, .52);
            return new Border { Width = size, Height = size, Background = brush, CornerRadius = new CornerRadius(3) };
        }
        var drawing = mod.Artwork switch
        {
            "sandbox" => "M 2,7 L 12,2 L 22,7 L 12,12 Z M 2,7 V 17 L 12,22 L 22,17 V 7 M 12,12 V 22 M 7,4.5 L 17,9.5",
            "shirt" => "M 8,3 L 3,6 L 1,11 L 6,14 L 7,12 V 22 H 17 V 12 L 18,14 L 23,11 L 21,6 L 16,3 A 4,4 0 0 1 8,3 Z",
            "lens" => "M 2,6 H 16 V 18 H 2 Z M 16,10 L 22,6 V 18 L 16,14 M 5,3 H 12 M 7,12 H 11",
            "phone" => "M 6,2 H 18 V 22 H 6 Z M 10,5 H 14 M 11,19 H 13",
            "bark" => "M 12,2 L 21,12 H 17 L 22,18 H 14 V 22 H 10 V 18 H 2 L 7,12 H 3 Z",
            "cone" => "M 10,2 H 14 L 20,19 H 4 Z M 7.5,12 H 16.5 M 2,19 H 22 V 22 H 2 Z",
            "nodes" => "M 12,6 L 5,16 M 12,6 L 19,16 M 5,16 H 19 M 12,1 A 3,3 0 1 0 12.01,1 M 4,15 A 3,3 0 1 0 4.01,15 M 20,15 A 3,3 0 1 0 20.01,15",
            _ => "M 3,7 L 12,2 L 21,7 V 17 L 12,22 L 3,17 Z M 3,7 L 12,12 L 21,7 M 12,12 V 22"
        };
        var path = new Path
        {
            Data = Geometry.Parse(drawing),
            Stroke = new SolidColorBrush(Accent(mod)),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        return new Viewbox { Width = size, Height = size, Child = path };
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static void AnimateVisiblePhone(Grid banner, ImageBrush image)
    {
        ScrollViewer? scroller = null;
        var playing = false;

        void Update()
        {
            var visible = banner.IsLoaded && banner.IsVisible && banner.ActualHeight > 0;
            if (visible && scroller is not null && scroller.IsAncestorOf(banner))
            {
                var bounds = banner.TransformToAncestor(scroller).TransformBounds(new Rect(banner.RenderSize));
                visible = bounds.IntersectsWith(new Rect(scroller.RenderSize));
            }
            if (visible == playing)
                return;
            playing = visible;
            image.BeginAnimation(ImageBrush.ImageSourceProperty, playing ? PhoneFrames.Value : null);
        }

        void ScrollChanged(object sender, ScrollChangedEventArgs args) => Update();
        banner.Loaded += (_, _) =>
        {
            for (var parent = VisualTreeHelper.GetParent(banner); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollViewer scroll)
                {
                    scroller = scroll;
                    scroller.ScrollChanged += ScrollChanged;
                    break;
                }
            }
            Update();
        };
        banner.IsVisibleChanged += (_, _) => Update();
        banner.Unloaded += (_, _) =>
        {
            if (scroller is not null)
                scroller.ScrollChanged -= ScrollChanged;
            scroller = null;
            playing = false;
            image.BeginAnimation(ImageBrush.ImageSourceProperty, null);
        };
    }

    private static ObjectAnimationUsingKeyFrames LoadPhoneFrames()
    {
        var uri = new Uri("pack://application:,,,/SimpleModManager;component/Assets/gorilla-phone.gif", UriKind.Absolute);
        var decoder = new GifBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var width = decoder.Frames[0].PixelWidth;
        var height = decoder.Frames[0].PixelHeight;
        var animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        var elapsed = TimeSpan.Zero;
        BitmapSource? previous = null;

        for (var index = 0; index < decoder.Frames.Count; index++)
        {
            var frame = decoder.Frames[index];
            var metadata = (BitmapMetadata)frame.Metadata;
            var x = (ushort?)metadata.GetQuery("/imgdesc/Left") ?? 0;
            var y = (ushort?)metadata.GetQuery("/imgdesc/Top") ?? 0;
            if (x == 0 && y == 0 && frame.PixelWidth == width && frame.PixelHeight == height)
                previous = frame;
            else
            {
                // The supplied GIF keeps the previous frame behind each changed rectangle.
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    if (previous is not null)
                        drawing.DrawImage(previous, new Rect(0, 0, width, height));
                    drawing.DrawImage(frame, new Rect(x, y, frame.PixelWidth, frame.PixelHeight));
                }
                var composited = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                composited.Render(visual);
                composited.Freeze();
                previous = composited;
            }

            if (index % 2 == 0)
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(previous, KeyTime.FromTimeSpan(elapsed)));
            var delay = (ushort?)metadata.GetQuery("/grctlext/Delay") ?? 4;
            elapsed += TimeSpan.FromMilliseconds(Math.Max(2, (int)delay) * 10);
        }

        animation.Duration = new Duration(elapsed);
        animation.Freeze();
        return animation;
    }
}

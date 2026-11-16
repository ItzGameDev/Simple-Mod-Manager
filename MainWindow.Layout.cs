using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SimpleModManager;

public partial class MainWindow
{
    private void InitializeResponsiveLayout()
    {
        var work = SystemParameters.WorkArea;
        FitWindowToWorkArea(work, firstOpen: true);
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        SourceInitialized += (_, _) => FitCurrentMonitor(firstOpen: true);
        LocationChanged += (_, _) => FitCurrentMonitor(firstOpen: false);
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(() => FitCurrentMonitor(firstOpen: false));
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= ScreenSettingsChanged;
        SystemParameters.StaticPropertyChanged += ScreenSettingsChanged;
        UpdateResponsiveLayout();
    }

    private void ScreenSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            Dispatcher.InvokeAsync(() => FitCurrentMonitor(firstOpen: false));
    }

    private void FitCurrentMonitor(bool firstOpen)
    {
        var source = PresentationSource.FromVisual(this);
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (source?.CompositionTarget is not { } target || handle == 0 ||
            !GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
            return;
        var scale = target.TransformFromDevice;
        var work = new Rect(info.Work.Left * scale.M11, info.Work.Top * scale.M22,
            (info.Work.Right - info.Work.Left) * scale.M11, (info.Work.Bottom - info.Work.Top) * scale.M22);
        FitWindowToWorkArea(work, firstOpen);
    }

    private void FitWindowToWorkArea(Rect work, bool firstOpen)
    {
        // WPF dimensions use device-independent units, including Windows display scaling.
        MinWidth = Math.Min(600, work.Width);
        MinHeight = Math.Min(420, work.Height);
        if (WindowState != WindowState.Normal)
            return;
        Width = Math.Min(Width, firstOpen ? Math.Max(MinWidth, work.Width * .92) : work.Width);
        Height = Math.Min(Height, firstOpen ? Math.Max(MinHeight, work.Height * .92) : work.Height);
        if (!firstOpen && !double.IsNaN(Left) && !double.IsNaN(Top))
        {
            var left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
            var top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
            if (Math.Abs(Left - left) > 1) Left = left;
            if (Math.Abs(Top - top) > 1) Top = top;
        }
    }

    private void UpdateResponsiveLayout()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var compact = width < 1050;
        SidebarColumn.Width = new GridLength(compact ? 174 : 211);
        SidebarContent.Margin = compact ? new Thickness(8, 15, 8, 12) : new Thickness(14, 24, 14, 16);
        LibraryPage.Margin = compact ? new Thickness(14, 14, 10, 4) : new Thickness(28, 25, 19, 4);
        LibraryHero.MinHeight = height < 560 ? 60 : height < 720 ? 94 : 135;
        LibraryTitle.FontSize = compact ? 27 : 34;
        LibraryIntro.FontSize = compact ? 12 : 14;
        LibraryIntro.Visibility = height < 560 ? Visibility.Collapsed : Visibility.Visible;
        HeroArtwork.Visibility = width < 900 ? Visibility.Collapsed : Visibility.Visible;

        var toolsWidth = Math.Max(0, width - SidebarColumn.Width.Value - LibraryPage.Margin.Left - LibraryPage.Margin.Right);
        var stacked = toolsWidth < 890;
        Grid.SetRow(CategoryFilterFrame, stacked ? 1 : 0);
        Grid.SetColumn(CategoryFilterFrame, stacked ? 0 : 1);
        Grid.SetColumnSpan(CategoryFilterFrame, stacked ? 2 : 1);
        Grid.SetColumnSpan(SearchFrame, stacked ? 2 : 1);
        SearchFrame.Margin = stacked ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        CategoryFilterFrame.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        ThemeOptions.Columns = width < 850 ? 2 : 4;
        CatalogTools.Margin = height < 720 ? new Thickness(0, 10, 7, 7) : new Thickness(0, 18, 7, 13);

        DetailsViewport.MaxHeight = Math.Max(80, height - 72);
        DownloadViewport.MaxHeight = Math.Max(80, height - 72);
        DetailsCard.Width = Math.Min(480, Math.Max(150, width - 36));
        DownloadCard.Width = Math.Min(465, Math.Max(150, width - 36));
        Dispatcher.InvokeAsync(AlignSelections, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}



using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PerplexityLocalBridge;
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app=new Application();app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/Appearance.Tests;component/Theme.xaml",UriKind.Relative)});
        foreach(var dark in new[]{true,false})
        {
            Theme.Apply(new Window(),dark);
            var menu=new ContextMenu{Style=(Style)app.Resources[typeof(ContextMenu)]};
            var check=new MenuItem{Header="Dark mode",IsCheckable=true,IsChecked=true,Style=(Style)app.Resources[typeof(MenuItem)]};
            var submenu=new MenuItem{Header="Settings",Style=(Style)app.Resources[typeof(MenuItem)]};submenu.Items.Add(new MenuItem{Header="API"});
            menu.Items.Add(new MenuItem{Header="Connect Perplexity + start API",Style=(Style)app.Resources[typeof(MenuItem)]});menu.Items.Add(check);menu.Items.Add(submenu);
            menu.ApplyTemplate();menu.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));menu.Arrange(new Rect(menu.DesiredSize));menu.UpdateLayout();
            check.ApplyTemplate();
            var tick=(TextBlock)check.Template.FindName("Check",check);
            if(tick.Visibility!=Visibility.Visible)throw new Exception("Checked-item cue is missing");
            var expected=((SolidColorBrush)app.Resources["PanelBrush"]).Color;
            var image=new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),(int)Math.Ceiling(menu.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(menu);
            var pixel=new byte[4];image.CopyPixels(new Int32Rect(7,12,1,1),pixel,4,0);
            if(pixel[0]!=expected.B||pixel[1]!=expected.G||pixel[2]!=expected.R)throw new Exception("Menu icon gutter does not match theme");
            var path=Path.GetFullPath(Path.Combine("artifacts","appearance",dark?"menu-dark.png":"menu-light.png"));Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(path);encoder.Save(output);
        }
        Console.WriteLine("PASS: dark/light menu gutter pixels match the theme, checked-item indicator remains visible, and menu renders were saved.");
    }
}

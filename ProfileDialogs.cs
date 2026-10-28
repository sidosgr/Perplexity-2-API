using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace PerplexityLocalBridge;

public static class ProfileDialogs
{
    private static Window Dialog(Window owner,string title,Panel content)
    {
        var window=new Window{Owner=owner,Title=title,Width=460,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=content,ShowInTaskbar=false,Icon=owner.Icon};
        window.SetResourceReference(Window.BackgroundProperty,"PanelBrush");window.SetResourceReference(Window.ForegroundProperty,"TextBrush");return window;
    }
    public static string? Name(Window owner,string title,string initial,Action<string> validate)
    {
        var panel=new StackPanel{Margin=new Thickness(22)};
        panel.Children.Add(new TextBlock{Text="Profile name",FontSize=18,FontWeight=FontWeights.SemiBold});
        var input=new TextBox{Text=initial,Margin=new Thickness(0,12,0,8),MaxLength=60};panel.Children.Add(input);
        var error=new TextBlock{Foreground=Brushes.IndianRed,TextWrapping=TextWrapping.Wrap};panel.Children.Add(error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        var save=new Button{Content=title.StartsWith("Rename")?"Rename":"Create profile",IsDefault=true};var cancel=new Button{Content="Cancel",IsCancel=true};buttons.Children.Add(save);buttons.Children.Add(cancel);panel.Children.Add(buttons);
        var window=Dialog(owner,title,panel);string? name=null;
        save.Click+=(_,_)=>{try{validate(input.Text.Trim());name=input.Text.Trim();window.DialogResult=true;}catch(ArgumentException ex){error.Text=ex.Message;input.Focus();}};
        window.Loaded+=(_,_)=>{input.Focus();input.SelectAll();};
        return window.ShowDialog()==true?name:null;
    }
    public static bool ConfirmRemoval(Window owner,AccountProfile profile,bool last)
    {
        var panel=new StackPanel{Margin=new Thickness(22)};
        panel.Children.Add(new TextBlock{Text="Remove "+profile.DisplayName+"?",FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock{Text="This removes the profile from the app and clears its saved browser sign-in data on this PC. Other profiles are kept."+(last?" A new empty profile will be shown so you can sign in again.":""),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,16)});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Cancel",IsDefault=true,IsCancel=true};var remove=new Button{Content="Remove profile",Background=Brushes.Firebrick,Foreground=Brushes.White};buttons.Children.Add(cancel);buttons.Children.Add(remove);panel.Children.Add(buttons);
        var window=Dialog(owner,"Remove profile",panel);remove.Click+=(_,_)=>window.DialogResult=true;
        return window.ShowDialog()==true;
    }
}

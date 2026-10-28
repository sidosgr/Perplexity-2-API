using System.Windows;
namespace PerplexityLocalBridge;
public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? activationWait;
    private bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        var user=System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value??Environment.UserName;
        var id=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user)))[..20];
        var name="Local\\Perplexity2Api-"+id;
        instance=new Mutex(false,name);
        try{ownsInstance=instance.WaitOne(0);}catch(AbandonedMutexException){ownsInstance=true;}
        if(!ownsInstance)
        {
            try{using var existing=EventWaitHandle.OpenExisting(name+"-activate");existing.Set();}catch(WaitHandleCannotBeOpenedException){ }
            Shutdown();return;
        }
        activate=new EventWaitHandle(false,EventResetMode.AutoReset,name+"-activate");
        activationWait=ThreadPool.RegisterWaitForSingleObject(activate,(_,_)=>Dispatcher.BeginInvoke(()=>
        {
            if(MainWindow is not null){MainWindow.Show();MainWindow.WindowState=WindowState.Normal;MainWindow.Activate();}
        }),null,Timeout.Infinite,false);
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activationWait?.Unregister(null);activate?.Dispose();if(ownsInstance)instance?.ReleaseMutex();instance?.Dispose();base.OnExit(e);
    }
}

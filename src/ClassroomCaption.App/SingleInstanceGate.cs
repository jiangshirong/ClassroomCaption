namespace ClassroomCaption.App;

// Mutex ownership stays on the WPF startup/exit thread. The event also retains
// activations arriving before the first window has finished initializing.
public sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private RegisteredWaitHandle? wait;
    public bool IsPrimary {get;}
    public SingleInstanceGate(string name)
    {
        activation=new EventWaitHandle(false,EventResetMode.AutoReset,name+".Activate");
        mutex=new Mutex(false,name+".Owner");
        try {IsPrimary=mutex.WaitOne(0);}
        catch(AbandonedMutexException){IsPrimary=true;}
    }
    public void ActivatePrimary()=>activation.Set();
    public void Listen(Action restore)
    {
        if(!IsPrimary)throw new InvalidOperationException();
        wait=ThreadPool.RegisterWaitForSingleObject(activation,(_,_)=>restore(),null,Timeout.Infinite,false);
    }
    public void Dispose()
    {
        wait?.Unregister(null);
        if(IsPrimary)mutex.ReleaseMutex();
        mutex.Dispose();activation.Dispose();
    }
}

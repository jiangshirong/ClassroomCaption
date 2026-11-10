using System.Windows;
using System.Windows.Media;

namespace ClassroomCaption.App;

// Use WPF's presentation cadence rather than competing dispatcher timers in both windows.
public sealed class CaptionAnimationClock
{
    private readonly Window owner;
    private readonly OutlinedText[] captions;
    private TimeSpan? previous;
    private bool attached;
    public bool Paused {get;set;}
    public CaptionAnimationClock(Window owner, params OutlinedText[] captions)
    {
        this.owner = owner; this.captions = captions;
        foreach(var caption in captions)caption.AnimationRequested+=UpdateSubscription;
        owner.Loaded += (_, _) => UpdateSubscription();
        owner.IsVisibleChanged += (_, _) => UpdateSubscription();
        owner.StateChanged += (_, _) => UpdateSubscription();
        owner.Closed += (_, _) => { Detach();foreach(var caption in captions)caption.AnimationRequested-=UpdateSubscription; };
    }
    private void UpdateSubscription()
    {
        if (!owner.IsLoaded || !owner.IsVisible || owner.WindowState == WindowState.Minimized || !captions.Any(c=>c.IsAnimating)) { Detach(); return; }
        if (attached) return;
        previous = null; CompositionTarget.Rendering += Render; attached = true;
    }
    private void Detach()
    {
        if (attached) CompositionTarget.Rendering -= Render;
        attached = false; previous = null;
    }
    private void Render(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame) return;
        var seconds = previous.HasValue ? Math.Max(0, (frame.RenderingTime - previous.Value).TotalSeconds) : 0;
        previous = frame.RenderingTime;
        if(Paused)return;
        foreach (var caption in captions) caption.AdvanceAnimation(seconds);
        if(!captions.Any(c=>c.IsAnimating))Detach();
    }
}

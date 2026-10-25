namespace SelfService.Portal;

// Ordinary actions may overlap (including cancellation); credential resume is exclusive.
public sealed class SessionResumeGate
{
    readonly object sync = new(); int actions; bool resuming;
    public IDisposable BeginAction()
    {
        lock (sync)
        {
            if (resuming) throw new PortalException("Cached connections are being verified. Wait for restart resume to finish.", 409);
            actions++; return new ActionLease(this);
        }
    }
    public bool TryBeginResume() { lock (sync) { if (resuming || actions != 0) return false; resuming = true; return true; } }
    public void EndResume() { lock (sync) resuming = false; }
    sealed class ActionLease(SessionResumeGate gate) : IDisposable
    {
        SessionResumeGate? active = gate;
        public void Dispose() { var g = Interlocked.Exchange(ref active, null); if (g is not null) lock (g.sync) g.actions--; }
    }
}

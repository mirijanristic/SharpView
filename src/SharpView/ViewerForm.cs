namespace SharpView;

/// <summary>
/// Form subclass that routes key input through a callback (bypassing the
/// unreliable KeyDown event in DoEvents render loops) and window hit testing
/// through <see cref="HitTestHandler"/>: the D3D-drawn top bar reports itself as
/// <see cref="HTCaption"/>, so Windows runs its native move loop for it —
/// drag-restore from maximized, Aero Snap, cross-monitor drags, double-click
/// restore and the right-click system menu all work like a real title bar.
/// Also opts out of the GDI redirection surface (WS_EX_NOREDIRECTIONBITMAP): every
/// client-area pixel — including its alpha — comes exclusively from the
/// DirectComposition-hosted swap chain, which is what makes the per-pixel
/// transparent window possible. Consequence: classic GDI child controls placed on
/// this form would have nowhere to paint (invisible but still clickable), so the
/// viewer draws all of its UI through Direct3D instead.
/// </summary>
sealed class ViewerForm : Form
{
    const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    const int WM_NCHITTEST = 0x0084;
    const int WM_NCMOUSEMOVE = 0x00A0;

    /// <summary>WM_NCHITTEST result: ordinary client area (mouse events reach us).</summary>
    public const int HTClient = 1;
    /// <summary>WM_NCHITTEST result: caption — Windows handles dragging and snapping.</summary>
    public const int HTCaption = 2;

    /// <summary>Return true if the key was handled; false to use default processing.</summary>
    public Func<Keys, bool>? KeyHandler;

    /// <summary>Hit test in client pixels. Return an HT* code, or 0 for default handling.</summary>
    public Func<int, int, int>? HitTestHandler;

    /// <summary>Raised on non-client mouse movement. The caption zone produces no
    /// regular MouseMove events, so this is how the render loop learns it should
    /// wake up and let the top bar fade in.</summary>
    public Action? NonClientMouseMove;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOREDIRECTIONBITMAP;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST when HitTestHandler is not null:
            {
                // lParam packs screen coords; extract as SIGNED shorts so monitors
                // left of / above the primary keep working.
                int packed = unchecked((int)(long)m.LParam);
                var screen = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
                var client = PointToClient(screen);
                int hit = HitTestHandler(client.X, client.Y);
                if (hit != 0)
                {
                    m.Result = (nint)hit;
                    return;
                }
                break;
            }
            case WM_NCMOUSEMOVE:
                NonClientMouseMove?.Invoke();
                break;
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        => KeyHandler?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);
}

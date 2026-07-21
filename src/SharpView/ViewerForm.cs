namespace SharpView;

/// <summary>
/// Form subclass that routes key input through a callback, bypassing the
/// unreliable KeyDown event in DoEvents render loops. Keys the callback does not
/// handle fall through to the default processing (so Alt+F4, Tab, etc. still work).
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

    /// <summary>Return true if the key was handled; false to use default processing.</summary>
    public Func<Keys, bool>? KeyHandler;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOREDIRECTIONBITMAP;
            return cp;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        => KeyHandler?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);
}

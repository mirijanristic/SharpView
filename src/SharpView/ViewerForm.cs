namespace SharpView;

/// <summary>
/// Form subclass that routes key input through a callback, bypassing the
/// unreliable KeyDown event in DoEvents render loops. Keys the callback does not
/// handle fall through to the default processing (so Alt+F4, Tab, etc. still work).
/// </summary>
sealed class ViewerForm : Form
{
    /// <summary>Return true if the key was handled; false to use default processing.</summary>
    public Func<Keys, bool>? KeyHandler;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        => KeyHandler?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);
}

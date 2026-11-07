using System.Text;

namespace Maid.Remote;

/// <summary>
/// The recent output of a session, for a phone that starts watching it: what was written since the
/// console last changed size, with that size. ConPTY paints the whole screen again after every resize,
/// so this is always at least the full screen (and since then everything that scrolled by). Older
/// output, written for another size, is let go of: replayed as well, it would leave copies of earlier
/// screens in the phone's scrollback. Used on the dispatcher thread only.
/// </summary>
internal sealed class TerminalReplay(int cols, int rows, int maxChars = TerminalReplay.DefaultMaxChars)
{
    public const int DefaultMaxChars = 512 * 1024;

    private readonly StringBuilder _text = new();

    public int Cols { get; private set; } = cols;
    public int Rows { get; private set; } = rows;

    public void Resize(int cols, int rows)
    {
        if (cols == Cols && rows == Rows) return;
        (Cols, Rows) = (cols, rows);
        _text.Clear();
    }

    public void Append(string text)
    {
        _text.Append(text);
        // The oldest output goes first; whatever screen it leaves half drawn is painted over soon after.
        if (_text.Length > maxChars) _text.Remove(0, _text.Length - maxChars);
    }

    /// <summary>The pieces to write, each at its size (one, as things are).</summary>
    public IEnumerable<object> Snapshot() => [new { cols = Cols, rows = Rows, data = _text.ToString() }];
}

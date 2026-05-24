namespace SpawnSpotter.Ui;

/// <summary>
/// Minimal in-place status-line renderer. Uses ANSI escape (cursor up + clear line)
/// when stdout is a TTY; otherwise prints periodically without overwriting.
/// </summary>
internal sealed class StatusLine
{
    private readonly bool _isTty;
    private int _lastLength;

    public StatusLine()
    {
        _isTty = !System.Console.IsOutputRedirected;
    }

    public void Render(string text)
    {
        if (_isTty)
        {
            // Carriage return + clear-to-end-of-line. Avoids cursor-position math.
            System.Console.Write('\r');
            System.Console.Write(text);
            if (text.Length < _lastLength)
            {
                System.Console.Write(new string(' ', _lastLength - text.Length));
            }
            _lastLength = text.Length;
        }
        else
        {
            System.Console.WriteLine(text);
        }
    }

    public void Close()
    {
        if (_isTty)
        {
            System.Console.WriteLine();
        }
    }
}

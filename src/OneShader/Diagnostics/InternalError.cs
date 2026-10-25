using System;

namespace OneShader.Diagnostics;

/// <summary>Formats a caught exception for an internal-error diagnostic so a bug report says where it happened.</summary>
internal static class InternalError
{
    /// <summary>The exception type, its message and the method that threw (no file paths or line numbers, so the text is the same on every machine).</summary>
    public static string Describe(Exception ex)
    {
        string text = ex.GetType().Name + ": " + ex.Message;
        string? stack = ex.StackTrace;
        if (!string.IsNullOrEmpty(stack))
        {
            // The innermost frame in the compiler itself; frames from the runtime's collections say little.
            string frame = stack.Split('\n')[0].Trim();
            foreach (string line in stack.Split('\n'))
            {
                if (line.Trim().StartsWith("at OneShader.", StringComparison.Ordinal))
                {
                    frame = line.Trim();
                    break;
                }
            }

            if (frame.StartsWith("at ", StringComparison.Ordinal))
            {
                frame = frame[3..];
            }

            int paren = frame.IndexOf('(', StringComparison.Ordinal);
            text += " at " + (paren > 0 ? frame[..paren] : frame);
        }

        return text;
    }
}

using System;
using System.IO;
using System.Text;

namespace OneShader.Tool;

internal static class Program
{
    private static int Main(string[] args)
    {
        using var stdout = Console.OpenStandardOutput();
        using var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            return Cli.Run(args, stdout, stderr);
        }
        catch (Exception ex)
        {
            // The compiler turns its own failures into diagnostics; this is the last net so a user never sees a stack trace.
            stderr.WriteLine("oneshader: error: internal error (" + ex.GetType().Name + ": " + ex.Message + "). This is a bug in oneshader; please report it with the command line and shader.");
            return 1;
        }
    }
}

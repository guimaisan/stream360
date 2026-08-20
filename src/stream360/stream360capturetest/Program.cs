using Stream360.CaptureTest;

namespace Stream360.CaptureTest;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        Application.Run(
            new Form1());
    }
}
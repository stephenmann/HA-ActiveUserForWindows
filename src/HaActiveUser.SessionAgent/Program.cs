namespace HaActiveUser.SessionAgent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            @"Local\HAActiveUserSessionAgent",
            out var createdNew);

        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext();
        Application.Run(context);
    }
}
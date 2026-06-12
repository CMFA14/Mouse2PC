using Mouse2PC.UI;
using Velopack;

namespace Mouse2PC;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Hooks de instalação/atualização do Velopack: precisa ser a
        // primeira coisa do Main (sai do processo em eventos de setup).
        VelopackApp.Build().Run();

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
    }
}

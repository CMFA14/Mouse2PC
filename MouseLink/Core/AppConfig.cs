using System.Text.Json;
using MouseLink.Net;

namespace MouseLink.Core;

public class AppConfig
{
    public string Mode { get; set; } = "controller"; // "controller" | "controlled"
    public string RemoteHost { get; set; } = "";
    public int Port { get; set; } = 24801;

    // Posições virtuais escolhidas no painel de layout, por id de tela
    // ("L0", "L1" = telas deste PC; "R0", "R1" = telas do PC remoto).
    public Dictionary<string, VirtualPos> Layout { get; set; } = new();

    // Cache dos monitores remotos (recebidos no hello), para permitir
    // editar o layout mesmo desconectado.
    public List<MonitorDto> RemoteMonitorsCache { get; set; } = new();
    public string RemoteName { get; set; } = "";

    public class VirtualPos
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    private static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MouseLink");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch { /* config corrompida: recomeça do zero */ }
        return new AppConfig();
    }

    public void Save()
    {
        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

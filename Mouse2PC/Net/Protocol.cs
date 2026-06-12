using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mouse2PC.Net;

// Mensagens trocadas como JSON, uma por linha, sobre TCP.
// O controlador converte coordenadas virtuais para coordenadas físicas
// do PC remoto antes de enviar, então o controlado só injeta o que recebe.

public class Msg
{
    [JsonPropertyName("t")] public string Type { get; set; } = "";

    // hello (controlado -> controlador)
    [JsonPropertyName("name")] public string? MachineName { get; set; }
    [JsonPropertyName("mons")] public List<MonitorDto>? Monitors { get; set; }

    // move (coordenadas físicas do PC remoto)
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }

    // btn: b = 0 esq, 1 dir, 2 meio, 3 x1, 4 x2 | d = pressionado
    [JsonPropertyName("b")] public int Button { get; set; }
    [JsonPropertyName("d")] public bool Down { get; set; }

    // wheel
    [JsonPropertyName("wv")] public int WheelV { get; set; }
    [JsonPropertyName("wh")] public int WheelH { get; set; }

    // key
    [JsonPropertyName("vk")] public int Vk { get; set; }
    [JsonPropertyName("sc")] public int Scan { get; set; }
    [JsonPropertyName("ext")] public bool Extended { get; set; }

    // ident: posição i = índice do monitor remoto, valor = número a exibir
    [JsonPropertyName("nums")] public int[]? Numbers { get; set; }

    // clip: conteúdo copiado (Ctrl+C) em um dos PCs
    [JsonPropertyName("txt")] public string? Text { get; set; }

    public const string Hello = "hello";
    public const string Move = "move";
    public const string Btn = "btn";
    public const string Wheel = "wheel";
    public const string Key = "key";
    // enviado quando o cursor sai das telas remotas, para soltar teclas presas
    public const string Leave = "leave";
    // pisca o número de cada tela no PC controlado (botão "Identificar")
    public const string Ident = "ident";
    // área de transferência compartilhada (texto)
    public const string Clip = "clip";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static Msg? Deserialize(string line)
    {
        try { return JsonSerializer.Deserialize<Msg>(line, Options); }
        catch { return null; }
    }
}

public class MonitorDto
{
    [JsonPropertyName("i")] public int Index { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
    [JsonPropertyName("n")] public string Name { get; set; } = "";
}

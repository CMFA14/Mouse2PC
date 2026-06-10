using Mouse2PC.Net;

namespace Mouse2PC.Core;

// Uma tela (local ou remota) posicionada no espaço virtual combinado.
public class ScreenNode
{
    public string Id { get; init; } = "";          // "L0".."Ln" / "R0".."Rn"
    public bool IsLocal { get; init; }
    public string Label { get; init; } = "";
    public Rectangle Physical { get; init; }        // coords reais na máquina dona
    public Rectangle Virtual { get; set; }          // posição no espaço combinado

    public Point PhysicalToVirtual(Point p) =>
        new(p.X - Physical.X + Virtual.X, p.Y - Physical.Y + Virtual.Y);

    public Point VirtualToPhysical(Point v) =>
        new(v.X - Virtual.X + Physical.X, v.Y - Virtual.Y + Physical.Y);
}

public class VirtualLayout
{
    public List<ScreenNode> Screens { get; } = new();

    public static VirtualLayout Build(AppConfig config, List<MonitorDto> remoteMonitors)
    {
        var layout = new VirtualLayout();

        var locals = Screen.AllScreens
            .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .ToList();

        for (int i = 0; i < locals.Count; i++)
        {
            layout.Screens.Add(new ScreenNode
            {
                Id = $"L{i}",
                IsLocal = true,
                Label = $"Este PC – Tela {i + 1}",
                Physical = locals[i].Bounds,
            });
        }

        foreach (var m in remoteMonitors)
        {
            layout.Screens.Add(new ScreenNode
            {
                Id = $"R{m.Index}",
                IsLocal = false,
                Label = $"Remoto – Tela {m.Index + 1}",
                Physical = new Rectangle(m.X, m.Y, m.W, m.H),
            });
        }

        // Posições virtuais: usa o que foi salvo no painel; telas novas ganham
        // um default (locais espelham o arranjo físico; remotas vão à direita).
        int localRight = layout.Screens.Where(s => s.IsLocal)
            .Select(s => s.Physical.Right).DefaultIfEmpty(0).Max();
        int remoteOffset = 0;

        foreach (var s in layout.Screens)
        {
            if (config.Layout.TryGetValue(s.Id, out var pos))
            {
                s.Virtual = new Rectangle(pos.X, pos.Y, s.Physical.Width, s.Physical.Height);
            }
            else if (s.IsLocal)
            {
                s.Virtual = s.Physical;
            }
            else
            {
                s.Virtual = new Rectangle(localRight + remoteOffset, 0,
                    s.Physical.Width, s.Physical.Height);
                remoteOffset += s.Physical.Width;
            }
        }

        return layout;
    }

    public ScreenNode? ScreenAt(Point v) =>
        Screens.FirstOrDefault(s => s.Virtual.Contains(v));

    public ScreenNode? LocalScreenAtPhysical(Point p) =>
        Screens.FirstOrDefault(s => s.IsLocal && s.Physical.Contains(p));

    // Move a posição virtual por um delta, mantendo o cursor dentro da união
    // das telas: se o destino cair fora de qualquer tela, tenta cada eixo
    // separadamente (deslizar ao longo da borda).
    public (Point pos, ScreenNode? screen) Move(Point current, int dx, int dy)
    {
        var candidates = new[]
        {
            new Point(current.X + dx, current.Y + dy),
            new Point(current.X + dx, current.Y),
            new Point(current.X, current.Y + dy),
        };

        foreach (var c in candidates)
        {
            var s = ScreenAt(c);
            if (s != null) return (c, s);
        }

        return (current, ScreenAt(current));
    }
}

using Velopack;
using Velopack.Sources;

namespace Mouse2PC.Core;

// Auto-update via GitHub Releases (Velopack). Só atua quando o app foi
// instalado pelo Setup gerado no CI; rodando o exe avulso, não faz nada.
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/CMFA14/Mouse2PC";

    public static async Task<string?> CheckAndPromptAsync(Form owner)
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!mgr.IsInstalled) return null;

            var update = await mgr.CheckForUpdatesAsync();
            if (update == null) return null;

            var version = update.TargetFullRelease.Version.ToString();
            var answer = MessageBox.Show(owner,
                $"Nova versão disponível: {version}\n" +
                $"(instalada: {mgr.CurrentVersion})\n\n" +
                "Atualizar agora? O Mouse2PC será reiniciado.",
                "Mouse2PC – Atualização",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return version;

            await mgr.DownloadUpdatesAsync(update);
            mgr.ApplyUpdatesAndRestart(update);
            return version;
        }
        catch
        {
            // Sem rede ou GitHub fora do ar: segue a vida, tenta na próxima.
            return null;
        }
    }
}

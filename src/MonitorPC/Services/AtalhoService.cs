using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D = System.Drawing;

namespace MonitorPC.Services;

public sealed class AtalhoDaIlha
{
    public string Nome { get; set; } = "";
    public string Destino { get; set; } = "";
}

public static class AtalhoService
{
    public const int Maximo = 6;

    public const string IaOutra = "outro";

    public sealed record Ia(string Id, string Nome, string Endereco);

    public static readonly Ia[] Ias =
    {
        new("chatgpt", "ChatGPT", "https://chatgpt.com/"),
        new("claude", "Claude", "https://claude.ai/new"),
        new("gemini", "Gemini", "https://gemini.google.com/app"),
        new("copilot", "Copilot", "https://copilot.microsoft.com/"),
        new("perplexity", "Perplexity", "https://www.perplexity.ai/"),
        new("deepseek", "DeepSeek", "https://chat.deepseek.com/")
    };

    public static (string Nome, string Destino)? IaEscolhida(AppConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.IlhaIa)) return null;

        if (cfg.IlhaIa == IaOutra)
        {
            var destino = cfg.IlhaIaEndereco?.Trim();
            if (string.IsNullOrEmpty(destino)) return null;

            if (!File.Exists(destino) && !destino.Contains(':') && NormalizarSite(destino) is { } site)
                destino = site;

            var nome = Uri.TryCreate(destino, UriKind.Absolute, out var uri) && !uri.IsFile && !string.IsNullOrEmpty(uri.Host)
                ? Host(uri)
                : Path.GetFileNameWithoutExtension(destino);
            return (string.IsNullOrEmpty(nome) ? "chat" : nome, destino);
        }

        var ia = Ias.FirstOrDefault(i => i.Id == cfg.IlhaIa);
        return ia is null ? null : (ia.Nome, ia.Endereco);
    }

    public const string MarcaDaIa = "kinfo:ia";

    public static void AbrirIa(AppConfig cfg)
    {
        if (IaEscolhida(cfg) is not { } ia) return;

        if (cfg.IlhaIaPreferirApp && cfg.IlhaIa != IaOutra && AppInstalado(ia.Nome) is { } app &&
            Abrir(@"shell:AppsFolder\" + app))
            return;

        Abrir(ia.Destino);
    }

    private static string[] NomesDoApp(string ia) => ia switch
    {
        "Copilot" => new[] { "Copilot", "Microsoft Copilot" },
        "ChatGPT" => new[] { "ChatGPT", "ChatGPT Desktop" },
        _ => new[] { ia }
    };

    public static string? AppInstalado(string ia)
    {
        var apps = AppsDoMenuIniciar();
        foreach (var nome in NomesDoApp(ia))
            if (apps.TryGetValue(nome, out var id))
                return id;

        return null;
    }

    private static Dictionary<string, string>? _apps;
    private static DateTime _appsLidosEm;

    private static Dictionary<string, string> AppsDoMenuIniciar()
    {
        if (_apps is not null && DateTime.UtcNow - _appsLidosEm < TimeSpan.FromMinutes(10)) return _apps;

        var apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is { } tipo)
            {
                dynamic shell = Activator.CreateInstance(tipo)!;
                dynamic? pasta = shell.NameSpace("shell:AppsFolder");
                if (pasta is not null)
                {
                    foreach (var item in pasta.Items())
                    {
                        string? nome = item.Name;
                        string? id = item.Path;
                        if (!string.IsNullOrWhiteSpace(nome) && !string.IsNullOrWhiteSpace(id))
                            apps.TryAdd(nome.Trim(), id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Atalho: apps do menu Iniciar", ex);
        }

        _apps = apps;
        _appsLidosEm = DateTime.UtcNow;
        return apps;
    }

    public static bool Abrir(string destino)
    {
        if (string.IsNullOrWhiteSpace(destino)) return false;

        try
        {
            if (AtualizacaoService.Elevado())
            {
                var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                info.ArgumentList.Add(destino);
                Process.Start(info)?.Dispose();
            }
            else
            {
                Process.Start(new ProcessStartInfo(destino) { UseShellExecute = true })?.Dispose();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("Atalho: abrir", ex);
            return false;
        }
    }

    public static string? NormalizarSite(string? texto)
    {
        var t = texto?.Trim();
        if (string.IsNullOrEmpty(t) || t.Contains(' ')) return null;

        if (!t.Contains("://")) t = "https://" + t;

        return Uri.TryCreate(t, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               uri.Host.Contains('.')
            ? uri.ToString()
            : null;
    }

    public static string Host(Uri uri) =>
        uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

    public static string NomeDoApp(string caminho)
    {
        try
        {
            if (caminho.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var descricao = FileVersionInfo.GetVersionInfo(caminho).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(descricao) && descricao.Length <= 24) return descricao;
            }
        }
        catch
        {
        }

        return Path.GetFileNameWithoutExtension(caminho);
    }

    private static readonly Dictionary<string, ImageSource?> Icones = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Icone(string destino)
    {
        if (Icones.TryGetValue(destino, out var guardado)) return guardado;

        ImageSource? imagem = null;
        try
        {
            if (File.Exists(destino))
            {
                using var icone = D.Icon.ExtractAssociatedIcon(destino);
                if (icone is not null)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(icone.Handle, Int32Rect.Empty,
                                                                  BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    imagem = bmp;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Atalho: ícone", ex);
        }

        Icones[destino] = imagem;
        return imagem;
    }
}

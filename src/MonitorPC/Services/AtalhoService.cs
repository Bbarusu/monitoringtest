using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using D = System.Drawing;

namespace MonitorPC.Services;

/// <summary>Um atalho da Kisland: um app (caminho do .exe ou .lnk) ou um site.</summary>
public sealed class AtalhoDaIlha
{
    public string Nome { get; set; } = "";
    public string Destino { get; set; } = "";
}

/// <summary>
/// Os atalhos da Kisland: o chat de IA do anel e os apps e sites que a pessoa
/// escolheu.
///
/// O KInfo costuma rodar como administrador, por causa dos sensores. Abrir um
/// site ou um app direto dele faria o navegador e o app rodarem como
/// administrador tambem — o Chrome reclama, arrastar arquivos para eles deixa
/// de funcionar, e e mais acesso do que um chat precisa. Por isso, elevado, o
/// pedido passa pelo Explorer, que abre tudo como o usuario comum.
/// </summary>
public static class AtalhoService
{
    /// <summary>Quantos atalhos cabem na Kisland aberta sem apertar.</summary>
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

    /// <summary>O chat escolhido para o anel, ou nulo se nenhum (ou "outro" sem endereco).</summary>
    public static (string Nome, string Destino)? IaEscolhida(AppConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.IlhaIa)) return null;

        if (cfg.IlhaIa == IaOutra)
        {
            var destino = cfg.IlhaIaEndereco?.Trim();
            if (string.IsNullOrEmpty(destino)) return null;

            // "meuchat.com" vira site; um caminho de app ou um endereco com
            // protocolo proprio ("claude:") fica como esta.
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

    /// <summary>
    /// Marca, no lugar do destino, do atalho do chat de IA na Kisland aberta:
    /// quem decide se abre o app ou o site e <see cref="AbrirIa"/>, na hora.
    /// </summary>
    public const string MarcaDaIa = "kinfo:ia";

    /// <summary>
    /// Abre o chat de IA escolhido: o app, se ele estiver instalado no PC (e a
    /// pessoa preferir assim), ou o site. Um endereco proprio ("Outro") abre
    /// como esta.
    /// </summary>
    public static void AbrirIa(AppConfig cfg)
    {
        if (IaEscolhida(cfg) is not { } ia) return;

        if (cfg.IlhaIaPreferirApp && cfg.IlhaIa != IaOutra && AppInstalado(ia.Nome) is { } app &&
            Abrir(@"shell:AppsFolder\" + app))
            return;

        Abrir(ia.Destino);
    }

    /// <summary>Nomes com que cada chat aparece no menu Iniciar.</summary>
    private static string[] NomesDoApp(string ia) => ia switch
    {
        "Copilot" => new[] { "Copilot", "Microsoft Copilot" },
        "ChatGPT" => new[] { "ChatGPT", "ChatGPT Desktop" },
        _ => new[] { ia }
    };

    /// <summary>
    /// O id do app do chat no menu Iniciar, ou nulo se nao estiver instalado.
    ///
    /// A busca e pela lista de apps do proprio Windows (shell:AppsFolder, a
    /// mesma do menu Iniciar), e nao por pastas de instalacao: cada chat se
    /// instala de um jeito (Microsoft Store, instalador proprio, app da web
    /// instalado pelo Edge ou pelo Chrome), e todos aparecem ali com o nome
    /// e um id que o Explorer sabe abrir.
    /// </summary>
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

    /// <summary>
    /// Nome e id de cada app do menu Iniciar. Ler leva uma fracao de segundo;
    /// a lista fica guardada por 10 minutos, o bastante para um app instalado
    /// agora aparecer logo.
    /// </summary>
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

    /// <summary>
    /// "youtube.com" vira "https://youtube.com". Devolve nulo para o que nao
    /// parece endereco nenhum.
    /// </summary>
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

    /// <summary>"youtube.com" de "https://www.youtube.com/...".</summary>
    public static string Host(Uri uri) =>
        uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

    /// <summary>Nome para um app escolhido: a descricao do executavel, ou o nome do arquivo.</summary>
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
            // Sem descricao legivel: fica o nome do arquivo.
        }

        return Path.GetFileNameWithoutExtension(caminho);
    }

    private static readonly Dictionary<string, ImageSource?> Icones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O icone do app, como o Windows mostra. Site nao tem: quem desenha e a
    /// Kisland, com a inicial do nome.
    /// </summary>
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

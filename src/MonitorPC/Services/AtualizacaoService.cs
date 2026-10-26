using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MonitorPC.Comum;

namespace MonitorPC.Services;

/// <summary>
/// O que a release publicada diz sobre a versao mais nova.
///
/// Tudo opcional de proposito: um manifesto malformado ou de uma versao futura
/// do formato nao pode derrubar a verificacao. Campo que falta vira "nao sei",
/// e nao sei resulta em nao oferecer nada.
/// </summary>
public sealed record Manifesto
{
    public string? Versao { get; init; }
    public string? Url { get; init; }
    public string? Sha256 { get; init; }
    public string? Notas { get; init; }
    public string? Data { get; init; }

    /// <summary>Tem o minimo para valer uma oferta de atualizacao.</summary>
    public bool Utilizavel =>
        !string.IsNullOrWhiteSpace(Versao)
        && !string.IsNullOrWhiteSpace(Url)
        && Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Sha256)
        && Sha256.Trim().Length == 64;
}

/// <summary>
/// Procura, baixa e confere uma versao nova do KInfo.
///
/// O instalador do projeto ja sabe atualizar: ele fecha o app aberto,
/// sobrescreve os arquivos e registra a tarefa de novo. Entao atualizar aqui e
/// baixar o instalador novo e executa-lo — nao ha um segundo mecanismo de
/// atualizacao para dar manutencao.
///
/// O que importa neste servico e o que ele se recusa a fazer:
///
/// Nao executa nada sem conferir o SHA-256 declarado no manifesto. O projeto
/// nao assina o executavel — assinatura exige certificado pago e pessoa
/// juridica —, entao o hash publicado e a unica integridade real que temos. Sem
/// ele, baixar e rodar seria executar o que viesse pelo fio.
///
/// Nao instala sozinho. A verificacao pode ser automatica; executar o
/// instalador e sempre uma decisao de quem esta na frente do PC, porque custa
/// uma confirmacao do Windows e fecha o app no meio.
///
/// Nao reclama quando falha sozinho. Uma verificacao automatica que nao
/// completou e silenciosa: avisar todo dia que a internet caiu transforma o
/// aviso em ruido, e aviso que virou ruido nao e lido quando importa.
/// </summary>
public sealed class AtualizacaoService
{
    /// <summary>
    /// A release publica do projeto. O codigo-fonte fica em repositorio
    /// privado; aqui so moram o instalador, este manifesto e o catalogo de
    /// especificacoes. O caminho <c>latest</c> sempre aponta para a release mais
    /// nova, entao publicar nao exige mexer no app.
    /// </summary>
    private const string UrlDoManifesto =
        "https://github.com/Bbarusu/kinfo-releases/releases/latest/download/atualizacao.json";

    private static readonly JsonSerializerOptions Formato = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>A versao que esta rodando agora.</summary>
    public static string VersaoAtual => Versao.Texto;

    /// <summary>
    /// Devolve o manifesto quando ha versao mais nova, ou <c>null</c> quando
    /// nao ha, quando nao deu para saber, ou quando o manifesto nao serve.
    /// </summary>
    public async Task<Manifesto?> ProcurarAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"KInfo/{Versao.Texto}");

            var texto = await http.GetStringAsync(UrlDoManifesto, ct);
            var manifesto = JsonSerializer.Deserialize<Manifesto>(texto, Formato);

            if (manifesto is null || !manifesto.Utilizavel)
            {
                Log.Nota("Manifesto de atualização ausente ou incompleto; nada a oferecer.");
                return null;
            }

            return Versao.EhMaisNova(manifesto.Versao) ? manifesto : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException)
        {
            // Sem rede, sem release publicada ainda, ou JSON quebrado. Nenhuma
            // das tres e defeito nosso nem merece incomodar o usuario.
            return null;
        }
        catch (Exception ex)
        {
            Log.Erro("Procurar atualização", ex);
            return null;
        }
    }

    /// <summary>
    /// Baixa o instalador e confere o hash. Devolve o caminho do arquivo
    /// conferido, ou <c>null</c> com o motivo em <paramref name="problema"/>.
    ///
    /// O arquivo so passa a existir no destino final depois de conferido: baixa
    /// para um nome temporario e renomeia. Sem isso, um download interrompido
    /// deixaria meio instalador com nome de instalador pronto.
    /// </summary>
    public async Task<string?> BaixarAsync(
        Manifesto manifesto,
        IProgress<double>? progresso,
        CancellationToken ct,
        Action<string>? problema = null)
    {
        var pasta = Path.Combine(Path.GetTempPath(), "KInfo-atualizacao");
        var parcial = Path.Combine(pasta, $"KInfo-Setup-{manifesto.Versao}.parcial");
        var destino = Path.Combine(pasta, $"KInfo-Setup-{manifesto.Versao}.exe");

        try
        {
            Directory.CreateDirectory(pasta);
            if (File.Exists(destino)) File.Delete(destino);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"KInfo/{Versao.Texto}");

            using (var resposta = await http.GetAsync(
                       manifesto.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resposta.EnsureSuccessStatusCode();

                var total = resposta.Content.Headers.ContentLength;

                await using var origem = await resposta.Content.ReadAsStreamAsync(ct);
                await using var arquivo = File.Create(parcial);

                var buffer = new byte[81920];
                long lidos = 0;
                int n;

                while ((n = await origem.ReadAsync(buffer, ct)) > 0)
                {
                    await arquivo.WriteAsync(buffer.AsMemory(0, n), ct);
                    lidos += n;

                    if (total is > 0) progresso?.Report((double)lidos / total.Value);
                }
            }

            var hash = await CalcularSha256Async(parcial, ct);

            if (!string.Equals(hash, manifesto.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Log.Nota($"Atualização recusada: hash não confere. " +
                         $"Esperado {manifesto.Sha256!.Trim()}, obtido {hash}.");

                problema?.Invoke("O arquivo baixado não corresponde ao que a versão " +
                                 "publicada declara. A atualização foi recusada e o " +
                                 "arquivo apagado. Tente de novo mais tarde.");
                return null;
            }

            File.Move(parcial, destino);
            return destino;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Erro("Baixar atualização", ex);

            problema?.Invoke("Não foi possível baixar a atualização. " +
                             "O motivo ficou no erro.log.");
            return null;
        }
        finally
        {
            try { if (File.Exists(parcial)) File.Delete(parcial); } catch { }
        }
    }

    private static async Task<string> CalcularSha256Async(string arquivo, CancellationToken ct)
    {
        await using var fluxo = File.OpenRead(arquivo);
        var bytes = await SHA256.HashDataAsync(fluxo, ct);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Executa o instalador conferido.
    ///
    /// <c>UseShellExecute</c> de proposito: o instalador pede elevacao pelo
    /// manifesto dele, e so por essa via o Windows mostra a confirmacao em vez
    /// de recusar a execucao.
    /// </summary>
    public static bool Executar(string instalador)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = instalador,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("Executar atualização", ex);
            return false;
        }
    }
}

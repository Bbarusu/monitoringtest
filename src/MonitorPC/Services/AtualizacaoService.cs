using System.IO;
using System.Net;
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
/// O que uma procura por atualizacao descobriu.
///
/// Tres estados, e nao dois, porque "nao ha nada novo" e "nao deu para saber"
/// sao respostas diferentes e a primeira versao disto as juntava num <c>null</c>.
/// O resultado era o app dizer "você já tem a versão mais recente" quando na
/// verdade nem tinha conseguido perguntar — afirmacao confiante sobre o que nao
/// se sabe, que e o pior defeito que um aviso pode ter.
/// </summary>
public sealed record Procura(Manifesto? Manifesto, bool Verificou, string? Motivo)
{
    /// <summary>Ha versao nova.</summary>
    public static Procura Encontrou(Manifesto manifesto) => new(manifesto, true, null);

    /// <summary>Perguntou e nao ha nada novo.</summary>
    public static Procura JaEstaAtualizado() => new(null, true, null);

    /// <summary>Nao deu para perguntar. <paramref name="motivo"/> completa
    /// "Não foi possível verificar: …".</summary>
    public static Procura Impedida(string motivo) => new(null, false, motivo);

    public bool TemNovidade => Manifesto is not null;
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
    public async Task<Procura> ProcurarAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"KInfo/{Versao.Texto}");

            using var resposta = await http.GetAsync(UrlDoManifesto, ct);

            // 404 e o caso de nao haver release publicada, que e diferente de
            // estar atualizado e diferente de falha de rede. Quem testa precisa
            // dessa distincao: foi exatamente ela que faltou da primeira vez.
            if (resposta.StatusCode == HttpStatusCode.NotFound)
                return Procura.Impedida(
                    "não há nenhuma versão publicada no repositório de releases");

            if (!resposta.IsSuccessStatusCode)
                return Procura.Impedida(
                    $"o servidor respondeu {(int)resposta.StatusCode}");

            var texto = await resposta.Content.ReadAsStringAsync(ct);
            var manifesto = JsonSerializer.Deserialize<Manifesto>(texto, Formato);

            if (manifesto is null || !manifesto.Utilizavel)
            {
                Log.Nota("Manifesto de atualização publicado está incompleto.");
                return Procura.Impedida("o arquivo de atualização publicado está incompleto");
            }

            return Versao.EhMaisNova(manifesto.Versao)
                ? Procura.Encontrou(manifesto)
                : Procura.JaEstaAtualizado();
        }
        catch (HttpRequestException)
        {
            return Procura.Impedida("não foi possível alcançar a internet agora");
        }
        catch (TaskCanceledException)
        {
            return Procura.Impedida("a consulta demorou demais e foi interrompida");
        }
        catch (JsonException)
        {
            return Procura.Impedida("o arquivo de atualização publicado não pôde ser lido");
        }
        catch (Exception ex)
        {
            Log.Erro("Procurar atualização", ex);
            return Procura.Impedida("houve um erro na verificação; o motivo ficou no erro.log");
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

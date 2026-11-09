using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MonitorPC.Comum;

namespace MonitorPC.Services;

public sealed record Manifesto
{
    public string? Versao { get; init; }
    public string? Url { get; init; }
    public string? Sha256 { get; init; }
    public string? Notas { get; init; }
    public string? Data { get; init; }

    public bool Utilizavel =>
        !string.IsNullOrWhiteSpace(Versao)
        && !string.IsNullOrWhiteSpace(Url)
        && Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Sha256)
        && Sha256.Trim().Length == 64;
}

public sealed record Procura(Manifesto? Manifesto, bool Verificou, string? Motivo)
{
    public static Procura Encontrou(Manifesto manifesto) => new(manifesto, true, null);

    public static Procura JaEstaAtualizado() => new(null, true, null);

    public static Procura Impedida(string motivo) => new(null, false, motivo);

    public bool TemNovidade => Manifesto is not null;
}

public sealed class AtualizacaoService
{
    private const string UrlDoManifesto =
        "https://github.com/Bbarusu/kinfo-releases/releases/latest/download/atualizacao.json";

    private static readonly JsonSerializerOptions Formato = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string VersaoAtual => Versao.Texto;

    public async Task<Procura> ProcurarAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"KInfo/{Versao.Texto}");

            using var resposta = await http.GetAsync(UrlDoManifesto, ct);

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

    public async Task<string?> BaixarAsync(
        Manifesto manifesto,
        IProgress<double>? progresso,
        CancellationToken ct,
        Action<string>? problema = null)
    {
        var pasta = Path.Combine(Path.GetTempPath(), "KInfo-atualizacao");

        var marca = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var parcial = Path.Combine(pasta, $"KInfo-Setup-{manifesto.Versao}-{marca}.parcial");
        var destino = Path.Combine(pasta, $"KInfo-Setup-{manifesto.Versao}-{marca}.exe");

        try
        {
            Directory.CreateDirectory(pasta);
            LimparSobras(pasta, manifesto.Versao);

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

    private static void LimparSobras(string pasta, string? versao)
    {
        try
        {
            foreach (var velho in Directory.GetFiles(pasta, "KInfo-Setup-*"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(velho) < TimeSpan.FromHours(1)
                        && versao is not null
                        && Path.GetFileName(velho).Contains(versao, StringComparison.Ordinal))
                        continue;

                    File.Delete(velho);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static async Task<string> CalcularSha256Async(string arquivo, CancellationToken ct)
    {
        await using var fluxo = File.OpenRead(arquivo);
        var bytes = await SHA256.HashDataAsync(fluxo, ct);
        return Convert.ToHexString(bytes);
    }

    public static bool Executar(string instalador)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = instalador,
                Arguments = ArgumentosDoInstalador(),
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

    private static string ArgumentosDoInstalador()
    {
        var argumentos = $"--atualizar --tema={(ThemeService.Claro ? "claro" : "escuro")}";

        try
        {
            if (System.Windows.Application.Current?.Resources["Acento"] is System.Windows.Media.SolidColorBrush acento)
                argumentos += $" --acento=#{acento.Color.R:X2}{acento.Color.G:X2}{acento.Color.B:X2}";
        }
        catch
        {
        }

        return argumentos;
    }

    public static bool Elevado()
    {
        try
        {
            using var identidade = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identidade)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

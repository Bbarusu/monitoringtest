using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MonitorPC.Services;

public sealed class Especificacao
{
    public string Nome { get; set; } = "";

    public double TdpW { get; set; }

    public double PotenciaMaxW { get; set; }

    public double TjMaxC { get; set; }
    public double ClockBaseMhz { get; set; }
    public double ClockBoostMhz { get; set; }
}

public sealed class CatalogoService
{
    private const string Url =
        "https://github.com/Bbarusu/kinfo-releases/releases/latest/download/catalogo.json";

    private static readonly TimeSpan Validade = TimeSpan.FromDays(7);

    private readonly AppConfig _cfg;
    private Catalogo _dados = new();

    public string Versao => _dados.Versao;

    public CatalogoService(AppConfig cfg)
    {
        _cfg = cfg;
        Carregar();
    }

    public Especificacao? Cpu(string? nome) => Casar(_dados.Cpus, nome);

    public Especificacao? Gpu(string? nome) => Casar(_dados.Gpus, nome);

    private static Especificacao? Casar(List<Especificacao> linhas, string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return null;

        var alvo = Normalizar(nome);

        return linhas
            .Where(e => alvo.Contains(Normalizar(e.Nome), StringComparison.Ordinal))
            .OrderByDescending(e => e.Nome.Length)
            .FirstOrDefault();
    }

    private static string Normalizar(string texto)
    {
        var t = texto.ToLowerInvariant();

        foreach (var lixo in new[]
                 {
                     "(tm)", "(r)", "(c)", "amd ", "nvidia ", "intel ", "geforce ",
                     "radeon ", "core ", "processor", "cpu", "graphics", "with ",
                     "eight-core", "6-core", "8-core", "12-core", "16-core"
                 })
            t = t.Replace(lixo, " ");

        return Regex.Replace(t, "[^a-z0-9]", "");
    }

    private void Carregar()
    {
        _dados = Ler(Embutido()) ?? new Catalogo();

        try
        {
            if (File.Exists(CaminhoCache))
            {
                var baixado = Ler(File.ReadAllText(CaminhoCache));

                if (baixado is not null &&
                    string.CompareOrdinal(baixado.Versao, _dados.Versao) >= 0)
                    _dados = baixado;
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Ler cache do catálogo", ex);
        }

        if (_cfg.AtualizarCatalogoOnline && Vencido()) _ = AtualizarAsync();
    }

    private bool Vencido()
    {
        try
        {
            return !File.Exists(CaminhoCache)
                   || DateTime.UtcNow - File.GetLastWriteTimeUtc(CaminhoCache) > Validade;
        }
        catch
        {
            return true;
        }
    }

    public async Task<bool> AtualizarAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MonitorPC/1.0");

            var texto = await http.GetStringAsync(Url);
            var baixado = Ler(texto);

            if (baixado is null || baixado.Cpus.Count + baixado.Gpus.Count == 0)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(CaminhoCache)!);
            await File.WriteAllTextAsync(CaminhoCache, texto);

            _dados = baixado;
            Atualizado?.Invoke();
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Erro("Atualizar catálogo", ex);
            return false;
        }
    }

    public event Action? Atualizado;

    private static Catalogo? Ler(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<Catalogo>(json, Opcoes);
        }
        catch (Exception ex)
        {
            Log.Erro("Interpretar catálogo", ex);
            return null;
        }
    }

    private static string? Embutido()
    {
        try
        {
            using var fluxo = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("catalogo.json");

            if (fluxo is null) return null;

            using var leitor = new StreamReader(fluxo);
            return leitor.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Erro("Ler catálogo embutido", ex);
            return null;
        }
    }

    private static string CaminhoCache => Path.Combine(
        Path.GetDirectoryName(AppConfig.Caminho)!, "catalogo.json");

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class Catalogo
    {
        public string Versao { get; set; } = "";
        public List<Especificacao> Cpus { get; set; } = new();
        public List<Especificacao> Gpus { get; set; } = new();
    }
}

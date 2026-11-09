using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed class AppConfig
{
    public const int OpacidadeMinima = 15;

    public double WattsPorModuloRam { get; set; } = 3.0;
    public double WattsPorDisco { get; set; } = 2.0;
    public double WattsPorVentoinha { get; set; } = 1.5;
    public double WattsPorLed { get; set; } = 1.5;
    public double WattsBombaWaterCooler { get; set; } = 6.0;
    public double WattsPlacaMae { get; set; } = 12.0;
    public double WattsOutros { get; set; } = 10.0;

    public int? ModulosRamManual { get; set; }
    public int? DiscosManual { get; set; }
    public int? VentoinhasManual { get; set; }

    public int PecasComLed { get; set; }
    public bool TemWaterCooler { get; set; }

    public int PotenciaMaximaGpuW { get; set; }

    public double PotenciaGpuMedida { get; set; }

    public double RotacaoGpuMedida { get; set; }

    public int RotacaoMaximaGpuRpm { get; set; }

    public int PotenciaMaximaCpuW { get; set; }

    public double PotenciaCpuMedida { get; set; }

    public Dictionary<string, CurvaDeVentoinha> CurvasDeVentoinha { get; set; } = new();

    public Dictionary<string, double> LeituraMaximaPorDisco { get; set; } = new();
    public Dictionary<string, double> EscritaMaximaPorDisco { get; set; } = new();

    public bool MostrarVentoinhasNaCpu { get; set; } = true;

    public List<string> VentoinhasOcultas { get; set; } = new();

    public bool AtualizarCatalogoOnline { get; set; } = true;

    public bool VerificarAtualizacoes { get; set; } = true;

    public DateTime? UltimaVerificacaoDeAtualizacao { get; set; }

    public double EficienciaFonte { get; set; } = 0.85;
    public int CapacidadeFonteW { get; set; } = 550;

    public double LimiteCpu { get; set; } = 75;
    public double LimiteGpu { get; set; } = 80;
    public double LimiteDisco { get; set; } = 55;
    public bool NotificarNoWindows { get; set; } = true;
    public bool SugerirEconomico { get; set; } = true;

    public Tema Tema { get; set; } = Tema.Escuro;
    public bool AcompanharCorDoSistema { get; set; } = true;
    public string CorDestaque { get; set; } = "#3B82F6";
    public bool Transparencia { get; set; } = true;

    [JsonIgnore] public bool TransparenciaAtiva => Transparencia && Tema != Tema.Preto;
    public int Opacidade { get; set; } = 85;

    public int? IlhaOpacidade { get; set; }

    public bool IlhaAtiva { get; set; }

    public bool IlhaMusica { get; set; } = true;
    public bool IlhaCpu { get; set; } = true;
    public bool IlhaGpu { get; set; } = true;
    public bool IlhaVolume { get; set; } = true;
    public bool IlhaRam { get; set; }
    public bool IlhaCpuUso { get; set; }
    public bool IlhaGpuUso { get; set; }
    public bool IlhaDisco { get; set; }
    public bool IlhaPotencia { get; set; }
    public bool IlhaRelogio { get; set; }

    public bool IlhaData { get; set; }

    public string? IlhaIa { get; set; }

    public string? IlhaIaEndereco { get; set; }

    public List<AtalhoDaIlha> IlhaAtalhos { get; set; } = new();

    public bool GuiaKislandVisto { get; set; }

    public int GuiaKislandVersaoVista { get; set; }

    public int NovidadesVersaoVista { get; set; }

    public bool IlhaFps { get; set; }

    public bool IlhaFicarEmTelaCheia { get; set; }

    public bool IlhaJogoCpu { get; set; } = true;
    public bool IlhaJogoCpuUso { get; set; }
    public bool IlhaJogoGpu { get; set; } = true;
    public bool IlhaJogoGpuUso { get; set; }
    public bool IlhaJogoRam { get; set; }
    public bool IlhaJogoPotencia { get; set; }
    public bool IlhaJogoRelogio { get; set; }
    public bool IlhaJogoMusica { get; set; }

    public bool IlhaPreta { get; set; }

    public bool IlhaProtegerTela { get; set; } = true;

    public bool IlhaMexerPixels { get; set; } = true;

    public int IlhaEscurecerMinutos { get; set; } = 5;

    public bool IlhaEsconderParado { get; set; } = true;

    public int IlhaEscala { get; set; } = 85;

    public bool IlhaAbertaPlayer { get; set; } = true;

    public bool IlhaAbertaTemperaturas { get; set; } = true;
    public bool IlhaAbertaSistema { get; set; } = true;

    public bool? IlhaAbertaCpu { get; set; }
    public bool? IlhaAbertaGpu { get; set; }
    public bool? IlhaAbertaRam { get; set; }
    public bool? IlhaAbertaDisco { get; set; }
    public bool? IlhaAbertaEnergia { get; set; }

    [JsonIgnore] public bool AbertaCpu => IlhaAbertaCpu ?? IlhaAbertaTemperaturas;
    [JsonIgnore] public bool AbertaGpu => IlhaAbertaGpu ?? IlhaAbertaTemperaturas;
    [JsonIgnore] public bool AbertaRam => IlhaAbertaRam ?? IlhaAbertaSistema;
    [JsonIgnore] public bool AbertaDisco => IlhaAbertaDisco ?? IlhaAbertaSistema;
    [JsonIgnore] public bool AbertaEnergia => IlhaAbertaEnergia ?? IlhaAbertaSistema;
    public bool IlhaAbertaVolume { get; set; } = true;
    public bool IlhaAbertaPerfis { get; set; } = true;
    public bool IlhaAbertaCalendario { get; set; } = true;
    public bool IlhaAbertaAtalhos { get; set; } = true;

    public bool IlhaSaidasDeSom { get; set; } = true;

    public bool IlhaMixer { get; set; } = true;

    public bool IlhaMixerAberto { get; set; }

    public bool IlhaBandeja { get; set; } = true;

    public bool Kdrop { get; set; }

    public string? KdropNome { get; set; }

    public string? KdropPasta { get; set; }

    public List<AparelhoConfiavel> KdropConfiaveis { get; set; } = new();

    public bool IlhaRodaMudaVolume { get; set; } = true;

    public bool IlhaBotaoPersonalizacao { get; set; } = true;

    public string? IlhaMonitor { get; set; }

    public int IlhaEsperaParaAbrirMs { get; set; } = 5000;

    public bool IlhaIaPreferirApp { get; set; } = true;

    public bool IlhaAbrirAoPassarMouse { get; set; } = true;

    public bool IlhaEsconderEmTelaCheia { get; set; } = true;

    public bool IlhaAvisoDeVolume { get; set; } = true;

    public bool IlhaAvisoDeFaixa { get; set; } = true;

    public bool FecharAoPerderFoco { get; set; }

    public int IntervaloPainelAberto { get; set; } = 1000;
    public int IntervaloPainelFechado { get; set; } = 3000;
    public int IntervaloSensoresLentos { get; set; } = 30000;

    public double LimiteLogMbs { get; set; } = 10;

    [JsonIgnore] public bool PosicaoLivre { get; set; }
    [JsonIgnore] public double PosicaoX { get; set; }
    [JsonIgnore] public double PosicaoY { get; set; }

    public bool ConfiguracaoInicialFeita { get; set; }

    [JsonIgnore]
    public static string Caminho => Path.Combine(Pasta, "config.json");

    [JsonIgnore]
    public static string Pasta { get; } = PrepararPasta();

    private static string PrepararPasta()
    {
        var dados = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var nova = Path.Combine(dados, "KInfo");

        try
        {
            var antiga = Path.Combine(dados, "MonitorPC");

            if (Directory.Exists(antiga) && !Directory.Exists(nova))
                Directory.Move(antiga, nova);
        }
        catch
        {
        }

        return nova;
    }

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppConfig Carregar()
    {
        try
        {
            if (File.Exists(Caminho))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Caminho)) ?? new AppConfig();
        }
        catch
        {
        }

        var cfg = new AppConfig();
        cfg.Salvar();
        return cfg;
    }

    public void Salvar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            File.WriteAllText(Caminho, JsonSerializer.Serialize(this, Opcoes));
        }
        catch
        {
        }
    }
}

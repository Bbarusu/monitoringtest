using LibreHardwareMonitor.Hardware;

namespace MonitorPC.Services;

public sealed record ResultadoCalibracao(
    string Chave, string Nome, string Peca, double RpmMaximo, string? Problema)
{
    public bool Deu => Problema is null && RpmMaximo > 0;
}

public sealed class CalibracaoService
{
    private static readonly TimeSpan Aceleracao = TimeSpan.FromSeconds(7);

    private static readonly TimeSpan Medicao = TimeSpan.FromSeconds(4);

    private readonly SensorService _sensores;

    private readonly List<(IControl Comando, ControlMode Modo, float Valor)> _mexidos = new();

    private readonly object _trava = new();

    public bool Rodando { get; private set; }

    public CalibracaoService(SensorService sensores) => _sensores = sensores;

    public async Task<List<ResultadoCalibracao>> CalibrarAsync(
        IProgress<string> aviso, CancellationToken ct)
    {
        if (Rodando) return new List<ResultadoCalibracao>();

        Rodando = true;

        using var rapido = _sensores.ModoRapido();

        try
        {
            return await Task.Run(() => Executar(aviso, ct), ct);
        }
        finally
        {
            Restaurar();
            Rodando = false;
        }
    }

    private List<ResultadoCalibracao> Executar(IProgress<string> aviso, CancellationToken ct)
    {
        var canais = _sensores.CanaisDeVentoinha();
        var alvos = canais.Where(c => c.Comandavel).ToList();

        var resultados = canais
            .Where(c => !c.Comandavel)
            .Select(c => new ResultadoCalibracao(c.Chave, c.Nome, c.Peca, 0,
                c.Gpu
                    ? "A placa de vídeo não aceita comando de ventoinha pelo app."
                    : "A placa não deixa o app comandar esta ventoinha."))
            .ToList();

        if (alvos.Count == 0)
        {
            aviso.Report("Nenhuma ventoinha aceita comando pelo app.");
            return resultados;
        }

        aviso.Report(alvos.Count == 1 ? "Acelerando a ventoinha a 100%…" : $"Acelerando {alvos.Count} ventoinhas a 100%…");

        foreach (var c in alvos)
        {
            try
            {
                var comando = c.Comando!;
                lock (_trava) _mexidos.Add((comando, comando.ControlMode, comando.SoftwareValue));

                var cheio = comando.MaxSoftwareValue > 0 ? comando.MaxSoftwareValue : 100;
                comando.SetSoftware(cheio);
            }
            catch (Exception ex)
            {
                Log.Erro($"Acelerar {c.Nome}", ex);
                resultados.Add(new ResultadoCalibracao(c.Chave, c.Nome, c.Peca, 0,
                    "A placa recusou o comando."));
            }
        }

        if (Esperar(Aceleracao, ct, aviso, "Esperando estabilizar")) return resultados;

        var picos = new Dictionary<string, double>();
        var fim = DateTime.UtcNow + Medicao;

        while (DateTime.UtcNow < fim && !ct.IsCancellationRequested)
        {
            foreach (var c in _sensores.CanaisDeVentoinha())
            {
                var rpm = c.Rpm;
                if (rpm > picos.GetValueOrDefault(c.Chave)) picos[c.Chave] = rpm;
            }

            aviso.Report($"Medindo… {(fim - DateTime.UtcNow).TotalSeconds:F0} s");
            Thread.Sleep(400);
        }

        foreach (var c in alvos)
        {
            if (resultados.Any(r => r.Chave == c.Chave)) continue;

            var rpm = picos.GetValueOrDefault(c.Chave);

            if (rpm <= 0)
            {
                resultados.Add(new ResultadoCalibracao(c.Chave, c.Nome, c.Peca, 0,
                    c.Gpu
                        ? "Não girou. Placas de vídeo seguram a ventoinha parada " +
                          "abaixo de uma temperatura, por firmware, mesmo com o " +
                          "PWM no máximo."
                        : "Não girou: o conector pode estar vazio ou a ventoinha " +
                          "não informa rotação."));
                continue;
            }

            _sensores.Especificacoes.DefinirCalibrado(c.Chave, rpm);
            resultados.Add(new ResultadoCalibracao(c.Chave, c.Nome, c.Peca, rpm, null));
        }

        return resultados;
    }

    private static bool Esperar(TimeSpan quanto, CancellationToken ct,
        IProgress<string> aviso, string rotulo)
    {
        var fim = DateTime.UtcNow + quanto;

        while (DateTime.UtcNow < fim)
        {
            if (ct.IsCancellationRequested) return true;
            aviso.Report($"{rotulo}… {(fim - DateTime.UtcNow).TotalSeconds:F0} s");
            Thread.Sleep(400);
        }

        return false;
    }

    public void Restaurar()
    {
        lock (_trava)
        {
            foreach (var (comando, modo, valor) in _mexidos)
            {
                try
                {
                    if (modo == ControlMode.Software) comando.SetSoftware(valor);
                    else comando.SetDefault();
                }
                catch (Exception ex)
                {
                    Log.Erro("Restaurar ventoinha", ex);
                }
            }

            _mexidos.Clear();
        }
    }
}

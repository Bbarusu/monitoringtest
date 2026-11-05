using LibreHardwareMonitor.Hardware;

namespace MonitorPC.Services;

/// <summary>O que a calibracao descobriu de uma ventoinha.</summary>
public sealed record ResultadoCalibracao(
    string Chave, string Nome, string Peca, double RpmMaximo, string? Problema)
{
    public bool Deu => Problema is null && RpmMaximo > 0;
}

/// <summary>
/// Mede a rotacao maxima real de cada ventoinha, forcando o PWM a 100% por
/// alguns segundos e lendo o taquimetro.
///
/// Existe porque nenhuma outra fonte responde essa pergunta. A placa-mae nao
/// publica o teto de um conector: ela so informa a rotacao de agora. Uma tabela
/// de internet sabe o modelo da placa de video, nao qual ventilador alguem
/// espetou no gabinete. E projetar pela curva rotacao x PWM so vale quando a
/// ventoinha ja passou perto do maximo sozinha, o que num PC bem refrigerado
/// pode nao acontecer nunca.
///
/// Forcar e medir responde em doze segundos, e a resposta vem do proprio
/// hardware — que e o ponto do app.
///
/// Todas sobem juntas, de proposito: uma de cada vez multiplicaria o tempo e o
/// barulho pelo numero de conectores, sem medir nada melhor. A restauracao e
/// garantida por <c>finally</c> e repetida ao fechar o app, porque deixar um
/// ventilador presa em 100% seria o pior defeito possivel deste servico.
/// </summary>
public sealed class CalibracaoService
{
    /// <summary>Tempo ate o ventilador parar de acelerar.</summary>
    private static readonly TimeSpan Aceleracao = TimeSpan.FromSeconds(7);

    /// <summary>Janela de medicao depois de estabilizar.</summary>
    private static readonly TimeSpan Medicao = TimeSpan.FromSeconds(4);

    private readonly SensorService _sensores;

    /// <summary>
    /// O que foi mexido e como estava antes. Fica no servico, e nao na pilha,
    /// para o fechamento do app conseguir desfazer mesmo se a tela sumir no meio.
    /// </summary>
    private readonly List<(IControl Comando, ControlMode Modo, float Valor)> _mexidos = new();

    /// <summary>
    /// Fechar o app no meio de uma calibracao faz o descarte do servico de
    /// sensores restaurar enquanto a medicao ainda esta acelerando ventoinha.
    /// Sao os dois unicos pontos que mexem na lista, e eles nao podem se cruzar.
    /// </summary>
    private readonly object _trava = new();

    public bool Rodando { get; private set; }

    public CalibracaoService(SensorService sensores) => _sensores = sensores;

    public async Task<List<ResultadoCalibracao>> CalibrarAsync(
        IProgress<string> aviso, CancellationToken ct)
    {
        if (Rodando) return new List<ResultadoCalibracao>();

        Rodando = true;

        // Sem o intervalo curto, a medicao perderia o pico: o laco normal com o
        // painel fechado le de 3 em 3 segundos.
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

        // 1. Acelerar todas.
        aviso.Report(alvos.Count == 1 ? "Acelerando a ventoinha a 100%…" : $"Acelerando {alvos.Count} ventoinhas a 100%…");

        foreach (var c in alvos)
        {
            try
            {
                var comando = c.Comando!;
                lock (_trava) _mexidos.Add((comando, comando.ControlMode, comando.SoftwareValue));

                // O proprio canal diz ate onde aceita ir. Quase sempre e 100,
                // mas fixar 100 numa placa que aceita menos faria o comando ser
                // recusado inteiro, e a ventoinha nem sairia do lugar.
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

        // 2. Medir, ficando com o maior de cada uma ao longo da janela: a
        //    rotacao oscila alguns rpm mesmo com o PWM fixo.
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

        // 3. Guardar. Uma ventoinha que ficou em zero com o PWM em 100% nao foi
        //    medida: ou nao esta ligada no conector, ou nao tem taquimetro.
        foreach (var c in alvos)
        {
            if (resultados.Any(r => r.Chave == c.Chave)) continue;

            var rpm = picos.GetValueOrDefault(c.Chave);

            if (rpm <= 0)
            {
                // Numa placa de video o motivo costuma ser outro: o firmware
                // segura a ventoinha parada abaixo de certa temperatura, e o
                // PWM no maximo nao vence essa decisao.
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

    /// <summary>Espera informando o que falta. Devolve true se foi cancelada.</summary>
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

    /// <summary>
    /// Devolve cada ventoinha ao que era antes. Chamada pelo <c>finally</c> da
    /// calibracao e de novo no fechamento do app: repetir nao custa nada, e
    /// deixar de chamar custaria caro.
    /// </summary>
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

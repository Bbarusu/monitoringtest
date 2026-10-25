using System.IO;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace MonitorPC.Services;

/// <summary>
/// Por que os sensores de baixo nivel nao estao respondendo.
///
/// Nasceu de um defeito concreto: o aviso do painel dizia sempre a mesma coisa
/// — "abra como administrador" — inclusive para quem ja tinha aberto como
/// administrador. Em duas maquinas diferentes das nossas o app nao leu
/// temperatura nenhuma e seguiu pedindo exatamente o que o usuario acabara de
/// fazer. O recado estava errado e, pior, escondia a informacao que resolveria.
///
/// A leitura de CPU e placa-mae depende de um driver de kernel que a biblioteca
/// de sensores registra e carrega sozinha. Esse carregamento pode parar em
/// lugares bem diferentes — falta de privilegio, servico que nao chega a ser
/// criado, driver criado mas recusado na hora de subir, antivirus — e cada um
/// desses pede uma acao diferente de quem esta na frente da maquina. Aqui o app
/// olha o que consegue olhar e conta.
///
/// O que esta classe NAO faz: afirmar a causa quando nao tem evidencia. Ela
/// prefere dizer "o servico nem existe" a inventar o motivo.
/// </summary>
public static class Diagnostico
{
    /// <summary>
    /// Nomes que a familia de drivers usada pela biblioteca de sensores
    /// costuma registrar. Mais de um porque o nome acompanha a versao do
    /// driver, e errar o nome aqui faria o app relatar "servico inexistente"
    /// numa maquina onde ele existe — um diagnostico errado seria pior que
    /// nenhum.
    /// </summary>
    private static readonly string[] NomesDoServico =
    {
        "WinRing0_1_2_0", "WinRing0_1_3_0", "WinRing0x64", "WinRing0"
    };

    // ------------------------------------------------------------------
    // Estado da maquina. Nada disso muda durante a sessao, entao roda uma vez.

    /// <summary>O processo tem privilegio de administrador.</summary>
    public static bool Elevado { get; } = ConferirElevacao();

    /// <summary>
    /// A Integridade de Memoria esta configurada como ligada neste Windows.
    ///
    /// Ela recusa o carregamento de drivers de uma lista mantida pela
    /// Microsoft, e a familia usada para ler MSR da CPU esta nessa lista. Em
    /// maquina de fabrica costuma vir ligada, o que explicaria o app funcionar
    /// num PC montado em casa e nao num notebook novo — mas explicaria apenas:
    /// confirmar e o motivo deste relatorio existir.
    /// </summary>
    public static bool IntegridadeDeMemoria { get; } = LerDword(
        @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
        "Enabled") == 1;

    /// <summary>Inicializacao segura ativa. Tambem pesa na aceitacao do driver.</summary>
    public static bool SecureBoot { get; } = LerDword(
        @"SYSTEM\CurrentControlSet\Control\SecureBoot\State",
        "UEFISecureBootEnabled") == 1;

    /// <summary>
    /// A tarefa agendada que o instalador cria para o app subir elevado sem
    /// pedir UAC toda vez.
    ///
    /// Entra no diagnostico porque e a outra metade do relato que originou esta
    /// classe: antes de "nao le os sensores" veio "o instalador nao colocou em
    /// administrador". Sao defeitos diferentes, e sem olhar nao da para saber se
    /// a tarefa nao existe ou se existe e nao serviu.
    /// </summary>
    public static bool TarefaDeElevacao { get; } = AcharTarefa();

    private static bool AcharTarefa()
    {
        try
        {
            // A entrada no registro e legivel sem elevacao; o arquivo em
            // System32\Tasks nao e. Por isso o registro vem primeiro.
            using var chave = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\KInfo");

            if (chave is not null) return true;
        }
        catch
        {
            // Segue para a conferencia por arquivo.
        }

        try
        {
            return File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks", "KInfo"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// O nome do servico do driver que existe na maquina, ou <c>null</c>.
    ///
    /// E a separacao mais util do diagnostico: se o servico nao existe, a
    /// criacao falhou (privilegio, politica); se existe e ainda assim nao ha
    /// leitura, ele foi criado e recusado na hora de subir — caso tipico de
    /// assinatura barrada.
    /// </summary>
    public static string? ServicoDoDriver { get; } = AcharServico();

    // ------------------------------------------------------------------

    private static bool ConferirElevacao()
    {
        try
        {
            using var identidade = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identidade).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Erro("Conferir elevação", ex);
            return false;
        }
    }

    private static int? LerDword(string caminho, string valor)
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(caminho);
            return chave?.GetValue(valor) as int?;
        }
        catch
        {
            // Sem a chave nao da para afirmar nada, e afirmar errado seria pior
            // que calar: quem le trata o nulo como "nao sei".
            return null;
        }
    }

    private static string? AcharServico()
    {
        foreach (var nome in NomesDoServico)
        {
            try
            {
                using var chave = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{nome}");

                if (chave is not null) return nome;
            }
            catch
            {
                // Ler servico do registro pode ser negado; seguir tentando os
                // outros nomes custa nada.
            }
        }

        return null;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// O recado da faixa de aviso do painel. Muda conforme o que foi
    /// descoberto, para ninguem ficar repetindo uma tentativa que ja falhou.
    /// </summary>
    public static string PorQueFaltamSensores()
    {
        if (!Elevado)
            return "Temperatura de CPU e placa-mãe indisponíveis. O driver que lê " +
                   "esses valores só carrega com privilégio de administrador — pelo " +
                   "atalho instalado isso acontece sozinho.";

        // Daqui para baixo o app JA esta como administrador. Mandar elevar de
        // novo seria o defeito que originou esta classe.
        var texto = new StringBuilder(
            "Temperatura de CPU e placa-mãe indisponíveis mesmo com privilégio de " +
            "administrador, então o problema não é permissão: o driver de sensores " +
            "não subiu. ");

        texto.Append(ServicoDoDriver is null
            ? "O serviço dele não chegou a ser criado nesta máquina. "
            : "O serviço dele existe, mas o Windows recusou carregá-lo. ");

        if (IntegridadeDeMemoria)
            texto.Append("A Integridade de Memória está ligada aqui, e ela bloqueia " +
                         "essa família de driver — fica em Segurança do Windows → " +
                         "Segurança do dispositivo → Isolamento de núcleo. É uma " +
                         "proteção real do sistema: desligá-la para ver temperaturas " +
                         "é uma troca, não um conserto. ");
        else
            texto.Append("Um antivírus pode ter posto o driver em quarentena, ou este " +
                         "modelo não expõe esses sensores — notebook com controlador " +
                         "próprio costuma não expor. ");

        texto.Append("O botão abaixo salva um relatório com o que foi encontrado.");

        return texto.ToString();
    }

    /// <summary>Versao curta, para o rodapé do painel.</summary>
    public static string ResumoCurto() => !Elevado
        ? "Sem acesso aos sensores de baixo nível"
        : ServicoDoDriver is null
            ? "Driver de sensores não foi registrado"
            : "Driver de sensores recusado pelo Windows";

    // ------------------------------------------------------------------

    /// <summary>
    /// Escreve um relatorio do que o app encontrou e devolve o caminho.
    ///
    /// Inclui o relatorio da propria biblioteca de sensores, que e onde aparece
    /// o erro exato do carregamento do driver — a informacao que nenhuma
    /// verificacao externa consegue adivinhar. E a diferenca entre o usuario
    /// relatar "nao le os sensores" e relatar o motivo.
    /// </summary>
    public static string Salvar(Computer? pc)
    {
        var caminho = Path.Combine(AppConfig.Pasta, "diagnostico.txt");
        var t = new StringBuilder();

        t.AppendLine("KInfo — diagnóstico dos sensores");
        t.AppendLine($"Gerado em {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        t.AppendLine();
        t.AppendLine("Este arquivo serve para descobrir por que a temperatura de CPU e");
        t.AppendLine("placa-mãe não aparece. Pode ser enviado como está: não contém");
        t.AppendLine("nada além de informação de hardware e de configuração do Windows.");
        t.AppendLine();

        t.AppendLine("--- Máquina ---");
        t.AppendLine($"Windows            : {Environment.OSVersion.Version}");
        t.AppendLine($"64 bits            : {Environment.Is64BitOperatingSystem}");
        t.AppendLine($"Processadores      : {Environment.ProcessorCount}");
        t.AppendLine();

        t.AppendLine("--- Por que o driver pode não ter subido ---");
        t.AppendLine($"Rodando como admin : {Sim(Elevado)}");
        t.AppendLine($"Serviço do driver  : {ServicoDoDriver ?? "não existe"}");
        t.AppendLine($"Integridade de Mem.: {SimTalvez(IntegridadeDeMemoria)}");
        t.AppendLine($"Inicialização segura: {SimTalvez(SecureBoot)}");
        t.AppendLine($"Tarefa de elevação : {(TarefaDeElevacao ? "existe" : "não existe")}");
        t.AppendLine($"Pasta do app       : {AppContext.BaseDirectory}");
        t.AppendLine($"Pasta de dados     : {AppConfig.Pasta}");
        t.AppendLine();

        t.AppendLine("--- Leitura da biblioteca de sensores ---");

        if (pc is null)
        {
            t.AppendLine("A biblioteca de sensores não foi aberta.");
        }
        else
        {
            // O que ela enxergou. Nas maquinas com defeito, placa de video,
            // memoria e disco aparecem e processador e placa-mae nao: a lista
            // mostra isso sem depender de interpretacao.
            t.AppendLine("Hardware detectado:");

            try
            {
                foreach (var h in pc.Hardware)
                    t.AppendLine($"  [{h.HardwareType}] {h.Name} · {h.Sensors.Count()} sensor(es)");

                if (!pc.Hardware.Any()) t.AppendLine("  (nenhum)");
            }
            catch (Exception ex)
            {
                t.AppendLine($"  não foi possível listar: {ex.Message}");
            }

            t.AppendLine();
            t.AppendLine(RelatorioDaBiblioteca(pc));
        }

        t.AppendLine();
        t.AppendLine("--- Últimos erros registrados ---");

        try
        {
            t.AppendLine(File.Exists(Log.Caminho)
                ? UltimasLinhas(Log.Caminho, 120)
                : "Nenhum erro registrado.");
        }
        catch (Exception ex)
        {
            t.AppendLine($"Não foi possível ler o registro: {ex.Message}");
        }

        Directory.CreateDirectory(AppConfig.Pasta);
        File.WriteAllText(caminho, t.ToString(), Encoding.UTF8);

        return caminho;
    }

    /// <summary>
    /// O relatorio interno da biblioteca de sensores, que e onde aparece o erro
    /// exato do carregamento do driver — a informacao que nenhuma verificacao
    /// nossa consegue adivinhar.
    ///
    /// Buscado por reflexao de proposito. O relatorio completo fica num metodo
    /// do objeto <c>Computer</c>, mas a assinatura dele varia entre versoes da
    /// biblioteca, e um diagnostico nao pode ser a peca que impede o projeto de
    /// compilar: se o metodo nao estiver ali, cai para o relatorio de cada
    /// hardware, que cobre quase tanto.
    /// </summary>
    private static string RelatorioDaBiblioteca(Computer pc)
    {
        try
        {
            var metodo = pc.GetType().GetMethod("GetReport", Type.EmptyTypes);

            if (metodo?.Invoke(pc, null) is string relatorio && relatorio.Length > 0)
                return relatorio;
        }
        catch (Exception ex)
        {
            Log.Erro("Relatório da biblioteca", ex);
        }

        var t = new StringBuilder("Relatório por componente:");
        t.AppendLine();

        foreach (var h in pc.Hardware)
        {
            try
            {
                t.AppendLine(h.GetReport());
            }
            catch (Exception ex)
            {
                t.AppendLine($"  {h.Name}: {ex.Message}");
            }
        }

        return t.ToString();
    }

    /// <summary>
    /// O registro de erros cresce a cada sessao, e o diagnostico so precisa do
    /// fim dele: anexar meses de historico atrapalharia quem for ler.
    /// </summary>
    private static string UltimasLinhas(string arquivo, int quantas)
    {
        var linhas = File.ReadAllLines(arquivo);
        return string.Join(Environment.NewLine, linhas.Skip(Math.Max(0, linhas.Length - quantas)));
    }

    private static string Sim(bool v) => v ? "sim" : "não";

    /// <summary>
    /// Separa "li e esta desligado" de "nao consegui ler": no segundo caso
    /// escrever "não" seria afirmar uma coisa que o app nao verificou.
    /// </summary>
    private static string SimTalvez(bool v) => v ? "ligada" : "desligada ou não informada";
}

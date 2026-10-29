using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace MonitorPC.Setup;

/// <summary>O que o usuario escolheu nas caixas da tela.</summary>
public sealed record OpcoesDeInstalacao(
    /// <summary>
    /// Registra a tarefa que da privilegio elevado ao app. Sem ela o app roda
    /// como usuario comum e perde temperatura de CPU e placa-mae — a escolha
    /// aparece na tela de instalacao justamente para nao ser uma descoberta
    /// depois de tudo instalado.
    /// </summary>
    bool AcessoCompletoAosSensores,

    /// <summary>
    /// Baixa e instala o PawnIO, o driver que da acesso aos sensores de baixo
    /// nivel.
    ///
    /// E a dependencia de verdade do app, e ela nao viaja aqui dentro: a
    /// LibreHardwareMonitorLib 0.9.6 nao embute mais driver nenhum, e sem o
    /// PawnIO nao ha temperatura de CPU nem chip de placa-mae, com ou sem
    /// privilegio de administrador.
    ///
    /// Baixar em vez de embutir e escolha deliberada. O PawnIO e GPL-2.0:
    /// carrega-lo pela interface de IOCTL, que e o que a biblioteca faz, esta
    /// coberto pela excecao de licenca dele, mas redistribuir o instalador
    /// dentro do nosso seria redistribuicao de GPL, com a obrigacao de
    /// acompanhar o codigo-fonte. Baixando da origem oficial na hora, quem
    /// distribui continua sendo o autor dele.
    /// </summary>
    bool InstalarPawnIo,

    bool AtalhoNaAreaDeTrabalho,
    bool IniciarComWindows,
    bool ExcluirDoDefender);

/// <summary>
/// O trabalho do instalador: copiar o app, deixar os sensores acessiveis e
/// registrar a desinstalacao.
///
/// O ponto central e a tarefa agendada. O app precisa de privilegio elevado
/// para o driver de sensores carregar — sem ele, temperatura de CPU e
/// placa-mae voltam zeradas. Pedir elevacao pelo manifesto resolveria, mas
/// cobraria um aviso do Windows toda vez que o app abrisse. Uma tarefa com
/// privilegio mais alto registrada na instalacao, quando o usuario ja
/// autorizou uma vez, deixa o app subir elevado sem perguntar de novo.
/// </summary>
public static class Instalacao
{
    public const string Nome = "KInfo";
    public const string NomeDaTarefa = "KInfo";
    private const string ChaveDeDesinstalacao =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KInfo";

    public static string PastaPadrao => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Nome);

    private static string Executavel(string pasta) => Path.Combine(pasta, "KInfo.exe");
    private static string Desinstalador(string pasta) => Path.Combine(pasta, "Desinstalar.exe");

    // ------------------------------------------------------------------

    /// <summary>
    /// Instala, e devolve o que não deu certo sem impedir a instalação.
    ///
    /// A separação importa: a tarefa dos sensores não existir é motivo para
    /// parar e dizer, porque sem ela o app perde metade do que faz. Já o
    /// desinstalador não ter sido copiado é um incômodo, e derrubar tudo por
    /// causa dele deixaria o usuário sem nada.
    /// </summary>
    public static async Task<List<string>> Instalar(
        string pasta, OpcoesDeInstalacao opcoes, IProgress<string> aviso, bool atualizacao = false)
    {
        var ressalvas = new List<string>();

        await Task.Run(() =>
        {
            aviso.Report("Fechando o app, se estiver aberto…");
            FecharAppAberto();

            aviso.Report("Limpando a instalação antiga, se houver…");
            LimparNomeAntigo();

            aviso.Report("Copiando os arquivos…");
            Directory.CreateDirectory(pasta);
            ExtrairPayload(pasta);

            aviso.Report("Preparando a desinstalação…");
            if (!InstalarDesinstalador(pasta))
                ressalvas.Add("Não foi possível copiar o desinstalador. " +
                              "Para remover o app depois, rode este instalador de novo " +
                              "com o argumento --desinstalar.");

            if (opcoes.ExcluirDoDefender)
            {
                aviso.Report("Pedindo exceção ao Windows Defender…");

                if (!ExcluirDoDefender(pasta))
                    ressalvas.Add("O Windows Defender não aceitou a exceção. Se o app " +
                                  "parar de abrir, procure a biblioteca de sensores no " +
                                  "Histórico de Proteção.");
            }

            if (opcoes.InstalarPawnIo)
            {
                var queixa = InstalarPawnIo(aviso);
                if (queixa is not null) ressalvas.Add(queixa);
            }

            if (opcoes.AcessoCompletoAosSensores)
            {
                aviso.Report("Registrando o acesso aos sensores…");
                CriarTarefa(pasta, opcoes.IniciarComWindows);
            }
            else
            {
                // Sem tarefa, o app nao tenta se elevar: ele verifica que ela
                // nao existe e segue como usuario comum.
                aviso.Report("Pulando o acesso elevado, como você escolheu…");
                Executar("schtasks.exe", $"/Delete /TN \"{NomeDaTarefa}\" /F");
            }

            aviso.Report("Criando os atalhos…");
            CriarAtalhos(pasta, opcoes);

            aviso.Report("Registrando no Windows…");
            RegistrarDesinstalacao(pasta, opcoes);

            // Pede ao app que a primeira abertura mostre o painel. Sem isto,
            // clicar em "Abrir o Monitor" só põe um ícone na bandeja e parece
            // que nada aconteceu. Na atualização não: o app volta como estava,
            // na bandeja, e a tela do instalador já disse que deu certo.
            if (!atualizacao) PedirParaAbrirOPainel();
        });

        return ressalvas;
    }

    /// <summary>
    /// Põe uma cópia deste instalador na pasta do app, para ele ser também o
    /// desinstalador — ele já sabe desfazer tudo que fez, e assim não há um
    /// segundo programa para manter em dia.
    ///
    /// A cópia direta por cima falha com acesso negado quando já existe um
    /// Desinstalar.exe ali: o Windows não deixa sobrescrever um executável.
    /// Mas deixa renomear, inclusive um que esteja rodando. Então o antigo sai
    /// da frente por renomeação e a cópia cai no lugar vazio.
    /// </summary>
    private static bool InstalarDesinstalador(string pasta)
    {
        var origem = Environment.ProcessPath;
        var destino = Desinstalador(pasta);

        if (origem is null) return false;

        // Rodando de dentro da própria pasta de instalação, origem e destino
        // são o mesmo arquivo e não há nada a fazer.
        if (string.Equals(origem, destino, StringComparison.OrdinalIgnoreCase)) return true;

        for (var tentativa = 1; tentativa <= 3; tentativa++)
        {
            try
            {
                if (File.Exists(destino))
                {
                    var velho = $"{destino}.{DateTime.Now:HHmmssfff}.antigo";
                    File.Move(destino, velho);

                    // Some agora se der; senão fica para a desinstalação, que
                    // apaga a pasta inteira.
                    try { File.Delete(velho); } catch { }
                }

                File.Copy(origem, destino);
                return true;
            }
            catch (Exception ex)
            {
                Anotar($"copiar o desinstalador, tentativa {tentativa}: {ex.Message}");
                Thread.Sleep(500);
            }
        }

        return false;
    }

    public static async Task Desinstalar(IProgress<string> aviso)
    {
        await Task.Run(() =>
        {
            var pasta = PastaInstalada() ?? PastaPadrao;

            aviso.Report("Fechando o app…");
            FecharAppAberto();

            aviso.Report("Removendo a tarefa dos sensores…");
            Executar("schtasks.exe", $"/Delete /TN \"{NomeDaTarefa}\" /F");

            aviso.Report("Removendo os atalhos…");
            foreach (var atalho in new[]
                     { AtalhoDoMenu(), AtalhoDaAreaDeTrabalho(), AtalhoDeInicializacao() })
                try { if (File.Exists(atalho)) File.Delete(atalho); } catch { }

            aviso.Report("Removendo a exceção do Defender…");
            PowerShell($"Remove-MpPreference -ExclusionPath '{pasta.Replace("'", "''")}'");

            aviso.Report("Apagando os arquivos…");
            ApagarPasta(pasta);

            try { Registry.LocalMachine.DeleteSubKeyTree(ChaveDeDesinstalacao, false); } catch { }
        });
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// O app publicado viaja zipado dentro deste executavel. Um arquivo so para
    /// baixar, sem pasta de apoio que possa se perder no caminho.
    /// </summary>
    private static void ExtrairPayload(string pasta)
    {
        using var fluxo = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.zip")
            ?? throw new InvalidOperationException(
                "Este instalador foi compilado sem o app dentro. Rode o publicar.cmd.");

        using var zip = new ZipArchive(fluxo, ZipArchiveMode.Read);
        zip.ExtractToDirectory(pasta, overwriteFiles: true);
    }

    /// <summary>
    /// Uma instancia antiga segurando os arquivos faria a copia falhar. Pede
    /// para fechar pela janela primeiro; so insiste com quem nao responde.
    /// </summary>
    /// <summary>
    /// O app se chamava Monitor de PC. Sem isto, instalar a versao nova
    /// deixaria para tras a tarefa, os atalhos e a pasta do nome antigo — e
    /// duas copias disputando o mesmo driver dao leitura zerada nas duas.
    ///
    /// A pasta de dados em AppData nao entra aqui: ela guarda o consumo e as
    /// curvas de ventoinha, e quem a renomeia, levando tudo junto, e o proprio
    /// app na primeira abertura.
    /// </summary>
    private static void LimparNomeAntigo()
    {
        const string antigo = "Monitor de PC";

        Executar("schtasks.exe", $"/Delete /TN \"{antigo}\" /F");

        try { Registry.LocalMachine.DeleteSubKeyTree(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MonitorDePC", false); }
        catch { }

        foreach (var pasta in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.Startup)
                 })
            try
            {
                var atalho = Path.Combine(pasta, $"{antigo}.lnk");
                if (File.Exists(atalho)) File.Delete(atalho);
            }
            catch { }

        try
        {
            var instalacao = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), antigo);

            if (Directory.Exists(instalacao)) Directory.Delete(instalacao, recursive: true);
        }
        catch { }
    }

    private static void FecharAppAberto()
    {
        // Os dois nomes: uma copia do app antigo ainda aberta seguraria os
        // arquivos e faria a copia falhar.
        foreach (var p in Process.GetProcessesByName("KInfo")
                     .Concat(Process.GetProcessesByName("MonitorPC")))
        {
            try
            {
                if (p.CloseMainWindow() && p.WaitForExit(4000)) continue;
                p.Kill();
                p.WaitForExit(4000);
            }
            catch
            {
                // Pode ter saido sozinho entre a lista e o pedido.
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A tarefa nao tem horario: ela existe para ser chamada sob demanda, pelo
    /// proprio app quando percebe que abriu sem privilegio. Com "iniciar com o
    /// Windows", ganha tambem um gatilho de logon.
    /// </summary>
    private static void CriarTarefa(string pasta, bool noLogon)
    {
        var usuario = $"{Environment.UserDomainName}\\{Environment.UserName}";

        var gatilho = noLogon
            ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{Seguro(usuario)}</UserId>" +
              "<Delay>PT20S</Delay></LogonTrigger>"
            : "";

        // O conteudo e a ordem de <Settings> seguem o que o proprio Agendador
        // de Tarefas exporta num arquivo versao 1.2, e nada alem disso.
        // UseUnifiedSchedulingEngine e DisallowStartOnRemoteAppSession ficaram
        // de fora: so existem a partir do esquema 1.3, e num arquivo 1.2 eles
        // derrubam a validacao inteira — o schtasks recusa falando em valor mal
        // formatado, sem dizer qual.
        //
        // ExecutionTimeLimit em PT0S e o unico desvio deliberado: o padrao sao
        // 72 horas, e ao fim delas o Windows encerraria um app que existe para
        // ficar aberto.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Abre o {Nome} com acesso aos sensores de hardware.</Description>
              </RegistrationInfo>
              <Triggers>{gatilho}</Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Seguro(usuario)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Seguro(Executavel(pasta))}</Command>
                  <Arguments>--elevado</Arguments>
                  <WorkingDirectory>{Seguro(pasta)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        // O schtasks le o XML em UTF-16; em UTF-8 ele recusa o arquivo.
        var temporario = Path.Combine(Path.GetTempPath(), "monitorpc-tarefa.xml");
        File.WriteAllText(temporario, xml, Encoding.Unicode);

        try
        {
            var (codigo, saida) = Executar(
                "schtasks.exe", $"/Create /TN \"{NomeDaTarefa}\" /XML \"{temporario}\" /F");

            // Conferir em vez de confiar. Sem isto, uma tarefa recusada passa
            // por instalação concluída e o app abre sem sensores, que foi
            // exatamente o que aconteceu na primeira versão deste instalador.
            if (codigo != 0 || Executar("schtasks.exe", $"/Query /TN \"{NomeDaTarefa}\"").Codigo != 0)
                throw new InvalidOperationException(
                    "O Windows recusou a tarefa que dá acesso aos sensores.\n\n" +
                    (saida.Length > 0 ? saida + "\n\n" : "") +
                    $"O passo a passo está em {Registro}. Dá para instalar sem ela, " +
                    "desmarcando \"Rodar como administrador\" — o app funciona, " +
                    "mas sem temperatura de CPU, tensões e ventoinhas.");
        }
        finally
        {
            try { File.Delete(temporario); } catch { }
        }
    }

    /// <summary>Escapa o que vai para dentro do XML da tarefa.</summary>
    private static string Seguro(string texto) => texto
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ------------------------------------------------------------------

    private static string AtalhoDoMenu() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        "Programs", $"{Nome}.lnk");

    private static string AtalhoDaAreaDeTrabalho() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Nome}.lnk");

    private static string AtalhoDeInicializacao() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), $"{Nome}.lnk");

    private static void CriarAtalhos(string pasta, OpcoesDeInstalacao opcoes)
    {
        // Os atalhos apontam direto para o executavel, e nao para o schtasks.
        // O app sobe sozinho pela tarefa quando percebe que abriu sem
        // privilegio, o que evita a janela preta do console piscando.
        CriarAtalho(AtalhoDoMenu(), Executavel(pasta), pasta);

        if (opcoes.AtalhoNaAreaDeTrabalho)
            CriarAtalho(AtalhoDaAreaDeTrabalho(), Executavel(pasta), pasta);

        // Com acesso elevado, quem abre junto com a sessao e o gatilho de logon
        // da tarefa. Sem ela, resta a pasta de inicializacao do Windows.
        try { if (File.Exists(AtalhoDeInicializacao())) File.Delete(AtalhoDeInicializacao()); }
        catch { }

        if (opcoes.IniciarComWindows && !opcoes.AcessoCompletoAosSensores)
            CriarAtalho(AtalhoDeInicializacao(), Executavel(pasta), pasta);
    }

    /// <summary>
    /// Marca que a proxima abertura deve mostrar o painel. E um arquivo vazio
    /// na pasta de configuracao do app, que o proprio app apaga ao ler.
    /// </summary>
    private static void PedirParaAbrirOPainel()
    {
        try
        {
            var pasta = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KInfo");

            Directory.CreateDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "abrir-apos-instalar"), "");
        }
        catch
        {
            // Um detalhe de conforto: se falhar, o app so abre na bandeja.
        }
    }

    private static void CriarAtalho(string atalho, string alvo, string pasta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(atalho)!);

        // Criar um .lnk em C# puro exigiria interop com IShellLink. O objeto COM
        // do Windows Script Host faz o mesmo em tres linhas e ja existe em
        // qualquer Windows.
        //
        // Tudo numa linha so, separado por ponto e virgula: um script com
        // quebras de linha passado em -Command vira um argumento com novas
        // linhas no meio, e o tratamento disso varia.
        PowerShell(
            $"$a = (New-Object -ComObject WScript.Shell).CreateShortcut('{Aspas(atalho)}'); " +
            $"$a.TargetPath = '{Aspas(alvo)}'; " +
            $"$a.WorkingDirectory = '{Aspas(pasta)}'; " +
            $"$a.Description = '{Nome}'; " +
            "$a.Save()");
    }

    private static string Aspas(string caminho) => caminho.Replace("'", "''");

    // ------------------------------------------------------------------

    /// <summary>
    /// O Defender marca a familia de drivers WinRing0 como ferramenta de risco,
    /// e e esse driver que a biblioteca de sensores carrega para ler a CPU e a
    /// placa-mae. Sem a excecao, ele pode pôr a biblioteca em quarentena e o
    /// app para de abrir — sem aviso nenhum que ligue uma coisa a outra.
    /// </summary>
    private static bool ExcluirDoDefender(string pasta) =>
        PowerShell($"Add-MpPreference -ExclusionPath '{Aspas(pasta)}'") == 0;

    private static void RegistrarDesinstalacao(string pasta, OpcoesDeInstalacao opcoes)
    {
        using var chave = Registry.LocalMachine.CreateSubKey(ChaveDeDesinstalacao);
        if (chave is null) return;

        chave.SetValue("DisplayName", Nome);
        // Lido do proprio executavel, e nao escrito a mao: este valor e o que o
        // Painel de Controle mostra e o que um atualizador compara, e uma
        // string literal aqui ficaria para tras na primeira versao nova.
        chave.SetValue("DisplayVersion", Comum.Versao.Texto);
        chave.SetValue("Publisher", "Projeto pessoal");
        chave.SetValue("InstallLocation", pasta);
        chave.SetValue("DisplayIcon", Executavel(pasta));
        chave.SetValue("UninstallString",
            $"\"{Desinstalador(pasta)}\" {App.ArgumentoDeDesinstalacao}");
        chave.SetValue("NoModify", 1, RegistryValueKind.DWord);
        chave.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // As escolhas desta instalação, para a próxima atualização repeti-las
        // sem perguntar de novo. Ficam na mesma chave, que a desinstalação já
        // apaga.
        Gravar(chave, "OpcaoSensores", opcoes.AcessoCompletoAosSensores);
        Gravar(chave, "OpcaoPawnIo", opcoes.InstalarPawnIo);
        Gravar(chave, "OpcaoAtalho", opcoes.AtalhoNaAreaDeTrabalho);
        Gravar(chave, "OpcaoLogon", opcoes.IniciarComWindows);
        Gravar(chave, "OpcaoDefender", opcoes.ExcluirDoDefender);

        static void Gravar(RegistryKey chave, string nome, bool valor) =>
            chave.SetValue(nome, valor ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>Versão que está instalada agora, como o instalador anterior a gravou.</summary>
    public static string? VersaoInstalada()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(ChaveDeDesinstalacao);
            return chave?.GetValue("DisplayVersion") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// As escolhas da instalação que existe, para a atualização repetir.
    /// Nulo quando não há instalação.
    ///
    /// Instalações feitas antes desta versão não gravaram nada. Para essas, o
    /// que a máquina mostra responde: a tarefa existe ou não, o atalho está na
    /// área de trabalho ou não, a exceção do Defender está lá ou não. Só o
    /// PawnIO não dá para deduzir — ausente pode ser recusa —, então ele não é
    /// instalado por dedução: o fim da atualização avisa se faltar.
    /// </summary>
    public static OpcoesDeInstalacao? OpcoesAnteriores()
    {
        var pasta = PastaInstalada();
        if (string.IsNullOrEmpty(pasta)) return null;

        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(ChaveDeDesinstalacao);

            bool? Lido(string nome) => chave?.GetValue(nome) is int v ? v != 0 : null;

            var tarefa = Lido("OpcaoSensores")
                         ?? Executar("schtasks.exe", $"/Query /TN \"{NomeDaTarefa}\"").Codigo == 0;

            var logon = Lido("OpcaoLogon") ?? (tarefa
                ? Executar("schtasks.exe", $"/Query /TN \"{NomeDaTarefa}\" /XML").Saida.Contains("LogonTrigger")
                : File.Exists(AtalhoDeInicializacao()));

            var atalho = Lido("OpcaoAtalho") ?? File.Exists(AtalhoDaAreaDeTrabalho());

            var defender = Lido("OpcaoDefender") ?? PowerShell(
                $"if ((Get-MpPreference).ExclusionPath -contains '{Aspas(pasta)}') {{ exit 0 }} else {{ exit 1 }}") == 0;

            var pawnIo = Lido("OpcaoPawnIo") ?? false;

            return new OpcoesDeInstalacao(tarefa, pawnIo, atalho, logon, defender);
        }
        catch (Exception ex)
        {
            Anotar($"ler as opções anteriores: {ex.Message}");
            return null;
        }
    }

    /// <summary>Para o fim da atualização dizer quando o driver dos sensores sumiu.</summary>
    public static bool DriverPresente() => PawnIoInstalado();

    public static string? PastaInstalada()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(ChaveDeDesinstalacao);
            return chave?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Apaga o que der. O desinstalador esta rodando de dentro da pasta, entao
    /// o proprio executavel nao sai agora: fica marcado para o Windows remover
    /// no proximo boot.
    /// </summary>
    private static void ApagarPasta(string pasta)
    {
        if (!Directory.Exists(pasta)) return;

        foreach (var arquivo in Directory.GetFiles(pasta, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(arquivo, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try { File.Delete(arquivo); } catch { }
        }

        try { Directory.Delete(pasta, recursive: true); } catch { }

        if (Directory.Exists(pasta))
            MarcarParaApagarNoBoot(pasta);
    }

    /// <summary>
    /// Deixa um comando solto que espera o desinstalador sair e entao apaga a
    /// pasta. E o jeito de remover o executavel que esta rodando de dentro dela.
    /// </summary>
    private static void MarcarParaApagarNoBoot(string pasta) => Executar("cmd.exe",
        $"/c start \"\" /min cmd.exe /c \"timeout /t 5 >nul & rd /s /q \"{pasta}\"\"");

    // ------------------------------------------------------------------

    public static bool ComoAdministrador()
    {
        try
        {
            using var identidade = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identidade).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static int PowerShell(string script) => Executar("powershell.exe",
        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"").Codigo;

    /// <summary>Onde cada passo da instalacao deixa registro do que aconteceu.</summary>
    public static string Registro => Path.Combine(Path.GetTempPath(), "KInfo-instalacao.log");

    /// <summary>
    /// Roda um programa e devolve o codigo de saida com o que ele escreveu.
    ///
    /// A versao anterior engolia tudo em silencio, e foi assim que uma tarefa
    /// que nunca chegou a existir passou por instalacao bem-sucedida: o app
    /// abria sem privilegio, sem sensores, e nada no caminho tinha dito nada.
    /// Agora toda chamada vai para o registro, e quem chama decide se a falha
    /// importa.
    /// </summary>
    /// <summary>
    /// A origem oficial do instalador do PawnIO. O caminho <c>latest</c> sempre
    /// aponta para a versao mais nova, entao o instalador nao carrega um numero
    /// de versao que envelhece — e a URL de download direto, que o projeto ja
    /// mudou de lugar uma vez, nao fica cravada aqui.
    /// </summary>
    private const string UrlDoPawnIo =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    /// <summary>
    /// Baixa o PawnIO da origem oficial e instala em silencio. Devolve
    /// <c>null</c> quando deu certo, ou o que dizer ao usuario quando nao.
    ///
    /// Nunca e motivo para derrubar a instalacao: sem o PawnIO o app abre e
    /// mostra placa de video, discos, memoria e uso de CPU, e o painel explica
    /// o que falta e onde pegar. Travar tudo aqui deixaria o usuario sem nada
    /// por causa de uma rede fora do ar.
    /// </summary>
    private static string? InstalarPawnIo(IProgress<string> aviso)
    {
        const string ComoResolver =
            "O app abre e funciona sem ele — placa de vídeo, discos, memória e " +
            "uso de CPU continuam normais —, mas temperatura de CPU e ventoinhas " +
            "da placa-mãe ficam de fora até o PawnIO ser instalado. Baixe em " +
            "pawnio.eu e reabra o app.";

        if (PawnIoInstalado())
        {
            Anotar("PawnIO já estava instalado; nada a fazer.");
            return null;
        }

        var destino = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");

        try
        {
            aviso.Report("Baixando o PawnIO, o driver dos sensores…");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Nome}-Setup/1.0");

                // Sincrono de proposito: isto roda dentro de um Task.Run, sem
                // contexto de UI para travar.
                var bytes = http.GetByteArrayAsync(UrlDoPawnIo).GetAwaiter().GetResult();
                File.WriteAllBytes(destino, bytes);

                Anotar($"PawnIO baixado: {bytes.Length} bytes em {destino}");
            }

            // Nao executar binario baixado sem conferir quem assinou. Nao e
            // validacao de cadeia completa — isso pediria WinVerifyTrust —, mas
            // um arquivo trocado no caminho nao chega aqui assinado pelo autor,
            // e sem assinatura nenhuma nos nao rodamos.
            var assinante = Assinante(destino);

            if (assinante is null)
                return "O PawnIO foi baixado mas não está assinado, então não foi " +
                       "executado. " + ComoResolver;

            if (!assinante.Contains("namazso", StringComparison.OrdinalIgnoreCase))
                return $"O PawnIO foi baixado mas está assinado por \"{assinante}\", " +
                       "e não pelo autor do projeto, então não foi executado. " +
                       ComoResolver;

            Anotar($"PawnIO assinado por: {assinante}");

            aviso.Report("Instalando o PawnIO…");

            // O proprio instalador dele se eleva; -install -silent e o que o
            // projeto documenta para instalacao desacompanhada.
            var (codigo, _) = Executar(destino, "-install -silent");

            // O que decide e o driver estar registrado, nao o codigo de saida.
            // Executar desiste de esperar em 30 s e devolve -1 nesse caso, o
            // que faria uma instalacao lenta porem bem-sucedida ser reportada
            // como falha.
            if (PawnIoInstalado())
            {
                Anotar($"PawnIO instalado (código {codigo}).");
                return null;
            }

            return codigo == 0
                ? "O instalador do PawnIO rodou mas o driver não apareceu no " +
                  "sistema. " + ComoResolver
                : $"O instalador do PawnIO terminou com código {codigo} e o driver " +
                  "não apareceu no sistema. " + ComoResolver;
        }
        catch (Exception ex)
        {
            Anotar($"PawnIO: exceção: {ex.Message}");

            return "Não foi possível baixar o PawnIO — a máquina pode estar sem " +
                   "internet agora. " + ComoResolver;
        }
        finally
        {
            try { if (File.Exists(destino)) File.Delete(destino); } catch { }
        }
    }

    /// <summary>O PawnIO esta registrado como servico neste Windows.</summary>
    private static bool PawnIoInstalado()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\PawnIO");

            return chave is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Quem assinou o executavel, ou <c>null</c> se ele nao tem assinatura.
    /// </summary>
    private static string? Assinante(string arquivo)
    {
        try
        {
            using var certificado = X509Certificate.CreateFromSignedFile(arquivo);
            return certificado.Subject;
        }
        catch
        {
            // Sem assinatura, CreateFromSignedFile lanca. Tratado como "nao
            // assinado", que e o que importa para a decisao de rodar ou nao.
            return null;
        }
    }

    private static (int Codigo, string Saida) Executar(string programa, string argumentos)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = programa,
                Arguments = argumentos,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (p is null) return (-1, "não foi possível iniciar " + programa);

            var saida = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(30000);

            Anotar($"{programa} {argumentos}\n  código {p.ExitCode}" +
                   (saida.Length > 0 ? $"\n  {saida.Replace("\n", "\n  ")}" : ""));

            return (p.ExitCode, saida);
        }
        catch (Exception ex)
        {
            Anotar($"{programa} {argumentos}\n  exceção: {ex.Message}");
            return (-1, ex.Message);
        }
    }

    private static void Anotar(string texto)
    {
        try
        {
            File.AppendAllText(Registro, $"[{DateTime.Now:HH:mm:ss}] {texto}\n");
        }
        catch
        {
            // Sem registro a instalação segue; é diagnóstico, não funcionalidade.
        }
    }
}

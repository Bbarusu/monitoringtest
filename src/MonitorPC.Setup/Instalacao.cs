using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
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
    public const string Nome = "Monitor de PC";
    public const string NomeDaTarefa = "Monitor de PC";
    private const string ChaveDeDesinstalacao =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MonitorDePC";

    public static string PastaPadrao => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Nome);

    private static string Executavel(string pasta) => Path.Combine(pasta, "MonitorPC.exe");
    private static string Desinstalador(string pasta) => Path.Combine(pasta, "Desinstalar.exe");

    // ------------------------------------------------------------------

    public static async Task Instalar(string pasta, OpcoesDeInstalacao opcoes, IProgress<string> aviso)
    {
        await Task.Run(() =>
        {
            aviso.Report("Fechando o app, se estiver aberto…");
            FecharAppAberto();

            aviso.Report("Copiando os arquivos…");
            Directory.CreateDirectory(pasta);
            ExtrairPayload(pasta);

            // O proprio instalador vira o desinstalador: ele ja sabe desfazer
            // tudo que fez, e nao ha segundo programa para manter em dia.
            aviso.Report("Preparando a desinstalação…");
            File.Copy(Environment.ProcessPath!, Desinstalador(pasta), overwrite: true);

            if (opcoes.ExcluirDoDefender)
            {
                aviso.Report("Pedindo exceção ao Windows Defender…");
                ExcluirDoDefender(pasta);
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
            RegistrarDesinstalacao(pasta);

            // Pede ao app que a primeira abertura mostre o painel. Sem isto,
            // clicar em "Abrir o Monitor" só põe um ícone na bandeja e parece
            // que nada aconteceu.
            PedirParaAbrirOPainel();
        });
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
    private static void FecharAppAberto()
    {
        foreach (var p in Process.GetProcessesByName("MonitorPC"))
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
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
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
            Executar("schtasks.exe", $"/Create /TN \"{NomeDaTarefa}\" /XML \"{temporario}\" /F");
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
                "MonitorPC");

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
    private static void ExcluirDoDefender(string pasta) =>
        PowerShell($"Add-MpPreference -ExclusionPath '{Aspas(pasta)}'");

    private static void RegistrarDesinstalacao(string pasta)
    {
        using var chave = Registry.LocalMachine.CreateSubKey(ChaveDeDesinstalacao);
        if (chave is null) return;

        chave.SetValue("DisplayName", Nome);
        chave.SetValue("DisplayVersion", "0.1.0");
        chave.SetValue("Publisher", "Projeto pessoal");
        chave.SetValue("InstallLocation", pasta);
        chave.SetValue("DisplayIcon", Executavel(pasta));
        chave.SetValue("UninstallString",
            $"\"{Desinstalador(pasta)}\" {App.ArgumentoDeDesinstalacao}");
        chave.SetValue("NoModify", 1, RegistryValueKind.DWord);
        chave.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

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

    private static void PowerShell(string script) => Executar("powershell.exe",
        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"");

    private static void Executar(string programa, string argumentos)
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

            p?.WaitForExit(30000);
        }
        catch
        {
            // Uma etapa acessoria que falha — a excecao do Defender, por
            // exemplo — nao pode derrubar a instalacao inteira. A tela mostra
            // no fim o que ficou de fora.
        }
    }
}

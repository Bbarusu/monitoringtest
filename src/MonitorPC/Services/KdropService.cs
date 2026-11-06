using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorPC.Services;

/// <summary>Um aparelho que aceita envios do Kdrop sem perguntar.</summary>
public sealed class AparelhoConfiavel
{
    public string Id { get; set; } = "";
    public string Nome { get; set; } = "";
}

/// <summary>
/// Um aparelho da casa que o Kdrop enxerga agora: outro PC com KInfo (achado
/// pela rede) ou um celular (com a bandeja aberta no navegador).
/// </summary>
/// <param name="Endereco">Para PCs: "http://192.168.0.20:48620/". Celulares nao tem.</param>
public sealed record AparelhoKdrop(string Id, string Nome, bool EhPc, string? Endereco, bool EhNotebook);

/// <summary>
/// Um item de uma bandeja, como o Kdrop mostra para fora: arquivo ou pasta
/// (que viaja como .zip). Tamanho -1 para pasta: somar tudo dentro dela a
/// cada anuncio custaria caro.
/// </summary>
public sealed record ItemKdrop(string Id, string Nome, long Tamanho, bool Pasta);

public enum EstadoDoPedido { Esperando, Aceito, Recusado, Expirado, Concluido }

/// <summary>Um envio chegando: quem manda, o que, e em que pe esta.</summary>
public sealed class PedidoKdrop
{
    public string Id { get; init; } = "";
    public string OrigemId { get; init; } = "";
    public string OrigemNome { get; init; } = "";
    public bool OrigemEhPc { get; init; }
    public List<ArquivoDoPedido> Arquivos { get; init; } = new();
    public DateTime CriadoEm { get; init; } = DateTime.UtcNow;

    public EstadoDoPedido Estado { get; set; }
    public long Recebidos { get; set; }
    public List<string> Salvos { get; } = new();

    /// <summary>
    /// Ultimo sinal de vida do envio: o aceite ou a ultima parte que chegou.
    /// Um pedido aceito so e esquecido depois de 30 minutos parado — um video
    /// grande pode levar mais que isso, mas nunca 30 minutos sem mandar nada.
    /// </summary>
    public DateTime AtividadeEm { get; set; } = DateTime.UtcNow;

    /// <summary>Cada arquivo do pedido, na ordem declarada: nulo ate a primeira parte chegar.</summary>
    internal ArquivoChegando?[] Chegando { get; set; } = Array.Empty<ArquivoChegando?>();

    public long Total => Arquivos.Sum(a => Math.Max(0, a.Tamanho));

    public int Porcentagem => Total <= 0 ? 0 : (int)Math.Clamp(Recebidos * 100 / Total, 0, 100);
}

public sealed class ArquivoDoPedido
{
    public string Nome { get; set; } = "";
    public long Tamanho { get; set; }
}

/// <summary>
/// Um arquivo do pedido sendo gravado. O nome no disco e escolhido uma vez,
/// na primeira parte, e as seguintes continuam o mesmo arquivo — escolher a
/// cada parte criava "video (2).mp4", "video (3).mp4"...
/// </summary>
internal sealed class ArquivoChegando
{
    public string Caminho { get; init; } = "";

    /// <summary>Bytes ja gravados, do inicio: e de onde a proxima parte continua.</summary>
    public long Gravados { get; set; }

    public bool Pronto { get; set; }

    /// <summary>Uma parte esta sendo gravada agora: outra para o mesmo arquivo espera.</summary>
    public bool Ocupado { get; set; }

    /// <summary>
    /// O arquivo fica aberto de uma parte para a outra. Abrir e fechar a cada
    /// parte fazia o antivirus do Windows reler o arquivo inteiro a cada
    /// fechamento — 8 MB, 16, 24... —, e um video de 137 MB levava minutos.
    /// Fecha quando termina, quando o envio e abandonado e quando o Kdrop para.
    /// </summary>
    public FileStream? Fluxo { get; set; }

    /// <summary>Quando a primeira parte chegou, para o log dizer a velocidade.</summary>
    public DateTime InicioEm { get; init; } = DateTime.UtcNow;

    public void Fechar()
    {
        try { Fluxo?.Dispose(); }
        catch (IOException) { }
        Fluxo = null;
    }
}

/// <summary>
/// O Kdrop: a bandeja da Kisland aberta para a casa.
///
/// Tres pecas, todas so na rede local e sem servico de fora:
/// - Um servidor HTTP na porta 48620, que serve a pagina do celular (a
///   bandeja vista de fora), entrega os arquivos da bandeja e recebe envios.
/// - Um aviso por UDP na porta 48621, a cada poucos segundos, para os outros
///   KInfo da rede saberem que este esta ligado — e e assim que os PCs
///   aparecem uns para os outros e para o celular.
/// - O cliente que manda para outro PC: pede, espera o aceite, envia.
///
/// Todo envio recebido passa pelo aceite na Kisland, a nao ser de aparelho
/// que a pessoa marcou como confiavel. A conexao e HTTP comum: criptografia
/// pediria certificado, e o celular mostraria aviso de site inseguro. O que
/// protege e o aceite, e a regra do Firewall so para redes privadas.
///
/// O servidor HTTP do Windows (HttpListener) exige o app elevado para ouvir
/// fora do proprio PC — o KInfo instalado com acesso completo ja roda assim.
/// </summary>
public sealed class KdropService : IDisposable
{
    public const int Porta = 48620;
    public const int PortaDescoberta = 48621;

    /// <summary>Sem resposta neste tempo, o pedido conta como recusado.</summary>
    public static readonly TimeSpan PrazoDoPedido = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PcSome = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan CelularSome = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppConfig _cfg;
    private readonly Func<IReadOnlyList<string>> _bandeja;
    private readonly string _id = Guid.NewGuid().ToString("N");

    private HttpListener? _http;
    private UdpClient? _udp;
    private CancellationTokenSource? _fim;
    private System.Threading.Timer? _anuncio;

    private readonly ConcurrentDictionary<string, (AparelhoKdrop Aparelho, DateTime Visto)> _pcs = new();
    private readonly ConcurrentDictionary<string, Celular> _celulares = new();
    private readonly ConcurrentDictionary<string, PedidoKdrop> _pedidos = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<ItemKdrop>> _bandejasDosPcs = new();
    private string _assinaturaDosAparelhos = "";
    private string _assinaturaDasBandejas = "";
    private string _assinaturaDaMinhaBandeja = "";

    private sealed class Celular
    {
        public string Nome = "";
        public DateTime Visto;
        public readonly List<string> Novos = new();
    }

    /// <summary>Por que nao esta rodando, quando nao esta.</summary>
    public string? Motivo { get; private set; }

    public bool Rodando => _http is not null;

    public string NomeDestePc => string.IsNullOrWhiteSpace(_cfg.KdropNome) ? Environment.MachineName : _cfg.KdropNome!.Trim();

    /// <summary>Endereco para o celular, como vai no QR Code. Nulo sem rede.</summary>
    public static string? Endereco => IpLocal() is { } ip ? $"http://{ip}:{Porta}/" : null;

    // Avisos, em thread qualquer.
    public event Action<PedidoKdrop>? PedidoChegou;
    public event Action<PedidoKdrop>? PedidoMudou;
    public event Action<string>? ArquivoRecebido;
    public event Action? AparelhosMudaram;
    public event Action? BandejasMudaram;

    public KdropService(AppConfig cfg, Func<IReadOnlyList<string>> bandeja)
    {
        _cfg = cfg;
        _bandeja = bandeja;
    }

    // ------------------------------------------------------------------
    // Ligar e desligar

    public bool Iniciar()
    {
        if (_http is not null) return true;

        if (!AtualizacaoService.Elevado())
        {
            Motivo = "O Kdrop precisa do KInfo instalado com acesso completo (rodando como administrador).";
            return false;
        }

        try
        {
            LiberarNoFirewall();

            var http = new HttpListener();
            http.Prefixes.Add($"http://+:{Porta}/");
            http.Start();
            _http = http;

            _fim = new CancellationTokenSource();
            _ = Task.Run(() => Atender(http, _fim.Token));

            IniciarDescoberta(_fim.Token);
            Motivo = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("Kdrop: iniciar", ex);
            Motivo = ex is HttpListenerException { ErrorCode: 32 or 183 }
                ? $"A porta {Porta} já está em uso por outro programa."
                : "O Windows não deixou o Kdrop abrir a porta na rede.";
            Parar();
            return false;
        }
    }

    public void Parar()
    {
        try { _fim?.Cancel(); } catch { }
        try { _anuncio?.Dispose(); } catch { }
        try { _http?.Stop(); _http?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }

        _http = null;
        _udp = null;
        _anuncio = null;
        _pcs.Clear();
        _celulares.Clear();
        _bandejasDosPcs.Clear();

        // Arquivos que estavam chegando ficam como estao (incompletos), mas
        // fechados: aberto, o arquivo ficaria preso ate o app fechar.
        foreach (var pedido in _pedidos.Values)
            lock (pedido)
                foreach (var chegando in pedido.Chegando)
                    chegando?.Fechar();
    }

    public void Dispose() => Parar();

    /// <summary>
    /// Regras de entrada no Firewall do Windows para as duas portas, so para
    /// redes privadas e de dominio: numa rede publica (cafe, aeroporto) o
    /// Kdrop fica fechado. Cria uma vez; se ja existe, nao mexe.
    /// </summary>
    private static void LiberarNoFirewall()
    {
        const string nome = "KInfo Kdrop";
        if (Netsh($"advfirewall firewall show rule name=\"{nome}\"") == 0) return;

        Netsh($"advfirewall firewall add rule name=\"{nome}\" dir=in action=allow protocol=TCP localport={Porta} profile=private,domain");
        Netsh($"advfirewall firewall add rule name=\"{nome}\" dir=in action=allow protocol=UDP localport={PortaDescoberta} profile=private,domain");
    }

    private static int Netsh(string argumentos)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", argumentos)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return -1;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            Log.Erro("Kdrop: firewall", ex);
            return -1;
        }
    }

    // ------------------------------------------------------------------
    // Aparelhos por perto

    /// <summary>PCs e celulares vistos ha pouco, PCs primeiro.</summary>
    public IReadOnlyList<AparelhoKdrop> Aparelhos()
    {
        var agora = DateTime.UtcNow;
        var lista = new List<AparelhoKdrop>();

        foreach (var (_, (pc, visto)) in _pcs)
            if (agora - visto < PcSome) lista.Add(pc);

        foreach (var (id, cel) in _celulares)
            if (agora - cel.Visto < CelularSome) lista.Add(new AparelhoKdrop(id, cel.Nome, false, null, false));

        return lista.OrderByDescending(a => a.EhPc).ThenBy(a => a.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Avisa a Kisland quando alguem chega ou some, e so entao.</summary>
    private void ConferirAparelhos()
    {
        var assinatura = string.Join("|", Aparelhos().Select(a => a.Id + a.Nome));
        if (assinatura != _assinaturaDosAparelhos)
        {
            _assinaturaDosAparelhos = assinatura;
            AparelhosMudaram?.Invoke();
        }

        ConferirBandejas();
    }

    /// <summary>
    /// O que esta na bandeja de cada outro PC da casa, de quem esta ligado
    /// agora. Chega junto com o anuncio de cada um, sem precisar perguntar.
    /// </summary>
    public IReadOnlyList<(AparelhoKdrop Pc, IReadOnlyList<ItemKdrop> Itens)> BandejasDosPcs() =>
        Aparelhos()
            .Where(a => a.EhPc)
            .Select(a => (Pc: a, Itens: _bandejasDosPcs.TryGetValue(a.Id, out var itens) ? itens : Array.Empty<ItemKdrop>()))
            .Where(b => b.Itens.Count > 0)
            .ToList();

    private void ConferirBandejas()
    {
        var assinatura = string.Join("|", BandejasDosPcs().Select(b => b.Pc.Id + ":" + string.Join(",", b.Itens.Select(i => i.Id))));
        if (assinatura == _assinaturaDasBandejas) return;

        _assinaturaDasBandejas = assinatura;
        BandejasMudaram?.Invoke();
    }

    /// <summary>
    /// A bandeja deste PC mudou (algo entrou ou saiu): anuncia ja, para os
    /// outros PCs verem sem esperar o proximo anuncio. O celular ve na
    /// proxima pergunta, em ate 2 s.
    /// </summary>
    public void BandejaMudou()
    {
        string assinatura;
        try { assinatura = string.Join("|", _bandeja()); }
        catch { return; }

        if (assinatura == _assinaturaDaMinhaBandeja) return;
        _assinaturaDaMinhaBandeja = assinatura;

        try { _anuncio?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Os itens da bandeja deste PC que ainda existem, arquivos e pastas.</summary>
    private List<ItemKdrop> MinhaBandeja()
    {
        var itens = new List<ItemKdrop>();
        foreach (var caminho in _bandeja())
        {
            try
            {
                if (File.Exists(caminho))
                    itens.Add(new ItemKdrop(IdDoArquivo(caminho), Path.GetFileName(caminho), new FileInfo(caminho).Length, false));
                else if (Directory.Exists(caminho))
                    itens.Add(new ItemKdrop(IdDoArquivo(caminho), Path.GetFileName(caminho.TrimEnd('\\', '/')), -1, true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sumiu ou ficou sem acesso no meio: fica de fora desta vez.
            }
        }

        return itens;
    }

    private sealed class Anuncio
    {
        public int Kdrop { get; set; }
        public string Id { get; set; } = "";
        public string Nome { get; set; } = "";
        public int Porta { get; set; }
        public bool Notebook { get; set; }

        /// <summary>O que esta na bandeja de quem anuncia (ate 12 itens, cabe folgado num pacote).</summary>
        public List<ItemKdrop>? Bandeja { get; set; }
    }

    private void IniciarDescoberta(CancellationToken fim)
    {
        try
        {
            var udp = new UdpClient { EnableBroadcast = true };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, PortaDescoberta));
            _udp = udp;

            _ = Task.Run(async () =>
            {
                while (!fim.IsCancellationRequested)
                {
                    try
                    {
                        var r = await udp.ReceiveAsync(fim);
                        var a = JsonSerializer.Deserialize<Anuncio>(r.Buffer, Json);
                        if (a is not { Kdrop: 1 } || a.Id == _id || string.IsNullOrEmpty(a.Id)) continue;

                        var endereco = $"http://{r.RemoteEndPoint.Address}:{(a.Porta > 0 ? a.Porta : Porta)}/";
                        _pcs[a.Id] = (new AparelhoKdrop(a.Id, a.Nome, true, endereco, a.Notebook), DateTime.UtcNow);
                        _bandejasDosPcs[a.Id] = (a.Bandeja ?? new List<ItemKdrop>())
                            .Where(i => !string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Nome))
                            .Take(50)
                            .ToList();
                        ConferirAparelhos();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex) { Log.Erro("Kdrop: descoberta", ex); await Task.Delay(1000, CancellationToken.None); }
                }
            }, fim);

            // Anuncia logo e a cada 5 s; aproveita para notar quem sumiu.
            _anuncio = new System.Threading.Timer(_ =>
            {
                try
                {
                    List<ItemKdrop> bandeja;
                    try { bandeja = MinhaBandeja(); }
                    catch { bandeja = new List<ItemKdrop>(); }

                    var anuncio = JsonSerializer.SerializeToUtf8Bytes(new Anuncio
                    {
                        Kdrop = 1, Id = _id, Nome = NomeDestePc, Porta = Porta, Notebook = EhNotebook(),
                        Bandeja = bandeja
                    }, Json);
                    _udp?.Send(anuncio, anuncio.Length, new IPEndPoint(IPAddress.Broadcast, PortaDescoberta));
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // Sem rede agora; tenta de novo no proximo anuncio.
                }

                ConferirAparelhos();
                ExpirarPedidos();
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            // Sem descoberta o Kdrop ainda funciona com o celular; so os
            // outros PCs nao aparecem.
            Log.Erro("Kdrop: abrir descoberta", ex);
        }
    }

    private static bool? _notebook;

    /// <summary>Tem bateria: e notebook. So para o icone do aparelho.</summary>
    private static bool EhNotebook()
    {
        if (_notebook is { } ja) return ja;
        try
        {
            _notebook = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus
                        != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
        }
        catch
        {
            _notebook = false;
        }

        return _notebook.Value;
    }

    /// <summary>
    /// O IPv4 deste PC na rede de casa: o da placa ligada que tem um gateway
    /// (a que sai para o roteador), e nao o de placas virtuais.
    /// </summary>
    public static string? IpLocal()
    {
        try
        {
            foreach (var placa in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (placa.OperationalStatus != OperationalStatus.Up ||
                    placa.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = placa.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                     !g.Address.Equals(IPAddress.Any)))
                    continue;

                var ip = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ip is not null) return ip.Address.ToString();
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Kdrop: endereço", ex);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Pedidos recebidos

    /// <summary>A resposta da Kisland a um pedido. "Lembrar" poe o aparelho entre os confiaveis.</summary>
    public void Responder(string pedidoId, bool aceitar, bool lembrar)
    {
        if (!_pedidos.TryGetValue(pedidoId, out var pedido)) return;

        lock (pedido)
        {
            if (pedido.Estado != EstadoDoPedido.Esperando) return;
            pedido.Estado = aceitar ? EstadoDoPedido.Aceito : EstadoDoPedido.Recusado;
            pedido.AtividadeEm = DateTime.UtcNow;
        }

        Log.Nota($"Kdrop: pedido {pedido.Id} {(aceitar ? "aceito" : "recusado")}");

        if (aceitar && lembrar && _cfg.KdropConfiaveis.All(c => c.Id != pedido.OrigemId))
        {
            _cfg.KdropConfiaveis.Add(new AparelhoConfiavel { Id = pedido.OrigemId, Nome = pedido.OrigemNome });
            _cfg.Salvar();
        }

        PedidoMudou?.Invoke(pedido);
    }

    /// <summary>Aceito e sem nenhuma parte chegando ha tanto tempo, o envio foi abandonado.</summary>
    public static readonly TimeSpan EnvioParado = TimeSpan.FromMinutes(30);

    private void ExpirarPedidos()
    {
        var agora = DateTime.UtcNow;
        foreach (var (id, pedido) in _pedidos)
        {
            var mudou = false;
            var incompletos = new List<string>();
            lock (pedido)
            {
                if (pedido.Estado == EstadoDoPedido.Esperando && agora - pedido.CriadoEm > PrazoDoPedido)
                {
                    pedido.Estado = EstadoDoPedido.Expirado;
                    mudou = true;
                }
                else if (pedido.Estado == EstadoDoPedido.Aceito && agora - pedido.AtividadeEm > EnvioParado &&
                         !pedido.Chegando.Any(c => c is { Ocupado: true }))
                {
                    // Abandonado no meio (o celular saiu da pagina, a rede
                    // caiu de vez): os pedacos gravados nao servem para nada.
                    pedido.Estado = EstadoDoPedido.Expirado;
                    foreach (var c in pedido.Chegando) c?.Fechar();
                    incompletos.AddRange(pedido.Chegando.Where(c => c is { Pronto: false }).Select(c => c!.Caminho));
                    mudou = true;
                }
            }

            foreach (var caminho in incompletos)
            {
                try { File.Delete(caminho); }
                catch (Exception ex) { Log.Erro("Kdrop: apagar envio incompleto", ex); }
            }

            if (mudou)
            {
                Log.Nota($"Kdrop: pedido {pedido.Id} expirado{(incompletos.Count > 0 ? $", {incompletos.Count} arquivo(s) incompleto(s) apagado(s)" : "")}");
                PedidoMudou?.Invoke(pedido);
            }

            // Terminado (ou recusado, ou expirado) ha mais de 2 h, sai da
            // memoria. Aceito e ainda recebendo, nunca: quem decide e o prazo
            // de parado, acima.
            if (pedido.Estado != EstadoDoPedido.Aceito &&
                agora - (pedido.AtividadeEm > pedido.CriadoEm ? pedido.AtividadeEm : pedido.CriadoEm) > TimeSpan.FromHours(2))
                _pedidos.TryRemove(id, out _);
        }
    }

    // ------------------------------------------------------------------
    // Para o celular

    /// <summary>Marca arquivos da bandeja como mandados para um celular: a pagina dele os destaca.</summary>
    public void AvisarCelular(string celularId, IEnumerable<string> caminhos)
    {
        if (!_celulares.TryGetValue(celularId, out var cel)) return;

        lock (cel.Novos)
        {
            foreach (var c in caminhos)
            {
                var id = IdDoArquivo(c);
                if (!cel.Novos.Contains(id)) cel.Novos.Add(id);
            }
        }
    }

    // ------------------------------------------------------------------
    // Para outro PC

    private sealed class RespostaDePedido
    {
        public string? Pedido { get; set; }
        public string? Estado { get; set; }
    }

    /// <summary>
    /// Manda arquivos para outro PC com KInfo: pede, espera o aceite (ate o
    /// prazo do pedido) e envia um por um. O progresso vai de 0 a 100; o
    /// resultado diz o que aconteceu, em poucas palavras, para a Kisland.
    /// </summary>
    public async Task<string> MandarParaPc(AparelhoKdrop pc, IReadOnlyList<string> caminhos, IProgress<int>? progresso)
    {
        if (pc.Endereco is null || caminhos.Count == 0) return "Nada para mandar";

        try
        {
            var arquivos = caminhos.Where(File.Exists).Select(c => new FileInfo(c)).ToList();
            if (arquivos.Count == 0) return "Nada para mandar";

            using var http = new HttpClient { BaseAddress = new Uri(pc.Endereco), Timeout = Timeout.InfiniteTimeSpan };

            var corpo = JsonSerializer.Serialize(new
            {
                cliente = _id,
                nome = NomeDestePc,
                pc = true,
                arquivos = arquivos.Select(a => new { nome = a.Name, tamanho = a.Length })
            }, Json);

            using var resposta = await http.PostAsync("api/pedido", new StringContent(corpo, Encoding.UTF8, "application/json"));
            var pedido = JsonSerializer.Deserialize<RespostaDePedido>(await resposta.Content.ReadAsStringAsync(), Json);
            if (pedido?.Pedido is not { } pedidoId) return "O outro PC não respondeu";

            // Espera o aceite, perguntando a cada segundo.
            var estado = pedido.Estado;
            var limite = DateTime.UtcNow + PrazoDoPedido + TimeSpan.FromSeconds(5);
            while (estado == "esperando" && DateTime.UtcNow < limite)
            {
                await Task.Delay(1000);
                var r = JsonSerializer.Deserialize<RespostaDePedido>(await http.GetStringAsync($"api/pedido/{pedidoId}"), Json);
                estado = r?.Estado;
            }

            if (estado != "aceito") return estado == "recusado" ? "Recusado" : "Sem resposta";

            var total = Math.Max(1, arquivos.Sum(a => a.Length));
            long enviados = 0;

            foreach (var arquivo in arquivos)
            {
                var antes = enviados;
                await using var fluxo = arquivo.OpenRead();
                var conteudo = new ConteudoComProgresso(fluxo, lidos =>
                    progresso?.Report((int)Math.Clamp((antes + lidos) * 100 / total, 0, 100)));

                using var envio = await http.PostAsync($"api/enviar/{pedidoId}?nome={Uri.EscapeDataString(arquivo.Name)}", conteudo);
                if (!envio.IsSuccessStatusCode) return "O envio falhou";

                enviados += arquivo.Length;
            }

            progresso?.Report(100);
            return "Enviado";
        }
        catch (Exception ex)
        {
            Log.Erro("Kdrop: mandar para PC", ex);
            return "Não deu para mandar";
        }
    }

    /// <summary>Corpo de envio que conta o que ja foi lido, para a barra de progresso.</summary>
    private sealed class ConteudoComProgresso : HttpContent
    {
        private readonly Stream _fluxo;
        private readonly Action<long> _lidos;

        public ConteudoComProgresso(Stream fluxo, Action<long> lidos)
        {
            _fluxo = fluxo;
            _lidos = lidos;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream destino, TransportContext? contexto)
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await _fluxo.ReadAsync(buffer)) > 0)
            {
                await destino.WriteAsync(buffer.AsMemory(0, n));
                total += n;
                _lidos(total);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _fluxo.CanSeek ? _fluxo.Length : -1;
            return _fluxo.CanSeek;
        }
    }

    // ------------------------------------------------------------------
    // Servidor

    private async Task Atender(HttpListener http, CancellationToken fim)
    {
        while (!fim.IsCancellationRequested && http.IsListening)
        {
            HttpListenerContext contexto;
            try
            {
                contexto = await http.GetContextAsync();
            }
            catch (Exception) when (fim.IsCancellationRequested || !http.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Erro("Kdrop: aceitar conexão", ex);
                continue;
            }

            _ = Task.Run(() => ResponderHttp(contexto));
        }
    }

    private async Task ResponderHttp(HttpListenerContext c)
    {
        var req = c.Request;
        var res = c.Response;

        try
        {
            var caminho = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var metodo = req.HttpMethod;

            if (metodo == "GET" && caminho is "" or "/index.html")
                await Pagina(res);
            else if (metodo == "GET" && caminho == "/teste")
                await PaginaDeTeste(res);
            else if (metodo == "GET" && caminho == "/api/estado")
                await Estado(req, res);
            else if (metodo == "GET" && caminho.StartsWith("/api/arquivo/"))
                await Baixar(caminho["/api/arquivo/".Length..], res);
            else if (metodo == "POST" && caminho == "/api/pedido")
                await NovoPedido(req, res);
            else if (metodo == "GET" && caminho.StartsWith("/api/pedido/"))
                await EstadoDoPedidoJson(caminho["/api/pedido/".Length..], res);
            else if (metodo == "POST" && caminho.StartsWith("/api/enviar/"))
                await Receber(caminho["/api/enviar/".Length..], req, res);
            else if (metodo == "GET" && caminho.StartsWith("/api/enviar/"))
                await EstadoDoEnvio(caminho["/api/enviar/".Length..], res);
            else
                res.StatusCode = 404;
        }
        catch (Exception ex)
        {
            Log.Erro("Kdrop: atender", ex);
            try { res.StatusCode = 500; } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private static string? _pagina;

    private async Task Pagina(HttpListenerResponse res)
    {
        if (_pagina is null)
        {
            using var recurso = Assembly.GetExecutingAssembly().GetManifestResourceStream("kdrop.html");
            if (recurso is null) { res.StatusCode = 500; return; }
            using var leitor = new StreamReader(recurso, Encoding.UTF8);
            _pagina = await leitor.ReadToEndAsync();
        }

        await Escrever(res, _pagina, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Pagina de diagnostico (/teste): cinco jeitos de escolher fotos, sem
    /// enviar nada. Existe para descobrir por que a galeria do iPhone (iOS 26)
    /// trava no botao de confirmar na pagina do Kdrop: os mesmos cinco jeitos
    /// funcionam numa pagina com HTTPS, e aqui rodam pelo mesmo HTTP do Kdrop.
    /// Se travarem aqui, o problema e o HTTP; se nao, e algo da pagina do
    /// Kdrop.
    /// </summary>
    private static async Task PaginaDeTeste(HttpListenerResponse res)
    {
        using var recurso = Assembly.GetExecutingAssembly().GetManifestResourceStream("kdrop-teste.html");
        if (recurso is null) { res.StatusCode = 500; return; }
        using var leitor = new StreamReader(recurso, Encoding.UTF8);
        await Escrever(res, await leitor.ReadToEndAsync(), "text/html; charset=utf-8");
    }

    private async Task Estado(HttpListenerRequest req, HttpListenerResponse res)
    {
        // Quem pergunta e um celular com a pagina aberta: fica visivel na
        // Kisland enquanto continuar perguntando.
        var cliente = req.QueryString["cliente"];
        string[] novos = Array.Empty<string>();

        if (!string.IsNullOrWhiteSpace(cliente) && cliente.Length <= 64)
        {
            var nome = req.QueryString["nome"];
            var cel = _celulares.GetOrAdd(cliente, _ => new Celular());
            cel.Nome = string.IsNullOrWhiteSpace(nome) ? "Celular" : nome.Trim()[..Math.Min(40, nome.Trim().Length)];
            cel.Visto = DateTime.UtcNow;
            ConferirAparelhos();

            lock (cel.Novos)
            {
                novos = cel.Novos.ToArray();
                cel.Novos.Clear();
            }
        }

        var pcs = new List<object> { new { nome = NomeDestePc, endereco = (string?)null, eu = true } };
        pcs.AddRange(Aparelhos().Where(a => a.EhPc).Select(a => (object)new { nome = a.Nome, endereco = a.Endereco, eu = false }));

        // Pasta vai como .zip, montado na hora do download.
        var bandeja = MinhaBandeja()
            .Select(i => new { id = i.Id, nome = i.Pasta ? i.Nome + ".zip" : i.Nome, tamanho = i.Tamanho, pasta = i.Pasta });

        await Escrever(res, JsonSerializer.Serialize(new
        {
            id = _id,
            nome = NomeDestePc,
            acento = _cfg.CorDestaque,
            pcs,
            bandeja,
            novos
        }, Json), "application/json; charset=utf-8");
    }

    /// <summary>Id curto e estavel de um arquivo da bandeja, pelo caminho.</summary>
    private static string IdDoArquivo(string caminho) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(caminho.ToLowerInvariant())))[..16];

    private async Task Baixar(string id, HttpListenerResponse res)
    {
        var caminho = _bandeja().FirstOrDefault(c => IdDoArquivo(c) == id);

        if (caminho is not null && Directory.Exists(caminho))
        {
            BaixarPasta(caminho, res);
            return;
        }

        if (caminho is null || !File.Exists(caminho))
        {
            res.StatusCode = 404;
            return;
        }

        var nome = Path.GetFileName(caminho);
        res.ContentType = "application/octet-stream";
        res.AddHeader("Content-Disposition", $"attachment; filename*=UTF-8''{Uri.EscapeDataString(nome)}");

        await using var fluxo = File.OpenRead(caminho);
        res.ContentLength64 = fluxo.Length;
        await fluxo.CopyToAsync(res.OutputStream);
    }

    /// <summary>
    /// Pasta da bandeja: vai como .zip, montado enquanto sai, sem arquivo
    /// temporario no disco. Sem tamanho conhecido antes, a resposta vai em
    /// pedacos (chunked). Arquivo que nao abre (em uso, sem acesso) fica de
    /// fora, e o resto vai.
    /// </summary>
    private static void BaixarPasta(string pasta, HttpListenerResponse res)
    {
        var nome = Path.GetFileName(pasta.TrimEnd('\\', '/')) + ".zip";
        res.ContentType = "application/zip";
        res.AddHeader("Content-Disposition", $"attachment; filename*=UTF-8''{Uri.EscapeDataString(nome)}");
        res.SendChunked = true;

        using var zip = new System.IO.Compression.ZipArchive(res.OutputStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
        foreach (var arquivo in Directory.EnumerateFiles(pasta, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            FileStream fluxo;
            try
            {
                fluxo = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (fluxo)
            {
                var relativo = Path.GetRelativePath(pasta, arquivo).Replace('\\', '/');
                var entrada = zip.CreateEntry(relativo, System.IO.Compression.CompressionLevel.Fastest);
                entrada.LastWriteTime = File.GetLastWriteTime(arquivo);
                using var destino = entrada.Open();
                fluxo.CopyTo(destino);
            }
        }
    }

    // ------------------------------------------------------------------
    // Buscar da bandeja de outro PC

    /// <summary>
    /// Traz um item da bandeja de outro PC para este: vai para a pasta do
    /// Kdrop e o caminho volta para entrar na bandeja daqui. Sem pedido nem
    /// aceite, porque quem busca e a propria pessoa, no PC dela, e o outro
    /// PC ja deixou o item a vista ao po-lo na bandeja. Pasta chega como .zip
    /// e e aberta numa pasta de mesmo nome.
    /// </summary>
    /// <returns>O caminho do que chegou, ou nulo se nao deu.</returns>
    public async Task<string?> BuscarDePc(AparelhoKdrop pc, ItemKdrop item, IProgress<int>? progresso)
    {
        if (pc.Endereco is null) return null;

        var pasta = Pasta(_cfg);
        string? temporario = null;

        try
        {
            Directory.CreateDirectory(pasta);

            using var http = new HttpClient { BaseAddress = new Uri(pc.Endereco), Timeout = Timeout.InfiniteTimeSpan };
            using var resposta = await http.GetAsync($"api/arquivo/{Uri.EscapeDataString(item.Id)}", HttpCompletionOption.ResponseHeadersRead);
            if (!resposta.IsSuccessStatusCode)
            {
                Log.Nota($"Kdrop: buscar {item.Nome} de {pc.Nome} deu {(int)resposta.StatusCode}");
                return null;
            }

            var nome = NomeSeguro(item.Nome);
            var destino = item.Pasta
                ? temporario = Path.Combine(Path.GetTempPath(), $"kdrop-{Guid.NewGuid():N}.zip")
                : CaminhoLivre(pasta, nome);

            var total = item.Tamanho > 0 ? item.Tamanho : resposta.Content.Headers.ContentLength ?? -1;
            await using (var origem = await resposta.Content.ReadAsStreamAsync())
            await using (var arquivo = File.Create(destino))
            {
                var buffer = new byte[81920];
                long lidos = 0;
                int n;
                var ultimoAviso = DateTime.MinValue;
                while ((n = await origem.ReadAsync(buffer)) > 0)
                {
                    await arquivo.WriteAsync(buffer.AsMemory(0, n));
                    lidos += n;
                    if (total > 0 && DateTime.UtcNow - ultimoAviso > TimeSpan.FromMilliseconds(250))
                    {
                        ultimoAviso = DateTime.UtcNow;
                        progresso?.Report((int)Math.Clamp(lidos * 100 / total, 0, 99));
                    }
                }
            }

            if (item.Pasta)
            {
                var final = PastaLivre(pasta, nome);
                System.IO.Compression.ZipFile.ExtractToDirectory(destino, final);
                destino = final;
            }

            progresso?.Report(100);
            Log.Nota($"Kdrop: buscado {Path.GetFileName(destino)} de {pc.Nome}");
            return destino;
        }
        catch (Exception ex)
        {
            Log.Erro("Kdrop: buscar de outro PC", ex);
            return null;
        }
        finally
        {
            if (temporario is not null)
                try { File.Delete(temporario); } catch { }
        }
    }

    /// <summary>"Fotos", e se ja existir, "Fotos (2)".</summary>
    private static string PastaLivre(string pasta, string nome)
    {
        var caminho = Path.Combine(pasta, nome);
        for (var i = 2; Directory.Exists(caminho) || File.Exists(caminho); i++)
            caminho = Path.Combine(pasta, $"{nome} ({i})");
        return caminho;
    }

    private sealed class PedidoRecebido
    {
        public string Cliente { get; set; } = "";
        public string Nome { get; set; } = "";
        public bool Pc { get; set; }
        public List<ArquivoDoPedido> Arquivos { get; set; } = new();
    }

    private async Task NovoPedido(HttpListenerRequest req, HttpListenerResponse res)
    {
        PedidoRecebido? dados;
        using (var leitor = new StreamReader(req.InputStream, Encoding.UTF8))
            dados = JsonSerializer.Deserialize<PedidoRecebido>(await leitor.ReadToEndAsync(), Json);

        if (dados is null || string.IsNullOrWhiteSpace(dados.Cliente) || dados.Arquivos.Count == 0 || dados.Arquivos.Count > 100)
        {
            res.StatusCode = 400;
            return;
        }

        var pedido = new PedidoKdrop
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            OrigemId = dados.Cliente,
            OrigemNome = string.IsNullOrWhiteSpace(dados.Nome) ? "Aparelho" : dados.Nome.Trim(),
            OrigemEhPc = dados.Pc,
            Arquivos = dados.Arquivos.Select(a => new ArquivoDoPedido { Nome = NomeSeguro(a.Nome), Tamanho = a.Tamanho }).ToList()
        };
        pedido.Chegando = new ArquivoChegando?[pedido.Arquivos.Count];

        // Aparelho confiavel nao precisa de aceite.
        var confiavel = _cfg.KdropConfiaveis.Any(c => c.Id == pedido.OrigemId);
        pedido.Estado = confiavel ? EstadoDoPedido.Aceito : EstadoDoPedido.Esperando;
        _pedidos[pedido.Id] = pedido;

        Log.Nota($"Kdrop: pedido {pedido.Id} de {pedido.OrigemNome}, {pedido.Arquivos.Count} arquivo(s){(confiavel ? ", aceito sozinho" : "")}");

        if (confiavel) PedidoMudou?.Invoke(pedido);
        else PedidoChegou?.Invoke(pedido);

        await Escrever(res, JsonSerializer.Serialize(new { pedido = pedido.Id, estado = Texto(pedido.Estado) }, Json),
                       "application/json; charset=utf-8");
    }

    private async Task EstadoDoPedidoJson(string id, HttpListenerResponse res)
    {
        if (!_pedidos.TryGetValue(id, out var pedido))
        {
            res.StatusCode = 404;
            return;
        }

        await Escrever(res, JsonSerializer.Serialize(new { pedido = pedido.Id, estado = Texto(pedido.Estado), porcentagem = pedido.Porcentagem }, Json),
                       "application/json; charset=utf-8");
    }

    private static string Texto(EstadoDoPedido e) => e switch
    {
        EstadoDoPedido.Esperando => "esperando",
        EstadoDoPedido.Aceito => "aceito",
        EstadoDoPedido.Recusado => "recusado",
        EstadoDoPedido.Expirado => "expirado",
        _ => "concluido"
    };

    /// <summary>
    /// Recebe um arquivo do pedido aceito, inteiro ou em partes.
    ///
    /// Em partes (a pagina do celular, desde a 0.1.29): <c>?arquivo=</c> e o
    /// indice do arquivo no pedido, <c>pos=</c> e de onde a parte comeca e
    /// <c>fim=1</c> marca a ultima. A parte tem de comecar exatamente onde o
    /// arquivo parou; se nao, a resposta e 409 com quanto ja foi gravado, e o
    /// celular continua dali. E isso que deixa um video grande sobreviver a
    /// uma queda de Wi-Fi ou a tela do celular apagando no meio.
    ///
    /// Inteiro (sem <c>arquivo</c>): o jeito antigo, que outros PCs com KInfo
    /// mais velho ainda usam. Vai para o primeiro arquivo do pedido ainda nao
    /// comecado, e termina quando o corpo termina.
    /// </summary>
    private async Task Receber(string id, HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!_pedidos.TryGetValue(id, out var pedido) || pedido.Estado != EstadoDoPedido.Aceito)
        {
            Log.Nota($"Kdrop: envio para pedido {id} {(pedido is null ? "desconhecido" : pedido.Estado.ToString())}, recusado");
            res.StatusCode = 403;
            await Escrever(res, JsonSerializer.Serialize(new { estado = pedido is null ? "desconhecido" : Texto(pedido.Estado) }, Json),
                           "application/json; charset=utf-8");
            return;
        }

        var emPartes = req.QueryString["arquivo"] is not null;
        var nome = NomeSeguro(req.QueryString["nome"] ?? "");
        int indice;
        long pos = 0;
        var ultima = true;

        if (emPartes)
        {
            if (!int.TryParse(req.QueryString["arquivo"], out indice) ||
                !long.TryParse(req.QueryString["pos"] ?? "0", out pos) || pos < 0)
            {
                res.StatusCode = 400;
                return;
            }

            ultima = req.QueryString["fim"] == "1";
        }
        else
        {
            // Cabem tantos arquivos quantos o pedido aceito declarou, e nada
            // alem. Nao confere o nome com o do pedido: o iPhone renomeia
            // fotos ao enviar ("image.jpg"), e acentos podem chegar
            // codificados de outro jeito.
            lock (pedido) indice = Array.FindIndex(pedido.Chegando, c => c is null);
        }

        ArquivoChegando estado = null!;
        int? recusa = null;
        lock (pedido)
        {
            if (indice < 0 || indice >= pedido.Chegando.Length)
            {
                Log.Nota($"Kdrop: envio a mais no pedido {id}, recusado");
                res.StatusCode = 403;
                return;
            }

            if (pedido.Chegando[indice] is null)
            {
                // O nome no disco e escolhido uma vez so, e o arquivo ja nasce
                // vazio, para dois arquivos de mesmo nome nao pegarem o mesmo
                // lugar.
                var pasta = Pasta(_cfg);
                Directory.CreateDirectory(pasta);
                var caminho = CaminhoLivre(pasta, nome);
                File.Create(caminho).Dispose();
                pedido.Chegando[indice] = new ArquivoChegando { Caminho = caminho };
            }

            estado = pedido.Chegando[indice]!;

            // Ja terminado: uma ultima parte repetida (a resposta se perdeu
            // no caminho) so ouve que esta tudo certo. Fora de ordem, ou com
            // outra parte do mesmo arquivo ainda sendo gravada: 409, com
            // quanto ja chegou, para o celular continuar dali.
            if (estado.Pronto) recusa = 200;
            else if (estado.Ocupado || (emPartes && pos != estado.Gravados)) recusa = 409;
            else
            {
                estado.Ocupado = true;
                pedido.AtividadeEm = DateTime.UtcNow;
            }
        }

        if (recusa is { } codigo)
        {
            res.StatusCode = codigo;
            await Escrever(res, JsonSerializer.Serialize(new { gravados = estado.Gravados, pronto = estado.Pronto }, Json),
                           "application/json; charset=utf-8");
            return;
        }

        var limite = pedido.Arquivos[indice].Tamanho;
        var inicioDaParte = DateTime.UtcNow;
        var posDaParte = estado.Gravados;
        try
        {
            var buffer = new byte[256 * 1024];
            var arquivo = estado.Fluxo ??= new FileStream(estado.Caminho, FileMode.OpenOrCreate, FileAccess.Write,
                                                           FileShare.Read, 1024 * 1024, useAsync: true);

            // Continua de onde parou, e corta o que uma parte interrompida
            // tenha deixado alem disso.
            if (arquivo.Length != estado.Gravados) arquivo.SetLength(estado.Gravados);
            arquivo.Seek(estado.Gravados, SeekOrigin.Begin);

            int n;
            var ultimoAviso = DateTime.MinValue;
            while ((n = await req.InputStream.ReadAsync(buffer)) > 0)
            {
                // Mais do que o pedido declarou nao entra: o aceite foi para
                // aquele tamanho. (Tamanho zero ou desconhecido nao limita.)
                if (limite > 0 && estado.Gravados + n > limite)
                {
                    Log.Nota($"Kdrop: envio maior que o declarado no pedido {id}, recusado");
                    res.StatusCode = 413;
                    return;
                }

                await arquivo.WriteAsync(buffer.AsMemory(0, n));
                lock (pedido)
                {
                    estado.Gravados += n;
                    pedido.Recebidos = pedido.Chegando.Sum(c => c?.Gravados ?? 0);
                    pedido.AtividadeEm = DateTime.UtcNow;
                }

                // Progresso para a Kisland, no maximo quatro vezes por segundo.
                if (DateTime.UtcNow - ultimoAviso > TimeSpan.FromMilliseconds(250))
                {
                    ultimoAviso = DateTime.UtcNow;
                    PedidoMudou?.Invoke(pedido);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException)
        {
            // A conexao caiu no meio da parte. O que chegou fica gravado, e a
            // proxima tentativa continua dali.
            Log.Nota($"Kdrop: parte interrompida no pedido {id}, arquivo {indice}, em {estado.Gravados} bytes");

            // Erro do proprio arquivo (disco cheio, por exemplo) deixa o fluxo
            // num estado incerto: fecha, e a proxima parte abre de novo.
            if (ex is IOException && ex is not HttpListenerException) lock (pedido) estado.Fechar();
            return;
        }
        finally
        {
            lock (pedido) estado.Ocupado = false;
        }

        // Parte lenta vai para o log, com a velocidade: e por ai que se ve se
        // a demora e da rede (Wi-Fi) ou do PC.
        var duracaoDaParte = DateTime.UtcNow - inicioDaParte;
        if (duracaoDaParte > TimeSpan.FromSeconds(5))
            Log.Nota($"Kdrop: parte lenta no pedido {id}: {(estado.Gravados - posDaParte) / 1048576.0:0.0} MB em " +
                     $"{duracaoDaParte.TotalSeconds:0.0} s");

        if (!ultima)
        {
            await Escrever(res, JsonSerializer.Serialize(new { gravados = estado.Gravados, pronto = false }, Json),
                           "application/json; charset=utf-8");
            return;
        }

        estado.Fechar();
        var segundos = Math.Max(0.1, (DateTime.UtcNow - estado.InicioEm).TotalSeconds);
        Log.Nota($"Kdrop: recebido {Path.GetFileName(estado.Caminho)} ({estado.Gravados / 1048576.0:0.0} MB em " +
                 $"{segundos:0} s, {estado.Gravados / 1048576.0 / segundos:0.0} MB/s)");

        bool terminou;
        lock (pedido)
        {
            estado.Pronto = true;
            pedido.Salvos.Add(estado.Caminho);
            terminou = pedido.Chegando.All(c => c is { Pronto: true });
            if (terminou) pedido.Estado = EstadoDoPedido.Concluido;
        }

        ArquivoRecebido?.Invoke(estado.Caminho);
        PedidoMudou?.Invoke(pedido);

        await Escrever(res, JsonSerializer.Serialize(new { gravados = estado.Gravados, pronto = true }, Json),
                       "application/json; charset=utf-8");
    }

    /// <summary>
    /// Quanto de cada arquivo do pedido ja chegou: o celular pergunta isto
    /// depois de uma queda, para continuar de onde parou.
    /// </summary>
    private async Task EstadoDoEnvio(string id, HttpListenerResponse res)
    {
        if (!_pedidos.TryGetValue(id, out var pedido))
        {
            res.StatusCode = 404;
            return;
        }

        object corpo;
        lock (pedido)
        {
            corpo = new
            {
                estado = Texto(pedido.Estado),
                arquivos = pedido.Chegando.Select(c => new { gravados = c?.Gravados ?? 0, pronto = c?.Pronto ?? false }).ToList()
            };
        }

        await Escrever(res, JsonSerializer.Serialize(corpo, Json), "application/json; charset=utf-8");
    }

    private static async Task Escrever(HttpListenerResponse res, string texto, string tipo)
    {
        var bytes = Encoding.UTF8.GetBytes(texto);
        res.ContentType = tipo;
        res.AddHeader("Cache-Control", "no-store");
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    // ------------------------------------------------------------------
    // Arquivos

    /// <summary>So o nome, sem pasta e sem caracteres que o Windows recusa.</summary>
    private static string NomeSeguro(string nome)
    {
        var limpo = Path.GetFileName(nome.Replace('\\', '/').Split('/').Last());
        foreach (var c in Path.GetInvalidFileNameChars()) limpo = limpo.Replace(c, '_');
        limpo = limpo.Trim().Trim('.');
        return string.IsNullOrEmpty(limpo) ? "arquivo" : limpo[..Math.Min(limpo.Length, 150)];
    }

    /// <summary>"foto.jpg", e se ja existir, "foto (2).jpg".</summary>
    private static string CaminhoLivre(string pasta, string nome)
    {
        var caminho = Path.Combine(pasta, nome);
        var raiz = Path.GetFileNameWithoutExtension(nome);
        var ext = Path.GetExtension(nome);
        for (var i = 2; File.Exists(caminho); i++)
            caminho = Path.Combine(pasta, $"{raiz} ({i}){ext}");
        return caminho;
    }

    /// <summary>A pasta escolhida, ou Downloads\Kdrop.</summary>
    public static string Pasta(AppConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.KdropPasta) ? cfg.KdropPasta! : Path.Combine(Downloads(), "Kdrop");

    /// <summary>A pasta Downloads de verdade, mesmo que a pessoa a tenha mudado de lugar.</summary>
    private static string Downloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try { return Marshal.PtrToStringUni(ptr)!; }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
        }
        catch
        {
            // Cai no caminho comum.
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint opcoes, IntPtr token, out IntPtr caminho);
}

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

    public long Total => Arquivos.Sum(a => Math.Max(0, a.Tamanho));

    public int Porcentagem => Total <= 0 ? 0 : (int)Math.Clamp(Recebidos * 100 / Total, 0, 100);
}

public sealed class ArquivoDoPedido
{
    public string Nome { get; set; } = "";
    public long Tamanho { get; set; }
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
    private string _assinaturaDosAparelhos = "";

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
        if (assinatura == _assinaturaDosAparelhos) return;

        _assinaturaDosAparelhos = assinatura;
        AparelhosMudaram?.Invoke();
    }

    private sealed class Anuncio
    {
        public int Kdrop { get; set; }
        public string Id { get; set; } = "";
        public string Nome { get; set; } = "";
        public int Porta { get; set; }
        public bool Notebook { get; set; }
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
                    var anuncio = JsonSerializer.SerializeToUtf8Bytes(new Anuncio
                    {
                        Kdrop = 1, Id = _id, Nome = NomeDestePc, Porta = Porta, Notebook = EhNotebook()
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
        }

        if (aceitar && lembrar && _cfg.KdropConfiaveis.All(c => c.Id != pedido.OrigemId))
        {
            _cfg.KdropConfiaveis.Add(new AparelhoConfiavel { Id = pedido.OrigemId, Nome = pedido.OrigemNome });
            _cfg.Salvar();
        }

        PedidoMudou?.Invoke(pedido);
    }

    private void ExpirarPedidos()
    {
        var agora = DateTime.UtcNow;
        foreach (var (id, pedido) in _pedidos)
        {
            var mudou = false;
            lock (pedido)
            {
                if (pedido.Estado == EstadoDoPedido.Esperando && agora - pedido.CriadoEm > PrazoDoPedido)
                {
                    pedido.Estado = EstadoDoPedido.Expirado;
                    mudou = true;
                }
            }

            if (mudou) PedidoMudou?.Invoke(pedido);

            // Pedido velho, terminado ou nao, sai da memoria.
            if (agora - pedido.CriadoEm > TimeSpan.FromHours(2)) _pedidos.TryRemove(id, out _);
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

        var bandeja = _bandeja()
            .Where(File.Exists)
            .Select(c => new FileInfo(c))
            .Select(f => new { id = IdDoArquivo(f.FullName), nome = f.Name, tamanho = f.Length });

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

        // Aparelho confiavel nao precisa de aceite.
        var confiavel = _cfg.KdropConfiaveis.Any(c => c.Id == pedido.OrigemId);
        pedido.Estado = confiavel ? EstadoDoPedido.Aceito : EstadoDoPedido.Esperando;
        _pedidos[pedido.Id] = pedido;

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

    private async Task Receber(string id, HttpListenerRequest req, HttpListenerResponse res)
    {
        if (!_pedidos.TryGetValue(id, out var pedido) || pedido.Estado != EstadoDoPedido.Aceito)
        {
            res.StatusCode = 403;
            return;
        }

        // So entra arquivo que estava no pedido aceito, pelo nome.
        var nome = NomeSeguro(req.QueryString["nome"] ?? "");
        if (pedido.Arquivos.All(a => a.Nome != nome))
        {
            res.StatusCode = 403;
            return;
        }

        var pasta = Pasta(_cfg);
        Directory.CreateDirectory(pasta);
        var destino = CaminhoLivre(pasta, nome);

        var buffer = new byte[81920];
        await using (var arquivo = File.Create(destino))
        {
            int n;
            var ultimoAviso = DateTime.MinValue;
            while ((n = await req.InputStream.ReadAsync(buffer)) > 0)
            {
                await arquivo.WriteAsync(buffer.AsMemory(0, n));
                lock (pedido) pedido.Recebidos += n;

                // Progresso para a Kisland, no maximo quatro vezes por segundo.
                if (DateTime.UtcNow - ultimoAviso > TimeSpan.FromMilliseconds(250))
                {
                    ultimoAviso = DateTime.UtcNow;
                    PedidoMudou?.Invoke(pedido);
                }
            }
        }

        bool terminou;
        lock (pedido)
        {
            pedido.Salvos.Add(destino);
            terminou = pedido.Salvos.Count >= pedido.Arquivos.Count;
            if (terminou) pedido.Estado = EstadoDoPedido.Concluido;
        }

        ArquivoRecebido?.Invoke(destino);
        PedidoMudou?.Invoke(pedido);

        await Escrever(res, "{\"ok\":true}", "application/json; charset=utf-8");
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

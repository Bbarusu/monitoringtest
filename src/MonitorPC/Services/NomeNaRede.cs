using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MonitorPC.Services;

public sealed class NomeNaRede : IDisposable
{
    public const int PortaMdns = 5353;
    private static readonly IPAddress Grupo = IPAddress.Parse("224.0.0.251");

    private UdpClient? _udp;
    private CancellationTokenSource? _fim;
    private byte[] _nome = Array.Empty<byte>();
    private string _nomeTexto = "";

    public string? Nome => _udp is null ? null : _nomeTexto;

    public static string NomePara(string nomeDoPc)
    {
        var semAcento = new StringBuilder();
        foreach (var c in nomeDoPc.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            semAcento.Append(char.IsLetterOrDigit(c) && c < 128 ? char.ToLowerInvariant(c) : '-');
        }

        var limpo = System.Text.RegularExpressions.Regex.Replace(semAcento.ToString(), "-+", "-").Trim('-');
        if (limpo.Length == 0) limpo = "pc";
        if (limpo.Length > 40) limpo = limpo[..40].TrimEnd('-');
        return $"kdrop-{limpo}.local";
    }

    public bool Iniciar(string nome)
    {
        Parar();

        try
        {
            _nomeTexto = nome;
            _nome = Codificar(nome);

            var udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, PortaMdns));

            if (KdropService.IpLocal() is { } ip && IPAddress.TryParse(ip, out var local))
            {
                udp.JoinMulticastGroup(Grupo, local);
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            }
            else
            {
                udp.JoinMulticastGroup(Grupo);
            }

            udp.MulticastLoopback = false;
            _udp = udp;
            _fim = new CancellationTokenSource();
            _ = Task.Run(() => Ouvir(udp, _fim.Token));

            Log.Nota($"Kdrop: endereço fixo {nome} respondendo na rede");
            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("Kdrop: endereço fixo (mDNS)", ex);
            Parar();
            return false;
        }
    }

    public void Parar()
    {
        try { _fim?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _fim = null;
    }

    public void Dispose() => Parar();

    private async Task Ouvir(UdpClient udp, CancellationToken fim)
    {
        while (!fim.IsCancellationRequested)
        {
            UdpReceiveResult recebido;
            try
            {
                recebido = await udp.ReceiveAsync(fim);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            try
            {
                Responder(udp, recebido.Buffer, recebido.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                Log.Erro("Kdrop: pergunta mDNS", ex);
            }
        }
    }

    private const ushort TipoA = 1;
    private const ushort TipoQualquer = 255;

    private void Responder(UdpClient udp, byte[] pacote, IPEndPoint origem)
    {
        if (pacote.Length < 12) return;

        var bandeiras = (ushort)(pacote[2] << 8 | pacote[3]);
        if ((bandeiras & 0x8000) != 0) return;

        var perguntas = pacote[4] << 8 | pacote[5];
        var pos = 12;
        var nossa = false;
        var inicioDaPergunta = 0;
        var fimDaPergunta = 0;

        for (var i = 0; i < perguntas && pos < pacote.Length; i++)
        {
            var inicio = pos;
            if (!LerNome(pacote, ref pos, out var nome)) return;
            if (pos + 4 > pacote.Length) return;

            var tipo = (ushort)(pacote[pos] << 8 | pacote[pos + 1]);
            pos += 4;

            if ((tipo == TipoA || tipo == TipoQualquer) &&
                string.Equals(nome, _nomeTexto, StringComparison.OrdinalIgnoreCase))
            {
                nossa = true;
                inicioDaPergunta = inicio;
                fimDaPergunta = pos;
                break;
            }
        }

        if (!nossa || KdropService.IpLocal() is not { } ip || !IPAddress.TryParse(ip, out var endereco)) return;

        var direta = origem.Port != PortaMdns;
        var resposta = MontarResposta(direta ? pacote[0..2] : new byte[2],
                                      direta ? pacote[inicioDaPergunta..fimDaPergunta] : null,
                                      endereco.GetAddressBytes());

        var destino = direta ? origem : new IPEndPoint(Grupo, PortaMdns);
        udp.Send(resposta, resposta.Length, destino);
    }

    private byte[] MontarResposta(byte[] id, byte[]? pergunta, byte[] ip)
    {
        using var m = new MemoryStream();
        m.Write(id);
        m.Write(new byte[] { 0x84, 0x00 });
        m.Write(new byte[] { 0x00, (byte)(pergunta is null ? 0 : 1) });
        m.Write(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 });
        if (pergunta is not null) m.Write(pergunta);

        m.Write(_nome);
        m.Write(new byte[] { 0x00, 0x01 });
        m.Write(new byte[] { 0x80, 0x01 });
        m.Write(new byte[] { 0x00, 0x00, 0x00, 0x78 });
        m.Write(new byte[] { 0x00, 0x04 });
        m.Write(ip);
        return m.ToArray();
    }

    private static byte[] Codificar(string nome)
    {
        using var m = new MemoryStream();
        foreach (var parte in nome.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(parte);
            m.WriteByte((byte)bytes.Length);
            m.Write(bytes);
        }

        m.WriteByte(0);
        return m.ToArray();
    }

    private static bool LerNome(byte[] pacote, ref int pos, out string nome)
    {
        var partes = new List<string>();
        var atual = pos;
        var pulou = false;
        var saltos = 0;

        while (true)
        {
            if (atual >= pacote.Length) { nome = ""; return false; }
            var tamanho = pacote[atual];

            if (tamanho == 0)
            {
                if (!pulou) pos = atual + 1;
                break;
            }

            if ((tamanho & 0xC0) == 0xC0)
            {
                if (atual + 1 >= pacote.Length || ++saltos > 10) { nome = ""; return false; }
                if (!pulou) pos = atual + 2;
                pulou = true;
                atual = (tamanho & 0x3F) << 8 | pacote[atual + 1];
                continue;
            }

            if (atual + 1 + tamanho > pacote.Length) { nome = ""; return false; }
            partes.Add(Encoding.ASCII.GetString(pacote, atual + 1, tamanho));
            atual += 1 + tamanho;
        }

        nome = string.Join('.', partes);
        return true;
    }
}

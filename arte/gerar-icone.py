"""
Gera o ícone do KInfo: o contorno de um chip, branco, sobre o quadrado azul do
acento.

Por que um script e não um .ico desenhado à mão: cada tamanho precisa de
proporções próprias. O mesmo desenho reduzido de 256 para 16 px vira borrão —
os pinos somem, o traço fecha o miolo. Aqui o traço engrossa e os pinos
diminuem de oito para quatro conforme o ícone encolhe, que é o que um conjunto
de ícones bem feito faz.

    python3 arte/gerar-icone.py

Escreve src/MonitorPC/kinfo.ico e arte/previa.png.
"""

import pathlib
from PIL import Image, ImageDraw

AZUL = (59, 130, 246, 255)
BRANCO = (255, 255, 255, 255)

ESCALA = 8

TAMANHOS = [16, 20, 24, 32, 48, 64, 128, 256]

def proporcoes(lado):
    """Proporções do desenho, em fração do lado, por faixa de tamanho."""
    if lado >= 48:
        return dict(corpo=0.46, traco=0.052, pino=0.078, pinos=8, miolo=0.15, cheio=False, raio=0.22)
    if lado >= 32:
        return dict(corpo=0.48, traco=0.070, pino=0.075, pinos=8, miolo=0.13, cheio=True, raio=0.18)
    if lado >= 24:
        return dict(corpo=0.50, traco=0.085, pino=0.075, pinos=4, miolo=0.16, cheio=True, raio=0.13)

    return dict(corpo=0.50, traco=0.105, pino=0.075, pinos=4, miolo=0, cheio=True, raio=0.08)

def desenhar(lado):
    s = lado * ESCALA
    p = proporcoes(lado)

    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=s * 0.22, fill=AZUL)

    traco = max(1, round(s * p["traco"]))
    meio = s / 2
    corpo = s * p["corpo"]
    c0, c1 = meio - corpo / 2, meio + corpo / 2

    comprimento = s * p["pino"]
    desloc = corpo * (0.26 if p["pinos"] == 8 else 0)
    posicoes = [meio - desloc, meio + desloc] if p["pinos"] == 8 else [meio]

    for v in posicoes:
        d.line([(v, c0 - comprimento), (v, c0)], fill=BRANCO, width=traco)
        d.line([(v, c1), (v, c1 + comprimento)], fill=BRANCO, width=traco)
        d.line([(c0 - comprimento, v), (c0, v)], fill=BRANCO, width=traco)
        d.line([(c1, v), (c1 + comprimento, v)], fill=BRANCO, width=traco)

    d.rounded_rectangle([c0, c0, c1, c1], radius=corpo * p["raio"],
                        outline=BRANCO, width=traco)

    m = s * p["miolo"]
    if m <= 0:
        return img.resize((lado, lado), Image.LANCZOS)

    caixa = [meio - m / 2, meio - m / 2, meio + m / 2, meio + m / 2]

    if p["cheio"]:
        d.rounded_rectangle(caixa, radius=m * 0.25, fill=BRANCO)
    else:
        d.rounded_rectangle(caixa, radius=m * 0.25, outline=BRANCO, width=traco)

    return img.resize((lado, lado), Image.LANCZOS)

def quadro_dib(img):
    """
    Um quadro do .ico no formato DIB, que é o que o Windows espera nos
    tamanhos pequenos.

    São os pixels de baixo para cima, em BGRA, seguidos da máscara AND —
    inútil num ícone de 32 bits, mas o formato exige que ela esteja lá.
    """
    l, a = img.size
    px = img.load()

    cabecalho = bytearray()
    cabecalho += (40).to_bytes(4, "little")
    cabecalho += l.to_bytes(4, "little", signed=True)
    cabecalho += (a * 2).to_bytes(4, "little", signed=True)
    cabecalho += (1).to_bytes(2, "little")
    cabecalho += (32).to_bytes(2, "little")
    cabecalho += (0).to_bytes(4, "little") * 6

    cores = bytearray()
    for y in range(a - 1, -1, -1):
        for x in range(l):
            r, g, b, al = px[x, y]
            cores += bytes((b, g, r, al))

    linha = ((l + 31) // 32) * 4
    mascara = bytes(linha * a)

    return bytes(cabecalho) + bytes(cores) + mascara

def escrever_ico(caminho, quadros):
    """
    Monta o .ico quadro a quadro.

    O save do Pillow com sizes= não serve aqui: ele guarda uma imagem só e
    reduz as outras, jogando fora o desenho próprio de cada tamanho — que é
    justamente o motivo deste script existir.
    """
    corpos = [quadro_dib(q) if q.size[0] < 128
              else _png(q) for q in quadros]

    cabecalho = (0).to_bytes(2, "little") + (1).to_bytes(2, "little") \
        + len(quadros).to_bytes(2, "little")

    deslocamento = 6 + 16 * len(quadros)
    entradas = bytearray()

    for q, corpo in zip(quadros, corpos):
        lado = q.size[0]
        entradas += bytes((lado if lado < 256 else 0, lado if lado < 256 else 0, 0, 0))
        entradas += (1).to_bytes(2, "little")
        entradas += (32).to_bytes(2, "little")
        entradas += len(corpo).to_bytes(4, "little")
        entradas += deslocamento.to_bytes(4, "little")
        deslocamento += len(corpo)

    caminho.write_bytes(cabecalho + bytes(entradas) + b"".join(corpos))

def _png(img):
    import io
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()

def main():
    raiz = pathlib.Path(__file__).resolve().parent.parent
    quadros = [desenhar(t) for t in TAMANHOS]

    destino = raiz / "src" / "MonitorPC" / "kinfo.ico"
    escrever_ico(destino, quadros)
    print("ícone:", destino)

    largura = sum(t for t in TAMANHOS) + 16 * len(TAMANHOS)
    previa = Image.new("RGBA", (largura, 300), (12, 13, 16, 255))
    x = 8
    for t, q in zip(TAMANHOS, quadros):
        previa.paste(q, (x, 150 - t // 2), q)
        x += t + 16

    grande = desenhar(256)
    previa.paste(grande, (largura - 272, 20), grande)

    saida = raiz / "arte" / "previa.png"
    previa.save(saida)
    print("prévia:", saida)

if __name__ == "__main__":
    main()

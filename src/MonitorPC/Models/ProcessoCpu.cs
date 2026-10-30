namespace MonitorPC.Models;

/// <summary>
/// Um processo e quanto da CPU ele usou no ultimo intervalo.
///
/// Separado de <see cref="ProcessoMemoria"/> de proposito, apesar da forma
/// parecida: memoria e um valor que existe agora e se le de uma vez; CPU so
/// existe entre duas leituras. Juntar os dois num registro so esconderia essa
/// diferenca, que e justamente a que o codigo precisa respeitar.
/// </summary>
/// <param name="CpuPct">
/// Porcentagem de um processador inteiro, somadas todas as threads e dividida
/// pelo numero de nucleos — a mesma conta do Gerenciador de Tarefas. Um
/// programa usando quatro nucleos a fundo num PC de oito marca 50, e nao 400.
/// </param>
public sealed record ProcessoCpu(
    int Pid,
    string Nome,
    string Titulo,
    double CpuPct,
    bool Protegido,
    bool TemJanela);

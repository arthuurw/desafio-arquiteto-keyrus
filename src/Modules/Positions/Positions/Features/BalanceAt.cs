namespace Positions.Features;

// Saldo de uma conta num instante: o `balanceAfter` do último lançamento até ele e a sequência desse lançamento
// (ADR-0003); sem lançamento até o instante, zero na versão 0.
internal sealed record BalanceAt(decimal Balance, long Version);

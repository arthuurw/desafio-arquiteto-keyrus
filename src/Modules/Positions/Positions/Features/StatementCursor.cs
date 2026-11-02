using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Positions.Features;

// Posição na navegação do extrato: a `sequence` do último item da página, em texto opaco (base64url). A página
// seguinte traz só sequências menores, então um lançamento novo nunca a desloca (ADR-0042). Não carrega o intervalo nem
// o tamanho da página, que o chamador reenvia.
internal sealed partial record StatementCursor(long BeforeSequence)
{
    private const string Prefix = "v1:";

    internal string Encode() =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(Prefix + BeforeSequence.ToString(CultureInfo.InvariantCulture)));

    internal static bool TryParse(string value, out StatementCursor? cursor)
    {
        cursor = null;
        if (value.Length > 64 || !Base64Url.IsValid(value))
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(value));
        var match = CursorPattern().Match(text);
        if (!match.Success || !long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return false;
        }

        cursor = new StatementCursor(sequence);
        return true;
    }

    [GeneratedRegex(@"^v1:([1-9][0-9]{0,18})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CursorPattern();
}

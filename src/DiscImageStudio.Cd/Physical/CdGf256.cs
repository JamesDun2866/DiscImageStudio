namespace DiscImageStudio.Cd.Physical;

/// <summary>
/// GF(2⁸) arithmetic of the CD CIRC: primitive polynomial x⁸ + x⁴ + x³ + x² + 1 (0x11D) with
/// primitive element α = 00000010 (ECMA-130 2nd edition, Annex C.7).
/// </summary>
internal static class CdGf256
{
    internal const int PrimitivePolynomial = 0x11D;

    private static readonly byte[] Exp = BuildExp();
    private static readonly byte[] Log = BuildLog();

    /// <summary>αⁿ for any non-negative exponent.</summary>
    internal static byte AlphaPower(int exponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exponent);
        return Exp[exponent % 255];
    }

    internal static byte Multiply(byte left, byte right)
        => left == 0 || right == 0 ? (byte)0 : Exp[Log[left] + Log[right]];

    internal static byte Inverse(byte value)
    {
        if (value == 0)
        {
            throw new DivideByZeroException("0 has no inverse in GF(256).");
        }

        return Exp[255 - Log[value]];
    }

    private static byte[] BuildExp()
    {
        // Doubled so Multiply can add two logarithms without a modulo.
        byte[] exp = new byte[510];
        int value = 1;
        for (int index = 0; index < 255; index++)
        {
            exp[index] = (byte)value;
            exp[index + 255] = (byte)value;
            value <<= 1;
            if ((value & 0x100) != 0)
            {
                value ^= PrimitivePolynomial;
            }
        }

        return exp;
    }

    private static byte[] BuildLog()
    {
        byte[] log = new byte[256];
        for (int index = 0; index < 255; index++)
        {
            log[Exp[index]] = (byte)index;
        }

        return log;
    }
}

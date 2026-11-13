namespace DiscImageStudio.Cd.Physical;

/// <summary>
/// The two Reed-Solomon codes of the CIRC (ECMA-130 Annex C.7): a codeword V of length n
/// satisfies H · V = 0, where row r (0..3) of H holds α^(r·(n−1−c)) in column c. C2 is the
/// (28,24) code with its four Q parity bytes in the middle (positions 12–15); C1 is the (32,28)
/// code with its four P parity bytes at the end (positions 28–31). The parity bytes are solved
/// directly from those check equations, so the encoder needs no generator polynomial.
/// </summary>
internal static class CdCircParity
{
    internal const int C2Length = 28;
    internal const int C1Length = 32;
    internal const int QStart = 12;
    internal const int PStart = 28;
    internal const int ParityCount = 4;

    private static readonly byte[,] C2Solver = BuildSolver(C2Length, QStart);
    private static readonly byte[,] C1Solver = BuildSolver(C1Length, PStart);

    /// <summary>Fills positions 12–15 of a 28-byte C2 vector (positions 0–11, 16–27 are data).</summary>
    internal static void EncodeC2(Span<byte> vector)
    {
        if (vector.Length != C2Length) throw new ArgumentException("C2 vectors have 28 bytes.", nameof(vector));
        Solve(vector, QStart, C2Solver);
    }

    /// <summary>Fills positions 28–31 of a 32-byte C1 vector (positions 0–27 are its input).</summary>
    internal static void EncodeC1(Span<byte> vector)
    {
        if (vector.Length != C1Length) throw new ArgumentException("C1 vectors have 32 bytes.", nameof(vector));
        Solve(vector, PStart, C1Solver);
    }

    /// <summary>The four check sums H · V; all zero for a valid (non-inverted) codeword.</summary>
    internal static void Syndromes(ReadOnlySpan<byte> vector, Span<byte> syndromes)
    {
        if (syndromes.Length < ParityCount) throw new ArgumentException("Four syndromes are required.", nameof(syndromes));
        int n = vector.Length;
        for (int row = 0; row < ParityCount; row++)
        {
            byte sum = 0;
            for (int column = 0; column < n; column++)
            {
                sum ^= CdGf256.Multiply(vector[column], CdGf256.AlphaPower(row * (n - 1 - column)));
            }

            syndromes[row] = sum;
        }
    }

    private static void Solve(Span<byte> vector, int parityStart, byte[,] solver)
    {
        int n = vector.Length;
        Span<byte> known = stackalloc byte[ParityCount];
        for (int row = 0; row < ParityCount; row++)
        {
            byte sum = 0;
            for (int column = 0; column < n; column++)
            {
                if (column >= parityStart && column < parityStart + ParityCount)
                {
                    continue;
                }

                sum ^= CdGf256.Multiply(vector[column], CdGf256.AlphaPower(row * (n - 1 - column)));
            }

            known[row] = sum;
        }

        // H_parity · q = known (subtraction is XOR in characteristic 2).
        for (int parity = 0; parity < ParityCount; parity++)
        {
            byte value = 0;
            for (int row = 0; row < ParityCount; row++)
            {
                value ^= CdGf256.Multiply(solver[parity, row], known[row]);
            }

            vector[parityStart + parity] = value;
        }
    }

    /// <summary>Inverse of the 4×4 check-matrix block that multiplies the parity bytes.</summary>
    private static byte[,] BuildSolver(int n, int parityStart)
    {
        byte[,] matrix = new byte[ParityCount, ParityCount * 2];
        for (int row = 0; row < ParityCount; row++)
        {
            for (int parity = 0; parity < ParityCount; parity++)
            {
                matrix[row, parity] = CdGf256.AlphaPower(row * (n - 1 - (parityStart + parity)));
            }

            matrix[row, ParityCount + row] = 1;
        }

        // Gauss-Jordan elimination over GF(256).
        for (int column = 0; column < ParityCount; column++)
        {
            int pivot = column;
            while (matrix[pivot, column] == 0)
            {
                pivot++;
            }

            if (pivot != column)
            {
                for (int k = 0; k < ParityCount * 2; k++)
                {
                    (matrix[pivot, k], matrix[column, k]) = (matrix[column, k], matrix[pivot, k]);
                }
            }

            byte scale = CdGf256.Inverse(matrix[column, column]);
            for (int k = 0; k < ParityCount * 2; k++)
            {
                matrix[column, k] = CdGf256.Multiply(matrix[column, k], scale);
            }

            for (int row = 0; row < ParityCount; row++)
            {
                if (row == column || matrix[row, column] == 0)
                {
                    continue;
                }

                byte factor = matrix[row, column];
                for (int k = 0; k < ParityCount * 2; k++)
                {
                    matrix[row, k] ^= CdGf256.Multiply(factor, matrix[column, k]);
                }
            }
        }

        byte[,] inverse = new byte[ParityCount, ParityCount];
        for (int row = 0; row < ParityCount; row++)
        {
            for (int k = 0; k < ParityCount; k++)
            {
                inverse[row, k] = matrix[row, ParityCount + k];
            }
        }

        return inverse;
    }
}

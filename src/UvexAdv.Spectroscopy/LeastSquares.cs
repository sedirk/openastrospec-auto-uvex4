namespace UvexAdv.Spectroscopy;

internal static class LeastSquares
{
    public static double[] Quadratic(IReadOnlyList<double> x, IReadOnlyList<double> y) => Polynomial(x, y, 2);

    public static double[] Polynomial(IReadOnlyList<double> x, IReadOnlyList<double> y, int degree)
    {
        if (x.Count != y.Count || x.Count < degree + 1 || degree is < 1 or > 3)
        {
            throw new ArgumentException("Insufficient or invalid polynomial samples.");
        }

        var size = degree + 1;
        var augmented = new double[size, size + 1];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                augmented[row, column] = x.Sum(value => Math.Pow(value, row + column));
            }

            augmented[row, size] = Enumerable.Range(0, x.Count).Sum(index => y[index] * Math.Pow(x[index], row));
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < size; row++)
            {
                if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot]))
                {
                    best = row;
                }
            }

            if (Math.Abs(augmented[best, pivot]) < 1e-12)
            {
                throw new InvalidOperationException("Polynomial fit is singular.");
            }

            if (best != pivot)
            {
                for (var column = pivot; column <= size; column++)
                {
                    (augmented[pivot, column], augmented[best, column]) = (augmented[best, column], augmented[pivot, column]);
                }
            }

            var divisor = augmented[pivot, pivot];
            for (var column = pivot; column <= size; column++)
            {
                augmented[pivot, column] /= divisor;
            }

            for (var row = 0; row < size; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = augmented[row, pivot];
                for (var column = pivot; column <= size; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        return Enumerable.Range(0, size).Select(row => augmented[row, size]).ToArray();
    }
}

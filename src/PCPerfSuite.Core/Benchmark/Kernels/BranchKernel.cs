using System.Runtime.CompilerServices;

namespace PCPerfSuite.Core.Benchmark.Kernels;

/// <summary>
/// Noyau à branchements : un parcours dont chaque pas dépend de la donnée lue (trois chemins aux corps différents, pour
/// qu'aucun ne se réduise à un cmov), sur une table de 64 Ko qui tient en L1/L2. C'est le prédicteur de branchement et le
/// front-end qu'on mesure, pas la mémoire. Opérations : un pas par itération.
/// </summary>
public sealed class BranchKernel : ICpuKernel
{
    public const int TableSize = 1 << 14;
    public const int StepsPerRun = 1 << 20;
    private const uint Mask = TableSize - 1;

    private readonly uint[] _table = new uint[TableSize];

    public BranchKernel(ulong seed)
    {
        var random = new SeededRandom(seed);
        random.Fill(_table);
    }

    public string Key => "branches";

    public string Label => "Branchements (parcours dépendant des données)";

    public string Unit => "Mops/s";

    public double OperationsPerRun => StepsPerRun;

    public double UnitScale => 1e6;

    public string InstructionSet => "scalaire";

    public bool IsComparable => true;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ulong Run()
    {
        uint[] table = _table;
        uint index = 0;
        ulong acc = 0x9E3779B97F4A7C15UL;
        for (int step = 0; step < StepsPerRun; step++)
        {
            uint v = table[index];
            if ((v & 1) != 0)
            {
                acc += v;
                index = (index + (v >> 3)) & Mask;
            }
            else if ((v & 2) != 0)
            {
                acc ^= (ulong)v << 7;
                index = (index * 3 + 1) & Mask;
            }
            else
            {
                acc = acc * 31 + v;
                index = (v ^ (uint)acc) & Mask;
            }

            if ((acc & 0x10) != 0) acc++;
        }
        return acc;
    }

    public void Dispose()
    {
        // Rien à libérer.
    }
}

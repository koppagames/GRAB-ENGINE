using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GrabEngine.AI;

/// <summary>
/// A tiny fully-connected network: ReLU hidden layer, linear outputs. ReLU is used
/// because GASM has MAX but no tanh, so exactly the same maths can be baked into a
/// real Code Block. The network is trained in the editor, then the weights are
/// emitted as constants; no runtime learning is needed in the retail game.
/// </summary>
public sealed class NeuralNetwork
{
    public int Inputs { get; }
    public int Hidden { get; }
    public int Outputs { get; }
    public float[] Weights { get; private set; }

    public NeuralNetwork(int inputs, int hidden, int outputs, float[]? weights = null, Random? rng = null)
    {
        if (inputs <= 0 || hidden < 0 || outputs <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputs), "Network inputs/outputs must be positive and hidden cannot be negative.");
        Inputs = inputs;
        Hidden = hidden;
        Outputs = outputs;
        int count = WeightCountFor(inputs, hidden, outputs);
        if (weights != null)
        {
            if (weights.Length != count)
                throw new ArgumentException($"Expected {count} weights, got {weights.Length}.", nameof(weights));
            Weights = (float[])weights.Clone();
        }
        else
        {
            Weights = new float[count];
            Initialize(rng ?? new Random(1));
        }
    }

    public int WeightCount => Weights.Length;

    public static int WeightCountFor(int inputs, int hidden, int outputs)
        => hidden == 0 ? inputs * outputs + outputs : inputs * hidden + hidden + hidden * outputs + outputs;

    private void Initialize(Random rng)
    {
        if (Hidden == 0)
        {
            float linearLimit = MathF.Sqrt(6f / MathF.Max(1, Inputs + Outputs));
            for (int i = 0; i < Weights.Length; i++)
                Weights[i] = (float)((rng.NextDouble() * 2.0 - 1.0) * linearLimit);
            return;
        }
        int w = 0;
        float limit = MathF.Sqrt(6f / MathF.Max(1, Inputs + Hidden));
        for (; w < Inputs * Hidden; w++)
            Weights[w] = (float)((rng.NextDouble() * 2.0 - 1.0) * limit);
        for (int h = 0; h < Hidden; h++, w++)
            Weights[w] = (float)((rng.NextDouble() * 2.0 - 1.0) * 0.25);
        float outLimit = MathF.Sqrt(6f / MathF.Max(1, Hidden + Outputs));
        for (; w < Weights.Length; w++)
            Weights[w] = (float)((rng.NextDouble() * 2.0 - 1.0) * outLimit);
    }

    public void Forward(ReadOnlySpan<float> input, Span<float> hidden, Span<float> output)
    {
        if (input.Length < Inputs || hidden.Length < Hidden || output.Length < Outputs)
            throw new ArgumentException("Forward buffers are too small.");
        if (Hidden == 0)
        {
            int linearBias = Inputs * Outputs;
            for (int o = 0; o < Outputs; o++)
            {
                float sum = 0;
                for (int i = 0; i < Inputs; i++)
                    sum += input[i] * Weights[o * Inputs + i];
                output[o] = sum + Weights[linearBias + o];
            }
            return;
        }

        int w = 0;
        int bias1 = Inputs * Hidden;
        for (int h = 0; h < Hidden; h++)
        {
            float sum = 0;
            for (int i = 0; i < Inputs; i++)
                sum += input[i] * Weights[w++];
            sum += Weights[bias1 + h];
            hidden[h] = sum > 0 ? sum : 0;
        }

        int w2 = bias1 + Hidden;
        int bias2 = w2 + Hidden * Outputs;
        for (int o = 0; o < Outputs; o++)
        {
            float sum = 0;
            for (int h = 0; h < Hidden; h++)
                sum += hidden[h] * Weights[w2 + o * Hidden + h];
            sum += Weights[bias2 + o];
            output[o] = sum;
        }
    }

    public float[] Evaluate(params float[] input)
    {
        var hidden = new float[Hidden];
        var output = new float[Outputs];
        Forward(input, hidden, output);
        return output;
    }

    public NeuralNetwork Clone() => new(Inputs, Hidden, Outputs, Weights);

    public static NeuralNetwork Crossover(NeuralNetwork a, NeuralNetwork b, Random rng)
    {
        if (a.Inputs != b.Inputs || a.Hidden != b.Hidden || a.Outputs != b.Outputs)
            throw new ArgumentException("Networks must have the same shape.");
        var weights = new float[a.Weights.Length];
        for (int i = 0; i < weights.Length; i++)
            weights[i] = rng.NextDouble() < 0.5 ? a.Weights[i] : b.Weights[i];
        return new NeuralNetwork(a.Inputs, a.Hidden, a.Outputs, weights);
    }

    /// <summary>
    /// Mutates weights in place. Most genes are untouched; a few get a small Gaussian
    /// nudge and a smaller number are fully reset so the population cannot get stuck.
    /// </summary>
    public void Mutate(Random rng, float rate, float scale, float resetChance = 0.035f)
    {
        for (int i = 0; i < Weights.Length; i++)
        {
            if (rng.NextDouble() >= rate) continue;
            if (rng.NextDouble() < resetChance)
                Weights[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            else
                Weights[i] += (float)(Gaussian(rng) * scale);
            Weights[i] = Math.Clamp(Weights[i], -8f, 8f);
        }
    }

    public static double Gaussian(Random rng)
    {
        // Box-Muller. The guard avoids Log(0).
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public string[] ToLines()
    {
        var lines = new List<string>();
        if (Hidden == 0)
        {
            int linearBias = Inputs * Outputs;
            for (int o = 0; o < Outputs; o++)
            {
                var values = new float[Inputs];
                for (int i = 0; i < Inputs; i++) values[i] = Weights[o * Inputs + i];
                lines.Add($"out{o}: " + string.Join(", ", values.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture))) +
                          $"  bias {Weights[linearBias + o].ToString("0.#####", CultureInfo.InvariantCulture)}");
            }
            return lines.ToArray();
        }
        int w = 0;
        for (int h = 0; h < Hidden; h++)
        {
            var values = new float[Inputs];
            for (int i = 0; i < Inputs; i++) values[i] = Weights[w++];
            float bias = Weights[Inputs * Hidden + h];
            lines.Add($"h{h}: " + string.Join(", ", values.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture))) +
                      $"  bias {bias.ToString("0.#####", CultureInfo.InvariantCulture)}");
        }
        int w2 = Inputs * Hidden + Hidden;
        int bias2 = w2 + Hidden * Outputs;
        for (int o = 0; o < Outputs; o++)
        {
            var values = new float[Hidden];
            for (int h = 0; h < Hidden; h++) values[h] = Weights[w2 + o * Hidden + h];
            lines.Add($"out{o}: " + string.Join(", ", values.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture))) +
                      $"  bias {Weights[bias2 + o].ToString("0.#####", CultureInfo.InvariantCulture)}");
        }
        return lines.ToArray();
    }
}

/// <summary>
/// The training simulator. It deliberately mirrors the generated C# script exactly:
/// 60 Hz integration, acceleration 22, damping 3/s, target (5, 5), walls at +/-6.5.
/// If training and the exported GASM disagree, the demo would not reproduce in game.
/// </summary>
public static class NeuralArena
{
    public const float HalfExtent = 6.5f;
    public const float TargetX = 5.0f;
    public const float TargetZ = 5.0f;
    public const float DemoStartX = -5.0f;
    public const float DemoStartZ = -5.0f;
    public const float Acceleration = 22.0f;
    public const float Drag = 3.0f;
    public const float MaxSpeed = 7.5f;
    public const float SuccessRadius = 0.65f;
    public const float DeltaTime = 1f / 60f;
    public const int DefaultMaxSteps = 480;

    public static readonly (float X, float Z)[] TrainingStarts =
    {
        (DemoStartX, DemoStartZ),
        (5f, -5f),
        (-5f, 5f),
        (0f, 0f)
    };

    public static readonly (float X, float Z)[] ValidationStarts =
    {
        (DemoStartX, DemoStartZ),
        (5f, -5f),
        (-5f, 5f),
        (0f, 0f),
        (5f, 4f),
        (0f, -5f)
    };

    public static float Distance(float x, float z)
    {
        float dx = TargetX - x;
        float dz = TargetZ - z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static NeuralEpisode Run(NeuralNetwork network, float startX, float startZ, int maxSteps, float[] hidden, float[] output)
    {
        float px = startX;
        float pz = startZ;
        float vx = 0;
        float vz = 0;
        float initial = Distance(px, pz);
        float best = initial;
        int wallHits = 0;
        bool reached = false;
        int step = 0;
        var inputs = new float[network.Inputs];

        for (; step < maxSteps; step++)
        {
            inputs[0] = (TargetX - px) / (HalfExtent * 2f);
            inputs[1] = (TargetZ - pz) / (HalfExtent * 2f);
            inputs[2] = vx / MaxSpeed;
            inputs[3] = vz / MaxSpeed;

            network.Forward(inputs, hidden, output);
            float ax = Math.Clamp(output[0], -1f, 1f);
            float az = Math.Clamp(output[1], -1f, 1f);

            vx += ax * Acceleration * DeltaTime;
            vz += az * Acceleration * DeltaTime;
            float damping = MathF.Max(0f, 1f - Drag * DeltaTime);
            vx *= damping;
            vz *= damping;

            px += vx * DeltaTime;
            pz += vz * DeltaTime;

            if (px < -HalfExtent) { px = -HalfExtent; vx = 0; wallHits++; }
            if (px > HalfExtent) { px = HalfExtent; vx = 0; wallHits++; }
            if (pz < -HalfExtent) { pz = -HalfExtent; vz = 0; wallHits++; }
            if (pz > HalfExtent) { pz = HalfExtent; vz = 0; wallHits++; }

            float distance = Distance(px, pz);
            if (distance < best) best = distance;
            if (distance <= SuccessRadius)
            {
                reached = true;
                step++;
                break;
            }
        }

        float final = Distance(px, pz);
        float fitness = (initial - final) * 10f;
        fitness += (initial - best) * 2f;
        fitness -= final * 2f;
        if (reached) fitness += 100f + (maxSteps - step) * 0.02f;
        fitness -= Math.Min(120, wallHits) * 0.5f;

        return new NeuralEpisode
        {
            StartX = startX,
            StartZ = startZ,
            EndX = px,
            EndZ = pz,
            InitialDistance = initial,
            FinalDistance = final,
            BestDistance = best,
            Steps = step,
            Reached = reached,
            WallHits = wallHits,
            Fitness = fitness
        };
    }
}

public sealed class NeuralEpisode
{
    public float StartX { get; set; }
    public float StartZ { get; set; }
    public float EndX { get; set; }
    public float EndZ { get; set; }
    public float InitialDistance { get; set; }
    public float FinalDistance { get; set; }
    public float BestDistance { get; set; }
    public int Steps { get; set; }
    public bool Reached { get; set; }
    public int WallHits { get; set; }
    public float Fitness { get; set; }

    public string Describe() =>
        FormattableString.Invariant(
            $"({StartX:0.0},{StartZ:0.0}) -> ({EndX:0.00},{EndZ:0.00})  distance {InitialDistance:0.00} -> {FinalDistance:0.00}  {(Reached ? "REACHED" : "missed")}  steps {Steps}  walls {WallHits}");
}

public sealed class NeuralTrainingOptions
{
    public int Population { get; set; } = 96;
    public int Hidden { get; set; } = 4;
    public int Generations { get; set; } = 80;
    public int Patience { get; set; } = 18;
    public int MaxSteps { get; set; } = NeuralArena.DefaultMaxSteps;
    public int Seed { get; set; } = 20261029;
    public float MutationRate { get; set; } = 0.22f;
    public float MutationScale { get; set; } = 0.35f;
    public bool StopWhenSolved { get; set; } = true;

    public NeuralTrainingOptions Clone() => (NeuralTrainingOptions)MemberwiseClone();
}

public sealed class NeuralGeneration
{
    public int Generation { get; set; }
    public float BestFitness { get; set; }
    public float AverageFitness { get; set; }
    public float BestDistance { get; set; }
    public float SuccessRate { get; set; }
    public bool Solved { get; set; }
    public float BestFitnessEver { get; set; }
}

public sealed class NeuralTrainingReport
{
    public NeuralTrainingOptions Options { get; set; } = new();
    public NeuralNetwork Network { get; set; } = new(4, 4, 2);
    public List<NeuralGeneration> History { get; } = new();
    public List<NeuralEpisode> Validation { get; } = new();
    public int GenerationsRun { get; set; }
    public float BestFitness { get; set; }
    public float BestDistance { get; set; }
    public float SuccessRate { get; set; }
    public bool Solved { get; set; }
    public string StopReason { get; set; } = "";
    public double ElapsedSeconds { get; set; }
    public NeuralEpisode? DemoRun { get; set; }

    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("GRAB ENGINE — Neural Ball training report");
        sb.AppendLine(FormattableString.Invariant($"network: 4 inputs -> {Options.Hidden} ReLU hidden -> 2 outputs ({Network.WeightCount} weights)"));
        sb.AppendLine(FormattableString.Invariant($"population: {Options.Population}   generations: {GenerationsRun}/{Options.Generations}   patience: {Options.Patience}   seed: {Options.Seed}"));
        sb.AppendLine(FormattableString.Invariant($"best fitness: {BestFitness:0.00}   best mean distance: {BestDistance:0.000}   validation success: {SuccessRate:P0}"));
        sb.AppendLine(FormattableString.Invariant($"solved: {Solved}   elapsed: {ElapsedSeconds:0.0}s"));
        sb.AppendLine("stop: " + StopReason);
        if (DemoRun != null) sb.AppendLine("demo: " + DemoRun.Describe());
        sb.AppendLine();
        sb.AppendLine("generation  best-fitness  best-distance  success  average-fitness");
        foreach (var g in History)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"{g.Generation,10}  {g.BestFitness,12:0.00}  {g.BestDistance,13:0.000}  {g.SuccessRate,7:P0}  {g.AverageFitness,14:0.00}"));
        }
        sb.AppendLine();
        sb.AppendLine("weights:");
        foreach (var line in Network.ToLines()) sb.AppendLine("  " + line);
        return sb.ToString();
    }
}

/// <summary>
/// Genetic algorithm. Each generation evaluates the whole population on the same
/// four starts, keeps the elite, then breeds with uniform crossover and Gaussian
/// mutation. Training stops when solved, when the user stops it, when the generation
/// limit is reached, or — the important one — when the best fitness has not improved
/// for Patience generations.
/// </summary>
public sealed class NeuralTrainer
{
    private readonly NeuralTrainingOptions _options;

    public NeuralTrainer(NeuralTrainingOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    private sealed class Genome
    {
        public NeuralNetwork Network = null!;
        public float Fitness;
    }

    public NeuralTrainingReport Train(Action<NeuralGeneration>? progress = null, System.Threading.CancellationToken token = default)
    {
        var options = _options;
        var rng = new Random(options.Seed);
        var hidden = new float[options.Hidden];
        var output = new float[2];
        var network = new NeuralNetwork(4, options.Hidden, 2, rng: rng);
        var population = new List<Genome>(options.Population);
        for (int i = 0; i < options.Population; i++)
        {
            population.Add(new Genome
            {
                Network = i == 0 ? network : new NeuralNetwork(4, options.Hidden, 2, rng: rng)
            });
        }

        var report = new NeuralTrainingReport
        {
            Options = options.Clone(),
            Network = population[0].Network.Clone()
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        float bestEver = float.MinValue;
        int stale = 0;
        string stopReason = "Generation limit reached.";
        bool solved = false;

        for (int generation = 0; generation < options.Generations; generation++)
        {
            token.ThrowIfCancellationRequested();

            // Evaluate every genome on the same starts so fitness is comparable
            // across generations (this is what makes early stopping meaningful).
            foreach (var genome in population)
            {
                float total = 0;
                for (int e = 0; e < NeuralArena.TrainingStarts.Length; e++)
                {
                    var start = NeuralArena.TrainingStarts[e];
                    total += NeuralArena.Run(genome.Network, start.X, start.Z, options.MaxSteps, hidden, output).Fitness;
                }
                genome.Fitness = total / NeuralArena.TrainingStarts.Length;
            }

            var ranked = population.OrderByDescending(g => g.Fitness).ToList();
            var champion = ranked[0];
            if (champion.Fitness > bestEver + 0.01f)
            {
                bestEver = champion.Fitness;
                stale = 0;
                report.Network = champion.Network.Clone();
            }
            else
            {
                stale++;
            }

            var validation = Validate(report.Network, options.MaxSteps, hidden, output);
            float success = validation.Count(e => e.Reached) / (float)validation.Count;
            float meanDistance = validation.Average(e => e.FinalDistance);
            bool allReached = validation.All(e => e.Reached);
            report.Validation.Clear();
            report.Validation.AddRange(validation);
            report.BestFitness = bestEver;
            report.BestDistance = meanDistance;
            report.SuccessRate = success;
            report.Solved = allReached;

            var stat = new NeuralGeneration
            {
                Generation = generation + 1,
                BestFitness = ranked[0].Fitness,
                AverageFitness = (float)population.Average(g => g.Fitness),
                BestDistance = meanDistance,
                SuccessRate = success,
                Solved = allReached,
                BestFitnessEver = bestEver
            };
            report.History.Add(stat);
            progress?.Invoke(stat);

            if (allReached)
            {
                solved = true;
                if (options.StopWhenSolved)
                {
                    stopReason = "Solved — the network reached the target from every validation start.";
                    break;
                }
            }
            if (stale >= options.Patience)
            {
                stopReason = $"Early stop — best fitness did not improve for {options.Patience} generations.";
                break;
            }

            // Breed the next generation. Elites survive untouched; the rest are
            // tournament-selected crossover children with Gaussian mutation.
            int elite = Math.Max(2, options.Population / 8);
            var next = new List<Genome>(options.Population);
            for (int i = 0; i < elite && i < ranked.Count; i++)
                next.Add(new Genome { Network = ranked[i].Network.Clone() });

            while (next.Count < options.Population)
            {
                if (rng.NextDouble() < 0.04)
                {
                    // Fresh blood keeps the search from collapsing onto one genome.
                    next.Add(new Genome { Network = new NeuralNetwork(4, options.Hidden, 2, rng: rng) });
                    continue;
                }
                var a = Tournament(ranked, rng);
                var b = Tournament(ranked, rng);
                var child = NeuralNetwork.Crossover(a.Network, b.Network, rng);
                child.Mutate(rng, options.MutationRate, options.MutationScale);
                next.Add(new Genome { Network = child });
            }
            population = next;
        }

        report.GenerationsRun = report.History.Count;
        report.Solved = solved;
        if (solved && stopReason == "Generation limit reached.")
            stopReason = "Generation limit reached — the network had already solved every validation start.";
        report.StopReason = stopReason;
        report.ElapsedSeconds = clock.Elapsed.TotalSeconds;
        report.BestDistance = report.Validation.Count > 0 ? report.Validation.Average(e => e.FinalDistance) : 0;
        report.SuccessRate = report.Validation.Count > 0 ? report.Validation.Count(e => e.Reached) / (float)report.Validation.Count : 0;
        report.DemoRun = NeuralArena.Run(report.Network, NeuralArena.DemoStartX, NeuralArena.DemoStartZ,
            options.MaxSteps, new float[options.Hidden], new float[2]);
        return report;
    }

    private static List<NeuralEpisode> Validate(NeuralNetwork network, int maxSteps, float[] hidden, float[] output)
    {
        var result = new List<NeuralEpisode>();
        foreach (var start in NeuralArena.ValidationStarts)
            result.Add(NeuralArena.Run(network, start.X, start.Z, maxSteps, hidden, output));
        return result;
    }

    private static Genome Tournament(List<Genome> ranked, Random rng)
    {
        Genome best = ranked[rng.Next(ranked.Count)];
        for (int i = 0; i < 2; i++)
        {
            var contender = ranked[rng.Next(ranked.Count)];
            if (contender.Fitness > best.Fitness) best = contender;
        }
        return best;
    }
}

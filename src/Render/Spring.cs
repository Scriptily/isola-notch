namespace Isola.Render;

/// <summary>
/// Spring parameters in Apple's terms: <c>Response</c> is the period of the
/// undamped oscillation in seconds (how quickly it gets there), <c>Damping</c>
/// is the damping ratio (1 = no overshoot, lower = bouncier). Same mapping as
/// SwiftUI's <c>.spring(response:dampingFraction:)</c>: stiffness = (2π/response)²,
/// damping coefficient = 4π·ζ/response, unit mass.
/// </summary>
public readonly record struct SpringSpec(double Response, double Damping)
{
    // Measured frame by frame from Apple's iPhone 14 Pro reveal film (30 fps):
    // the compact island grows 445→761 px, peaks at 781 (6.3% overshoot)
    // 0.30 s in, settles by ~0.6 s. Overshoot gives ζ ≈ 0.66; the time to peak
    // gives ω₀ ≈ 14 rad/s → response ≈ 0.45 s.
    public static readonly SpringSpec Expand = new(0.45, 0.68);
    /// <summary>Collapsing is quicker and barely overshoots in the same footage.</summary>
    public static readonly SpringSpec Collapse = new(0.38, 0.86);
    public static readonly SpringSpec Smooth = new(0.32, 1.0);
    public static readonly SpringSpec Content = new(0.26, 1.0);
    public static readonly SpringSpec Snappy = new(0.22, 0.82);
    public static readonly SpringSpec Press = new(0.18, 0.62);
    public static readonly SpringSpec Wobbly = new(0.55, 0.5);
    public static readonly SpringSpec Slow = new(0.9, 1.0);
    /// <summary>Reduce Motion: no overshoot, short.</summary>
    public static readonly SpringSpec Reduced = new(0.18, 1.0);
}

/// <summary>
/// One animated value driven by a damped harmonic oscillator, solved in closed
/// form so a frame of any length lands exactly where the physics says (a hitch
/// never makes it overshoot more). Retargeting keeps the current value and
/// velocity, which is what makes every animation interruptible without a jump.
/// </summary>
public sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;
    public SpringSpec Spec;
    /// <summary>Settled when within this of the target and moving slower than 10× it per second.</summary>
    public double Epsilon;

    public Spring(double value, SpringSpec spec, double epsilon = 0.005)
    {
        Value = Target = value; Spec = spec; Epsilon = epsilon;
    }

    public bool Settled => Math.Abs(Value - Target) <= Epsilon && Math.Abs(Velocity) <= Epsilon * 10;

    public void To(double target, SpringSpec? spec = null)
    {
        Target = target;
        if (spec.HasValue) Spec = spec.Value;
    }

    /// <summary>Jump without animating (initial placement, Reduce Motion snaps).</summary>
    public void Snap(double v) { Value = Target = v; Velocity = 0; }

    /// <summary>Add a velocity kick, e.g. a "bump" when something arrives.</summary>
    public void Kick(double v) => Velocity += v;

    /// <returns>true while still moving.</returns>
    public bool Step(double dt)
    {
        if (Settled) { Value = Target; Velocity = 0; return false; }
        if (dt <= 0) return true;
        dt = Math.Min(dt, 0.25);   // after a long stall, don't teleport: resume
        (double d, double v) = Solve(Value - Target, Velocity, Spec, dt);
        Value = Target + d; Velocity = v;
        if (Settled) { Value = Target; Velocity = 0; return false; }
        return true;
    }

    /// <summary>Displacement and velocity after <paramref name="t"/> seconds.</summary>
    public static (double d, double v) Solve(double d0, double v0, SpringSpec s, double t)
    {
        double response = Math.Max(0.02, s.Response);
        double w0 = 2 * Math.PI / response;
        double z = Math.Max(0, s.Damping);
        if (z < 0.999)
        {
            double wd = w0 * Math.Sqrt(1 - z * z), a = z * w0;
            double B = (v0 + a * d0) / wd;
            double e = Math.Exp(-a * t), c = Math.Cos(wd * t), sn = Math.Sin(wd * t);
            double d = e * (d0 * c + B * sn);
            double v = e * (-a * (d0 * c + B * sn) + (-d0 * wd * sn + B * wd * c));
            return (d, v);
        }
        if (z <= 1.001)
        {
            double C = v0 + w0 * d0, e = Math.Exp(-w0 * t);
            return (e * (d0 + C * t), e * (C - w0 * (d0 + C * t)));
        }
        {
            double r = Math.Sqrt(z * z - 1);
            double r1 = -w0 * (z - r), r2 = -w0 * (z + r);
            double c2 = (v0 - r1 * d0) / (r2 - r1), c1 = d0 - c2;
            double e1 = Math.Exp(r1 * t), e2 = Math.Exp(r2 * t);
            return (c1 * e1 + c2 * e2, c1 * r1 * e1 + c2 * r2 * e2);
        }
    }

    public override string ToString() => $"{Value:0.###}→{Target:0.###} v={Velocity:0.##}";
}

/// <summary>A bag of springs stepped together; reports whether anything still moves.</summary>
public sealed class Animator
{
    readonly List<Spring> springs = new();
    public bool ReduceMotion;

    public Spring Make(double v, SpringSpec spec, double eps = 0.005)
    {
        var s = new Spring(v, spec, eps);
        springs.Add(s);
        return s;
    }

    public bool Step(double dt)
    {
        bool moving = false;
        foreach (var s in springs)
        {
            if (ReduceMotion && s.Spec.Damping < 1) s.Spec = s.Spec with { Damping = 1 };
            moving |= s.Step(dt);
        }
        return moving;
    }

    public bool Moving { get { foreach (var s in springs) if (!s.Settled) return true; return false; } }
}

public static class Ease
{
    public static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Smooth(double t) { t = Clamp01(t); return t * t * (3 - 2 * t); }
    public static double OutCubic(double t) { t = Clamp01(t); return 1 - Math.Pow(1 - t, 3); }
    public static double InOutSine(double t) => -(Math.Cos(Math.PI * Clamp01(t)) - 1) / 2;
    /// <summary>Remap v from [a,b] to [0,1], clamped.</summary>
    public static double Range(double v, double a, double b) => Clamp01((v - a) / (b - a));
}

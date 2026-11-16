namespace Ah.Hooks.Plumbing;

/// <summary>
/// What a hook unit protects, which decides whether its refusal may be softened to advice. Every unit declares one; there is no default.
/// </summary>
internal enum GateClass
{
    /// <summary>The ah2 binary, trust stores, daemon integrity, hook-payload authenticity and sender identity. Always blocks.</summary>
    BlockA,

    /// <summary>The push and ship regime. Always blocks.</summary>
    BlockB,

    /// <summary>A rule or cap the user set explicitly. Always blocks.</summary>
    BlockC,

    /// <summary>Some refusals of the unit block and some are advice; the unit says which.</summary>
    Split,

    /// <summary>A refusal that may be given as advice instead of a block.</summary>
    Advisory,

    /// <summary>The unit never refuses: it observes, injects context or raises an alarm.</summary>
    NotAGate,
}
